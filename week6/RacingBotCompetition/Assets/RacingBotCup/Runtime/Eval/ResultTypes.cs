using System.Collections.Generic;

namespace RacingBotCup.Eval
{
    public enum RunStatus
    {
        Finished = 0,

        /// <summary>Four wheels off the road for longer than the rules allow.</summary>
        DidNotFinish = 1,

        /// <summary>Still running when the clock ran out.</summary>
        TimedOut = 2,
    }

    /// <summary>One car, one track, one lap.</summary>
    public sealed class RunResult
    {
        public int Seed;
        public string Driver;
        public RunStatus Status;
        public float Time;
        public int CheckpointsPassed;
        public int CheckpointCount;
        public float DistanceTraveled;

        public bool Completed => Status == RunStatus.Finished;
    }

    /// <summary>The pair of runs behind one track's score.</summary>
    public sealed class TrackScore
    {
        public int Seed;
        public float BaselineTime;
        public float AgentTime;
        public RunStatus AgentStatus;
        public RunStatus BaselineStatus;
        public float Score;

        /// <summary>
        /// False when the baseline itself failed to set a time. Such a track has no denominator,
        /// so it is reported rather than quietly scored as zero for everyone.
        /// </summary>
        public bool IsScorable => BaselineStatus == RunStatus.Finished && BaselineTime > 0f;
    }

    public sealed class AggregateScore
    {
        public float Total;
        public float CompletionRate;
        public float ScoreStdDev;
        public int TrackCount;
        public List<TrackScore> Tracks = new List<TrackScore>();
    }

    /// <summary>
    /// What actually gets submitted. Scores plus enough provenance for the leaderboard to tell
    /// whether two entries were produced by the same rules.
    /// </summary>
    public sealed class SubmissionPayload
    {
        public const string CurrentSchemaVersion = "1.0";

        public string SchemaVersion = CurrentSchemaVersion;
        public string ParticipantId;
        public string SubmittedAtUtc;
        public string SeedSetId;
        public int[] Seeds;
        public AggregateScore Score;
        public IntegrityBlock Integrity;
        public string Note;
    }

    /// <summary>
    /// Version stamps for every sealed component, plus a checksum over the payload.
    ///
    /// This is tamper-EVIDENT, not tamper-proof. Anything computed on a competitor's own machine
    /// can be forged by that competitor, and no client-side scheme changes that. Its job is to make
    /// an accidental edit — a hand-tweaked result.json, a stale build, a modified CarSpec — visible
    /// to the organisers rather than silently ranked. The real defence against a determined cheat
    /// is the private finals track (기획서 §8).
    /// </summary>
    public sealed class IntegrityBlock
    {
        public string CarSpecHash;
        public string TrackGeneratorVersion;
        public string ScoreModuleVersion;
        public string BaselineBotVersion;
        public string RulesHash;
        public string AgentHash;
        public string Checksum;
    }
}
