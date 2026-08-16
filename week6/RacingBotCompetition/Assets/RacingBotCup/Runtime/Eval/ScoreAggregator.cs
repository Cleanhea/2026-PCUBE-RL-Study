using System;
using System.Collections.Generic;
using UnityEngine;

namespace RacingBotCup.Eval
{
    /// <summary>
    /// ★ 봉인 모듈 — SEALED SCORING MODULE ★
    ///
    /// The single place where lap times become a score. Because the competition is scored on each
    /// competitor's own machine, this file is the one part of the project everyone must be running
    /// unmodified for the leaderboard to mean anything. Do not edit it; <see cref="Version"/> is
    /// stamped into every submission so a changed formula is visible.
    ///
    /// Deliberately free of Unity scene state — every method is a pure function of its arguments,
    /// which makes the whole scoring rule unit-testable without entering play mode.
    ///
    /// The formula (기획서 §6):
    /// <code>
    ///   S_t   = clamp(T_baseline / T_agent, 0, 2.0)   // finished
    ///   S_t   = 0                                     // DNF or timeout
    ///   Total = mean(S_t)
    /// </code>
    /// A score of 1.0 means "matched the baseline bot". DNF scoring zero is what makes a reliable
    /// policy beat a fast-but-fragile one, which is the generalisation pressure the whole event is
    /// built around.
    /// </summary>
    public static class ScoreAggregator
    {
        public const string Version = "1.0.0";

        /// <summary>A run twice as fast as the baseline earns the maximum. Beyond that adds nothing.</summary>
        public const float MaxTrackScore = 2.0f;

        /// <summary>
        /// Score for a single track. Returns 0 for anything that is not a clean finish, and for a
        /// track whose baseline never set a time.
        /// </summary>
        public static float ComputeTrackScore(float baselineTime, float agentTime, RunStatus agentStatus)
        {
            if (agentStatus != RunStatus.Finished)
            {
                return 0f;
            }

            if (baselineTime <= 0f || agentTime <= 0f || float.IsNaN(agentTime) || float.IsNaN(baselineTime))
            {
                return 0f;
            }

            return Mathf.Clamp(baselineTime / agentTime, 0f, MaxTrackScore);
        }

        public static TrackScore BuildTrackScore(int seed, RunResult baseline, RunResult agent)
        {
            var score = new TrackScore
            {
                Seed = seed,
                BaselineTime = baseline.Time,
                BaselineStatus = baseline.Status,
                AgentTime = agent.Time,
                AgentStatus = agent.Status,
            };

            score.Score = score.IsScorable
                ? ComputeTrackScore(baseline.Time, agent.Time, agent.Status)
                : 0f;

            return score;
        }

        /// <summary>
        /// Combines per-track scores. Only scorable tracks contribute, so one broken track cannot
        /// drag the whole field's totals down.
        /// </summary>
        public static AggregateScore Aggregate(IReadOnlyList<TrackScore> tracks)
        {
            var result = new AggregateScore();

            if (tracks == null || tracks.Count == 0)
            {
                return result;
            }

            var scorable = new List<TrackScore>(tracks.Count);
            foreach (var track in tracks)
            {
                result.Tracks.Add(track);
                if (track.IsScorable)
                {
                    scorable.Add(track);
                }
            }

            result.TrackCount = scorable.Count;
            if (scorable.Count == 0)
            {
                return result;
            }

            var sum = 0f;
            var finished = 0;
            foreach (var track in scorable)
            {
                sum += track.Score;
                if (track.AgentStatus == RunStatus.Finished)
                {
                    finished++;
                }
            }

            result.Total = sum / scorable.Count;
            result.CompletionRate = finished / (float)scorable.Count;

            var variance = 0f;
            foreach (var track in scorable)
            {
                var delta = track.Score - result.Total;
                variance += delta * delta;
            }

            result.ScoreStdDev = Mathf.Sqrt(variance / scorable.Count);
            return result;
        }

        /// <summary>
        /// Ranking order: total score, then completion rate, then consistency (기획서 §6 동점 처리).
        /// Negative means <paramref name="a"/> ranks ahead of <paramref name="b"/>.
        /// </summary>
        public static int CompareForRanking(AggregateScore a, AggregateScore b)
        {
            if (a == null && b == null)
            {
                return 0;
            }

            if (a == null)
            {
                return 1;
            }

            if (b == null)
            {
                return -1;
            }

            var byTotal = b.Total.CompareTo(a.Total);
            if (byTotal != 0)
            {
                return byTotal;
            }

            var byCompletion = b.CompletionRate.CompareTo(a.CompletionRate);
            if (byCompletion != 0)
            {
                return byCompletion;
            }

            // Lower spread wins: a policy that is evenly good generalises better than one that is
            // brilliant on three tracks and hopeless on the rest.
            return a.ScoreStdDev.CompareTo(b.ScoreStdDev);
        }

        /// <summary>Efficiency award metric (기획서 §9): score per log of training steps.</summary>
        public static float ComputeEfficiency(float total, long trainingSteps)
        {
            if (trainingSteps <= 1)
            {
                return 0f;
            }

            return total / Mathf.Log(trainingSteps);
        }
    }
}
