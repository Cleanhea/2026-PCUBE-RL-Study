using System.Collections.Generic;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using RacingBotCup.Eval;
using RacingBotCup.Track;
using UnityEngine;

namespace RacingBotCup.Tests
{
    /// <summary>
    /// Guards the property the whole competition rests on: a seed names one track, everywhere,
    /// every time. 기획서 §12 lists non-reproducible evaluation as the top risk, and a generator
    /// that quietly drifts would invalidate every score already on the board.
    /// </summary>
    public sealed class DeterminismTests
    {
        static readonly int[] k_Seeds = { 1017, 2451, 9302, 12960 };

        [Test]
        public void SameSeedProducesTheSameTrack([ValueSource(nameof(k_Seeds))] int seed)
        {
            var first = TrackGenerator.Generate(seed);
            var second = TrackGenerator.Generate(seed);

            Assert.AreEqual(first.Model.TotalLength, second.Model.TotalLength, 1e-4f, "Track length drifted.");
            Assert.AreEqual(first.Attempts, second.Attempts, "Generation took a different path.");
            Assert.AreEqual(first.Model.Samples.Count, second.Model.Samples.Count);

            for (var i = 0; i < first.Model.Samples.Count; i += 37)
            {
                Assert.AreEqual(
                    first.Model.Samples[i].Position,
                    second.Model.Samples[i].Position,
                    $"Centreline sample {i} moved between generations.");
            }
        }

        [Test]
        public void DifferentSeedsProduceDifferentTracks()
        {
            var a = TrackGenerator.Generate(1017);
            var b = TrackGenerator.Generate(1018);

            Assert.AreNotEqual(a.Model.TotalLength, b.Model.TotalLength,
                "Neighbouring seeds collapsed to the same layout — the PRNG is not decorrelating.");
        }

        [Test]
        public void ProjectionRecoversTheDistanceItWasSampledAt()
        {
            var track = TrackGenerator.Generate(2451);
            var model = track.Model;

            for (var fraction = 0f; fraction < 1f; fraction += 0.05f)
            {
                var distance = fraction * model.TotalLength;
                var sample = model.SampleAtDistance(distance);
                var projection = model.Project(sample.Position);

                Assert.AreEqual(distance, projection.Distance, 1.5f,
                    $"Projecting a point taken from the centreline at {distance:F1} m came back elsewhere.");
                Assert.Less(Mathf.Abs(projection.Lateral), 0.5f,
                    "A centreline point should project to roughly zero lateral offset.");
            }
        }

        [Test]
        public void CentrelineIsInsideTheRoadAndOffsetPointsAreNot()
        {
            var model = TrackGenerator.Generate(5236).Model;
            var sample = model.SampleAtDistance(model.TotalLength * 0.25f);

            Assert.IsTrue(model.SampleSurface(sample.Position).OnTrack);

            var wellOutside = sample.Position + sample.Right * (sample.Width * 2f);
            Assert.IsFalse(model.SampleSurface(wellOutside).OnTrack);
        }

        [Test]
        public void SubmissionCodeSurvivesARoundTrip()
        {
            var payload = BuildPayload();
            var code = SubmissionCodec.Encode(payload);

            Assert.IsTrue(SubmissionCodec.TryDecode(code, out var decoded, out var error), error);
            Assert.AreEqual(payload.ParticipantId, decoded.ParticipantId);
            Assert.AreEqual(payload.Score.Total, decoded.Score.Total, 1e-6f);
            Assert.IsTrue(SubmissionCodec.VerifyChecksum(decoded, out var verifyError), verifyError);
        }

        /// <summary>
        /// The leaderboard script never sees a C# float's bits — only whatever decimal text
        /// JSON carried it as, read back as a JS double. float32 holds ~7 significant digits;
        /// a two-digit lap time needs nine to survive six-decimal formatting, so a native
        /// <c>float.ToString("F6")</c> and the same value's double-precision JSON reading can
        /// round differently in the last digit. 62.3456789f/58.7654321f sit past that cliff
        /// deliberately — 62.5f/58.25f in <see cref="BuildPayload"/> are exact binary fractions
        /// and would never have caught this.
        /// </summary>
        [Test]
        public void ChecksumVerifiesUnderADoubleOnlyJsonReader()
        {
            var tracks = new List<TrackScore>
            {
                ScoreAggregator.BuildTrackScore(
                    1017,
                    new RunResult { Status = RunStatus.Finished, Time = 62.3456789f },
                    new RunResult { Status = RunStatus.Finished, Time = 58.7654321f }),
            };

            var payload = new SubmissionPayload
            {
                ParticipantId = "tester",
                SubmittedAtUtc = "2026-08-15T00:00:00Z",
                SeedSetId = "public-v1",
                Seeds = new[] { 1017 },
                Score = ScoreAggregator.Aggregate(tracks),
                Integrity = new IntegrityBlock
                {
                    CarSpecHash = Vehicle.CarSpec.SpecHash,
                    TrackGeneratorVersion = TrackGenerator.Version,
                    ScoreModuleVersion = ScoreAggregator.Version,
                    BaselineBotVersion = Agent.BaselineBot.Version,
                    RulesHash = RaceRules.Hash,
                    AgentHash = "test-agent/heuristic",
                },
            };
            SubmissionCodec.Sign(payload);

            var claimed = payload.Integrity.Checksum;
            var recomputed = RecomputeChecksumAsADoubleOnlyReaderWould(payload);

            Assert.AreEqual(claimed, recomputed,
                "A double-precision reader (i.e. the leaderboard's JavaScript) could not " +
                "reproduce the signed checksum — the float32/double formatting drift is back.");
        }

        /// <summary>
        /// Stands in for the leaderboard's Google Apps Script: rebuilds the canonical form from
        /// the payload's own JSON text with every number read as a double, exactly as
        /// <c>JSON.parse</c> would, instead of through a float-typed field. Intentionally
        /// independent of <c>SubmissionCodec</c>'s internals so it can't share a bug with them.
        /// </summary>
        static string RecomputeChecksumAsADoubleOnlyReaderWould(SubmissionPayload payload)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var json = Newtonsoft.Json.Linq.JObject.Parse(SubmissionCodec.ToJson(payload));
            var score = json["Score"];
            var integrity = json["Integrity"];

            var canon = new System.Text.StringBuilder();
            canon.Append((string)json["SchemaVersion"]).Append('|');
            canon.Append((string)json["ParticipantId"]).Append('|');
            canon.Append((string)json["SeedSetId"]).Append('|');
            canon.Append((string)json["SubmittedAtUtc"]).Append('|');
            canon.Append(((double)score["Total"]).ToString("F6", culture)).Append('|');
            canon.Append(((double)score["CompletionRate"]).ToString("F6", culture)).Append('|');
            canon.Append(((double)score["ScoreStdDev"]).ToString("F6", culture)).Append('|');
            canon.Append((int)score["TrackCount"]).Append('|');

            foreach (var track in score["Tracks"])
            {
                canon.Append((int)track["Seed"]).Append(',');
                canon.Append(((double)track["BaselineTime"]).ToString("F6", culture)).Append(',');
                canon.Append(((double)track["AgentTime"]).ToString("F6", culture)).Append(',');
                canon.Append((int)track["AgentStatus"]).Append(',');
                canon.Append((int)track["BaselineStatus"]).Append(',');
                canon.Append(((double)track["Score"]).ToString("F6", culture)).Append(';');
            }

            canon.Append('|');
            canon.Append((string)integrity["CarSpecHash"]).Append(',');
            canon.Append((string)integrity["TrackGeneratorVersion"]).Append(',');
            canon.Append((string)integrity["ScoreModuleVersion"]).Append(',');
            canon.Append((string)integrity["BaselineBotVersion"]).Append(',');
            canon.Append((string)integrity["RulesHash"]).Append(',');
            canon.Append((string)integrity["AgentHash"]);
            canon.Append('|').Append("RacingBotCup/2026/score-v1");

            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var digest = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(canon.ToString()));
                var hex = new System.Text.StringBuilder(24);
                for (var i = 0; i < 12; i++)
                {
                    hex.Append(digest[i].ToString("x2", culture));
                }

                return hex.ToString();
            }
        }

        [Test]
        public void EditingAScoreBreaksTheChecksum()
        {
            var payload = BuildPayload();
            Assert.IsTrue(SubmissionCodec.VerifyChecksum(payload, out _));

            payload.Score.Total += 0.25f;

            Assert.IsFalse(SubmissionCodec.VerifyChecksum(payload, out var error));
            Assert.IsNotNull(error);
        }

        [Test]
        public void SealedHashesAreStableWithinARun()
        {
            Assert.AreEqual(Vehicle.CarSpec.SpecHash, Vehicle.CarSpec.SpecHash);
            Assert.AreEqual(RaceRules.Hash, RaceRules.Hash);
            Assert.IsNotEmpty(Vehicle.CarSpec.SpecHash);
            Assert.IsNotEmpty(RaceRules.Hash);
        }

        [Test]
        public void EvaluationSeedsAllProduceValidCircuits()
        {
            // The scored circuits have to be clean: a layout that fell back to a relaxed shape is
            // not the one the seed is supposed to name, and everyone is racing it.
            foreach (var seed in SeedSet.Default().Seeds)
            {
                // Matches EvaluationRunner exactly: hazard sections are always off for the official
                // seed set, so this is the circuit competitors are actually scored on.
                var track = TrackGenerator.Generate(seed, enableHazardSections: false);
                Assert.IsTrue(track.FullyValid,
                    $"Evaluation seed {seed} produced a relaxed layout: {track.ValidationNote}");
            }
        }

        static SubmissionPayload BuildPayload()
        {
            var tracks = new List<TrackScore>
            {
                ScoreAggregator.BuildTrackScore(
                    1017,
                    new RunResult { Status = RunStatus.Finished, Time = 62.5f },
                    new RunResult { Status = RunStatus.Finished, Time = 58.25f }),
            };

            var payload = new SubmissionPayload
            {
                ParticipantId = "tester",
                SubmittedAtUtc = "2026-08-15T00:00:00Z",
                SeedSetId = "public-v1",
                Seeds = new[] { 1017 },
                Score = ScoreAggregator.Aggregate(tracks),
                Integrity = new IntegrityBlock
                {
                    CarSpecHash = Vehicle.CarSpec.SpecHash,
                    TrackGeneratorVersion = TrackGenerator.Version,
                    ScoreModuleVersion = ScoreAggregator.Version,
                    BaselineBotVersion = Agent.BaselineBot.Version,
                    RulesHash = RaceRules.Hash,
                    AgentHash = "test-agent/heuristic",
                },
            };

            SubmissionCodec.Sign(payload);
            return payload;
        }
    }
}
