using RacingBotCup.Eval;
using RacingBotCup.Racing;
using RacingBotCup.Track;
using RacingBotCup.Vehicle;
using Unity.MLAgents;
using UnityEngine;

namespace RacingBotCup.Agent
{
    /// <summary>
    /// Runs training episodes on the circuit and car already sitting in the scene.
    ///
    /// It no longer builds anything: the track and the car are scene objects you can select and
    /// inspect before pressing Play. All this does is decide when an episode is over and put the
    /// car back on the start line — plus, by default, roll a new circuit each time.
    ///
    /// Rolling a new circuit every episode is the point of the whole competition. A policy trained
    /// on one layout learns that layout; the score is for generalisation, so the ground has to keep
    /// moving.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class TrainingArena : MonoBehaviour
    {
        [Header("씬 참조")]
        [SerializeField] TrackInstance m_Track;

        [Tooltip("씬에 놓인 차. 자식에 RacerAgent가 붙어 있어야 합니다")]
        [SerializeField] CarController m_Car;

        [Header("커리큘럼")]
        [Tooltip("에피소드마다 새 트랙을 뽑습니다. 끄면 씬의 시드를 계속 씁니다")]
        [SerializeField] bool m_RandomizeEachEpisode = true;

        [Tooltip("연습용 시드 대역. 평가 시드와 겹치지 않게 두세요")]
        [SerializeField] int m_SeedMin;

        [SerializeField] int m_SeedMax = 100000;

        [SerializeField] float m_MaxEpisodeSeconds = 200f;

        RacerAgent m_Agent;
        RaceContext m_Context;
        DeterministicRandom m_Random;
        bool m_Ready;

        void Awake()
        {
            m_Random = new DeterministicRandom(System.Environment.TickCount ^ GetInstanceID());
            Time.fixedDeltaTime = CarSpec.FixedDeltaTime;
        }

        void Start()
        {
            if (!Resolve())
            {
                enabled = false;
                return;
            }

            m_Agent.Configure(manualStepping: false);
            m_Agent.EpisodeBegun += OnEpisodeBegun;

            if (m_Agent.GetComponent<DecisionRequester>() == null)
            {
                var requester = m_Agent.gameObject.AddComponent<DecisionRequester>();
                requester.DecisionPeriod = RaceRules.DecisionPeriod;
                requester.TakeActionsBetweenDecisions = false;
            }

            ResetCar();
            m_Ready = true;
        }

        bool Resolve()
        {
            if (m_Track == null)
            {
                m_Track = FindFirstObjectByType<TrackInstance>();
            }

            if (m_Car == null)
            {
                m_Car = FindFirstObjectByType<CarController>();
            }

            if (m_Track == null || m_Car == null)
            {
                Debug.LogError("[RacingBotCup] Training scene needs a TrackInstance and a car. " +
                               "Run RacingBotCup > Build Scenes to recreate it.");
                return false;
            }

            m_Agent = m_Car.GetComponentInChildren<RacerAgent>();
            if (m_Agent == null)
            {
                Debug.LogError($"[RacingBotCup] No RacerAgent found under '{m_Car.name}'. " +
                               "Drag your agent prefab in as a child of the car.");
                return false;
            }

            return true;
        }

        void OnDestroy()
        {
            if (m_Agent != null)
            {
                m_Agent.EpisodeBegun -= OnEpisodeBegun;
            }
        }

        void OnEpisodeBegun()
        {
            if (!m_Ready)
            {
                return;
            }

            if (m_RandomizeEachEpisode)
            {
                m_Track.Randomize(m_Random.Range(m_SeedMin, Mathf.Max(m_SeedMin + 1, m_SeedMax)));
            }

            ResetCar();
        }

        void ResetCar()
        {
            var rig = new RacerRig(m_Car, m_Agent);
            m_Context = rig.PlaceOnTrack(m_Track.Model);
            Physics.SyncTransforms();
        }

        void FixedUpdate()
        {
            if (!m_Ready || m_Context == null)
            {
                return;
            }

            m_Context.Refresh(Time.fixedDeltaTime);

            if (m_Context.LapCompletedThisTick)
            {
                RecordEpisodeStats(lapCompleted: true);
                m_Agent.OnLapCompleted(m_Context.ElapsedTime);
                m_Agent.EndEpisode();
                return;
            }

            if (m_Context.OffTrackDuration >= RaceRules.OffTrackDnfSeconds ||
                m_Context.ElapsedTime >= m_MaxEpisodeSeconds)
            {
                RecordEpisodeStats(lapCompleted: false);
                m_Agent.OnRunFailed();
                m_Agent.EndEpisode();
            }
        }

        /// <summary>
        /// Reports the final state of every training episode to TensorBoard.
        ///
        /// The clear time is only logged when the lap actually finished. Logging every episode's
        /// elapsed time mixed real lap times in with runs that went off track after two seconds or
        /// sat at the timeout ceiling, so the curve tracked how the car failed rather than how fast
        /// it was. Progress and the clear rate still go in on every episode — the clear rate is what
        /// says whether a falling clear time means a faster car or just an easier sample of laps.
        /// </summary>
        void RecordEpisodeStats(bool lapCompleted)
        {
            var stats = Academy.Instance.StatsRecorder;
            stats.Add("Episode/Progress", m_Context.Checkpoints.Progress);
            stats.Add("Episode/ClearRate", lapCompleted ? 1f : 0f);

            if (lapCompleted)
            {
                stats.Add("Episode/ClearSeconds", m_Context.ElapsedTime);
            }
        }
    }
}
