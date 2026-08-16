using RacingBotCup.Track;
using RacingBotCup.Vehicle;
using UnityEngine;

namespace RacingBotCup.Racing
{
    /// <summary>
    /// Live state of one run, refreshed once per physics step by the session that owns it.
    ///
    /// Drivers read from here rather than querying the track themselves, so the expensive
    /// centreline projection happens exactly once per tick and every driver — bot or policy —
    /// sees precisely the same numbers.
    /// </summary>
    public sealed class RaceContext
    {
        public CarController Car { get; }

        public TrackModel Track { get; }

        public CheckpointRing Checkpoints { get; }

        public TrackModel.Projection Projection { get; private set; }

        public float ElapsedTime { get; private set; }

        public float TimeSinceCheckpoint { get; private set; }

        /// <summary>Seconds the car has been continuously off the road with every wheel.</summary>
        public float OffTrackDuration { get; private set; }

        /// <summary>Metres of valid progress gained on the most recent tick.</summary>
        public float ProgressDelta { get; private set; }

        public bool LapCompletedThisTick { get; private set; }

        public RaceContext(CarController car, TrackModel track, CheckpointRing checkpoints)
        {
            Car = car;
            Track = track;
            Checkpoints = checkpoints;
            Reset();
        }

        public void Reset()
        {
            Checkpoints.Reset();
            Projection = Track.Project(Car.transform.position);
            ElapsedTime = 0f;
            TimeSinceCheckpoint = 0f;
            OffTrackDuration = 0f;
            ProgressDelta = 0f;
            LapCompletedThisTick = false;
        }

        public void Refresh(float deltaTime)
        {
            ElapsedTime += deltaTime;
            TimeSinceCheckpoint += deltaTime;

            Projection = Track.Project(Car.transform.position);

            var previousPassed = Checkpoints.Passed;
            var previousTraveled = Checkpoints.TraveledDistance;

            // The car cannot advance along the centreline faster than it is physically moving.
            // A larger jump means the projection snapped to a different part of the loop.
            var plausibleDelta = Car.Speed * deltaTime * 1.5f + 1f;
            LapCompletedThisTick = Checkpoints.Update(Projection, plausibleDelta);

            ProgressDelta = Checkpoints.TraveledDistance - previousTraveled;

            if (Checkpoints.Passed > previousPassed)
            {
                TimeSinceCheckpoint = 0f;
            }

            OffTrackDuration = Car.AllWheelsOffTrack ? OffTrackDuration + deltaTime : 0f;
        }
    }
}
