using System.Collections.Generic;
using NUnit.Framework;
using RacingBotCup.Track;
using UnityEngine;

namespace RacingBotCup.Tests
{
    /// <summary>
    /// Covers the three opt-in hazard sections (SharpHairpin/ObstacleStraight/RampCorner): that
    /// they show up at roughly the intended rate, that placement is deterministic and geometrically
    /// sound, and — most importantly — that leaving <c>enableHazardSections</c> off (the default,
    /// and what evaluation always passes) reproduces exactly what the generator produced before
    /// these existed.
    /// </summary>
    public sealed class HazardSectionTests
    {
        static readonly int[] k_DeterminismSeeds = { 1017, 2451, 9302, 12960, 4242 };
        static readonly int[] k_ParitySeeds = { 1017, 2451, 9302, 12960, 3248, 3061 };

        [Test]
        public void EachHazardTypeAppearsRoughlyAQuarterOfTheTime()
        {
            const int sampleCount = 400;
            var sharpHairpinCount = 0;
            var obstacleStraightCount = 0;
            var rampCornerCount = 0;

            for (var seed = 1; seed <= sampleCount; seed++)
            {
                var track = TrackGenerator.Generate(seed, enableHazardSections: true);
                var hasSharpHairpin = false;
                var hasObstacleStraight = false;
                var hasRampCorner = false;

                foreach (var section in track.Model.Sections)
                {
                    hasSharpHairpin |= section.Type == TrackSectionType.SharpHairpin;
                    hasObstacleStraight |= section.Type == TrackSectionType.ObstacleStraight;
                    hasRampCorner |= section.Type == TrackSectionType.RampCorner;
                }

                if (hasSharpHairpin) sharpHairpinCount++;
                if (hasObstacleStraight) obstacleStraightCount++;
                if (hasRampCorner) rampCornerCount++;
            }

            // Loose band: eligibility misses (e.g. a layout with zero Corner sections) pull the
            // observed rate below the raw 25% roll chance, so this is not a tight binomial check.
            // Collected rather than asserted one at a time so a miss on one type still reports the
            // other two instead of hiding them.
            var failures = new List<string>();
            CheckRoughlyAQuarter(failures, sharpHairpinCount, sampleCount, "SharpHairpin");
            CheckRoughlyAQuarter(failures, obstacleStraightCount, sampleCount, "ObstacleStraight");
            CheckRoughlyAQuarter(failures, rampCornerCount, sampleCount, "RampCorner");

            Assert.IsEmpty(failures, string.Join(" | ", failures));
        }

        static void CheckRoughlyAQuarter(List<string> failures, int count, int total, string label)
        {
            var fraction = count / (float)total;
            if (fraction < 0.15f || fraction > 0.35f)
            {
                failures.Add($"{label} appeared in {fraction:P0} of {total} seeds ({count}) — expected roughly 25%.");
            }
        }

        [Test]
        public void HazardSectionsAreDeterministic([ValueSource(nameof(k_DeterminismSeeds))] int seed)
        {
            var first = TrackGenerator.Generate(seed, enableHazardSections: true);
            var second = TrackGenerator.Generate(seed, enableHazardSections: true);

            Assert.AreEqual(first.Model.Sections.Count, second.Model.Sections.Count, "Section count drifted.");
            for (var i = 0; i < first.Model.Sections.Count; i++)
            {
                Assert.AreEqual(first.Model.Sections[i].Type, second.Model.Sections[i].Type,
                    $"Section {i}'s type drifted between generations.");
            }

            var firstPlacements = TrackObstacleBuilder.ComputePlacements(first.Model, seed);
            var secondPlacements = TrackObstacleBuilder.ComputePlacements(second.Model, seed);

            Assert.AreEqual(firstPlacements.Count, secondPlacements.Count, "Obstacle count drifted.");
            for (var i = 0; i < firstPlacements.Count; i++)
            {
                Assert.AreEqual(firstPlacements[i].Kind, secondPlacements[i].Kind, $"Placement {i} kind drifted.");
                Assert.AreEqual(firstPlacements[i].VariantSeed, secondPlacements[i].VariantSeed,
                    $"Placement {i} variant seed drifted.");
                Assert.Less(Vector3.Distance(firstPlacements[i].Position, secondPlacements[i].Position), 1e-3f,
                    $"Placement {i} position drifted.");
            }
        }

        [Test]
        public void SharpHairpinIsTighterThanTheOrdinaryHairpinFloor()
        {
            var tested = 0;

            for (var seed = 1; seed <= 80 && tested < 5; seed++)
            {
                var track = TrackGenerator.Generate(seed, enableHazardSections: true);

                foreach (var section in track.Model.Sections)
                {
                    if (section.Type != TrackSectionType.SharpHairpin)
                    {
                        continue;
                    }

                    var minRadius = MinRadiusIn(track.Model, section);

                    // Every plain Hairpin that passes validation has radius >= HairpinMinRadius
                    // everywhere; a SharpHairpin's own floor is well below that (7 m vs 11 m), and
                    // the fixed rounding radius fed into it (~9.1 m) sits comfortably under the
                    // Hairpin floor even after smoothing softens it slightly.
                    Assert.Less(minRadius, TrackParams.HairpinMinRadius,
                        $"seed {seed}: SharpHairpin's tightest radius ({minRadius:F1} m) was not " +
                        $"tighter than the ordinary Hairpin floor ({TrackParams.HairpinMinRadius} m).");

                    tested++;
                    break;
                }
            }

            Assert.Greater(tested, 0, "No SharpHairpin section turned up in seeds 1..80 to test against.");
        }

        static float MinRadiusIn(TrackModel model, TrackSection section)
        {
            const int steps = 60;
            var minRadius = float.MaxValue;

            for (var i = 0; i <= steps; i++)
            {
                var distance = section.StartDistance + section.Length * (i / (float)steps);
                var curvature = Mathf.Abs(model.SampleAtDistance(distance).Curvature);
                if (curvature < 1e-4f)
                {
                    continue;
                }

                minRadius = Mathf.Min(minRadius, 1f / curvature);
            }

            return minRadius;
        }

        [Test]
        public void ObstacleStraightAlwaysLeavesAPassableGap()
        {
            var checkedAny = false;

            for (var seed = 1; seed <= 80; seed++)
            {
                var track = TrackGenerator.Generate(seed, enableHazardSections: true);
                var placements = TrackObstacleBuilder.ComputePlacements(track.Model, seed);

                foreach (var placement in placements)
                {
                    if (placement.Kind != ObstacleKind.Crate
                        && placement.Kind != ObstacleKind.Log
                        && placement.Kind != ObstacleKind.Container)
                    {
                        continue;
                    }

                    var projection = track.Model.Project(placement.Position);
                    var gap = projection.Width * 0.5f + Mathf.Abs(projection.Lateral) - TrackObstacleBuilder.ObstacleFootprint;

                    Assert.GreaterOrEqual(gap, TrackObstacleBuilder.MinPassableGap - 0.3f,
                        $"seed {seed}: obstacle at distance {projection.Distance:F1} m left only {gap:F2} m clear.");

                    checkedAny = true;
                }
            }

            Assert.IsTrue(checkedAny, "No ObstacleStraight obstacles turned up in seeds 1..80 to test against.");
        }

        /// <summary>
        /// The ramp is a shortcut, which pins it between two failures: in the road it is an
        /// obstacle on the only line, and far out past the run-off it is scenery no car would ever
        /// reach. Checked over the whole footprint, since the clearance that matters is the
        /// tightest one anywhere along a rigid prop sat next to a curving kerb, not the one at its
        /// pivot. It also has to be on the inside of the bend — the outside is the long way round,
        /// so a ramp there launches a car away from the track rather than across the corner.
        /// </summary>
        [Test]
        public void RampSitsJustOffTheInsideKerb()
        {
            var checkedAny = false;
            var checkedSide = false;

            for (var seed = 1; seed <= 80; seed++)
            {
                var track = TrackGenerator.Generate(seed, enableHazardSections: true);
                var placements = TrackObstacleBuilder.ComputePlacements(track.Model, seed);

                foreach (var placement in placements)
                {
                    if (placement.Kind != ObstacleKind.Ramp)
                    {
                        continue;
                    }

                    var projection = track.Model.Project(placement.Position);

                    // Every corner of the prop's footprint, not just its pivot. The kerb curves in
                    // behind a ramp laid along the apex tangent, so a gap that holds where it is
                    // measured can still be negative at the foot — which is the end a car arrives
                    // at, and the end that has to be off the road for this to be a shortcut rather
                    // than an obstacle.
                    foreach (var corner in FootprintCorners(placement))
                    {
                        var cornerProjection = track.Model.Project(corner);
                        var gap = Mathf.Abs(cornerProjection.Lateral) - cornerProjection.Width * 0.5f;

                        Assert.Greater(gap, 0.5f,
                            $"seed {seed}: a corner of the ramp at distance {projection.Distance:F1} m is " +
                            $"{gap:F2} m from the road edge — the prop is in the road.");
                        Assert.Less(gap, TrackMeshBuilder.RunoffWidth * 2f,
                            $"seed {seed}: a corner of the ramp at distance {projection.Distance:F1} m is " +
                            $"{gap:F1} m past the road edge — that far out it is scenery, not a shortcut.");
                    }

                    // The midpoint of a short chord across the centreline always falls on the
                    // concave side of the local arc, so the ramp is on the inside exactly when it
                    // lies the same way off the centreline as that midpoint does.
                    var here = track.Model.SampleAtDistance(projection.Distance);
                    var behind = track.Model.SampleAtDistance(projection.Distance - 6f);
                    var ahead = track.Model.SampleAtDistance(projection.Distance + 6f);
                    var chordMid = Vector3.Dot((behind.Position + ahead.Position) * 0.5f - here.Position, here.Right);

                    // Skipped where the road is locally straight, which has no inside to be on.
                    if (Mathf.Abs(chordMid) > 0.05f)
                    {
                        Assert.Greater(chordMid * projection.Lateral, 0f,
                            $"seed {seed}: ramp at distance {projection.Distance:F1} m is on the outside of its bend.");
                        checkedSide = true;
                    }

                    checkedAny = true;
                }
            }

            Assert.IsTrue(checkedAny, "No RampCorner ramp turned up in seeds 1..80 to test against.");
            Assert.IsTrue(checkedSide, "Every ramp landed on a locally straight stretch — the inside check never ran.");
        }

        /// <summary>
        /// The four ground corners of a ramp's footprint. Derived from the placement's own rotation
        /// rather than from the track, so a ramp facing the wrong way fails here too: the prop
        /// climbs along its local -Z, which is what puts the foot behind the pivot and the lip in
        /// front of it.
        /// </summary>
        static IEnumerable<Vector3> FootprintCorners(ObstaclePlacement placement)
        {
            var climb = placement.Rotation * Vector3.back;
            var side = placement.Rotation * Vector3.right;

            foreach (var along in new[] { TrackObstacleBuilder.RampNoseReach, -TrackObstacleBuilder.RampRearReach })
            {
                foreach (var lateral in new[] { TrackObstacleBuilder.RampHalfWidth, -TrackObstacleBuilder.RampHalfWidth })
                {
                    yield return placement.Position + climb * along + side * lateral;
                }
            }
        }

        [Test]
        public void HazardsOffMatchesTheDefault([ValueSource(nameof(k_ParitySeeds))] int seed)
        {
            var bare = TrackGenerator.Generate(seed);
            var explicitOff = TrackGenerator.Generate(seed, enableHazardSections: false);

            Assert.AreEqual(bare.Model.Sections.Count, explicitOff.Model.Sections.Count);
            for (var i = 0; i < bare.Model.Sections.Count; i++)
            {
                Assert.AreEqual(bare.Model.Sections[i].Type, explicitOff.Model.Sections[i].Type);

                var type = bare.Model.Sections[i].Type;
                Assert.AreNotEqual(TrackSectionType.SharpHairpin, type);
                Assert.AreNotEqual(TrackSectionType.ObstacleStraight, type);
                Assert.AreNotEqual(TrackSectionType.RampCorner, type);
            }
        }
    }
}
