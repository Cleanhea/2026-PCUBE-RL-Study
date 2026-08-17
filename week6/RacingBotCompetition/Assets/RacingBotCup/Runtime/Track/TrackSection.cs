namespace RacingBotCup.Track
{
    /// <summary>
    /// The vocabulary a circuit is written in. Every metre of track belongs to exactly one of
    /// these, which is what lets a layout be described (and driven) as a sequence of recognisable
    /// challenges rather than as undifferentiated curvature.
    /// </summary>
    public enum TrackSectionType
    {
        /// <summary>Flat out. Long enough to reach top speed and to matter for slipstream feel.</summary>
        Straight = 0,

        /// <summary>A sweeper taken without fully lifting — the bread and butter of a lap.</summary>
        Corner = 1,

        /// <summary>Near-180° at walking pace. The slowest point on the circuit.</summary>
        Hairpin = 2,

        /// <summary>A sharp left-right (or right-left) flick over a short distance.</summary>
        Chicane = 3,

        /// <summary>A flowing alternating sequence where carrying momentum is the whole game.</summary>
        Esses = 4,

        /// <summary>
        /// A hairpin pulled tighter than <see cref="Hairpin"/> — interior angle down to ~53°.
        /// Concrete barrier props fence off the inside apex so the infield can't be cut.
        /// </summary>
        SharpHairpin = 5,

        /// <summary>A straight with crates, logs and containers scattered across it to weave around.</summary>
        ObstacleStraight = 6,

        /// <summary>
        /// A corner sharp enough to be worth not taking, with a ramp sitting off its inside kerb.
        /// The road round is the safe line; the ramp is a second one, launching a car that leaves
        /// the road across the infield to rejoin further round.
        /// </summary>
        RampCorner = 7,
    }

    /// <summary>
    /// One typed stretch of circuit, with the arc-length span it occupies on the centreline.
    /// </summary>
    public readonly struct TrackSection
    {
        public readonly int Index;
        public readonly TrackSectionType Type;
        public readonly float StartDistance;
        public readonly float EndDistance;

        /// <summary>
        /// True when this is a straight that ends in something slow. These are the heavy braking
        /// zones — where the biggest time is won and lost, and where an agent that brakes too late
        /// ends up in the run-off.
        /// </summary>
        public readonly bool IsBrakingZone;

        public TrackSection(
            int index,
            TrackSectionType type,
            float startDistance,
            float endDistance,
            bool isBrakingZone)
        {
            Index = index;
            Type = type;
            StartDistance = startDistance;
            EndDistance = endDistance;
            IsBrakingZone = isBrakingZone;
        }

        public float Length => EndDistance - StartDistance;

        public string Label => IsBrakingZone ? "Straight (braking)" : Type.ToString();
    }
}
