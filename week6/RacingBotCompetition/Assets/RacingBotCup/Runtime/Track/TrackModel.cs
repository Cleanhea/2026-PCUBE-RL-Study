using System.Collections.Generic;
using RacingBotCup.Vehicle;
using UnityEngine;
using UnityEngine.Splines;

namespace RacingBotCup.Track
{
    /// <summary>
    /// Queryable form of a generated track: the centreline resampled at uniform arc length,
    /// plus a uniform grid for O(1) nearest-point lookup.
    ///
    /// Everything the observation catalogue and the baseline bot need (signed distance to the
    /// centreline, road width, curvature ahead, lap progress) is answered from this table rather
    /// than from the spline itself — spline projection is iterative and far too slow to call for
    /// four wheels every physics tick.
    /// </summary>
    public sealed class TrackModel : ISurfaceProvider
    {
        public struct Sample
        {
            public Vector3 Position;
            public Vector3 Forward;
            public Vector3 Right;
            public float Distance;
            public float Width;
            public float Curvature;
        }

        public readonly struct Projection
        {
            /// <summary>Arc length along the centreline, in metres from the start line.</summary>
            public readonly float Distance;

            /// <summary>Signed lateral offset in metres. Positive is right of the racing direction.</summary>
            public readonly float Lateral;

            public readonly float Width;
            public readonly Vector3 Position;
            public readonly Vector3 Forward;
            public readonly float Curvature;

            public Projection(float distance, float lateral, float width, Vector3 position, Vector3 forward, float curvature)
            {
                Distance = distance;
                Lateral = lateral;
                Width = width;
                Position = position;
                Forward = forward;
                Curvature = curvature;
            }

            public bool IsOnRoad => Mathf.Abs(Lateral) <= Width * 0.5f;

            /// <summary>
            /// True once past the gravel run-off band, out on the grass. The run-off strip itself
            /// (<see cref="IsOnRoad"/> false but this false too) is the forgiving buffer; this is
            /// "actually off the circuit".
            /// </summary>
            public bool IsBeyondRunoff => Mathf.Abs(Lateral) > Width * 0.5f + TrackMeshBuilder.RunoffWidth;
        }

        const float k_SampleSpacing = 0.5f;

        /// <summary>Half-width of the stencil used to measure heading and curvature, in metres.</summary>
        const float k_DirectionStencil = 2.5f;
        const float k_CellSize = 8f;
        const int k_DenseSamplesPerMetre = 3;

        readonly Sample[] m_Samples;
        readonly Dictionary<long, List<int>> m_Grid = new Dictionary<long, List<int>>();

        TrackSection[] m_Sections = System.Array.Empty<TrackSection>();

        public float TotalLength { get; }

        public TrackParams Params { get; }

        public IReadOnlyList<Sample> Samples => m_Samples;

        /// <summary>The typed sections making up this circuit, in lap order.</summary>
        public IReadOnlyList<TrackSection> Sections => m_Sections;

        public TrackModel(Spline spline, TrackParams trackParams)
        {
            Params = trackParams;

            var dense = SampleDense(spline, out var cumulative);
            TotalLength = cumulative[cumulative.Length - 1];

            var count = Mathf.Max(8, Mathf.RoundToInt(TotalLength / k_SampleSpacing));
            m_Samples = new Sample[count];

            var spacing = TotalLength / count;
            for (var i = 0; i < count; i++)
            {
                var distance = i * spacing;
                m_Samples[i].Position = PointAtArcLength(dense, cumulative, distance);
                m_Samples[i].Distance = distance;
                m_Samples[i].Width = trackParams.WidthAt(distance / TotalLength);
            }

            // Direction and curvature are measured across a couple of metres rather than between
            // neighbouring samples. Spline evaluation carries sub-metre jitter, and a half-metre
            // stencil turns that jitter into a heading that flips back and forth — which the mesh
            // builder then renders as run-off bands weaving across the racing line.
            var stencil = Mathf.Max(1, Mathf.RoundToInt(k_DirectionStencil / spacing));

            for (var i = 0; i < count; i++)
            {
                var next = m_Samples[(i + stencil) % count].Position;
                var previous = m_Samples[(i - stencil + count) % count].Position;
                var forward = next - previous;
                forward.y = 0f;
                m_Samples[i].Forward = forward.sqrMagnitude > 1e-8f ? forward.normalized : Vector3.forward;
                m_Samples[i].Right = Vector3.Cross(Vector3.up, m_Samples[i].Forward).normalized;
            }

            for (var i = 0; i < count; i++)
            {
                var forwardNext = m_Samples[(i + stencil) % count].Forward;
                var forwardPrevious = m_Samples[(i - stencil + count) % count].Forward;
                var turn = Vector3.SignedAngle(forwardPrevious, forwardNext, Vector3.up) * Mathf.Deg2Rad;
                m_Samples[i].Curvature = turn / (2f * stencil * spacing);
            }

            BuildGrid();
        }

        static Vector3[] SampleDense(Spline spline, out float[] cumulative)
        {
            // A generous fixed density: the resampling pass below only needs the polyline to be
            // dense enough that chord length approximates arc length.
            var length = spline.GetLength();
            var count = Mathf.Clamp(Mathf.RoundToInt(length * k_DenseSamplesPerMetre), 256, 32768);

            var points = new Vector3[count + 1];
            cumulative = new float[count + 1];

            for (var i = 0; i <= count; i++)
            {
                var t = i / (float)count;
                points[i] = spline.EvaluatePosition(t);
                cumulative[i] = i == 0 ? 0f : cumulative[i - 1] + Vector3.Distance(points[i - 1], points[i]);
            }

            return points;
        }

        static Vector3 PointAtArcLength(Vector3[] points, float[] cumulative, float distance)
        {
            var total = cumulative[cumulative.Length - 1];
            distance = Mathf.Repeat(distance, total);

            var low = 0;
            var high = cumulative.Length - 1;
            while (low < high - 1)
            {
                var mid = (low + high) / 2;
                if (cumulative[mid] <= distance)
                {
                    low = mid;
                }
                else
                {
                    high = mid;
                }
            }

            var span = cumulative[high] - cumulative[low];
            var t = span > 1e-6f ? (distance - cumulative[low]) / span : 0f;
            return Vector3.Lerp(points[low], points[high], t);
        }

        void BuildGrid()
        {
            for (var i = 0; i < m_Samples.Length; i++)
            {
                var key = CellKey(m_Samples[i].Position);
                if (!m_Grid.TryGetValue(key, out var bucket))
                {
                    bucket = new List<int>();
                    m_Grid[key] = bucket;
                }

                bucket.Add(i);
            }
        }

        static long CellKey(Vector3 position)
        {
            return CellKey(Mathf.FloorToInt(position.x / k_CellSize), Mathf.FloorToInt(position.z / k_CellSize));
        }

        static long CellKey(int x, int z)
        {
            return ((long)x << 32) ^ (uint)z;
        }

        /// <summary>Projects a world position onto the centreline.</summary>
        public Projection Project(Vector3 worldPosition)
        {
            var nearest = FindNearestSample(worldPosition);
            var count = m_Samples.Length;

            // Refine against both adjacent segments so the result is continuous rather than
            // quantised to the 0.5 m sample spacing.
            var best = RefineOnSegment(worldPosition, (nearest - 1 + count) % count, nearest);
            var forward = RefineOnSegment(worldPosition, nearest, (nearest + 1) % count);

            if (forward.SqrDistance < best.SqrDistance)
            {
                best = forward;
            }

            var sample = m_Samples[best.Index];
            var next = m_Samples[(best.Index + 1) % count];
            var t = best.T;

            var position = Vector3.Lerp(sample.Position, next.Position, t);
            var tangent = Vector3.Slerp(sample.Forward, next.Forward, t);
            var right = Vector3.Cross(Vector3.up, tangent).normalized;
            var width = Mathf.Lerp(sample.Width, next.Width, t);
            var curvature = Mathf.Lerp(sample.Curvature, next.Curvature, t);

            var distance = sample.Distance + t * SegmentLength(best.Index);
            var offset = worldPosition - position;
            offset.y = 0f;

            return new Projection(
                Mathf.Repeat(distance, TotalLength),
                Vector3.Dot(offset, right),
                width,
                position,
                tangent,
                curvature);
        }

        float SegmentLength(int index)
        {
            var count = m_Samples.Length;
            var next = (index + 1) % count;
            return next == 0
                ? TotalLength - m_Samples[index].Distance
                : m_Samples[next].Distance - m_Samples[index].Distance;
        }

        readonly struct SegmentHit
        {
            public readonly int Index;
            public readonly float T;
            public readonly float SqrDistance;

            public SegmentHit(int index, float t, float sqrDistance)
            {
                Index = index;
                T = t;
                SqrDistance = sqrDistance;
            }
        }

        SegmentHit RefineOnSegment(Vector3 worldPosition, int index, int nextIndex)
        {
            var a = m_Samples[index].Position;
            var b = m_Samples[nextIndex].Position;

            var ab = b - a;
            ab.y = 0f;

            var ap = worldPosition - a;
            ap.y = 0f;

            var lengthSquared = ab.sqrMagnitude;
            var t = lengthSquared > 1e-8f ? Mathf.Clamp01(Vector3.Dot(ap, ab) / lengthSquared) : 0f;

            var closest = a + ab * t;
            var delta = worldPosition - closest;
            delta.y = 0f;

            return new SegmentHit(index, t, delta.sqrMagnitude);
        }

        int FindNearestSample(Vector3 worldPosition)
        {
            var cellX = Mathf.FloorToInt(worldPosition.x / k_CellSize);
            var cellZ = Mathf.FloorToInt(worldPosition.z / k_CellSize);

            var best = -1;
            var bestSqr = float.MaxValue;

            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (!m_Grid.TryGetValue(CellKey(cellX + dx, cellZ + dz), out var bucket))
                    {
                        continue;
                    }

                    foreach (var index in bucket)
                    {
                        var sqr = SqrDistanceXZ(m_Samples[index].Position, worldPosition);
                        if (sqr < bestSqr)
                        {
                            bestSqr = sqr;
                            best = index;
                        }
                    }
                }
            }

            if (best >= 0)
            {
                return best;
            }

            // Far off the circuit (more than one cell away). Rare enough that a linear sweep is
            // cheaper than maintaining a deeper spatial structure.
            for (var i = 0; i < m_Samples.Length; i++)
            {
                var sqr = SqrDistanceXZ(m_Samples[i].Position, worldPosition);
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = i;
                }
            }

            return Mathf.Max(0, best);
        }

        static float SqrDistanceXZ(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        /// <summary>
        /// Moves the whole circuit to a new world position.
        ///
        /// Evaluation lays ten circuits out on a grid. Shifting the model once is far cheaper and
        /// less error-prone than offsetting every projection, waypoint and start pose at the call
        /// site — and it means a competitor's observation code never has to know it is running in
        /// environment seven.
        /// </summary>
        public void Rebase(Vector3 offset)
        {
            if (offset == Vector3.zero)
            {
                return;
            }

            for (var i = 0; i < m_Samples.Length; i++)
            {
                m_Samples[i].Position += offset;
            }

            m_Grid.Clear();
            BuildGrid();
        }

        internal void AssignSections(TrackSection[] sections)
        {
            m_Sections = sections ?? System.Array.Empty<TrackSection>();
        }

        /// <summary>The section covering a given arc length.</summary>
        public TrackSection SectionAt(float distance)
        {
            if (m_Sections.Length == 0)
            {
                return default;
            }

            var wrapped = Mathf.Repeat(distance, TotalLength);
            for (var i = m_Sections.Length - 1; i >= 0; i--)
            {
                if (wrapped >= m_Sections[i].StartDistance)
                {
                    return m_Sections[i];
                }
            }

            return m_Sections[0];
        }

        public Sample SampleAtDistance(float distance)
        {
            var count = m_Samples.Length;
            var wrapped = Mathf.Repeat(distance, TotalLength);
            var exact = wrapped / TotalLength * count;
            var index = Mathf.FloorToInt(exact) % count;
            var next = (index + 1) % count;
            var t = exact - Mathf.Floor(exact);

            return new Sample
            {
                Position = Vector3.Lerp(m_Samples[index].Position, m_Samples[next].Position, t),
                Forward = Vector3.Slerp(m_Samples[index].Forward, m_Samples[next].Forward, t),
                Right = Vector3.Slerp(m_Samples[index].Right, m_Samples[next].Right, t),
                Distance = wrapped,
                Width = Mathf.Lerp(m_Samples[index].Width, m_Samples[next].Width, t),
                Curvature = Mathf.Lerp(m_Samples[index].Curvature, m_Samples[next].Curvature, t),
            };
        }

        /// <summary>Mean curvature over a window starting at <paramref name="distance"/>.</summary>
        public float AverageCurvature(float distance, float window)
        {
            const int steps = 5;
            var total = 0f;
            for (var i = 0; i < steps; i++)
            {
                total += SampleAtDistance(distance + window * i / steps).Curvature;
            }

            return total / steps;
        }

        public SurfaceSample SampleSurface(Vector3 worldPosition)
        {
            var projection = Project(worldPosition);
            if (projection.IsOnRoad)
            {
                return SurfaceSample.Tarmac;
            }

            var brakeForce = projection.IsBeyondRunoff ? CarSpec.OffRoadBrakeForce : 0f;
            return new SurfaceSample(false, CarSpec.OffTrackGripMultiplier, brakeForce);
        }

        /// <summary>Pose a car should start from: on the centreline at distance 0, facing forwards.</summary>
        public Pose GetStartPose(float heightOffset)
        {
            var sample = SampleAtDistance(0f);
            return new Pose(
                sample.Position + Vector3.up * heightOffset,
                Quaternion.LookRotation(sample.Forward, Vector3.up));
        }
    }
}
