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
        const string k_ChecksumSalt = "RacingBotCup/2026/score-v1";

        public const string CodePrefix = "RBC1:";

        public static string ToJson(SubmissionPayload payload)
        {
            return JsonConvert.SerializeObject(payload, Formatting.Indented);
        }

        /// <summary>
        /// Canonical text the checksum is taken over. Order and formatting must never drift.
        ///
        /// Floats use fixed six-decimal formatting rather than round-trip ("R") notation so the
        /// leaderboard script can rebuild this exact string in JavaScript — round-trip formatting
        /// differs between the two languages and would fail verification on valid submissions.
        /// </summary>
        const string k_FloatFormat = "F6";

        /// <summary>
        /// Formats a float the way the leaderboard script necessarily will: it never sees the
        /// float bits, only whatever decimal text JSON carried them as, parsed back as a JS
        /// double. <c>value.ToString("F6")</c> rounds the *true* float32 value instead, which for
        /// anything needing more than float32's ~7 significant digits (a two-digit lap time to
        /// six decimals is nine) lands on different last-digit noise than the double reconstructed
        /// from that same value's round-trip text — silently breaking every such checksum. Routing
        /// through the round-trip string first, the same value JSON would carry, keeps both sides
        /// rounding the identical number.
        /// </summary>
        static string FormatFloat(float value, CultureInfo culture)
        {
            var roundTripText = value.ToString("R", culture);
            return double.Parse(roundTripText, culture).ToString(k_FloatFormat, culture);
        }

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
                builder.Append(FormatFloat(payload.Score.Total, culture)).Append('|');
                builder.Append(FormatFloat(payload.Score.CompletionRate, culture)).Append('|');
                builder.Append(FormatFloat(payload.Score.ScoreStdDev, culture)).Append('|');
                builder.Append(payload.Score.TrackCount.ToString(culture)).Append('|');

                foreach (var track in payload.Score.Tracks)
                {
                    builder.Append(track.Seed.ToString(culture)).Append(',');
                    builder.Append(FormatFloat(track.BaselineTime, culture)).Append(',');
                    builder.Append(FormatFloat(track.AgentTime, culture)).Append(',');
                    builder.Append(((int)track.AgentStatus).ToString(culture)).Append(',');
                    builder.Append(((int)track.BaselineStatus).ToString(culture)).Append(',');
                    builder.Append(FormatFloat(track.Score, culture)).Append(';');
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
