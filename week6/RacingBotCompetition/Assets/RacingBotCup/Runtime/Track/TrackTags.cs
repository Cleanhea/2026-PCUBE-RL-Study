using UnityEngine;

namespace RacingBotCup.Track
{
    /// <summary>
    /// Tags on the circuit geometry. Ray and grid sensors detect surfaces by these, so a competitor
    /// who wants to tell tarmac from run-off asks for them by name.
    /// </summary>
    public static class TrackTags
    {
        public const string Track = "Track";
        public const string OffTrack = "OffTrack";

        /// <summary>Solid props to avoid: concrete barriers, crates, logs, containers.</summary>
        public const string Obstacle = "Obstacle";

        /// <summary>A ramp prop, tagged separately from <see cref="Obstacle"/> since it is meant to be used, not avoided.</summary>
        public const string Ramp = "Ramp";
    }

    /// <summary>Layers the harness relies on.</summary>
    public static class RacingLayers
    {
        public const string VehicleLayerName = "Vehicle";

        public static int VehicleLayer => LayerMask.NameToLayer(VehicleLayerName);

        /// <summary>
        /// What ray and grid sensors should be allowed to see. The car is excluded — rays start
        /// inside its own body collider, and without this every ray would report a hit at zero
        /// distance. Set this as the <c>RayLayerMask</c> on any ray sensor you add.
        /// </summary>
        public static LayerMask SensorMask
        {
            get
            {
                var vehicle = VehicleLayer;
                return vehicle < 0 ? ~0 : ~(1 << vehicle);
            }
        }
    }
}
