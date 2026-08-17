using System;
using System.Collections.Generic;
using UnityEngine;

namespace RacingBotCup.Track
{
    /// <summary>What a placed prop is, for tagging and for picking a prefab out of the catalogue.</summary>
    public enum ObstacleKind
    {
        Barrier,
        Crate,
        Log,
        Container,
        Ramp,
    }

    /// <summary>
    /// One prop's pose. <see cref="VariantSeed"/> is a raw random value rather than an index into a
    /// specific catalogue — it keeps placement a pure function of the track model and seed, with no
    /// dependency on how many prefab variants happen to be assigned.
    /// </summary>
    public readonly struct ObstaclePlacement
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly ObstacleKind Kind;
        public readonly uint VariantSeed;

        public ObstaclePlacement(Vector3 position, Quaternion rotation, ObstacleKind kind, uint variantSeed)
        {
            Position = position;
            Rotation = rotation;
            Kind = kind;
            VariantSeed = variantSeed;
        }
    }

    /// <summary>Prefab references for every prop kind a decorated section can place.</summary>
    [Serializable]
    public sealed class TrackPropCatalogue
    {
        public GameObject[] Barriers = Array.Empty<GameObject>();
        public GameObject[] Crates = Array.Empty<GameObject>();
        public GameObject[] Logs = Array.Empty<GameObject>();
        public GameObject[] Containers = Array.Empty<GameObject>();
        public GameObject Ramp;

        public GameObject Pick(ObstacleKind kind, uint variantSeed)
        {
            switch (kind)
            {
                case ObstacleKind.Barrier: return PickFrom(Barriers, variantSeed);
                case ObstacleKind.Crate: return PickFrom(Crates, variantSeed);
                case ObstacleKind.Log: return PickFrom(Logs, variantSeed);
                case ObstacleKind.Container: return PickFrom(Containers, variantSeed);
                case ObstacleKind.Ramp: return Ramp;
                default: return null;
            }
        }

        static GameObject PickFrom(GameObject[] variants, uint variantSeed)
        {
            return variants == null || variants.Length == 0
                ? null
                : variants[(int)(variantSeed % (uint)variants.Length)];
        }
    }

    /// <summary>
    /// Places the props that decorate <see cref="TrackSectionType.SharpHairpin"/>,
    /// <see cref="TrackSectionType.ObstacleStraight"/> and <see cref="TrackSectionType.RampCorner"/>
    /// sections, once the rest of the circuit is built.
    ///
    /// Split the same way <see cref="TrackMeshBuilder"/> separates geometry from materials:
    /// <see cref="ComputePlacements"/> is pure data derived only from the finished
    /// <see cref="TrackModel"/> and the seed, so it is unit-testable with no prefabs loaded.
    /// <see cref="Build"/> is the thin layer that turns that data into scene objects.
    /// </summary>
    public static class TrackObstacleBuilder
    {
        /// <summary>Distinct from every other stream derived off a track seed — layout decoration
        /// and centreline geometry each have their own — so retries during validation never shift
        /// where props land, and vice versa.</summary>
        const int k_ObstacleSalt = 913311;

        const float k_BarrierSpacing = 2.2f;
        const float k_BarrierMargin = 2.0f;

        const int k_MinObstacles = 3;
        const int k_MaxObstaclesExclusive = 6;
        const float k_ObstacleRoadMargin = 0.3f;

        /// <summary>Half-extent assumed for any dodge-obstacle footprint, regardless of which
        /// prefab variant actually lands there. Public so tests can verify passability without
        /// duplicating the number.</summary>
        public const float ObstacleFootprint = 1.25f;

        /// <summary>Minimum clear road width guaranteed on the side opposite every placed obstacle.</summary>
        public const float MinPassableGap = 3.2f;

        const float k_SectionEdgeMargin = 0.12f;
        const float k_RampScanStep = 2f;

        /// <summary>Half the ramp prop's width — SM_Prop_Ramp_Mesh_01 measures 5.8 m across.</summary>
        public const float RampHalfWidth = 2.95f;

        /// <summary>How far the ramp's take-off lip reaches ahead of its pivot.</summary>
        public const float RampNoseReach = 2.25f;

        /// <summary>How far the ramp's foot reaches back behind its pivot.</summary>
        public const float RampRearReach = 4.75f;

        /// <summary>
        /// Clear road left between the kerb and the ramp's near side. Held along the prop's whole
        /// length rather than only where it is measured — see <see cref="RampInsideClearance"/>.
        /// </summary>
        public const float RampKerbGap = 1.5f;

        public static List<ObstaclePlacement> ComputePlacements(TrackModel model, int seed)
        {
            var placements = new List<ObstaclePlacement>();
            var random = new DeterministicRandom(seed).Derive(k_ObstacleSalt);

            foreach (var section in model.Sections)
            {
                switch (section.Type)
                {
                    case TrackSectionType.SharpHairpin:
                        PlaceBarrierFence(model, section, ref random, placements);
                        break;
                    case TrackSectionType.ObstacleStraight:
                        PlaceDodgeObstacles(model, section, ref random, placements);
                        break;
                    case TrackSectionType.RampCorner:
                        PlaceRamp(model, section, placements);
                        break;
                }
            }

            return placements;
        }

        public static GameObject Build(TrackModel model, TrackPropCatalogue props, int seed)
        {
            var root = new GameObject("Obstacles");

            if (props == null)
            {
                return root;
            }

            foreach (var placement in ComputePlacements(model, seed))
            {
                var prefab = props.Pick(placement.Kind, placement.VariantSeed);
                if (prefab == null)
                {
                    continue;
                }

                var instance = UnityEngine.Object.Instantiate(prefab, placement.Position, placement.Rotation, root.transform);
                instance.tag = placement.Kind == ObstacleKind.Ramp ? TrackTags.Ramp : TrackTags.Obstacle;
            }

            return root;
        }

        /// <summary>
        /// A chain of barrier props hugging the inside kerb through the tight middle of a
        /// SharpHairpin, fencing off the infield so the apex can't be cut. Placed outside the
        /// drivable width (inside the gravel run-off), so the actual racing line never touches
        /// them.
        /// </summary>
        static void PlaceBarrierFence(
            TrackModel model,
            TrackSection section,
            ref DeterministicRandom random,
            List<ObstaclePlacement> placements)
        {
            var length = section.Length;
            var start = section.StartDistance + length * k_SectionEdgeMargin;
            var end = section.EndDistance - length * k_SectionEdgeMargin;
            if (end <= start)
            {
                return;
            }

            var steps = Mathf.Max(1, Mathf.RoundToInt((end - start) / k_BarrierSpacing));

            for (var i = 0; i <= steps; i++)
            {
                var distance = start + (end - start) * (i / (float)steps);
                var sample = model.SampleAtDistance(distance);

                // Local, not once for the whole section: a hairpin's own curve can wobble (an
                // over-tight vertex pull briefly bending the other way before recovering) enough
                // that a single section-wide inside direction stops matching the actual concave
                // side partway through, and the fence visibly cuts across the road right where that
                // happens instead of hugging either kerb.
                var insideSign = InsideSignAt(model, distance);
                var offset = sample.Width * 0.5f + k_BarrierMargin;
                var position = sample.Position - insideSign * sample.Right * offset;

                // SM_Prop_Barrier_Concrete's long axis is local X, not the Z that LookRotation
                // aligns to Forward (confirmed against the mesh's own bounds) — without this the
                // barrier's long side lies across the road instead of along the kerb it's meant to
                // fence.
                var rotation = Quaternion.LookRotation(sample.Forward, Vector3.up) * Quaternion.Euler(0f, -90f, 0f);

                placements.Add(new ObstaclePlacement(position, rotation, ObstacleKind.Barrier, random.NextUInt()));
            }
        }

        /// <summary>Local window used to judge which way the road is curving at a single point.</summary>
        const float k_InsideSignWindow = 6f;

        /// <summary>
        /// Which sign, in <c>Position - insideSign*Right*offset</c>, moves toward the inside of the
        /// bend at <paramref name="distance"/> — found geometrically rather than by trusting a
        /// curvature-sign convention: the midpoint of the chord between a point a little behind and
        /// a little ahead always falls on the concave (inside) side of the local arc, so the
        /// direction from the road at <paramref name="distance"/> toward that chord midpoint IS the
        /// inside direction. That direction is <c>+Right*Sign(dot)</c> (not <c>-Right*Sign(dot)</c>
        /// — verified by hand against a worked quarter-circle example, left and right turns both),
        /// which is why this returns the sign negated: the call site subtracts, so the sign returned
        /// has to be the one that makes subtracting land inside.
        /// </summary>
        static float InsideSignAt(TrackModel model, float distance)
        {
            var behind = model.SampleAtDistance(distance - k_InsideSignWindow);
            var ahead = model.SampleAtDistance(distance + k_InsideSignWindow);
            var here = model.SampleAtDistance(distance);

            var chordMid = (behind.Position + ahead.Position) * 0.5f;
            var dot = Vector3.Dot(chordMid - here.Position, here.Right);

            return Mathf.Abs(dot) < 1e-4f ? -1f : -Mathf.Sign(dot);
        }

        /// <summary>
        /// Scatters 3-5 crates/logs/containers across the middle of an ObstacleStraight, each
        /// offset far enough from centre to force a real lane change but never so far that less
        /// than <see cref="MinPassableGap"/> of clear road remains on the opposite side.
        /// </summary>
        static void PlaceDodgeObstacles(
            TrackModel model,
            TrackSection section,
            ref DeterministicRandom random,
            List<ObstaclePlacement> placements)
        {
            var length = section.Length;
            var usableStart = section.StartDistance + length * k_SectionEdgeMargin;
            var usableEnd = section.EndDistance - length * k_SectionEdgeMargin;
            var usableLength = usableEnd - usableStart;
            if (usableLength <= 0f)
            {
                return;
            }

            var count = random.Range(k_MinObstacles, k_MaxObstaclesExclusive);
            var step = usableLength / count;
            var sign = random.NextFloat() < 0.5f ? 1f : -1f;
            var kinds = new[] { ObstacleKind.Crate, ObstacleKind.Log, ObstacleKind.Container };

            for (var i = 0; i < count; i++)
            {
                var jitter = random.Range(-step * 0.3f, step * 0.3f);
                var distance = usableStart + step * (i + 0.5f) + jitter;
                var sample = model.SampleAtDistance(distance);

                // Gap on the clear (opposite) side at offset o is (o - footprint + halfWidth) — it
                // grows with o, so the smallest offset we'd ever use is the binding case. Skipping
                // there when that's already too tight guarantees every offset actually picked leaves
                // at least MinPassableGap clear, without needing to re-check after the roll.
                var halfWidth = sample.Width * 0.5f;
                var maxOffset = halfWidth - ObstacleFootprint - k_ObstacleRoadMargin;
                var minOffset = Mathf.Min(maxOffset, Mathf.Max(0.5f, halfWidth * 0.15f));
                var worstCaseGap = minOffset - ObstacleFootprint + halfWidth;

                // Too narrow here to leave a guaranteed-passable gap — skip this anchor rather than
                // risk blocking the road.
                if (maxOffset < minOffset || worstCaseGap < MinPassableGap)
                {
                    continue;
                }

                var laneSign = i % 2 == 0 ? sign : -sign;
                var offset = random.Range(minOffset, maxOffset);
                var position = sample.Position + sample.Right * (laneSign * offset);
                var rotation = Quaternion.LookRotation(sample.Forward, Vector3.up)
                               * Quaternion.Euler(0f, random.Range(-25f, 25f), 0f);

                var kind = kinds[random.Range(0, kinds.Length)];
                placements.Add(new ObstaclePlacement(position, rotation, kind, random.NextUInt()));
            }
        }

        /// <summary>
        /// One ramp, just off the inside kerb at the point of tightest curvature within a
        /// RampCorner — off the road, in the infield the bend encloses, so taking it is a shortcut
        /// rather than the only line through the section.
        /// </summary>
        static void PlaceRamp(TrackModel model, TrackSection section, List<ObstaclePlacement> placements)
        {
            var length = section.Length;
            if (length <= 0f)
            {
                return;
            }

            var steps = Mathf.Max(2, Mathf.RoundToInt(length / k_RampScanStep));
            var bestDistance = section.StartDistance;
            var bestCurvature = -1f;

            for (var i = 0; i <= steps; i++)
            {
                var distance = section.StartDistance + length * (i / (float)steps);
                var curvature = Mathf.Abs(model.SampleAtDistance(distance).Curvature);
                if (curvature > bestCurvature)
                {
                    bestCurvature = curvature;
                    bestDistance = distance;
                }
            }

            var apex = model.SampleAtDistance(bestDistance);

            // Same construction as the hairpin fence, and the inside is measured locally for the
            // same reason: a section's own curve can wobble enough that one direction taken for the
            // whole section stops matching the concave side at the apex itself.
            var insideSign = InsideSignAt(model, bestDistance);
            var clearance = RampInsideClearance(apex.Curvature, apex.Width);
            var position = apex.Position - insideSign * apex.Right * (apex.Width * 0.5f + clearance);

            // Aiming the launch along the apex tangent is what makes this a shortcut instead of a
            // jump into the scenery: a line parallel to the tangent and offset toward the inside is
            // a chord of the corner, so a car that gets airborne here comes back down on the road
            // further round rather than deeper into the infield.
            //
            // The flip is the mesh's, not the maths': SM_Prop_Ramp_Mesh_01 rises toward its local
            // -Z (its lip sits at the mesh's minimum Z, its foot at the maximum — measured off the
            // mesh's own vertices), which is the opposite end to the +Z that LookRotation aligns to
            // Forward. Without it the ramp faces back down the road and the car meets the drop-off
            // face head on.
            var rotation = Quaternion.LookRotation(apex.Forward, Vector3.up) * Quaternion.Euler(0f, 180f, 0f);

            placements.Add(new ObstaclePlacement(position, rotation, ObstacleKind.Ramp, 0u));
        }

        /// <summary>
        /// How far past the road edge, on the inside of the bend, a ramp is centred at an apex of
        /// the given centreline curvature and width.
        ///
        /// A flat number does not work here. The prop is a rigid 5.8 x 7 m box laid along the
        /// tangent at one point, while the kerb it has to clear keeps curving in behind it: over
        /// the prop's own length the inside edge closes by roughly <c>reach^2 / 2r</c>, which on the
        /// tightest corners this generator produces is more than a metre — so a gap measured at the
        /// pivot alone leaves the ramp's foot standing in the road, which is exactly where a car
        /// arrives. The curvature term is that closing distance, which is what holds the gap
        /// constant along the prop instead of only where it happens to be measured.
        ///
        /// Measured against the inside kerb's radius rather than the centreline's, because that is
        /// the edge being cleared, and on a tight corner it is appreciably tighter than the
        /// centreline the offset is built from.
        /// </summary>
        public static float RampInsideClearance(float curvature, float width)
        {
            var centreRadius = Mathf.Abs(curvature) > 1e-4f ? 1f / Mathf.Abs(curvature) : float.MaxValue;
            var kerbRadius = Mathf.Max(RampRearReach, centreRadius - width * 0.5f);

            return RampKerbGap + RampHalfWidth + RampRearReach * RampRearReach / (2f * kerbRadius);
        }
    }
}
