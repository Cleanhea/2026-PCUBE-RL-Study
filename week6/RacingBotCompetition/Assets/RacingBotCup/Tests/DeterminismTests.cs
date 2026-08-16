using System.Collections.Generic;
using NUnit.Framework;
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
                var track = TrackGenerator.Generate(seed);
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
