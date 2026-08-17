using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

namespace RacingBotCup.Track
{
    /// <summary>A circuit produced from a seed. Same seed in, same circuit out, on every machine.</summary>
    public sealed class GeneratedTrack
    {
        public int Seed;
        public TrackParams Params;
        public CircuitLayout Layout;
        public Spline Spline;
        public TrackModel Model;
        public int Attempts;
        public bool FullyValid;
        public string ValidationNote = "";
    }

    /// <summary>
    /// Builds an F1-style circuit: a sequence of named sections (straight, corner, hairpin,
    /// chicane, esses) laid around a closed ring.
    ///
    /// The ring is what makes this tractable. Laying sections end to end like a turtle produces a
    /// shape that almost never closes; anchoring each section between two points on a circle means
    /// the loop closes by construction, and each section only has to decide how to get from its
    /// own anchor to the next one.
    ///
    /// Each section draws its own centreline analytically and hands back a dense polyline, which
    /// then becomes the spline's knots. Placing a handful of control points and letting AutoSmooth
    /// interpolate does not survive contact with a hairpin: the tangent it derives for a knot
    /// depends on how far away its neighbours are, so where a 40 m straight meets an 8 m arc the
    /// long side overshoots and folds the corner into a 2 m cusp. Knots every few metres leave the
    /// smoothing nothing to invent.
    /// </summary>
    public static class TrackGenerator
    {
        /// <summary>Bumped to 3.x when circuits gained irregular silhouettes; 3.1 added optional
        /// hazard sections (SharpHairpin/ObstacleStraight/RampCorner).</summary>
        public const string Version = "3.1.0";

        const int k_MaxAttempts = 10;
        const float k_ValidationSpacing = 2f;

        /// <summary>Derive salt for the hazard-selection stream — distinct from every per-attempt
        /// stream (<c>Derive(attempt)</c>, attempt in [0, k_MaxAttempts)) so rolling hazards once
        /// up front never collides with or gets reset by the retry loop.</summary>
        const int k_HazardSalt = 913317;

        /// <summary>Spacing of the centreline samples that become spline knots, in metres.</summary>
        const float k_SampleSpacing = 7f;

        /// <summary>
        /// Smoothing passes over the finished centreline.
        ///
        /// Sections are drawn independently and meet at their shared anchor, where a straight
        /// arriving on one heading meets a leg leaving on another. That junction is a corner of
        /// zero radius — invisible on screen, but a wall as far as the car is concerned. A few
        /// low-pass passes round every junction at once and leave straights and arcs alone, since
        /// averaging collinear or evenly curved points returns them unchanged.
        /// </summary>
        const int k_SmoothingPasses = 10;

        /// <summary>How far either side of a section boundary counts as the transition into it.</summary>
        const float k_TransitionMetres = 30f;

        static readonly float k_ChicaneShapePeak = ShapePeak(1);
        static readonly float k_EssesShapePeak = ShapePeak(2);

        public static GeneratedTrack Generate(int seed, bool enableHazardSections = false)
        {
            var trackParams = TrackParams.FromSeed(seed);

            // Rolled once per seed, off the attempt loop's streams entirely: re-rolling per attempt
            // would let "first attempt to validate wins" silently favour whichever hazard mix
            // happens to be easiest to validate, badly skewing the intended ~25% appearance rate.
            var hazardRandom = new DeterministicRandom(seed).Derive(k_HazardSalt);
            var hazards = enableHazardSections ? CircuitLayout.RollHazards(ref hazardRandom) : CircuitLayout.HazardSelection.None;

            GeneratedTrack best = null;
            var bestScore = float.MaxValue;

            for (var attempt = 0; attempt < k_MaxAttempts; attempt++)
            {
                var random = new DeterministicRandom(seed).Derive(attempt);

                // Each retry redraws the layout too, so a hostile seed explores genuinely different
                // circuits rather than nudging the same bad one. The hazard mix itself does not vary
                // between attempts — see hazards above.
                var layout = CircuitLayout.Build(ref random, trackParams.TargetLength, hazards);

                // Later attempts flatten the anchor jitter and open the corners out, so a seed that
                // keeps producing something too tight converges instead of exhausting the budget.
                var ease = attempt / (float)k_MaxAttempts;
                var candidateParams = trackParams;
                candidateParams.RadialPerturbation *= 1f - ease * 0.7f;
                candidateParams.ShapeAmplitude *= 1f - ease * 0.45f;
                candidateParams.ChicaneThrow *= 1f + ease * 0.7f;
                candidateParams.EssesThrow *= 1f + ease * 0.7f;
                candidateParams.HairpinReach *= 1f - ease * 0.35f;
                candidateParams.SharpHairpinReach *= 1f - ease * 0.35f;
                candidateParams.RampCornerThrow *= 1f - ease * 0.35f;

                var points = new List<float3>();
                var sectionStart = new int[layout.Count];
                BuildCentreline(layout, candidateParams, ref random, points, sectionStart);
                SmoothClosed(points, k_SmoothingPasses);

                var spline = CreateSpline(points);
                spline = ScaleToTargetLength(spline, points, candidateParams);

                var knotFractions = KnotArcFractions(spline);
                var score = Validate(spline, knotFractions, sectionStart, layout, candidateParams, out var note);

                if (score <= 0f)
                {
                    return Finish(seed, trackParams, layout, spline, sectionStart, knotFractions, attempt + 1, true, "");
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    best = Finish(seed, trackParams, layout, spline, sectionStart, knotFractions, attempt + 1, false, note);
                }
            }

            Debug.LogWarning(
                $"[RacingBotCup] Seed {seed} did not produce a fully valid circuit in {k_MaxAttempts} attempts " +
                $"({best?.ValidationNote}). Using the closest candidate.");

            return best;
        }

        /// <summary>
        /// Cumulative arc length at each knot, as a fraction of the whole lap.
        ///
        /// Unity's spline <c>t</c> is normalised by arc length, not by curve index — sampling at
        /// <c>knotIndex / knotCount</c> lands somewhere unrelated to that knot. Everything that
        /// needs to know where a section begins goes through this table instead.
        /// </summary>
        static float[] KnotArcFractions(Spline spline)
        {
            var fractions = new float[spline.Count + 1];
            for (var i = 0; i < spline.Count; i++)
            {
                fractions[i + 1] = fractions[i] + spline.GetCurveLength(i);
            }

            var total = fractions[spline.Count];
            if (total < 1e-4f)
            {
                return fractions;
            }

            for (var i = 0; i <= spline.Count; i++)
            {
                fractions[i] /= total;
            }

            return fractions;
        }

        static GeneratedTrack Finish(
            int seed,
            TrackParams trackParams,
            CircuitLayout layout,
            Spline spline,
            int[] sectionStart,
            float[] knotFractions,
            int attempts,
            bool valid,
            string note)
        {
            var model = new TrackModel(spline, trackParams);
            model.AssignSections(BuildSections(model, layout, sectionStart, knotFractions));

            return new GeneratedTrack
            {
                Seed = seed,
                Params = trackParams,
                Layout = layout,
                Spline = spline,
                Model = model,
                Attempts = attempts,
                FullyValid = valid,
                ValidationNote = note,
            };
        }

        // ------------------------------------------------------------------
        // Layout → centreline
        // ------------------------------------------------------------------

        static void BuildCentreline(
            CircuitLayout layout,
            TrackParams trackParams,
            ref DeterministicRandom random,
            List<float3> points,
            int[] sectionStart)
        {
            var count = layout.Count;
            var radius = trackParams.BaseRadius;

            var anchors = new Vector3[count];
            var angle = 0f;

            for (var i = 0; i < count; i++)
            {
                // The anchors remain ordered by angle, so the loop still closes by construction.
                // A low-frequency radial profile makes the overall footprint read as a circuit
                // rather than a lightly dented circle; small per-anchor noise keeps two tracks
                // with the same broad profile from feeling too geometric.
                var primary = Mathf.Sin(angle * trackParams.ShapeLobes + trackParams.ShapePhase)
                              * trackParams.ShapeAmplitude;
                var secondary = Mathf.Sin(angle * (trackParams.ShapeLobes + 1) + trackParams.ShapeSecondaryPhase)
                                * trackParams.ShapeAmplitude * 0.35f;
                var jitter = random.NextSigned() * trackParams.RadialPerturbation;
                var anchorRadius = radius * (1f + primary + secondary + jitter);
                anchors[i] = new Vector3(Mathf.Cos(angle) * anchorRadius, 0f, Mathf.Sin(angle) * anchorRadius);
                angle += layout.Spans[i];
            }

            for (var i = 0; i < count; i++)
            {
                sectionStart[i] = points.Count;
                EmitSection(
                    layout.Types[i],
                    anchors[i],
                    anchors[(i + 1) % count],
                    trackParams,
                    ref random,
                    points);
            }
        }

        /// <summary>
        /// Appends one section's centreline, from its own anchor up to but not including the next
        /// one — the next section contributes that point.
        /// </summary>
        static void EmitSection(
            TrackSectionType type,
            Vector3 from,
            Vector3 to,
            TrackParams trackParams,
            ref DeterministicRandom random,
            List<float3> points)
        {
            var chord = to - from;
            var length = chord.magnitude;
            if (length < 1e-3f)
            {
                points.Add(from);
                return;
            }

            var forward = chord / length;
            var lateral = Vector3.Cross(Vector3.up, forward);
            var middle = (from + to) * 0.5f;

            // Away from the circuit's centre, which sits at the origin.
            var outward = middle.sqrMagnitude > 1e-4f ? middle.normalized : lateral;

            switch (type)
            {
                case TrackSectionType.Straight:
                case TrackSectionType.ObstacleStraight:
                    // Obstacles are decoration placed afterward over a plain straight — the
                    // centreline itself does not change.
                    EmitLine(points, from, to);
                    break;

                case TrackSectionType.Corner:
                {
                    // Bulging outward opens the corner into a sweeper; inward tightens it.
                    var bulge = length * random.Range(-0.05f, 0.16f);
                    var steps = Mathf.Max(2, Mathf.RoundToInt(length / k_SampleSpacing));

                    for (var i = 0; i < steps; i++)
                    {
                        var t = i / (float)steps;
                        points.Add(from + forward * (length * t) + outward * (Mathf.Sin(Mathf.PI * t) * bulge));
                    }

                    break;
                }

                case TrackSectionType.RampCorner:
                {
                    // Same shape as Corner, but the bulge is inward-only and much larger, so the
                    // road takes a decidedly long way round. That is what gives the ramp off the
                    // inside kerb something to be a shortcut to.
                    var bulge = -length * trackParams.RampCornerThrow;
                    var steps = Mathf.Max(2, Mathf.RoundToInt(length / k_SampleSpacing));

                    for (var i = 0; i < steps; i++)
                    {
                        var t = i / (float)steps;
                        points.Add(from + forward * (length * t) + outward * (Mathf.Sin(Mathf.PI * t) * bulge));
                    }

                    break;
                }

                case TrackSectionType.Hairpin:
                {
                    // The vertex is pulled in towards the middle of the circuit, so the two legs
                    // meet at a sharp angle and the rounding at the tip becomes the slowest corner
                    // on the lap.
                    //
                    // An earlier version spiked outwards instead, aiming for a true 180° turn. That
                    // cannot work on a closed ring: doubling back winds the heading a full turn and
                    // the rest of the lap has no way to unwind it.
                    var vertex = middle - outward * (length * trackParams.HairpinReach * 0.55f);
                    var radius = Mathf.Max(TrackParams.HairpinMinRadius * 1.5f, 16f);
                    EmitRoundedCorner(points, from, vertex, to, radius);
                    break;
                }

                case TrackSectionType.SharpHairpin:
                {
                    // `outward` points away from the circuit's centre, not necessarily
                    // perpendicular to *this section's own chord*. A pull with any component
                    // *along* forward drags the vertex off the chord's perpendicular bisector and
                    // produces an uncontrolled angle instead of the one asked for (verified in
                    // isolation: a perpendicular pull lands the interior angle within 2° of
                    // 2*atan(0.5/reach); a skewed one does not). Projecting that component out
                    // keeps the pull perpendicular, matching the angle formula.
                    //
                    // Several self-contained constructions were tried here to reach interior angles
                    // as tight as ~30° without leaning on SmoothClosed for the junctions (a cubic
                    // Hermite blend, a multi-arc turn-then-straight chain) — all of them, even where
                    // every individual piece used a safe, generous radius, still came out badly
                    // distorted after SmoothClosed + the spline's AutoSmooth: this generator's
                    // pipeline tolerates one curvature *transition* well (that is what it was built
                    // for) but not several close together, however gentle each one is on its own.
                    // EmitRoundedCorner keeps that count to the two this pipeline already handles
                    // for the plain Hairpin below; SharpHairpinReach is tuned to a range that stays
                    // reliably valid with it rather than to the tightest angle the vertex math alone
                    // would allow.
                    var outwardPerp = outward - forward * Vector3.Dot(outward, forward);
                    outwardPerp = outwardPerp.sqrMagnitude > 1e-6f ? outwardPerp.normalized : lateral;

                    var vertex = middle - outwardPerp * (length * trackParams.SharpHairpinReach);
                    var radius = Mathf.Max(TrackParams.SharpHairpinMinRadius * 1.3f, 9f);
                    EmitRoundedCorner(points, from, vertex, to, radius);
                    break;
                }

                case TrackSectionType.Chicane:
                {
                    var amplitude = AmplitudeForRadius(
                        1, length, trackParams.MinCurvatureRadius * trackParams.ChicaneThrow,
                        trackParams.BaseWidth * 1.8f);

                    EmitWave(points, from, forward, lateral, length, 1, amplitude,
                        random.NextFloat() < 0.5f ? 1f : -1f);
                    break;
                }

                case TrackSectionType.Esses:
                {
                    var amplitude = AmplitudeForRadius(
                        2, length, trackParams.MinCurvatureRadius * trackParams.EssesThrow,
                        trackParams.BaseWidth * 1.6f);

                    EmitWave(points, from, forward, lateral, length, 2, amplitude,
                        random.NextFloat() < 0.5f ? 1f : -1f);
                    break;
                }
            }
        }

        /// <summary>
        /// Weighted moving average around a closed loop. Straights and constant-radius arcs survive
        /// it — averaging collinear or evenly spaced curved points reproduces them — while a sharp
        /// junction is spread into a corner the car can actually take.
        /// </summary>
        static void SmoothClosed(List<float3> points, int passes)
        {
            var count = points.Count;
            if (count < 5 || passes <= 0)
            {
                return;
            }

            var buffer = new float3[count];

            for (var pass = 0; pass < passes; pass++)
            {
                for (var i = 0; i < count; i++)
                {
                    var previous = points[(i - 1 + count) % count];
                    var next = points[(i + 1) % count];
                    buffer[i] = previous * 0.25f + points[i] * 0.5f + next * 0.25f;
                }

                for (var i = 0; i < count; i++)
                {
                    points[i] = buffer[i];
                }
            }
        }

        /// <summary>Samples the segment from <paramref name="a"/> up to but excluding <paramref name="b"/>.</summary>
        static void EmitLine(List<float3> points, Vector3 a, Vector3 b, float spacing = k_SampleSpacing)
        {
            var length = Vector3.Distance(a, b);
            var steps = Mathf.Max(1, Mathf.RoundToInt(length / spacing));

            for (var i = 0; i < steps; i++)
            {
                points.Add(Vector3.Lerp(a, b, i / (float)steps));
            }
        }

        /// <summary>
        /// Two legs meeting at a corner of a known radius, with the tip rounded into a real
        /// circular arc. Setting the radius explicitly is the point: every other way of tightening
        /// a corner leaves the actual radius as something only the validator finds out about.
        /// Used for <see cref="TrackSectionType.Hairpin"/>; <see cref="TrackSectionType.SharpHairpin"/>
        /// has its own <see cref="EmitSharpHairpin"/> instead — see that method for why.
        /// </summary>
        static void EmitRoundedCorner(
            List<float3> points, Vector3 from, Vector3 vertex, Vector3 to, float radius,
            float sampleSpacing = k_SampleSpacing)
        {
            var toStart = from - vertex;
            var toEnd = to - vertex;

            var legIn = toStart.magnitude;
            var legOut = toEnd.magnitude;
            if (legIn < 1e-3f || legOut < 1e-3f)
            {
                EmitLine(points, from, to, sampleSpacing);
                return;
            }

            var dirIn = toStart / legIn;
            var dirOut = toEnd / legOut;

            var interior = Vector3.Angle(dirIn, dirOut) * Mathf.Deg2Rad;
            if (interior < 0.08f || interior > Mathf.PI - 0.08f)
            {
                EmitLine(points, from, to, sampleSpacing);
                return;
            }

            // How far back from the vertex the arc has to start for this radius.
            var tangent = Mathf.Min(radius / Mathf.Tan(interior * 0.5f), Mathf.Min(legIn, legOut) * 0.85f);
            var arcStart = vertex + dirIn * tangent;
            var arcEnd = vertex + dirOut * tangent;

            var bisector = (dirIn + dirOut).normalized;
            var centre = vertex + bisector * (tangent / Mathf.Cos(interior * 0.5f));

            EmitLine(points, from, arcStart, sampleSpacing);

            var startArm = arcStart - centre;
            var sweep = Vector3.SignedAngle(startArm, arcEnd - centre, Vector3.up);
            var arcLength = Mathf.Abs(sweep) * Mathf.Deg2Rad * startArm.magnitude;
            var steps = Mathf.Max(3, Mathf.RoundToInt(arcLength / Mathf.Min(sampleSpacing, radius * 0.35f)));

            for (var i = 0; i < steps; i++)
            {
                points.Add(centre + Quaternion.AngleAxis(sweep * (i / (float)steps), Vector3.up) * startArm);
            }

            EmitLine(points, arcEnd, to, sampleSpacing);
        }

        /// <summary>
        /// A lateral swing across the section: <c>sin(2πpt)·sin(πt)</c>.
        ///
        /// The <c>sin(πt)</c> envelope is what makes this joinable — it takes both the offset and
        /// its slope to zero at each end, so the section meets its neighbours running along the
        /// chord instead of arriving at an angle.
        /// </summary>
        static void EmitWave(
            List<float3> points,
            Vector3 from,
            Vector3 forward,
            Vector3 lateral,
            float length,
            int periods,
            float amplitude,
            float sign)
        {
            var steps = Mathf.Max(12, Mathf.RoundToInt(length / k_SampleSpacing));

            for (var i = 0; i < steps; i++)
            {
                var t = i / (float)steps;
                points.Add(from + forward * (length * t) + lateral * (sign * amplitude * Shape(periods, t)));
            }
        }

        static float Shape(int periods, float t)
        {
            return Mathf.Sin(2f * Mathf.PI * periods * t) * Mathf.Sin(Mathf.PI * t);
        }

        /// <summary>Largest |d²/dt²| of the swing shape, measured once rather than guessed.</summary>
        static float ShapePeak(int periods)
        {
            const int samples = 512;
            var step = 1f / samples;
            var peak = 0f;

            for (var i = 1; i < samples; i++)
            {
                var t = i * step;
                var second = (Shape(periods, t + step) - 2f * Shape(periods, t) + Shape(periods, t - step))
                             / (step * step);
                peak = Mathf.Max(peak, Mathf.Abs(second));
            }

            return peak;
        }

        /// <summary>
        /// How far a swing may reach without dropping below a target corner radius.
        ///
        /// With lateral offset A·shape(t) over a chord of L, curvature peaks at A·peak/L², so the
        /// amplitude a chicane can afford follows from how tight it is allowed to be. Picking the
        /// amplitude directly instead — as a multiple of road width — is what produced 2 m cusps:
        /// the same throw is gentle over 200 m and vicious over 60 m.
        /// </summary>
        static float AmplitudeForRadius(int periods, float length, float targetRadius, float maxAmplitude)
        {
            var peak = periods == 1 ? k_ChicaneShapePeak : k_EssesShapePeak;
            if (targetRadius < 1f || peak < 1e-3f)
            {
                return 0f;
            }

            return Mathf.Min(length * length / (peak * targetRadius), maxAmplitude);
        }

        static Spline CreateSpline(List<float3> points)
        {
            var spline = new Spline();
            foreach (var point in points)
            {
                spline.Add(point, TangentMode.AutoSmooth);
            }

            spline.Closed = true;
            return spline;
        }

        static Spline ScaleToTargetLength(Spline spline, List<float3> points, TrackParams trackParams)
        {
            var length = spline.GetLength();
            if (length < 1f)
            {
                return spline;
            }

            var scale = trackParams.TargetLength / length;
            for (var i = 0; i < points.Count; i++)
            {
                points[i] *= scale;
            }

            return CreateSpline(points);
        }

        // ------------------------------------------------------------------
        // Validation
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns 0 when the circuit is acceptable, otherwise a positive severity used to pick the
        /// least-bad candidate if every attempt fails.
        /// </summary>
        static float Validate(
            Spline spline,
            float[] knotFractions,
            int[] sectionStart,
            CircuitLayout layout,
            TrackParams trackParams,
            out string note)
        {
            note = "";

            var curvature = CurvatureViolation(
                spline, knotFractions, sectionStart, layout, trackParams, out var worstType, out var worstRadius);
            var proximity = ProximityViolation(spline, trackParams);

            if (curvature > 0f)
            {
                note = $"{worstType} radius {worstRadius:F1} m too tight";
            }
            else if (proximity > 0f)
            {
                note = "circuit passes too close to itself";
            }

            return curvature + proximity;
        }

        /// <summary>
        /// Radius limits are per section type. A hairpin is supposed to be tight — holding it to
        /// the same limit as a sweeper would just delete the feature.
        /// </summary>
        static float MinRadiusFor(TrackSectionType type, TrackParams trackParams)
        {
            switch (type)
            {
                case TrackSectionType.Hairpin:
                    return TrackParams.HairpinMinRadius;
                case TrackSectionType.SharpHairpin:
                    return TrackParams.SharpHairpinMinRadius;
                case TrackSectionType.Chicane:
                    return TrackParams.HairpinMinRadius * 1.25f;
                case TrackSectionType.RampCorner:
                    return TrackParams.HairpinMinRadius * 1.25f;
                case TrackSectionType.Esses:
                    return Mathf.Min(trackParams.MinCurvatureRadius, 22f);
                default:
                    return trackParams.MinCurvatureRadius;
            }
        }

        static float CurvatureViolation(
            Spline spline,
            float[] knotFractions,
            int[] sectionStart,
            CircuitLayout layout,
            TrackParams trackParams,
            out TrackSectionType worstType,
            out float worstRadius)
        {
            worstType = TrackSectionType.Corner;
            worstRadius = float.MaxValue;

            if (spline.Count == 0)
            {
                return float.MaxValue;
            }

            var length = spline.GetLength();
            var worst = 0f;

            var starts = new float[layout.Count];
            var limits = new float[layout.Count];
            for (var i = 0; i < layout.Count; i++)
            {
                starts[i] = knotFractions[sectionStart[i]];
                limits[i] = MinRadiusFor(layout.Types[i], trackParams);
            }

            // Curvature does not respect section boundaries: the last stretch of a straight is
            // already turning into the hairpin that follows it. Judging that metre by the straight's
            // limit would condemn every circuit that has a corner entry — so near a boundary the
            // looser of the neighbouring limits applies.
            var transition = k_TransitionMetres / Mathf.Max(1f, length);
            var samples = Mathf.Clamp(Mathf.CeilToInt(length), 64, 4096);

            for (var i = 0; i < samples; i++)
            {
                var t = i / (float)samples;

                var index = SectionIndexAt(starts, t);
                var limit = Mathf.Min(
                    limits[index],
                    Mathf.Min(
                        limits[SectionIndexAt(starts, Mathf.Repeat(t - transition, 1f))],
                        limits[SectionIndexAt(starts, Mathf.Repeat(t + transition, 1f))]));

                var curvatureValue = Mathf.Abs(spline.EvaluateCurvature(Mathf.Clamp(t, 0f, 0.99999f)));
                if (curvatureValue < 1e-5f)
                {
                    continue;
                }

                var radius = 1f / curvatureValue;
                if (radius >= limit)
                {
                    continue;
                }

                var severity = (limit - radius) / limit;
                if (severity > worst)
                {
                    worst = severity;
                    worstType = layout.Types[index];
                    worstRadius = radius;
                }
            }

            return worst * 50f;
        }

        static int SectionIndexAt(float[] starts, float fraction)
        {
            for (var i = starts.Length - 1; i >= 0; i--)
            {
                if (fraction >= starts[i])
                {
                    return i;
                }
            }

            return 0;
        }

        static float ProximityViolation(Spline spline, TrackParams trackParams)
        {
            var length = spline.GetLength();
            var count = Mathf.Clamp(Mathf.RoundToInt(length / k_ValidationSpacing), 32, 4096);
            var spacing = length / count;

            var points = new Vector3[count];
            for (var i = 0; i < count; i++)
            {
                points[i] = spline.EvaluatePosition(i / (float)count);
            }

            // Two stretches of circuit must clear each other by their full built width — road plus
            // gravel on both sides — or the run-off bands interleave and the lap reads as a braid
            // from above, with no way to tell which strand is the one you are on.
            var clearance = trackParams.MaxWidth + 2f * TrackMeshBuilder.RunoffWidth + 8f;
            var clearanceSquared = clearance * clearance;
            var skip = Mathf.CeilToInt(clearance * 2.2f / spacing);
            var worst = 0f;

            for (var i = 0; i < count; i++)
            {
                for (var j = i + skip; j < count; j++)
                {
                    if (count - (j - i) < skip)
                    {
                        continue;
                    }

                    var dx = points[i].x - points[j].x;
                    var dz = points[i].z - points[j].z;
                    var squared = dx * dx + dz * dz;

                    if (squared < clearanceSquared)
                    {
                        worst = Mathf.Max(worst, clearance - Mathf.Sqrt(squared));
                    }
                }
            }

            return worst;
        }

        // ------------------------------------------------------------------
        // Sections → arc length
        // ------------------------------------------------------------------

        static TrackSection[] BuildSections(
            TrackModel model,
            CircuitLayout layout,
            int[] sectionStart,
            float[] knotFractions)
        {
            var sections = new TrackSection[layout.Count];
            var starts = new float[layout.Count];

            // Scaled by the model's own measured length so section boundaries land in exactly the
            // same arc-length space the rest of the harness works in.
            for (var i = 0; i < layout.Count; i++)
            {
                starts[i] = knotFractions[sectionStart[i]] * model.TotalLength;
            }

            for (var i = 0; i < layout.Count; i++)
            {
                var end = i + 1 < layout.Count ? starts[i + 1] : model.TotalLength;
                sections[i] = new TrackSection(i, layout.Types[i], starts[i], end, layout.BrakingZones[i]);
            }

            return sections;
        }
    }
}
