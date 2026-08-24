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
    float m_LateralSpeed;
    float m_ForwardSpeed;

    float m_ExistentialReward;

    [HideInInspector]
    public Rigidbody agentRb;
    SoccerSettings m_SoccerSettings;

    SoccerEnvController m_SoccerEnvController;

    // Reset anchor + spawn rotation, read by SoccerEnvController.ResetScene().
    public Vector3 initialPos;
    public float rotSign;

    public override void Initialize()
    {
        initialPos = transform.position;
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

        m_SoccerSettings = FindFirstObjectByType<SoccerSettings>();
        m_SoccerEnvController = FindFirstObjectByType<SoccerEnvController>();

        agentRb = GetComponent<Rigidbody>();
        agentRb.maxAngularVelocity = 500;

        m_ExistentialReward = 1f / m_SoccerEnvController.MaxEnvironmentSteps;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        sensor.AddObservation(agentRb.linearVelocity);
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
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        AddReward(position == Position.Goalie ? m_ExistentialReward : -m_ExistentialReward);
        
        if(position == Position.Goalie)
        {
            var strayed = Mathf.Abs(transform.position.x - initialPos.x);
            AddReward(-m_ExistentialReward * strayed);
        }

        else if(position == Position.Striker && m_SoccerEnvController != null)
        {
            var attackDir = team == Team.Blue ? 1f : -1f;
            var ahead = attackDir * (transform.position.x - m_SoccerEnvController.ball.transform.position.x);
            if (ahead > 0.5f)
            {
                AddReward(-m_ExistentialReward * ahead * 0.01f);
            }
        }
        MoveAgent(actions.DiscreteActions);
    }

    void FixedUpdate()
    {

    }

    // Keyboard control (every keyboard player shares these keys):
    // W/S = forward/back, Q/E = strafe left/right, A/D = rotate.
    void MoveAgent(ActionSegment<int> act)
    {

        var dirToGo = Vector3.zero;
        var rotateDir = Vector3.zero;
        m_KickPower = 0f;

        switch (act[0])
        {
            case 1: dirToGo = transform.forward * m_ForwardSpeed; m_KickPower = 1; break;
            case 2: dirToGo = transform.forward * -m_ForwardSpeed; break;
        }

        switch (act[1])
        {
            case 1: dirToGo = transform.right * m_LateralSpeed; break;
            case 2: dirToGo = transform.right * -m_LateralSpeed; break;
        }

        switch (act[2])
        {
            case 1: rotateDir = transform.up * 1f; break;
            case 2: rotateDir = transform.up * -1f; break;
        }

        transform.Rotate(rotateDir, Time.deltaTime * 100f);
        agentRb.AddForce(dirToGo * m_SoccerSettings.agentRunSpeed, ForceMode.VelocityChange);
    }

    /// <summary>
    /// Used to provide a "kick" to the ball.
    /// </summary>
    void OnCollisionEnter(Collision c)
    {
        var force = k_Power * m_KickPower;
        if (position == Position.Goalie)
        {
            force = k_Power;
        }
        if (c.gameObject.CompareTag("ball"))
        {
            var dir = c.contacts[0].point - transform.position;
            dir = dir.normalized;
            c.gameObject.GetComponent<Rigidbody>().AddForce(dir * force);

            if(position == Position.Striker)
            {
                var relativePostion = (transform.position - c.gameObject.transform.position).normalized;
                var sign = team == Team.Blue ? -1f : 1f;
                float reward = sign * relativePostion.x * 0.01f;
                AddReward(reward + 0.005f);
            }
            else if(position == Position.Goalie)
            {
                var awayFromGoal = team == Team.Blue ? 1f : -1f;
                var save = 0.1f + 0.2f * Mathf.Clamp01(awayFromGoal*dir.x);
                AddReward(save);
            }
        }
    }
}
