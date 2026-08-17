using UnityEngine;

namespace RacingBotCup.Vehicle
{
    /// <summary>
    /// Noise-free surface query used by <see cref="CarController"/> to decide per-wheel grip.
    /// Kept as an interface so the vehicle never takes a hard dependency on the track code —
    /// a bare test scene can run the car with no provider at all.
    /// </summary>
    public interface ISurfaceProvider
    {
        SurfaceSample SampleSurface(Vector3 worldPosition);
    }

    public readonly struct SurfaceSample
    {
        /// <summary>True when the point is within the drivable road surface.</summary>
        public readonly bool OnTrack;

        /// <summary>Multiplier applied to tyre friction stiffness. 1 on tarmac, lower off-track.</summary>
        public readonly float GripMultiplier;

        /// <summary>
        /// Extra braking force (N) this wheel contributes, opposing the car's velocity. Zero on
        /// tarmac and on the run-off strip — only once a wheel is fully off, past the run-off band,
        /// does the terrain itself start costing speed rather than just grip.
        /// </summary>
        public readonly float OffRoadBrakeForce;

        public SurfaceSample(bool onTrack, float gripMultiplier, float offRoadBrakeForce = 0f)
        {
            OnTrack = onTrack;
            GripMultiplier = gripMultiplier;
            OffRoadBrakeForce = offRoadBrakeForce;
        }

        /// <summary>Fallback used when no provider is attached: full grip everywhere.</summary>
        public static SurfaceSample Tarmac => new SurfaceSample(true, 1f);
    }
}
