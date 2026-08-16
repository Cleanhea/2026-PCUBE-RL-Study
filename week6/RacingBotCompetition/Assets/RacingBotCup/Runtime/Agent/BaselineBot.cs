using RacingBotCup.Racing;
using RacingBotCup.Vehicle;
using UnityEngine;

namespace RacingBotCup.Agent
{
    /// <summary>
    /// The rule-based reference driver: pure pursuit along the centreline with curvature-based
    /// braking. Its lap time is the denominator of every score, so <c>S = 1.0</c> means "as fast as
    /// this bot" (기획서 §6).
    ///
    /// It is tuned to finish, not to win. A baseline that occasionally spins would make the whole
    /// leaderboard noisy, because every competitor's score on that track would move with it.
    /// </summary>
    public sealed class BaselineBot : MonoBehaviour, IDriver
    {
        /// <summary>Bump this if the bot's behaviour changes — old scores are no longer comparable.</summary>
        public const string Version = "1.0.0";

        const float k_MinLookahead = 8f;
        const float k_MaxLookahead = 35f;
        const float k_LookaheadPerSpeed = 0.7f;

        /// <summary>Lateral acceleration the bot is willing to ask of the tyres, in m/s².</summary>
        const float k_LateralAccelBudget = 8.5f;

        const float k_MinTargetSpeed = 7f;
        const float k_MaxTargetSpeed = 42f;
        const float k_ThrottleGain = 0.45f;
        const float k_BrakeGain = 0.30f;
        const float k_ScanStep = 5f;

        RaceContext m_Context;
        CarController m_Car;

        public string DriverName => $"BaselineBot v{Version}";

        public void Bind(RaceContext context)
        {
            m_Context = context;
            m_Car = context.Car;
        }

        public void BeginRun()
        {
        }

        public void EndRun()
        {
            m_Car.SetInput(0f, 0f);
        }

        public void Tick()
        {
            if (m_Context == null)
            {
                return;
            }

            var projection = m_Context.Projection;
            var speed = m_Car.ForwardSpeed;

            m_Car.SetInput(
                ComputeSteer(projection, speed),
                ComputeThrottle(projection, speed));
        }

        float ComputeSteer(Track.TrackModel.Projection projection, float speed)
        {
            var lookahead = Mathf.Clamp(
                k_MinLookahead + speed * k_LookaheadPerSpeed,
                k_MinLookahead,
                k_MaxLookahead);

            var target = m_Context.Track.SampleAtDistance(projection.Distance + lookahead).Position;
            var local = m_Car.transform.InverseTransformPoint(target);

            // Guard the denominator: if the target has ended up behind the car (a spin), steering
            // hard toward it is exactly the recovery we want.
            var angle = Mathf.Atan2(local.x, Mathf.Max(0.5f, local.z)) * Mathf.Rad2Deg;
            return Mathf.Clamp(angle / CarSpec.MaxSteerAngle, -1f, 1f);
        }

        float ComputeThrottle(Track.TrackModel.Projection projection, float speed)
        {
            var targetSpeed = ComputeTargetSpeed(projection, speed);
            var error = targetSpeed - speed;

            return error >= 0f
                ? Mathf.Clamp01(error * k_ThrottleGain)
                : Mathf.Clamp(error * k_BrakeGain, -1f, 0f);
        }

        /// <summary>
        /// Looks ahead far enough to stop for the tightest corner in braking range, then picks the
        /// speed that corner allows: v = sqrt(a_lat / |curvature|).
        /// </summary>
        float ComputeTargetSpeed(Track.TrackModel.Projection projection, float speed)
        {
            var scanDistance = Mathf.Clamp(20f + speed * 1.6f, 30f, 140f);
            var steps = Mathf.CeilToInt(scanDistance / k_ScanStep);

            var limit = k_MaxTargetSpeed;

            for (var i = 0; i <= steps; i++)
            {
                var ahead = projection.Distance + i * k_ScanStep;
                var curvature = Mathf.Abs(m_Context.Track.AverageCurvature(ahead, k_ScanStep));

                if (curvature < 1e-4f)
                {
                    continue;
                }

                var cornerSpeed = Mathf.Sqrt(k_LateralAccelBudget / curvature);

                // Corners further away can be reached at higher speed — there is still room to
                // brake. This is a coarse stand-in for a proper braking-distance solve, and it
                // errs on the cautious side, which is what a reference bot should do.
                var reachDistance = i * k_ScanStep;
                var allowed = Mathf.Sqrt(cornerSpeed * cornerSpeed + 2f * k_LateralAccelBudget * reachDistance);

                limit = Mathf.Min(limit, allowed);
            }

            // Off the road there is far less grip, so the bot backs off rather than sliding further.
            if (!projection.IsOnRoad)
            {
                limit = Mathf.Min(limit, 14f);
            }

            return Mathf.Clamp(limit, k_MinTargetSpeed, k_MaxTargetSpeed);
        }
    }
}
