using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace RacingBotCup.Eval
{
    /// <summary>
    /// Turns a scored result into something a competitor can paste into the submission form, and
    /// back again on the organisers' side.
    ///
    /// The payload is gzipped before base64 so a full twelve-track result stays around a kilobyte
    /// of text — small enough for a form field, and opaque enough that nobody edits a number in it
    /// by accident.
    /// </summary>
    public static class SubmissionCodec
    {
        /// <summary>
        /// Mixed into the checksum. This is obfuscation, not cryptography: the constant ships
        /// inside the build every competitor runs, so anyone determined can recompute a checksum.
        /// It exists to catch casual edits, and it is one of the reasons the finals are run by the
        /// organisers on private tracks.
        /// </summary>
        const string k_ChecksumSalt = "RacingBotCup/2026/score-v2";

        public const string CodePrefix = "RBC1:";

        public static string ToJson(SubmissionPayload payload)
        {
            return JsonConvert.SerializeObject(payload, Formatting.Indented);
        }

        /// <summary>
        /// Six decimal places, the precision the leaderboard compares at, expressed as the scale a
        /// value is multiplied by before it becomes a canonical integer. See <see cref="Quantize"/>
        /// for why the canonical form carries integers instead of formatted decimals.
        /// </summary>
        const double k_QuantizeScale = 1e6;

        /// <summary>
        /// Renders a float the way the leaderboard script necessarily must: it never sees the
        /// float bits, only whatever decimal text JSON carried them as, parsed back as a JS
        /// double. Two conversions have to survive that gap.
        ///
        /// The first is float32 → double. Going via the round-trip string — the same text JSON
        /// carries — rebuilds the exact double the reader gets, where a plain widening cast would
        /// hand us a different, wider number.
        ///
        /// The second is double → text, and it has no safe crossing. Unity's Mono rounds
        /// <c>ToString("F6")</c> off the value's 15-digit decimal form, so 28.0604515 becomes
        /// "28.060452"; JavaScript's <c>toFixed(6)</c> rounds the true binary value
        /// (28.06045149999999918…) and gets "28.060451". Every six-decimal midpoint — and float32
        /// round-trip text lands on one often — broke verification on a perfectly honest
        /// submission. So the canonical form formats no decimals at all: it scales and floors into
        /// an integer using only IEEE-754 multiply, add and floor, which both runtimes compute
        /// bit for bit alike.
        /// </summary>
        static string Quantize(float value, CultureInfo culture)
        {
            var asDouble = double.Parse(value.ToString("R", culture), culture);
            return ((long)Math.Floor(asDouble * k_QuantizeScale + 0.5)).ToString(culture);
        }

        /// <summary>
        /// Canonical text the checksum is taken over. Order and layout must never drift — the
        /// leaderboard script rebuilds this exact string in JavaScript to verify a submission.
        /// </summary>
        static string CanonicalForm(SubmissionPayload payload)
        {
            var culture = CultureInfo.InvariantCulture;
            var builder = new StringBuilder();

            builder.Append(payload.SchemaVersion).Append('|');
            builder.Append(payload.ParticipantId).Append('|');
            builder.Append(payload.SeedSetId).Append('|');
            builder.Append(payload.SubmittedAtUtc).Append('|');

            if (payload.Score != null)
            {
                builder.Append(Quantize(payload.Score.Total, culture)).Append('|');
                builder.Append(Quantize(payload.Score.CompletionRate, culture)).Append('|');
                builder.Append(Quantize(payload.Score.ScoreStdDev, culture)).Append('|');
                builder.Append(payload.Score.TrackCount.ToString(culture)).Append('|');

                foreach (var track in payload.Score.Tracks)
                {
                    builder.Append(track.Seed.ToString(culture)).Append(',');
                    builder.Append(Quantize(track.BaselineTime, culture)).Append(',');
                    builder.Append(Quantize(track.AgentTime, culture)).Append(',');
                    builder.Append(((int)track.AgentStatus).ToString(culture)).Append(',');
                    builder.Append(((int)track.BaselineStatus).ToString(culture)).Append(',');
                    builder.Append(Quantize(track.Score, culture)).Append(';');
                }
            }

            var integrity = payload.Integrity;
            if (integrity != null)
            {
                builder.Append('|');
                builder.Append(integrity.CarSpecHash).Append(',');
                builder.Append(integrity.TrackGeneratorVersion).Append(',');
                builder.Append(integrity.ScoreModuleVersion).Append(',');
                builder.Append(integrity.BaselineBotVersion).Append(',');
                builder.Append(integrity.RulesHash).Append(',');
                builder.Append(integrity.AgentHash);
            }

            builder.Append('|').Append(k_ChecksumSalt);
            return builder.ToString();
        }

        public static string ComputeChecksum(SubmissionPayload payload)
        {
            using (var sha = SHA256.Create())
            {
                var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(CanonicalForm(payload)));
                var hex = new StringBuilder(24);
                for (var i = 0; i < 12; i++)
                {
                    hex.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return hex.ToString();
            }
        }

        public static void Sign(SubmissionPayload payload)
        {
            payload.Integrity ??= new IntegrityBlock();
            payload.Integrity.Checksum = null;
            payload.Integrity.Checksum = ComputeChecksum(payload);
        }

        public static bool VerifyChecksum(SubmissionPayload payload, out string error)
        {
            error = null;

            if (payload?.Integrity == null || string.IsNullOrEmpty(payload.Integrity.Checksum))
            {
                error = "Submission carries no checksum.";
                return false;
            }

            var claimed = payload.Integrity.Checksum;
            payload.Integrity.Checksum = null;
            var actual = ComputeChecksum(payload);
            payload.Integrity.Checksum = claimed;

            if (!string.Equals(claimed, actual, StringComparison.Ordinal))
            {
                error = $"Checksum mismatch (expected {actual}, found {claimed}).";
                return false;
            }

            return true;
        }

        /// <summary>Compact single-line code for the submission form.</summary>
        public static string Encode(SubmissionPayload payload)
        {
            var json = JsonConvert.SerializeObject(payload, Formatting.None);
            var raw = Encoding.UTF8.GetBytes(json);

            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true))
                {
                    gzip.Write(raw, 0, raw.Length);
                }

                return CodePrefix + Convert.ToBase64String(output.ToArray());
            }
        }

        public static bool TryDecode(string code, out SubmissionPayload payload, out string error)
        {
            payload = null;
            error = null;

            if (string.IsNullOrWhiteSpace(code))
            {
                error = "Submission code is empty.";
                return false;
            }

            var trimmed = code.Trim();
            if (!trimmed.StartsWith(CodePrefix, StringComparison.Ordinal))
            {
                error = $"Submission code must start with '{CodePrefix}'.";
                return false;
            }

            try
            {
                var compressed = Convert.FromBase64String(trimmed.Substring(CodePrefix.Length));

                using (var input = new MemoryStream(compressed))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                using (var reader = new StreamReader(gzip, Encoding.UTF8))
                {
                    payload = JsonConvert.DeserializeObject<SubmissionPayload>(reader.ReadToEnd());
                }
            }
            catch (Exception exception)
            {
                error = $"Submission code could not be read: {exception.Message}";
                return false;
            }

            if (payload == null)
            {
                error = "Submission code decoded to nothing.";
                return false;
            }

            return true;
        }
    }
}
