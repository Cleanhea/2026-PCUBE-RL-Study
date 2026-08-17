using System;
using Unity.InferenceEngine;
using UnityEngine;

namespace RacingBotCup.Eval
{
    /// <summary>Who drives the translucent car that laps alongside the competitor's own — or
    /// whether there is one at all.</summary>
    public enum GhostMode
    {
        /// <summary>The baseline bot, shown translucent as it sets the time the run is scored
        /// against.</summary>
        BaselineBot,

        /// <summary>A model the competitor supplies — usually an earlier version of their own policy,
        /// so a lap can be watched against the one it is meant to beat.</summary>
        Model,

        /// <summary>No ghost. The baseline still races — its time is still the score — it is just
        /// rendered as an ordinary car instead of a translucent one.</summary>
        None,
    }

    /// <summary>
    /// The ghost is one reference car, and this picks who is in it, if anyone.
    ///
    /// Only ever scenery: the baseline still runs whichever mode is chosen, because the score is
    /// measured against its time and nothing here is allowed to change that. What the mode changes
    /// is which car is on screen — with a model ghost the baseline runs unseen, so the view stays
    /// two cars rather than three; with no ghost the baseline runs in plain view instead of
    /// translucent.
    /// </summary>
    [Serializable]
    public sealed class GhostConfig
    {
        [Tooltip("기본 봇: 채점 기준인 베이스라인 봇이 반투명 고스트로 보입니다\n" +
                 "모델: 아래에 등록한 모델이 고스트로 보이고, 베이스라인은 화면에서 숨겨집니다\n" +
                 "없음: 고스트를 끕니다 — 베이스라인은 반투명 없이 평범한 차로 보입니다")]
        public GhostMode Mode = GhostMode.BaselineBot;

        [Tooltip("고스트가 사용할 .onnx 모델. 보통 내 예전 모델을 넣습니다 (모드가 Model일 때만 쓰입니다)")]
        public ModelAsset Model;

        [Tooltip("고스트 모델을 태울 에이전트 프리팹. 비워 두면 내 에이전트 프리팹을 그대로 씁니다.\n" +
                 "예전 모델이 지금과 다른 센서로 학습됐다면 그때의 프리팹을 여기에 지정하세요")]
        public GameObject AgentPrefab;

        /// <summary>
        /// True when the ghost needs a car of its own. A baseline ghost does not — the baseline is
        /// already on the circuit, and showing it translucent costs nothing extra.
        /// </summary>
        public bool NeedsOwnCar => Mode == GhostMode.Model && Model != null;
    }
}
