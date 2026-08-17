using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RacingBotCup.Track;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Networking;

namespace RacingBotCup.Eval
{
    /// <summary>One circuit's outcome, shown as a row in the Inspector.</summary>
    [Serializable]
    public sealed class TrackResultRow
    {
        public int Seed;
        public float BaselineTime;
        public float AgentTime;
        public RunStatus Status;
        public float Score;

        public override string ToString()
        {
            return Status == RunStatus.Finished
                ? $"seed {Seed}: S={Score:F3}  bot {BaselineTime:F2}s / me {AgentTime:F2}s"
                : $"seed {Seed}: S=0  bot {BaselineTime:F2}s / me {Status}";
        }
    }

    /// <summary>
    /// The competitor's whole evaluation workflow in one component: point it at an agent prefab and
    /// a model, run, read the results, submit.
    ///
    /// Everything else has a sensible default already filled in, so the two fields that matter are
    /// the two at the top.
    /// </summary>
    public sealed class RaceEvaluator : MonoBehaviour
    {
        [Header("제출 정보")]
        [Tooltip("리더보드에 표시될 이름")]
        [SerializeField] string m_ParticipantId = "";

        [Tooltip("한 줄 설명 (선택)")]
        [SerializeField] string m_Note = "";

        [Header("내 에이전트")]
        [Tooltip("RacerAgent를 상속한 컴포넌트가 붙은 프리팹")]
        [SerializeField] GameObject m_AgentPrefab;

        [Tooltip("학습된 .onnx 모델. 비워 두면 키보드로 조작됩니다")]
        [SerializeField] ModelAsset m_Model;

        [Header("고스트 (같이 달리는 반투명 차, 꺼도 됨)")]
        [Tooltip("기준으로 삼을 차 한 대. 기본 봇이거나, 내가 등록한 예전 모델이거나, 없음입니다.\n" +
                 "점수에는 반영되지 않습니다")]
        [SerializeField] GhostConfig m_Ghost = new GhostConfig();

        [Header("설정 (기본값 그대로 두세요)")]
        [SerializeField] GameObject m_CarPrefab;
        [SerializeField] TextAsset m_SeedSetAsset;
        [SerializeField] TrackMaterials m_Materials = new TrackMaterials();
        [SerializeField] TrackPropCatalogue m_Props = new TrackPropCatalogue();
        [SerializeField] SubmissionConfig m_SubmissionConfig;


        [SerializeField] bool m_RunOnStart;

        [Header("결과")]
        [SerializeField] string m_Status = "idle";
        [SerializeField] float m_Total;
        [SerializeField] float m_CompletionRate;
        [SerializeField] float m_ScoreStdDev;
        [SerializeField] List<TrackResultRow> m_Results = new List<TrackResultRow>();

        SubmissionPayload m_Payload;
        string m_Code;

        public string Status => m_Status;

        public bool IsRunning { get; private set; }

        public bool HasResults => m_Payload != null;

        public string SubmissionCode => m_Code;

        public SubmissionConfig SubmissionConfig => m_SubmissionConfig;

        public string ParticipantId => m_ParticipantId;

        public IReadOnlyList<TrackResultRow> Results => m_Results;

        void Start()
        {
            if (m_RunOnStart)
            {
                RunEvaluation();
            }
        }

        /// <summary>Runs the full evaluation, one circuit at a time, paced to real time so it can
        /// always be watched start to finish.</summary>
        public void RunEvaluation()
        {
            if (IsRunning)
            {
                return;
            }

            StartCoroutine(RunRoutine());
        }

        IEnumerator RunRoutine()
        {
            IsRunning = true;
            m_Payload = null;
            m_Code = null;
            m_Results.Clear();
            m_Status = "starting";

            var seeds = SeedSet.Default();
            if (m_SeedSetAsset != null && !SeedSet.TryParse(m_SeedSetAsset.text, out seeds, out var seedError))
            {
                Fail(seedError);
                yield break;
            }

            var runner = gameObject.AddComponent<EvaluationRunner>();
            var request = new EvaluationRunner.Request
            {
                CarPrefab = m_CarPrefab,
                AgentPrefab = m_AgentPrefab,
                Model = m_Model,
                Seeds = seeds,
                Materials = m_Materials,
                Props = m_Props,
                Ghost = m_Ghost,
                ParticipantId = m_ParticipantId,
                Note = m_Note,
            };

            yield return runner.Run(
                request,
                Accept,
                progress => m_Status = progress.ToString(),
                Fail);

            Destroy(runner);
            IsRunning = false;
        }

        void Accept(SubmissionPayload payload)
        {
            m_Payload = payload;
            m_Code = SubmissionCodec.Encode(payload);

            var score = payload.Score;
            m_Total = score.Total;
            m_CompletionRate = score.CompletionRate;
            m_ScoreStdDev = score.ScoreStdDev;

            m_Results.Clear();
            foreach (var track in score.Tracks)
            {
                m_Results.Add(new TrackResultRow
                {
                    Seed = track.Seed,
                    BaselineTime = track.BaselineTime,
                    AgentTime = track.AgentTime,
                    Status = track.AgentStatus,
                    Score = track.Score,
                });
            }

            m_Status = $"done — total {score.Total:F3}";
            WriteToDisk();

            Debug.Log($"[RacingBotCup] Evaluation complete — total {score.Total:F3}, " +
                      $"completion {score.CompletionRate:P0}, spread {score.ScoreStdDev:F3} " +
                      $"over {score.TrackCount} circuits.");
        }

        void Fail(string message)
        {
            m_Status = "failed";
            IsRunning = false;
            Debug.LogError($"[RacingBotCup] {message}");
        }

        // ------------------------------------------------------------------
        // Submission
        // ------------------------------------------------------------------

        /// <summary>
        /// Posts the result to the leaderboard form. Returns the request so the caller can wait on
        /// it — the Inspector button pumps it from the editor, play mode yields on it.
        /// </summary>
        public UnityWebRequest BuildSubmission(out string error)
        {
            error = null;

            if (m_Payload == null || string.IsNullOrEmpty(m_Code))
            {
                error = "No result to submit. Run an evaluation first.";
                return null;
            }

            if (string.IsNullOrWhiteSpace(m_ParticipantId))
            {
                error = "Set your GitHub ID before submitting.";
                return null;
            }

            if (m_SubmissionConfig == null)
            {
                error = "No SubmissionConfig assigned.";
                return null;
            }

            var reason = m_SubmissionConfig.Explain();
            if (reason != null)
            {
                error = reason;
                return null;
            }

            return FormSubmitter.Build(m_SubmissionConfig, m_ParticipantId, m_Code);
        }

        /// <summary>Loads a result written by an earlier run, so submitting still works after play mode.</summary>
        public bool TryLoadLatestResult(out string error)
        {
            error = null;
            var directory = OutputDirectory;

            if (!Directory.Exists(directory))
            {
                error = "No saved results found.";
                return false;
            }

            FileInfo newest = null;
            foreach (var file in new DirectoryInfo(directory).GetFiles("*.json"))
            {
                if (newest == null || file.LastWriteTimeUtc > newest.LastWriteTimeUtc)
                {
                    newest = file;
                }
            }

            if (newest == null)
            {
                error = "No saved results found.";
                return false;
            }

            try
            {
                var payload = Newtonsoft.Json.JsonConvert.DeserializeObject<SubmissionPayload>(
                    File.ReadAllText(newest.FullName));

                if (payload?.Score == null)
                {
                    error = $"{newest.Name} could not be read.";
                    return false;
                }

                Accept(payload);
                m_Status = $"loaded {newest.Name}";
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        public static string OutputDirectory =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "EvaluationResults"));

        void WriteToDisk()
        {
            try
            {
                Directory.CreateDirectory(OutputDirectory);

                var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var baseName = string.IsNullOrWhiteSpace(m_ParticipantId) ? "result" : m_ParticipantId.Trim();

                File.WriteAllText(Path.Combine(OutputDirectory, $"{baseName}-{stamp}.json"),
                    SubmissionCodec.ToJson(m_Payload));
                File.WriteAllText(Path.Combine(OutputDirectory, $"{baseName}-{stamp}.txt"), m_Code);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[RacingBotCup] Could not write results: {exception.Message}");
            }
        }
    }
}
