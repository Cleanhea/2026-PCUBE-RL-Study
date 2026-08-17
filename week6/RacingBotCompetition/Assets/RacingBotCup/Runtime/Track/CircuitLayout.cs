using System.Collections.Generic;
using UnityEngine;

namespace RacingBotCup.Track
{
    /// <summary>
    /// Decides what kind of circuit a seed describes, before any geometry exists.
    ///
    /// Sections are drawn as short motifs rather than one at a time. A hairpin that nobody
    /// approaches at speed is just a slow corner, so the motif that contains it also contains the
    /// straight leading into it — the braking zone comes from the grammar, not from luck.
    /// </summary>
    public sealed class CircuitLayout
    {
        /// <summary>Relative arc length each type occupies around the loop.</summary>
        static readonly Dictionary<TrackSectionType, float> k_Weights = new Dictionary<TrackSectionType, float>
        {
            [TrackSectionType.Straight] = 3.2f,
            [TrackSectionType.Corner] = 1.2f,
            // A hairpin is two approach legs meeting at a tight tip, so it needs enough chord to
            // fit both — a sliver of arc leaves no room for the legs and the tip collapses.
            [TrackSectionType.Hairpin] = 1.0f,
            // Chicanes and esses need room: their character comes from a swing across the section,
            // and a swing squeezed into a short chord becomes a cusp rather than a corner.
            [TrackSectionType.Chicane] = 1.5f,
            [TrackSectionType.Esses] = 2.6f,

            // Decorated variants keep their base type's weight — swapping one in should not shift
            // how much of the lap is allotted to that kind of section.
            [TrackSectionType.SharpHairpin] = 1.0f,
            [TrackSectionType.ObstacleStraight] = 3.2f,
            [TrackSectionType.RampCorner] = 1.2f,
        };

        /// <summary>Chance each hazard variant appears at all in a given circuit, independently.</summary>
        const float k_HazardChance = 0.25f;

        /// <summary>
        /// Which hazard variants a circuit gets, decided once per seed — never re-rolled per
        /// validation attempt. <see cref="TrackGenerator.Generate"/> retries with a fresh layout
        /// (and fresh centreline noise) up to <see cref="TrackGenerator.k_MaxAttempts"/> times, and
        /// returns whichever attempt validates first. Re-rolling hazards on every attempt would let
        /// that race silently discriminate against whichever variant happens to be harder to
        /// validate: an attempt that rolled "no SharpHairpin" is more likely to pass on its first
        /// try, so it wins the race far more than 75% of the time — this struct is what keeps a
        /// seed's hazard mix fixed while still letting the per-attempt easing loosen the *geometry*
        /// enough for a stubborn seed to converge with the hazard still in it.
        /// </summary>
        public struct HazardSelection
        {
            public static readonly HazardSelection None = default;

            public bool SharpHairpin;
            public float SharpHairpinPick;
            public bool ObstacleStraight;
            public float ObstacleStraightPick;
            public bool RampCorner;
            public float RampCornerPick;
        }

        /// <summary>Rolls a <see cref="HazardSelection"/> from the given stream. Call once per seed,
        /// on a stream separate from the per-attempt one, then pass the same result to every
        /// <see cref="Build"/> call for that seed.</summary>
        public static HazardSelection RollHazards(ref DeterministicRandom random)
        {
            return new HazardSelection
            {
                SharpHairpin = random.NextFloat() < k_HazardChance,
                SharpHairpinPick = random.NextFloat(),
                ObstacleStraight = random.NextFloat() < k_HazardChance,
                ObstacleStraightPick = random.NextFloat(),
                RampCorner = random.NextFloat() < k_HazardChance,
                RampCornerPick = random.NextFloat(),
            };
        }

        /// <summary>Metres of circuit per section. Roughly a section every football pitch and a half.</summary>
        const float k_MetresPerSection = 160f;

        /// <summary>The main straight is stretched so one part of the lap is clearly the fastest.</summary>
        const float k_MainStraightBonus = 1.45f;

        const int k_MinSections = 7;
        const int k_MaxSections = 14;
        const int k_MaxHairpins = 2;
        const int k_MaxChicanes = 2;
        const int k_MaxEsses = 2;

        static readonly TrackSectionType[][] k_Motifs =
        {
            // Heavy braking into the slowest corner on the circuit.
            new[] { TrackSectionType.Straight, TrackSectionType.Hairpin },
            // Flat out, then hard on the brakes for a quick direction change.
            new[] { TrackSectionType.Straight, TrackSectionType.Chicane },
            // A fast corner taken at the end of a straight.
            new[] { TrackSectionType.Straight, TrackSectionType.Corner },
            // Momentum section.
            new[] { TrackSectionType.Esses },
            // A double-apex complex.
            new[] { TrackSectionType.Corner, TrackSectionType.Corner },
            // A single sweeper linking two other features.
            new[] { TrackSectionType.Corner },
            // Chicane onto a corner, as at the end of many street circuits.
            new[] { TrackSectionType.Chicane, TrackSectionType.Corner },
        };

        public TrackSectionType[] Types { get; private set; }

        public bool[] BrakingZones { get; private set; }

        /// <summary>Angular span of each section around the base circle, in radians. Sums to 2π.</summary>
        public float[] Spans { get; private set; }

        public int MainStraightIndex { get; private set; }

        public int Count => Types.Length;

        /// <param name="targetLength">
        /// Circuit length in metres. Section count scales with it — packing fourteen features into
        /// 900 m leaves every one of them too short to be recognisable.
        /// </param>
        /// <param name="hazards">
        /// Which hazard variants to apply, decided once per seed via <see cref="RollHazards"/> —
        /// pass <see cref="HazardSelection.None"/> to disable them entirely (the default everywhere
        /// except <see cref="TrackInstance"/>'s training-facing default).
        /// </param>
        public static CircuitLayout Build(ref DeterministicRandom random, float targetLength, HazardSelection hazards = default)
        {
            var types = new List<TrackSectionType>
            {
                // Every circuit opens with its main straight and the braking zone at the end of it.
                TrackSectionType.Straight,
                TrackSectionType.Hairpin,
            };

            var hairpins = 1;
            var chicanes = 0;
            var esses = 0;
            var budget = Mathf.RoundToInt(targetLength / k_MetresPerSection);
            var target = Mathf.Clamp(budget + random.Range(-1, 2), k_MinSections, k_MaxSections);

            // Bounded: each pass appends at least one section, so the count always reaches target.
            while (types.Count < target)
            {
                var motif = k_Motifs[random.Range(0, k_Motifs.Length)];

                var addsHairpin = Contains(motif, TrackSectionType.Hairpin);
                var addsChicane = Contains(motif, TrackSectionType.Chicane);
                var addsEsses = Contains(motif, TrackSectionType.Esses);

                if (addsHairpin && hairpins >= k_MaxHairpins)
                {
                    continue;
                }

                if (addsChicane && chicanes >= k_MaxChicanes)
                {
                    continue;
                }

                if (addsEsses && esses >= k_MaxEsses)
                {
                    continue;
                }

                if (types.Count + motif.Length > k_MaxSections)
                {
                    types.Add(TrackSectionType.Corner);
                    continue;
                }

                // Two chicanes back to back read as one confused wiggle rather than two features.
                if (addsChicane && types[types.Count - 1] == TrackSectionType.Chicane)
                {
                    continue;
                }

                types.AddRange(motif);
                hairpins += addsHairpin ? 1 : 0;
                chicanes += addsChicane ? 1 : 0;
                esses += addsEsses ? 1 : 0;
            }

            ApplyHazardSections(types, hazards);

            var layout = new CircuitLayout
            {
                Types = types.ToArray(),
                MainStraightIndex = 0,
            };

            layout.MarkBrakingZones();
            layout.ComputeSpans();
            return layout;
        }

        static bool Contains(TrackSectionType[] motif, TrackSectionType type)
        {
            foreach (var entry in motif)
            {
                if (entry == type)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Applies whichever hazard variants <paramref name="hazards"/> selected, converting one
        /// eligible section of the matching base type into each. Runs after the base layout is
        /// decided and before <see cref="MarkBrakingZones"/> / <see cref="ComputeSpans"/>, so both
        /// see the final, decorated type list.
        /// </summary>
        static void ApplyHazardSections(List<TrackSectionType> types, HazardSelection hazards)
        {
            if (hazards.SharpHairpin)
            {
                TryDecorate(types, hazards.SharpHairpinPick, TrackSectionType.Hairpin, TrackSectionType.SharpHairpin, skipIndex: -1);
            }

            // Index 0 is always the main straight (see the two seed entries at the top of Build) and
            // always leads into the opening hairpin — keeping it a plain Straight guarantees the
            // circuit's mandatory heavy-braking-zone invariant survives regardless of what else here
            // gets decorated.
            if (hazards.ObstacleStraight)
            {
                TryDecorate(types, hazards.ObstacleStraightPick, TrackSectionType.Straight, TrackSectionType.ObstacleStraight, skipIndex: 0);
            }

            if (hazards.RampCorner)
            {
                TryDecorate(types, hazards.RampCornerPick, TrackSectionType.Corner, TrackSectionType.RampCorner, skipIndex: -1);
            }
        }

        /// <summary>
        /// Converts the section at <c>eligible[floor(pick * eligible.Count)]</c> — a stored fraction
        /// rather than a stored index, since which indices are eligible can differ from one retry
        /// attempt's layout to the next, but "roughly this far through the eligible list" still
        /// picks out a consistent, reproducible choice on each of them.
        /// </summary>
        static void TryDecorate(
            List<TrackSectionType> types,
            float pick,
            TrackSectionType baseType,
            TrackSectionType decoratedType,
            int skipIndex)
        {
            var eligible = new List<int>();
            for (var i = 0; i < types.Count; i++)
            {
                if (types[i] == baseType && i != skipIndex)
                {
                    eligible.Add(i);
                }
            }

            if (eligible.Count == 0)
            {
                return;
            }

            var index = Mathf.Clamp(Mathf.FloorToInt(pick * eligible.Count), 0, eligible.Count - 1);
            types[eligible[index]] = decoratedType;
        }

        void MarkBrakingZones()
        {
            BrakingZones = new bool[Types.Length];
            for (var i = 0; i < Types.Length; i++)
            {
                if (Types[i] != TrackSectionType.Straight)
                {
                    continue;
                }

                var next = Types[(i + 1) % Types.Length];
                BrakingZones[i] = next == TrackSectionType.Hairpin
                                  || next == TrackSectionType.SharpHairpin
                                  || next == TrackSectionType.Chicane;
            }
        }

        void ComputeSpans()
        {
            Spans = new float[Types.Length];

            var total = 0f;
            for (var i = 0; i < Types.Length; i++)
            {
                var weight = k_Weights[Types[i]];
                if (i == MainStraightIndex)
                {
                    weight *= k_MainStraightBonus;
                }

                Spans[i] = weight;
                total += weight;
            }

            var scale = Mathf.PI * 2f / total;
            for (var i = 0; i < Spans.Length; i++)
            {
                Spans[i] *= scale;
            }
        }

        public string Describe()
        {
            var builder = new System.Text.StringBuilder();
            for (var i = 0; i < Types.Length; i++)
            {
                builder.Append(BrakingZones[i] ? "Straight(brk)" : Types[i].ToString());
                if (i < Types.Length - 1)
                {
                    builder.Append(" > ");
                }
            }

            return builder.ToString();
        }
    }
}
