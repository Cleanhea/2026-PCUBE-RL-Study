using System;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace RacingBotCup.Eval
{
    /// <summary>
    /// The driving rules from 기획서 §6. Like <see cref="Vehicle.CarSpec"/> these are sealed and
    /// hashed into every submission — a competitor running with a longer DNF grace period is not
    /// running the same competition as everyone else.
    /// </summary>
    public static class RaceRules
    {
        public const string Version = "1.0.0";

        /// <summary>Checkpoints per lap. All must be passed in order for the lap to count.</summary>
        public const int CheckpointCount = 24;

        /// <summary>
        /// Seconds with all four wheels off the road before the run is abandoned.
        ///
        /// Long enough to sit out a full RampCorner shortcut — leaving the road, crossing the
        /// run-off, the launch itself and the flight over the infield are all off-road time, and at
        /// the old three seconds that legitimate line was scored as a car that had crashed out.
        /// </summary>
        public const float OffTrackDnfSeconds = 6f;

        /// <summary>
        /// Agent timeout as a multiple of the baseline bot's time on the same track. Every run is
        /// watched start to finish now (see <see cref="Eval.EvaluationRunner"/>), so a stuck agent
        /// costs real wall-clock time to sit out — lower than 3x trades a little patience for a lot
        /// less time watching a car that has already lost drive in circles.
        /// </summary>
        public const float TimeoutMultiplier = 1.5f;

        /// <summary>
        /// Hard ceiling for the baseline's own run, which has no reference to scale from. The
        /// baseline reliably finishes every published seed in under two minutes; this is a backstop
        /// for a pathological seed, not a number it is expected to approach.
        /// </summary>
        public const float BaselineTimeoutSeconds = 120f;

        /// <summary>How far above the road the car is placed for a standing start.</summary>
        public const float StartHeightOffset = 0.7f;

        /// <summary>Laps per run. One is enough with no tyre or fuel model (기획서 §13-5).</summary>
        public const int Laps = 1;

        /// <summary>
        /// Physics steps between policy decisions. Fixed here rather than left to each competitor's
        /// DecisionRequester because a policy trained at one decision rate and evaluated at another
        /// drives quite differently — and the gap would look like a scoring bug, not a setup one.
        /// At the 0.02 s timestep this is a decision every 80 ms.
        /// </summary>
        public const int DecisionPeriod = 4;

        /// <summary>
        /// How far outside the road edge a checkpoint may still be credited, as a multiple of the
        /// half-width. Slightly generous so clipping an apex is not punished as a shortcut.
        /// </summary>
        public const float CheckpointLateralTolerance = 1.5f;

        static string s_Hash;

        public static string Hash
        {
            get
            {
                if (s_Hash != null)
                {
                    return s_Hash;
                }

                var fields = typeof(RaceRules).GetFields(BindingFlags.Public | BindingFlags.Static);
                Array.Sort(fields, (a, b) => string.CompareOrdinal(a.Name, b.Name));

                var builder = new StringBuilder();
                foreach (var field in fields)
                {
                    var value = field.GetValue(null);
                    builder.Append(field.Name).Append('=');
                    builder.Append(value is float f
                        ? f.ToString("R", CultureInfo.InvariantCulture)
                        : Convert.ToString(value, CultureInfo.InvariantCulture));
                    builder.Append(';');
                }

                using (var sha = SHA256.Create())
                {
                    var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                    var hex = new StringBuilder(16);
                    for (var i = 0; i < 8; i++)
                    {
                        hex.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
                    }

                    s_Hash = hex.ToString();
                }

                return s_Hash;
            }
        }
    }
}
