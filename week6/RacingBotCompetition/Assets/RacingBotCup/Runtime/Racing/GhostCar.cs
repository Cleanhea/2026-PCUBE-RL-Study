using UnityEngine;

namespace RacingBotCup.Racing
{
    /// <summary>
    /// Marks a car as scenery: it laps the circuit for the viewer's benefit and nothing reads its
    /// result.
    ///
    /// The chase camera and the HUD need this because a ghost driven by a policy is otherwise
    /// indistinguishable from the competitor's own car — both are racing, and both carry a
    /// <see cref="RacingBotCup.Agent.RacerAgent"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GhostCar : MonoBehaviour
    {
    }
}
