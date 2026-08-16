using UnityEngine;

namespace RacingBotCup.Racing
{
    /// <summary>
    /// Publishes one car's run time so the on-screen HUD can show it.
    ///
    /// It sits on the car rather than on the harness because with ten environments running at once
    /// there is no single clock to read — the display follows whichever car the camera is on, and
    /// asks that car what its time is. <see cref="RacerRig.PlaceOnTrack"/> attaches one to every
    /// car it puts on a circuit, so training and evaluation both get it for free.
    ///
    /// Read-only by construction: it holds a reference to the context the harness already updates
    /// and never advances anything itself.
    /// </summary>
    public sealed class RaceClock : MonoBehaviour
    {
        RaceContext m_Context;

        /// <summary>Seconds since the car left the start line, or 0 when it is not running.</summary>
        public float Elapsed => m_Context?.ElapsedTime ?? 0f;

        /// <summary>The most recent completed lap, or 0 if none has been set yet.</summary>
        public float LastLap { get; private set; }

        public bool HasLap => LastLap > 0f;

        /// <summary>
        /// True once the run currently loaded has crossed the line. Distinct from
        /// <see cref="HasLap"/>: training starts a fresh episode after every lap, so the clock has a
        /// previous time to show while the car in front of you is mid-lap again.
        /// </summary>
        public bool IsFinished { get; private set; }

        public void Bind(RaceContext context)
        {
            // Training tears the run down the instant the lap lands — end of episode, new circuit,
            // rebind — all inside one physics step, so no frame ever renders with the finished
            // context still attached. Catching it on the way out is the only place it is visible.
            LatchLap();

            m_Context = context;
            IsFinished = false;
        }

        void LateUpdate()
        {
            // Evaluation is the other way round: the context is simply left alone once the car has
            // crossed the line, so the flag stays set and this picks it up.
            LatchLap();
        }

        void LatchLap()
        {
            if (m_Context == null || !m_Context.LapCompletedThisTick)
            {
                return;
            }

            LastLap = m_Context.ElapsedTime;
            IsFinished = true;
        }

        /// <summary>Formats seconds as m:ss.mmm — how a lap time is read at a circuit.</summary>
        public static string Format(float seconds)
        {
            if (seconds <= 0f)
            {
                return "--:--.---";
            }

            var minutes = Mathf.FloorToInt(seconds / 60f);
            var remainder = seconds - minutes * 60f;
            return $"{minutes}:{remainder:00.000}";
        }
    }
}
