namespace RacingBotCup.Track
{
    /// <summary>
    /// Seeded xorshift PRNG.
    ///
    /// <see cref="UnityEngine.Random"/> is deliberately not used anywhere in track generation:
    /// it is global mutable state, so anything else touching it — an editor tool, a particle
    /// system, another script's Awake — would silently change the track a seed produces.
    /// A track must be byte-identical for a given seed on every machine, forever.
    /// </summary>
    public struct DeterministicRandom
    {
        uint m_State;

        public DeterministicRandom(int seed)
        {
            // SplitMix32 finaliser first, so neighbouring seeds (12, 13, 14…) decorrelate.
            var x = (uint)seed + 0x9E3779B9u;
            x ^= x >> 16;
            x *= 0x85EBCA6Bu;
            x ^= x >> 13;
            x *= 0xC2B2AE35u;
            x ^= x >> 16;
            m_State = x == 0u ? 0x9E3779B9u : x;
        }

        public uint NextUInt()
        {
            var x = m_State;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            m_State = x;
            return x;
        }

        /// <summary>Uniform float in [0, 1).</summary>
        public float NextFloat()
        {
            return (NextUInt() >> 8) * (1f / 16777216f);
        }

        public float Range(float min, float max)
        {
            return min + (max - min) * NextFloat();
        }

        public int Range(int minInclusive, int maxExclusive)
        {
            var span = (uint)(maxExclusive - minInclusive);
            return span == 0u ? minInclusive : minInclusive + (int)(NextUInt() % span);
        }

        /// <summary>Uniform float in [-1, 1).</summary>
        public float NextSigned()
        {
            return NextFloat() * 2f - 1f;
        }

        /// <summary>
        /// Produces an independent stream from this one. Used to retry generation with a fresh
        /// layout while keeping the whole process a pure function of the original seed.
        /// </summary>
        public DeterministicRandom Derive(int salt)
        {
            return new DeterministicRandom((int)(NextUInt() ^ (uint)(salt * 0x27D4EB2Du)));
        }
    }
}
