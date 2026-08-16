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

        public SurfaceSample(bool onTrack, float gripMultiplier)
        {
            OnTrack = onTrack;
            GripMultiplier = gripMultiplier;
        }

        /// <summary>Fallback used when no provider is attached: full grip everywhere.</summary>
        public static SurfaceSample Tarmac => new SurfaceSample(true, 1f);
    }
}
