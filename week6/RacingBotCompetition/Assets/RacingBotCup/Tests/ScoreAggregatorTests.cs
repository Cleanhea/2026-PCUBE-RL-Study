using System.Collections.Generic;
using NUnit.Framework;
using RacingBotCup.Eval;

namespace RacingBotCup.Tests
{
    /// <summary>
    /// The scoring rule is the one thing every competitor must be running identically, so it gets
    /// tested directly. <see cref="ScoreAggregator"/> is pure, which is what lets these run without
    /// a scene, a car, or play mode.
    /// </summary>
    public sealed class ScoreAggregatorTests
    {
        static RunResult Run(RunStatus status, float time)
        {
            return new RunResult { Status = status, Time = time };
        }

        [Test]
        public void MatchingTheBaselineScoresOne()
        {
            Assert.AreEqual(1f, ScoreAggregator.ComputeTrackScore(60f, 60f, RunStatus.Finished), 1e-5f);
        }

        [Test]
        public void FasterThanBaselineScoresAboveOne()
        {
            Assert.AreEqual(1.5f, ScoreAggregator.ComputeTrackScore(90f, 60f, RunStatus.Finished), 1e-5f);
        }

        [Test]
        public void ScoreIsCappedAtTwo()
        {
            // Ten times faster than the bot still earns exactly the cap.
            Assert.AreEqual(
                ScoreAggregator.MaxTrackScore,
                ScoreAggregator.ComputeTrackScore(600f, 60f, RunStatus.Finished),
                1e-5f);
        }

        [Test]
        public void DidNotFinishScoresZeroRegardlessOfTime()
        {
            Assert.AreEqual(0f, ScoreAggregator.ComputeTrackScore(60f, 10f, RunStatus.DidNotFinish));
            Assert.AreEqual(0f, ScoreAggregator.ComputeTrackScore(60f, 10f, RunStatus.TimedOut));
        }

        [Test]
        public void DegenerateTimesScoreZeroRatherThanInfinity()
        {
            Assert.AreEqual(0f, ScoreAggregator.ComputeTrackScore(60f, 0f, RunStatus.Finished));
            Assert.AreEqual(0f, ScoreAggregator.ComputeTrackScore(0f, 60f, RunStatus.Finished));
        }

        [Test]
        public void TrackWithFailedBaselineIsExcludedFromTheAverage()
        {
            var good = ScoreAggregator.BuildTrackScore(1, Run(RunStatus.Finished, 60f), Run(RunStatus.Finished, 60f));
            var broken = ScoreAggregator.BuildTrackScore(
                2, Run(RunStatus.DidNotFinish, 0f), Run(RunStatus.Finished, 50f));

            var aggregate = ScoreAggregator.Aggregate(new List<TrackScore> { good, broken });

            Assert.IsFalse(broken.IsScorable);
            Assert.AreEqual(1, aggregate.TrackCount, "Only the scorable track should count.");
            Assert.AreEqual(1f, aggregate.Total, 1e-5f);
            Assert.AreEqual(2, aggregate.Tracks.Count, "Both tracks stay visible in the report.");
        }

        [Test]
        public void AggregateAveragesAndReportsCompletion()
        {
            var tracks = new List<TrackScore>
            {
                ScoreAggregator.BuildTrackScore(1, Run(RunStatus.Finished, 60f), Run(RunStatus.Finished, 60f)),
                ScoreAggregator.BuildTrackScore(2, Run(RunStatus.Finished, 60f), Run(RunStatus.Finished, 30f)),
                ScoreAggregator.BuildTrackScore(3, Run(RunStatus.Finished, 60f), Run(RunStatus.DidNotFinish, 0f)),
            };

            var aggregate = ScoreAggregator.Aggregate(tracks);

            // (1.0 + 2.0 + 0.0) / 3
            Assert.AreEqual(1f, aggregate.Total, 1e-5f);
            Assert.AreEqual(2f / 3f, aggregate.CompletionRate, 1e-5f);
            Assert.AreEqual(3, aggregate.TrackCount);
        }

        [Test]
        public void EmptyInputProducesZeroedResultRatherThanThrowing()
        {
            var aggregate = ScoreAggregator.Aggregate(new List<TrackScore>());

            Assert.AreEqual(0f, aggregate.Total);
            Assert.AreEqual(0, aggregate.TrackCount);
        }

        [Test]
        public void HigherTotalRanksFirst()
        {
            var strong = new AggregateScore { Total = 1.4f, CompletionRate = 1f, ScoreStdDev = 0.5f };
            var weak = new AggregateScore { Total = 1.2f, CompletionRate = 1f, ScoreStdDev = 0.1f };

            Assert.Less(ScoreAggregator.CompareForRanking(strong, weak), 0);
        }

        [Test]
        public void TiedTotalsAreBrokenByCompletionRate()
        {
            var reliable = new AggregateScore { Total = 1.2f, CompletionRate = 1f, ScoreStdDev = 0.4f };
            var patchy = new AggregateScore { Total = 1.2f, CompletionRate = 0.8f, ScoreStdDev = 0.1f };

            Assert.Less(ScoreAggregator.CompareForRanking(reliable, patchy), 0);
        }

        [Test]
        public void TiedTotalAndCompletionAreBrokenByConsistency()
        {
            var consistent = new AggregateScore { Total = 1.2f, CompletionRate = 1f, ScoreStdDev = 0.05f };
            var erratic = new AggregateScore { Total = 1.2f, CompletionRate = 1f, ScoreStdDev = 0.6f };

            Assert.Less(ScoreAggregator.CompareForRanking(consistent, erratic), 0);
        }
    }
}
