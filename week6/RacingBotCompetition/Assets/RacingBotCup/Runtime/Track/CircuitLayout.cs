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
        };

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
        public static CircuitLayout Build(ref DeterministicRandom random, float targetLength)
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
                BrakingZones[i] = next == TrackSectionType.Hairpin || next == TrackSectionType.Chicane;
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
