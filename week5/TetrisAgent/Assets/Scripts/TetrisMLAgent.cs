using System.Collections;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;
using Tetris;

/// <summary>
/// SAC로 테트리스를 학습하는 ML-Agents 에이전트.
/// 로직/렌더는 TetrisCore/TetrisEnv/TetrisBoard가 담당하고, 이 클래스는
/// (관측 → 액션 → 보상) 인터페이스만 ML-Agents에 연결한다.
///
/// 결정 1회 = 조각 1개 배치. Decision Requester(Period 1)와 함께 쓴다.
/// </summary>
public class TetrisMLAgent : Agent
{
    [Header("(선택) 관전용 보드 — 비우면 렌더 안 함")]
    [SerializeField] TetrisBoard viewBoard;

    [Header("환경")]
    [Tooltip("0 = 매 에피소드 랜덤 시드. 고정하면 디버깅 시 재현 가능.")]
    [SerializeField] int seed = 0;

    [Header("보상 (SAC는 스케일에 민감 — 대략 [-1,1] 유지)")]
    [Tooltip("조각 하나를 놓을 때마다의 생존 보상. 게임을 오래 끌게 만든다.")]
    [SerializeField] float surviveReward = 0.01f;
    [Tooltip("라인 클리어 보상 [0,1,2,3,4]줄. 테트리스(4줄)를 강하게 우대(비선형).")]
    [SerializeField] float[] lineRewards = { 0f, 0.10f, 0.30f, 0.60f, 1.0f };
    [Tooltip("게임오버 / 배치 불가(사실상 탑아웃) 기본 페널티.")]
    [SerializeField] float gameOverPenalty = -1f;
    [Tooltip("이 조각 수 안에 게임오버되면 '빨리 죽을수록' 추가 벌점(0에 가까울수록 최대). 빠른 자살 전략 차단.")]
    [SerializeField] int earlyGameOverSteps = 20;
    [Tooltip("빠른 게임오버 추가 벌점의 최대 크기(0개 배치 시). 클수록 일찍 죽는 걸 강하게 억제.")]
    [SerializeField] float earlyGameOverWeight = 2f;

    [Header("관전 애니메이션 (추론 시 조각 이동을 눈으로 보기)")]
    [Tooltip("켜면 추론 중 조각을 목표 위치까지 한 칸씩 움직여 보여준다. 학습(Default)엔 영향 없음.")]
    [SerializeField] bool animatePlacement = false;
    [Tooltip("애니메이션 한 스텝(회전/이동/낙하 한 칸) 사이 대기(초). 작을수록 빠름.")]
    [SerializeField] float animStepDelay = 0.05f;

    [Header("보드 형태 shaping (배치 후 지표 '증가분'만 벌점)")]
    [Tooltip("새로 생긴 구멍당 벌점. 구멍은 테트리스에서 치명적이라 가장 크게.")]
    [SerializeField] float holeWeight = 0.05f;
    [Tooltip("최대 높이 증가당 벌점.")]
    [SerializeField] float heightWeight = 0.01f;
    [Tooltip("열 높이 편차(bumpiness) 증가당 벌점.")]
    [SerializeField] float bumpinessWeight = 0.01f;

    TetrisEnv env;
    int prevHoles, prevMaxHeight, prevBumpiness;
    int piecesThisEpisode;   // 이번 에피소드에 성공적으로 놓은 조각 수 (빠른 게임오버 판정용)
    readonly int[] clearCounts = new int[5]; // 이번 에피소드 라인 클리어 횟수: [1]싱글 [2]더블 [3]트리플 [4]테트리스

    public override void Initialize()
    {
        env = new TetrisEnv(seed);
        if (viewBoard != null)
        {
            viewBoard.Bind(env.Core); // 이 에이전트 보드를 화면에 렌더(입력·중력 없이)
            // 추론(Inference Only) 모드면 HUD 에 표시.
            var bp = GetComponent<BehaviorParameters>();
            if (bp != null && bp.BehaviorType == BehaviorType.InferenceOnly)
                viewBoard.statusLabel = "INFERENCE";
        }

        // 애니메이션 모드는 배치가 끝날 때마다 직접 다음 결정을 요청한다(자체 페이싱).
        // 그래서 주기적으로 결정을 던지는 DecisionRequester 는 꺼서 중복 요청을 막는다.
        if (animatePlacement)
        {
            var dr = GetComponent<DecisionRequester>();
            if (dr != null) dr.enabled = false;
        }
    }

    public override void OnEpisodeBegin()
    {
        env.Reset();
        // 빈 보드 기준선(모두 0). shaping은 여기서부터의 '증가분'으로 계산한다.
        prevHoles = prevMaxHeight = prevBumpiness = 0;
        piecesThisEpisode = 0;
        System.Array.Clear(clearCounts, 0, clearCounts.Length);

        if (animatePlacement) RequestDecision(); // 자체 페이싱: 첫(또는 리셋 후) 조각 결정 요청
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // 보드 점유 200 + 현재 조각 one-hot 7 + 다음 조각 one-hot 7 = 214.
        // Behavior Parameters의 Space Size를 이 길이와 반드시 일치시킬 것.
        foreach (float v in env.GetObservation())
            sensor.AddObservation(v);
    }

    public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
    {
        // 배치 불가능한 (열,회전)을 후보에서 제거 → 학습이 훨씬 수월.
        // 단, 전부 마스킹하면 ML-Agents가 예외를 던진다(유효 액션 0개 금지).
        // 유효 액션이 하나도 없으면(사실상 탑아웃) 마스킹을 건너뛰고,
        // OnActionReceived에서 무효 배치를 게임오버로 처리한다.
        int validCount = 0;
        for (int a = 0; a < TetrisEnv.ActionCount; a++)
            if (env.IsValid(a)) validCount++;
        if (validCount == 0) return;

        for (int a = 0; a < TetrisEnv.ActionCount; a++)
            if (!env.IsValid(a)) actionMask.SetActionEnabled(0, a, false);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        int a = actions.DiscreteActions[0];

        // 관전 모드: 즉시 배치 대신 목표까지 한 칸씩 움직여 보여준다(보상 로직 생략 — 추론 시각화 전용).
        if (animatePlacement) { StartCoroutine(AnimatePlacement(a)); return; }

        var r = env.Step(a); // ← 조각 한 개 배치 = 한 스텝

        // 무효 배치는 마스킹이 있으면 정상적으론 안 나온다. 나왔다면 놓을 곳이 없다는 뜻(탑아웃).
        if (!r.valid)
        {
            ApplyGameOverPenalty();
            EndEpisode();
            return;
        }

        piecesThisEpisode++;

        // 1) 생존 + 라인 클리어(비선형)
        AddReward(surviveReward);
        AddReward(lineRewards[Mathf.Clamp(r.linesCleared, 0, lineRewards.Length - 1)]);
        if (r.linesCleared > 0) clearCounts[Mathf.Clamp(r.linesCleared, 1, 4)]++;

        // 2) 보드 형태 shaping: 배치 후 지표의 '증가분'만 벌점(줄면 벌점 없음).
        ComputeBoardFeatures(out int holes, out int maxHeight, out int bumpiness);
        AddReward(-holeWeight * Mathf.Max(0, holes - prevHoles));
        AddReward(-heightWeight * Mathf.Max(0, maxHeight - prevMaxHeight));
        AddReward(-bumpinessWeight * Mathf.Max(0, bumpiness - prevBumpiness));
        prevHoles = holes; prevMaxHeight = maxHeight; prevBumpiness = bumpiness;

        if (r.gameOver)
        {
            ApplyGameOverPenalty();
            EndEpisode();
        }
    }

    // 관전용: 조각을 목표 위치까지 한 칸씩 움직여 배치. 끝나면 다음 결정을 요청해 루프를 잇는다.
    IEnumerator AnimatePlacement(int action)
    {
        if (!env.Core.BeginPlacement(action % TetrisEnv.Columns, action / TetrisEnv.Columns))
        {
            EndEpisode();   // 배치 불가(탑아웃) → OnEpisodeBegin 이 다음 결정 요청
            yield break;
        }
        var wait = new WaitForSeconds(animStepDelay);
        while (!env.Core.StepPlacement()) yield return wait;

        if (env.Core.GameOver) EndEpisode();  // OnEpisodeBegin 이 다음 결정 요청
        else RequestDecision();               // 다음 조각 결정
    }

    // 게임오버 벌점 = 기본 + '빨리 죽을수록' 커지는 추가 벌점.
    // 놓은 조각 수가 earlyGameOverSteps 미만이면, 적을수록 최대 earlyGameOverWeight 까지 선형 가중.
    // → 한 줄로 빨리 쌓아 자살하는 지역 최적(누적 shaping 벌점 회피)을 차단한다.
    void ApplyGameOverPenalty()
    {
        // 에피소드 종료 시점의 레벨/라인을 텐서보드에 기록(Metrics/ 아래에 뜬다).
        var stats = Academy.Instance.StatsRecorder;
        stats.Add("Tetris/Level", env.Core.Level);
        stats.Add("Tetris/Lines", env.Core.Lines);
        // 에피소드당 종류별 라인 클리어 횟수 → 텐서보드에서 네 그래프를 겹쳐 비교.
        stats.Add("Clears/Single", clearCounts[1]);
        stats.Add("Clears/Double", clearCounts[2]);
        stats.Add("Clears/Triple", clearCounts[3]);
        stats.Add("Clears/Tetris", clearCounts[4]);
        AddReward(gameOverPenalty);
        if (earlyGameOverSteps > 0 && piecesThisEpisode < earlyGameOverSteps)
            AddReward(-earlyGameOverWeight * (earlyGameOverSteps - piecesThisEpisode) / (float)earlyGameOverSteps);
    }

    // 열별 높이(맨 위 블록 +1), 구멍 수(윗 블록 밑의 빈칸), 최대 높이, bumpiness(인접 높이차 합).
    void ComputeBoardFeatures(out int holes, out int maxHeight, out int bumpiness)
    {
        var grid = env.Core.Grid;
        holes = 0; maxHeight = 0; bumpiness = 0;
        int prevH = 0;
        for (int x = 0; x < TetrisCore.Width; x++)
        {
            int top = -1;
            for (int y = TetrisCore.Height - 1; y >= 0; y--)
                if (grid[x, y] != TetrisCore.Empty) { top = y; break; }

            int h = top + 1;                 // 이 열의 높이
            if (h > maxHeight) maxHeight = h;
            for (int y = 0; y < top; y++)    // top 아래의 빈칸 = 구멍
                if (grid[x, y] == TetrisCore.Empty) holes++;
            if (x > 0) bumpiness += Mathf.Abs(h - prevH);
            prevH = h;
        }
    }

    // 사람이 키로 테스트할 때만(Behavior Type = Heuristic Only). 학습엔 불필요 — 첫 유효 액션 선택.
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var d = actionsOut.DiscreteActions;
        for (int a = 0; a < TetrisEnv.ActionCount; a++)
            if (env.IsValid(a)) { d[0] = a; return; }
        d[0] = 0;
    }
}
