using UnityEngine;

namespace RacingBotCup.Track
{
    /// <summary>
    /// Ordered checkpoints around the loop, and the per-run state that credits them.
    ///
    /// Checkpoints are arc-length positions rather than trigger colliders. Triggers depend on
    /// collision callback ordering, which is exactly the kind of thing that makes two runs of the
    /// same seed disagree; arc length is a pure function of the car's position.
    ///
    /// Two rules close the obvious holes:
    /// <list type="bullet">
    /// <item>A checkpoint only counts while the car is on or near the road, so nobody is credited
    /// for a lap driven across the infield.</item>
    /// <item>Progress that jumps further than the car could physically have travelled since the
    /// last tick is discarded. Cutting a corner makes the projected arc length leap, and that leap
    /// earns nothing.</item>
    /// </list>
    /// </summary>
    public sealed class CheckpointRing
    {
        readonly TrackModel m_Model;
        readonly float m_Spacing;
        readonly float m_LateralTolerance;

        float m_LastDistance;
        float m_Traveled;
        bool m_Started;

        public int Count { get; }

        public int Passed { get; private set; }

        public bool LapComplete { get; private set; }

        /// <summary>
        /// How much of the final physics step had elapsed when the finish line was crossed, 0 to 1.
        /// Lets the lap time resolve finer than the step it was detected on.
        /// </summary>
        public float LapCrossingFraction { get; private set; } = 1f;

        /// <summary>Fraction of the lap completed, 0 to 1.</summary>
        public float Progress => Count == 0 ? 0f : Mathf.Clamp01(m_Traveled / m_Model.TotalLength);

        /// <summary>Metres of valid forward progress since the run started.</summary>
        public float TraveledDistance => m_Traveled;

        public CheckpointRing(TrackModel model, int count = 24, float lateralTolerance = 1.5f)
        {
            m_Model = model;
            Count = Mathf.Max(4, count);
            m_Spacing = model.TotalLength / Count;
            m_LateralTolerance = lateralTolerance;
            Reset();
        }

        public void Reset()
        {
            m_LastDistance = 0f;
            m_Traveled = 0f;
            m_Started = false;
            Passed = 0;
            LapComplete = false;
        }

        /// <summary>
        /// Advances lap state. <paramref name="maxPlausibleDelta"/> is how far the car could have
        /// moved along the centreline since the previous call — anything beyond that is treated as
        /// a projection jump and ignored.
        /// </summary>
        /// <returns>True on the tick the lap completes.</returns>
        public bool Update(TrackModel.Projection projection, float maxPlausibleDelta)
        {
            if (LapComplete)
            {
                return false;
            }

            if (!m_Started)
            {
                m_LastDistance = projection.Distance;
                m_Started = true;
                return false;
            }

            var delta = ShortestSignedDelta(m_LastDistance, projection.Distance, m_Model.TotalLength);
            m_LastDistance = projection.Distance;

            if (Mathf.Abs(delta) > Mathf.Max(1f, maxPlausibleDelta))
            {
                return false;
            }

            var onRoad = Mathf.Abs(projection.Lateral) <= projection.Width * 0.5f * m_LateralTolerance;
            if (!onRoad && delta > 0f)
            {
                // Distance covered off the road still counts towards the lap, but no checkpoint is
                // credited until the car is back on it. Running wide through a corner — or taking
                // the ramp shortcut off a RampCorner's inside kerb — therefore costs nothing, while
                // a lap driven entirely off-track is impossible anyway: the DNF rule ends it long
                // before a full loop.
                m_Traveled += delta;
                return false;
            }

            m_Traveled += delta;

            var earned = Mathf.Clamp(Mathf.FloorToInt(m_Traveled / m_Spacing), 0, Count);
            if (earned > Passed)
            {
                Passed = earned;
            }

            if (Passed >= Count && m_Traveled >= m_Model.TotalLength)
            {
                // Where inside this tick the line was actually crossed. Without it a lap time is
                // quantised to the physics step, and a car arriving a millimetre either side of a
                // step boundary reports a time a whole 20 ms apart — which shows up as two runs of
                // the same submission disagreeing.
                var overshoot = m_Traveled - m_Model.TotalLength;
                LapCrossingFraction = delta > 1e-6f ? Mathf.Clamp01(1f - overshoot / delta) : 1f;

                LapComplete = true;
                return true;
            }

            return false;
        }

        /// <summary>Difference between two arc positions on a loop, taking the shorter way round.</summary>
        static float ShortestSignedDelta(float from, float to, float loopLength)
        {
            var delta = to - from;
            var half = loopLength * 0.5f;

            if (delta > half)
            {
                delta -= loopLength;
            }
            else if (delta < -half)
            {
                delta += loopLength;
            }

            return delta;
        }

        public Vector3 GetCheckpointPosition(int index)
        {
            return m_Model.SampleAtDistance(index * m_Spacing).Position;
        }
    }
}
