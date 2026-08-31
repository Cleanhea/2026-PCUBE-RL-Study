using UnityEngine;
using UnityEngine.InputSystem;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

public enum Team
{
    Blue = 0,
    Red = 1
}

// 앞으로 적용할 것: bservations / rewards / policy
// 현재 적용된 것: movement, kick physics
public class AgentSoccer : Agent
{
    public enum Position
    {
        Striker,
        Goalie,
        Generic
    }

    // Set per-player in the prefab/scene (blue vs red, and role).
    public Team team;
    public Position position;

    float m_KickPower;
    const float k_Power = 2000f;
    const float k_KeeperPassiveKick = 0.5f;
    const float k_BallTouchReward = 0.02f;
    const float k_KeeperClearReward = 0.04f;
    const float k_BallApproachReward = 0.01f;
    const float k_TouchRewardBudget = 0.15f;
    const float k_KeeperClearBudget = 0.15f;
    const float k_ExistentialBudget = 0.18f;       // striker pays it, keeper earns it
    const float k_AheadOfBallBudget = 0.02f;       // per excess unit over a full episode
    const float k_AheadOfBallFreeMargin = 2f;      // allow useful
    const float k_KeeperMaxStrayEpisodePenalty = 0.36f;

    float m_LateralSpeed;
    float m_ForwardSpeed;

    float m_ExistentialReward;
    float m_AheadOfBallPenalty;
    float m_KeeperStrayPenalty;

    float m_TouchRewardRemaining;
    float m_ClearRewardRemaining;

    float m_PrevBallDist;
    bool m_HasPrevBallDist;
    private bool m_InitialPosCaptured = false;

    [HideInInspector]
    public Rigidbody agentRb;
    [SerializeField] SoccerSettings m_SoccerSettings;

    [SerializeField]
    SoccerEnvController m_SoccerEnvController;

    public Vector3 initialPos;
    public float rotSign;

    public override void Initialize()
    {
        if (!m_InitialPosCaptured)
        {
            initialPos = transform.position;
            m_InitialPosCaptured = true;
        }

        if (team == Team.Blue)
        {
            rotSign = 1f;
        }
        else
        {
            rotSign = -1f;
        }

        if (position == Position.Goalie)
        {
            m_LateralSpeed = 1.0f;
            m_ForwardSpeed = 1.0f;
        }
        else if (position == Position.Striker)
        {
            m_LateralSpeed = 0.3f;
            m_ForwardSpeed = 1.3f;
        }
        else
        {
            m_LateralSpeed = 0.3f;
            m_ForwardSpeed = 1.0f;
        }

        agentRb = GetComponent<Rigidbody>();
        agentRb.maxAngularVelocity = 500;

        var maxSteps = Mathf.Max(1, m_SoccerEnvController.MaxEnvironmentSteps);

        m_ExistentialReward = k_ExistentialBudget / maxSteps;
        m_AheadOfBallPenalty = k_AheadOfBallBudget / maxSteps;
        m_KeeperStrayPenalty = k_KeeperMaxStrayEpisodePenalty / maxSteps;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        sensor.AddObservation(transform.InverseTransformDirection(agentRb.linearVelocity));
        if (position == Position.Goalie && m_SoccerEnvController != null)
        {
            var attackDir = team == Team.Blue ? 1f : -1f;
            // + = advanced upfield off my line, - = tucked in behind it.
            sensor.AddObservation(
                attackDir * (transform.position.x - initialPos.x) / SoccerEnvController.PitchHalfX);
            // + = ball is still upfield of me, - = ball has got in behind me.
            sensor.AddObservation(
                attackDir * (m_SoccerEnvController.ball.transform.position.x - transform.position.x)
                / (2f * SoccerEnvController.PitchHalfX));
        }
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var d = actionsOut.DiscreteActions;
        var kb = Keyboard.current;
        if (kb == null) return;

        d[0] = kb.wKey.isPressed ? 1 : kb.sKey.isPressed ? 2 : 0;
        d[1] = kb.eKey.isPressed ? 1 : kb.qKey.isPressed ? 2 : 0;
        d[2] = kb.dKey.isPressed ? 1 : kb.aKey.isPressed ? 2 : 0;
    }

    public override void OnEpisodeBegin()
    {
        agentRb.linearVelocity = Vector3.zero;
        agentRb.angularVelocity = Vector3.zero;
        ResetEpisodeBudgets();
    }

    public void ResetEpisodeBudgets()
    {
        m_TouchRewardRemaining = k_TouchRewardBudget;
        m_ClearRewardRemaining = k_KeeperClearBudget;
        m_HasPrevBallDist = false;
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        AddReward(position == Position.Goalie ? m_ExistentialReward : -m_ExistentialReward);

        if (position == Position.Goalie)
        {
            var normalizedStray = Mathf.Clamp01(
                Mathf.Abs(transform.position.x - initialPos.x) / SoccerEnvController.PitchHalfX);
            AddReward(-m_KeeperStrayPenalty * normalizedStray);
        }

        if (position == Position.Striker && m_SoccerEnvController != null)
        {
            var attackDir = team == Team.Blue ? 1f : -1f;
            var ahead = attackDir *
                (transform.position.x - m_SoccerEnvController.ball.transform.position.x);
            var excessAhead = ahead - k_AheadOfBallFreeMargin;
            if (excessAhead > 0f)
            {
                AddReward(-m_AheadOfBallPenalty * excessAhead);
            }

            // Reward closing distance to the ball and penalise opening it symmetrically.
            // Without terminal settlement, the episode total is proportional to how much
            // closer the striker actually finished, rather than just its initial distance.
            var ballDist = Vector3.Distance(transform.position, m_SoccerEnvController.ball.transform.position);
            if (m_HasPrevBallDist)
            {
                AddReward((m_PrevBallDist - ballDist) * k_BallApproachReward);
            }
            m_PrevBallDist = ballDist;
            m_HasPrevBallDist = true;
        }
        MoveAgent(actions.DiscreteActions);
    }

    void FixedUpdate()
    {

    }

    void MoveAgent(ActionSegment<int> act)
    {

        var dirToGo = Vector3.zero;
        float rotationInput = 0f;
        m_KickPower = 0f;

        switch (act[0])
        {
            case 1: dirToGo += transform.forward * m_ForwardSpeed; m_KickPower = 1; break;
            case 2: dirToGo += transform.forward * -m_ForwardSpeed; break;
        }

        switch (act[1])
        {
            case 1: dirToGo += transform.right * m_LateralSpeed; break;
            case 2: dirToGo += transform.right * -m_LateralSpeed; break;
        }

        switch (act[2])
        {
            case 1: rotationInput = 1f; break;
            case 2: rotationInput = -1f; break;
        }

        Quaternion rotation = Quaternion.Euler(
        0f,
        rotationInput * 100f * Time.fixedDeltaTime,
        0f);

        agentRb.MoveRotation(
            agentRb.rotation * rotation
        );
        agentRb.AddForce(dirToGo * m_SoccerSettings.agentRunSpeed, ForceMode.VelocityChange);
        float maxSpeed = 8f;

        Vector3 horizontalVelocity = new Vector3(agentRb.linearVelocity.x, 0f, agentRb.linearVelocity.z);

        if (horizontalVelocity.magnitude > maxSpeed)
        {
            horizontalVelocity = horizontalVelocity.normalized * maxSpeed;

            agentRb.linearVelocity = new Vector3(
                horizontalVelocity.x,
                agentRb.linearVelocity.y,
                horizontalVelocity.z
            );
        }
    }

    /// <summary>
    /// Used to provide a "kick" to the ball.
    /// </summary>
    void OnCollisionEnter(Collision c)
    {
        var force = k_Power * (position == Position.Goalie
            ? Mathf.Max(m_KickPower, k_KeeperPassiveKick)
            : m_KickPower);
        if (c.gameObject.CompareTag("ball"))
        {
            var dir = c.contacts[0].point - transform.position;
            dir = dir.normalized;
            c.gameObject.GetComponent<Rigidbody>().AddForce(dir * force);


            if (position == Position.Striker && m_KickPower > 0f)
            {
                float attackSign = team == Team.Blue ? 1f : -1f;
                float forwardKick = attackSign * dir.x;

                if (forwardKick > 0f)
                {
                    // Spend from the episode budget so dribbling cannot out-earn a goal.
                    var touchReward = Mathf.Min(k_BallTouchReward * forwardKick, m_TouchRewardRemaining);
                    m_TouchRewardRemaining -= touchReward;
                    AddReward(touchReward);
                }
                else
                {
                    AddReward(k_BallTouchReward * forwardKick * 0.5f);
                }
            }
            else if (position == Position.Goalie)
            {
                var awayFromGoal = team == Team.Blue ? 1f : -1f;
                var clearanceQuality = Mathf.Clamp(awayFromGoal * dir.x, -1f, 1f);

                if (clearanceQuality < 0f)
                {
                    // Knocking the ball back towards your own goal is wrong anywhere on the
                    // pitch, is applied in full, and costs no budget.
                    AddReward(k_KeeperClearReward * clearanceQuality);
                }
                else
                {
                    // Only pay for clearances made while actually defending. The old gate
                    // asked where the *ball* was, not where the keeper was, so a keeper
                    // could dribble the length of its own half farming an uncapped 0.04 a
                    // contact -- and once past halfway the group progress reward took over.
                    var arenaCenterX = m_SoccerEnvController.transform.position.x;
                    var keeperInOwnHalf = team == Team.Blue
                        ? transform.position.x <= arenaCenterX
                        : transform.position.x >= arenaCenterX;
                    var ballX = c.gameObject.transform.position.x;
                    var ballInOwnHalf = team == Team.Blue
                        ? ballX <= arenaCenterX
                        : ballX >= arenaCenterX;

                    if (keeperInOwnHalf && ballInOwnHalf)
                    {
                        var clearReward = Mathf.Min(k_KeeperClearReward * clearanceQuality, m_ClearRewardRemaining);
                        m_ClearRewardRemaining -= clearReward;
                        AddReward(clearReward);
                    }
                }
            }
        }
    }
}