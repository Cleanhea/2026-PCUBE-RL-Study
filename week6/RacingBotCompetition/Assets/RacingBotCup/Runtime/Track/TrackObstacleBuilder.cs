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

            var insideSign = InsideSign(model, section);
            var steps = Mathf.Max(1, Mathf.RoundToInt((end - start) / k_BarrierSpacing));

            for (var i = 0; i <= steps; i++)
            {
                var distance = start + (end - start) * (i / (float)steps);
                var sample = model.SampleAtDistance(distance);

                var offset = sample.Width * 0.5f + k_BarrierMargin;
                var position = sample.Position - insideSign * sample.Right * offset;
                var rotation = Quaternion.LookRotation(sample.Forward, Vector3.up);

                placements.Add(new ObstaclePlacement(position, rotation, ObstacleKind.Barrier, random.NextUInt()));
            }
        }

        /// <summary>
        /// Which side of the centreline is the inside of the bend, found geometrically rather than
        /// by trusting a curvature-sign convention: the midpoint of the chord between a section's
        /// start and end always falls on the concave (inside) side of a simple single-bend arc.
        /// </summary>
        static float InsideSign(TrackModel model, TrackSection section)
        {
            var startSample = model.SampleAtDistance(section.StartDistance);
            var endSample = model.SampleAtDistance(section.EndDistance);
            var midSample = model.SampleAtDistance((section.StartDistance + section.EndDistance) * 0.5f);

            var chordMid = (startSample.Position + endSample.Position) * 0.5f;
            var dot = Vector3.Dot(chordMid - midSample.Position, midSample.Right);

            return Mathf.Abs(dot) < 1e-4f ? 1f : Mathf.Sign(dot);
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
        /// One ramp, at the point of tightest curvature within a RampCorner — the apex of the
        /// section's own (already inward-pulled) racing line.
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
            var rotation = Quaternion.LookRotation(apex.Forward, Vector3.up);
            placements.Add(new ObstaclePlacement(apex.Position, rotation, ObstacleKind.Ramp, 0u));
        }
    }
}
