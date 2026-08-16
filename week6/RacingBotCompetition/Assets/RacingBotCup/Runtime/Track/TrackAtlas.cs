using System;
using UnityEngine;

namespace RacingBotCup.Track
{
    /// <summary>
    /// UV coordinates into the PolygonStreetRacer road textures.
    ///
    /// Every one of those textures is a large tileable asphalt field with a strip of flat colour
    /// swatches tucked into the bottom-left corner (measured, not guessed: six 44 px swatches in
    /// the bottom 26 px of a 2048² image). Synty art addresses colour by pointing UVs at a swatch
    /// rather than by using separate materials, which is what lets the road surface, the red/white
    /// kerbs and the start line all live in one mesh with one material.
    /// </summary>
    public static class TrackAtlas
    {
        const float k_SwatchV = 0.0066f;

        public static readonly Vector2 White = new Vector2(0.0107f, k_SwatchV);
        public static readonly Vector2 Yellow = new Vector2(0.0322f, k_SwatchV);
        public static readonly Vector2 Red = new Vector2(0.0537f, k_SwatchV);
        public static readonly Vector2 Dark = new Vector2(0.0752f, k_SwatchV);

        // The asphalt field, sampled well clear of the swatch strip.
        const float k_AsphaltMinU = 0.35f;
        const float k_AsphaltMaxU = 0.65f;
        const float k_AsphaltMinV = 0.30f;
        const float k_AsphaltMaxV = 0.70f;

        /// <summary>Metres of track per back-and-forth sweep through the asphalt region.</summary>
        const float k_AsphaltPeriod = 90f;

        /// <summary>
        /// Sand colour for the gravel run-off, taken from the swatch strip.
        ///
        /// The obvious choice looked like the sand-coloured road texture (Ground_08), but that one
        /// is a yellow-and-black hazard pattern — its average just happens to read as tan. Sampling
        /// its field striped the run-off. A swatch is a single flat colour by construction, so the
        /// band comes out solid whatever else changes.
        /// </summary>
        public static readonly Vector2 Gravel = Yellow;

        /// <summary>
        /// UV for a point on the road surface. The along-track coordinate ping-pongs instead of
        /// tiling: wrapping would run the UV through the swatch strip and paint red and white
        /// stripes across the racing line, and a sawtooth would leave a seam at every repeat.
        /// </summary>
        public static Vector2 Asphalt(float distance, float acrossFraction)
        {
            return Surface(distance, acrossFraction, k_AsphaltPeriod);
        }

        /// <summary>UV for the run-off. Flat sand, so "off" reads instantly at any distance.</summary>
        public static Vector2 Runoff(float distance, float acrossFraction)
        {
            return Gravel;
        }

        static Vector2 Surface(float distance, float acrossFraction, float period)
        {
            var u = Mathf.Lerp(k_AsphaltMinU, k_AsphaltMaxU, Mathf.Clamp01(acrossFraction));
            var sweep = Mathf.PingPong(distance / period, 1f);
            var v = Mathf.Lerp(k_AsphaltMinV, k_AsphaltMaxV, sweep);
            return new Vector2(u, v);
        }
    }

    /// <summary>
    /// Materials the circuit is built from, all taken from the art pack.
    ///
    /// Every one of them must keep its texture tiling at 1×1. UVs here address specific regions of
    /// the atlas — the asphalt field, the red and white kerb swatches — and tiling multiplies those
    /// coordinates, which wraps them straight through the swatch strip. Setting the run-off
    /// material to 8×8 to get finer gravel detail is what painted yellow stripes down both sides of
    /// the circuit. Use the per-surface sweep period above for that instead: it varies the sample
    /// point while staying inside the safe region.
    /// </summary>
    [Serializable]
    public sealed class TrackMaterials
    {
        [Tooltip("Racing surface. Also supplies the kerb and start-line colours from its swatch strip.")]
        public Material Road;

        [Tooltip("Gravel run-off immediately outside the kerbs.")]
        public Material Runoff;

        [Tooltip("Grass beyond the run-off. A flat colour — it takes no atlas UVs.")]
        public Material Ground;

        public bool IsComplete => Road != null && Runoff != null && Ground != null;
    }
}
