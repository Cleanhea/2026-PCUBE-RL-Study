using UnityEngine;

namespace RacingBotCup.Track
{
    /// <summary>
    /// Difficulty knobs sampled from a seed. Ranges follow 기획서 §5.
    ///
    /// Corner count is no longer sampled here — the number and kind of corners now come from
    /// <see cref="CircuitLayout"/>, so what is left is the circuit's physical envelope.
    /// </summary>
    public struct TrackParams
    {
        /// <summary>Floor on corner radius for hairpins, which are meant to be the tight bit.</summary>
        public const float HairpinMinRadius = 11f;

        /// <summary>Floor on corner radius for <see cref="TrackSectionType.SharpHairpin"/> — tighter still.</summary>
        public const float SharpHairpinMinRadius = 7f;

        public int Seed;

        /// <summary>Target centreline length in metres (800–2000).</summary>
        public float TargetLength;

        /// <summary>Mean road width in metres. Kept in the upper half of the 6–12 m envelope:
        /// a chicane on a 6 m road is a wall, not a corner.</summary>
        public float BaseWidth;

        /// <summary>Half-amplitude of the along-track width variation.</summary>
        public float WidthVariation;

        public float WidthPhase;

        public float WidthWaves;

        /// <summary>Tightest radius allowed outside hairpins and chicanes, in metres.</summary>
        public float MinCurvatureRadius;

        /// <summary>How far section anchors are pushed off the base circle, as a fraction of radius.</summary>
        public float RadialPerturbation;

        /// <summary>Large-scale radial deformation that gives the circuit an irregular silhouette.</summary>
        public float ShapeAmplitude;

        public int ShapeLobes;

        public float ShapePhase;

        public float ShapeSecondaryPhase;

        /// <summary>Outward reach of a hairpin, as a multiple of its anchor gap.</summary>
        public float HairpinReach;

        /// <summary>
        /// Reach of a <see cref="TrackSectionType.SharpHairpin"/>'s vertex pull, as a fraction of
        /// its chord length. The interior angle at the vertex is <c>2*atan(0.5/SharpHairpinReach)</c>
        /// — the sampled range targets roughly 53°-70°, tighter than an ordinary hairpin's own
        /// ~68°-87° without pulling hard enough to fight <see cref="TrackGenerator.SmoothClosed"/>
        /// and the spline's auto tangents into eroding the turn back down or failing curvature
        /// validation outright — both of which several more aggressive reach ranges and several
        /// self-contained junction constructions ran into in practice (see
        /// <see cref="TrackGenerator.EmitSection"/>'s <c>SharpHairpin</c> case).
        /// </summary>
        public float SharpHairpinReach;

        /// <summary>
        /// How hard a <see cref="TrackSectionType.RampCorner"/> bends, as a fraction of its chord
        /// length, used directly as the sine-bulge amplitude factor. This is what decides whether
        /// the ramp off its inside kerb is worth taking: the sharper the bend, the more the chord
        /// across it saves over the road round.
        /// </summary>
        public float RampCornerThrow;

        /// <summary>
        /// How tight a chicane is allowed to be, as a multiple of <see cref="MinCurvatureRadius"/>.
        /// Below 1 the chicane bites harder than an ordinary corner.
        /// </summary>
        public float ChicaneThrow;

        /// <summary>Same idea for an esses sequence, which should flow rather than bite.</summary>
        public float EssesThrow;

        public static TrackParams FromSeed(int seed)
        {
            var random = new DeterministicRandom(seed);

            var result = new TrackParams
            {
                Seed = seed,
                TargetLength = random.Range(800f, 2000f),
                BaseWidth = random.Range(8f, 12f),
                MinCurvatureRadius = random.Range(22f, 38f),
                RadialPerturbation = random.Range(0.025f, 0.075f),
                ShapeAmplitude = random.Range(0.10f, 0.18f),
                ShapeLobes = random.Range(2, 5),
                ShapePhase = random.Range(0f, Mathf.PI * 2f),
                ShapeSecondaryPhase = random.Range(0f, Mathf.PI * 2f),
                HairpinReach = random.Range(0.95f, 1.35f),
                SharpHairpinReach = random.Range(0.7f, 1.0f),
                RampCornerThrow = random.Range(0.18f, 0.30f),
                ChicaneThrow = random.Range(0.9f, 1.3f),
                EssesThrow = random.Range(1.0f, 1.5f),
                WidthPhase = random.Range(0f, Mathf.PI * 2f),
                WidthWaves = random.Range(2f, 5f),
            };

            // Width may vary along the track but must stay inside the 6–12 m envelope.
            var headroom = Mathf.Min(result.BaseWidth - 6f, 12f - result.BaseWidth);
            result.WidthVariation = random.Range(0f, Mathf.Max(0f, headroom) * 0.6f);

            return result;
        }

        /// <summary>Radius of the base circle the section anchors sit on.</summary>
        public float BaseRadius => TargetLength / (2f * Mathf.PI);

        /// <summary>Road width at a normalised position along the loop.</summary>
        public float WidthAt(float normalizedDistance)
        {
            var wave = Mathf.Sin(normalizedDistance * Mathf.PI * 2f * WidthWaves + WidthPhase);
            return BaseWidth + WidthVariation * wave;
        }

        public float MaxWidth => BaseWidth + WidthVariation;
    }
}
