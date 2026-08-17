using RacingBotCup.Racing;
using RacingBotCup.Track;
using RacingBotCup.Vehicle;
using UnityEngine;

namespace RacingBotCup.Agent
{
    /// <summary>
    /// The rule-based reference driver: pure pursuit along the centreline with curvature-based
    /// braking, a shrunk lookahead through tight corners, and a dodge — with a matching speed
    /// check — around anything tagged <see cref="TrackTags.Obstacle"/> in its way. Its lap time is
    /// the denominator of every score, so <c>S = 1.0</c> means "as fast as this bot" (기획서 §6).
    ///
    /// It is tuned to finish, not to win. A baseline that occasionally spins would make the whole
    /// leaderboard noisy, because every competitor's score on that track would move with it. That
    /// now has to hold on hazard circuits too (기획서 §5, SharpHairpin/ObstacleStraight/RampCorner) —
    /// evaluation stopped excluding them once the seed set was reset for the new competition.
    /// </summary>
    public sealed class BaselineBot : MonoBehaviour, IDriver
    {
        /// <summary>Bump this if the bot's behaviour changes — old scores are no longer comparable.</summary>
        public const string Version = "1.1.0";

        const float k_MinLookahead = 8f;
        const float k_MaxLookahead = 35f;
        const float k_LookaheadPerSpeed = 0.7f;

        /// <summary>
        /// Caps how far ahead the pursuit target may sit as a fraction of the upcoming corner's own
        /// radius. An 8 m lookahead already spans most of a 7 m-radius SharpHairpin chord to chord,
        /// so aiming at its far end reads the bend as far shallower than it is and steers nowhere
        /// near sharply enough to hold the road through it.
        /// </summary>
        const float k_TightCurveLookaheadFactor = 0.85f;
        const float k_MinTightLookahead = 4f;

        /// <summary>
        /// Only curvature tighter than this triggers the lookahead shrink above. Strictly below the
        /// ordinary Hairpin floor (11 m, see <see cref="TrackParams.HairpinMinRadius"/>) so only a
        /// SharpHairpin's own 7 m floor ever qualifies — every other corner already validated fine at
        /// the plain speed-based lookahead, and shrinking it there too turned out to make ordinary
        /// Hairpins and RampCorners run wide instead of the SharpHairpins it was meant to fix.
        /// </summary>
        const float k_TightCurveRadiusThreshold = 10f;

        /// <summary>Lateral acceleration the bot is willing to ask of the tyres, in m/s².</summary>
        const float k_LateralAccelBudget = 8.5f;

        /// <summary>
        /// Fraction of <see cref="k_LateralAccelBudget"/> actually planned for through a corner.
        /// The unmargined budget put SharpHairpin's cornering speed right at the tyres' limit with
        /// nothing held back for the steering rate limit's lag onto full lock or a tick of control
        /// noise — either one was enough to run wide off a 7 m-radius bend.
        /// </summary>
        const float k_CorneringSafetyMargin = 0.8f;

        const float k_MinTargetSpeed = 5f;
        const float k_MaxTargetSpeed = 42f;
        const float k_ThrottleGain = 0.45f;
        const float k_BrakeGain = 0.30f;
        const float k_ScanStep = 5f;

        /// <summary>How far ahead an obstacle still gets dodged rather than ignored.</summary>
        const float k_ObstacleLookahead = 35f;

        /// <summary>How far the obstacle-detecting sphere cast sweeps ahead.</summary>
        const float k_ObstacleCastDistance = 55f;

        /// <summary>
        /// Clearance aimed for beyond the obstacle's own footprint. <see cref="TrackObstacleBuilder"/>
        /// never places one without at least <see cref="TrackObstacleBuilder.MinPassableGap"/> of
        /// clear road on the far side, so aiming this far past its centre lands inside that
        /// guaranteed-clear span without swinging all the way to the kerb — which would risk cutting
        /// off whichever obstacle comes next in an alternating-lane run.
        /// </summary>
        const float k_ObstacleClearance = TrackObstacleBuilder.ObstacleFootprint + 1.1f;

        const float k_ObstacleKerbMargin = 0.6f;

        /// <summary>
        /// Speed cap once an obstacle is within dodge range. Steering alone can outrun the car's own
        /// lateral grip: coming into an ObstacleStraight still at a straight's cruising speed left too
        /// little time to cross a couple of metres sideways before reaching it, clipping the prop
        /// square on rather than sliding past it.
        /// </summary>
        const float k_ObstacleApproachSpeed = 16f;

        static readonly RaycastHit[] s_ObstacleHits = new RaycastHit[16];

        /// <summary>Below this the car counts as "not really moving" for stuck detection.</summary>
        const float k_StuckSpeedThreshold = 0.6f;

        /// <summary>How long it may sit near-stationary before the recovery manoeuvre kicks in.</summary>
        const float k_StuckPatience = 1.5f;

        /// <summary>How long the recovery manoeuvre reverses for.</summary>
        const float k_RecoveryDuration = 1.2f;

        const float k_RecoveryThrottle = -0.7f;

        RaceContext m_Context;
        CarController m_Car;

        float m_StuckTimer;
        float m_RecoveryTimer;

        public string DriverName => $"BaselineBot v{Version}";

        public void Bind(RaceContext context)
        {
            m_Context = context;
            m_Car = context.Car;
        }

        public void BeginRun()
        {
            m_StuckTimer = 0f;
            m_RecoveryTimer = 0f;
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
            var dt = Time.fixedDeltaTime;

            // A wedged-against-an-obstacle car has no forward move that gets it out — pure pursuit
            // only ever asks for more throttle into the same wall. Backing off for a beat and
            // re-approaching (with a fresh read on whatever it is stuck against) recovers it instead
            // of sitting at the timeout for the rest of the run.
            if (m_RecoveryTimer > 0f)
            {
                m_RecoveryTimer -= dt;
                m_Car.SetInput(Mathf.Clamp(-projection.Lateral * 0.15f, -1f, 1f), k_RecoveryThrottle);
                return;
            }

            if (Mathf.Abs(speed) < k_StuckSpeedThreshold)
            {
                m_StuckTimer += dt;
                if (m_StuckTimer > k_StuckPatience)
                {
                    m_RecoveryTimer = k_RecoveryDuration;
                    m_StuckTimer = 0f;
                    m_Car.SetInput(0f, k_RecoveryThrottle);
                    return;
                }
            }
            else
            {
                m_StuckTimer = 0f;
            }

            var hasObstacle = TryFindObstacleAhead(projection, out var obstacleDistance, out var obstacleLateral);

            m_Car.SetInput(
                ComputeSteer(projection, speed, hasObstacle, obstacleDistance, obstacleLateral),
                ComputeThrottle(projection, speed, hasObstacle));
        }

        float ComputeSteer(
            TrackModel.Projection projection, float speed, bool hasObstacle, float obstacleDistance, float obstacleLateral)
        {
            var speedLookahead = Mathf.Clamp(
                k_MinLookahead + speed * k_LookaheadPerSpeed,
                k_MinLookahead,
                k_MaxLookahead);

            var curvatureAhead = Mathf.Abs(m_Context.Track.AverageCurvature(projection.Distance, speedLookahead));
            var radiusAhead = curvatureAhead > 1e-4f ? 1f / curvatureAhead : float.MaxValue;
            var lookahead = radiusAhead < k_TightCurveRadiusThreshold
                ? Mathf.Clamp(
                    Mathf.Min(speedLookahead, k_TightCurveLookaheadFactor * radiusAhead),
                    k_MinTightLookahead,
                    k_MaxLookahead)
                : speedLookahead;

            var target = m_Context.Track.SampleAtDistance(projection.Distance + lookahead).Position;

            if (hasObstacle)
            {
                var atObstacle = m_Context.Track.SampleAtDistance(obstacleDistance);
                var limit = Mathf.Max(0f, atObstacle.Width * 0.5f - k_ObstacleKerbMargin);
                var sign = obstacleLateral >= 0f ? -1f : 1f;
                var avoidLateral = Mathf.Clamp(obstacleLateral + sign * k_ObstacleClearance, -limit, limit);
                target = atObstacle.Position + atObstacle.Right * avoidLateral;
            }

            var local = m_Car.transform.InverseTransformPoint(target);

            // Guard the denominator: if the target has ended up behind the car (a spin), steering
            // hard toward it is exactly the recovery we want.
            var angle = Mathf.Atan2(local.x, Mathf.Max(0.5f, local.z)) * Mathf.Rad2Deg;
            return Mathf.Clamp(angle / CarSpec.MaxSteerAngle, -1f, 1f);
        }

        /// <summary>
        /// The nearest <see cref="TrackTags.Obstacle"/>-tagged prop ahead of the car and actually on
        /// the road — a SharpHairpin's kerb fence sits outside the road's own half-width by design
        /// and must never trigger a dodge, so anything further off than that is ignored here.
        ///
        /// Swept with <c>SphereCast</c> rather than <c>OverlapSphere</c>: the Container prop variants
        /// use a non-convex <see cref="MeshCollider"/>, which Unity's overlap queries silently skip
        /// over (they require a convex collider) but its cast queries do not.
        /// </summary>
        bool TryFindObstacleAhead(TrackModel.Projection projection, out float distance, out float lateral)
        {
            distance = 0f;
            lateral = 0f;

            var origin = m_Context.Track.SampleAtDistance(projection.Distance);
            var castRadius = Mathf.Max(1f, origin.Width * 0.5f);

            var hitCount = Physics.SphereCastNonAlloc(
                origin.Position + Vector3.up * 0.5f, castRadius, origin.Forward, s_ObstacleHits, k_ObstacleCastDistance);

            var bestAhead = float.MaxValue;
            var found = false;

            for (var i = 0; i < hitCount; i++)
            {
                var hit = s_ObstacleHits[i];
                if (hit.collider == null || !hit.collider.CompareTag(TrackTags.Obstacle))
                {
                    continue;
                }

                var obstacleProjection = m_Context.Track.Project(hit.point);
                var ahead = Mathf.Repeat(obstacleProjection.Distance - projection.Distance, m_Context.Track.TotalLength);

                if (ahead > k_ObstacleLookahead || Mathf.Abs(obstacleProjection.Lateral) > obstacleProjection.Width * 0.5f)
                {
                    continue;
                }

                if (ahead < bestAhead)
                {
                    bestAhead = ahead;
                    distance = obstacleProjection.Distance;
                    lateral = obstacleProjection.Lateral;
                    found = true;
                }
            }

            return found;
        }

        float ComputeThrottle(TrackModel.Projection projection, float speed, bool hasObstacle)
        {
            var targetSpeed = ComputeTargetSpeed(projection, speed);
            if (hasObstacle)
            {
                targetSpeed = Mathf.Min(targetSpeed, k_ObstacleApproachSpeed);
            }

            var error = targetSpeed - speed;

            return error >= 0f
                ? Mathf.Clamp01(error * k_ThrottleGain)
                : Mathf.Clamp(error * k_BrakeGain, -1f, 0f);
        }

        /// <summary>
        /// Looks ahead far enough to stop for the tightest corner in braking range, then picks the
        /// speed that corner allows: v = sqrt(a_lat·margin / |curvature|).
        /// </summary>
        float ComputeTargetSpeed(TrackModel.Projection projection, float speed)
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

                var cornerSpeed = Mathf.Sqrt(k_LateralAccelBudget * k_CorneringSafetyMargin / curvature);

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
