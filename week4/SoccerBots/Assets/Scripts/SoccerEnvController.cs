using System.Collections.Generic;
using Unity.MLAgents;
using UnityEngine;

// spawn/reset the players and ball, handle a goal (score + group reward + reset)
public class SoccerEnvController : MonoBehaviour
{
    [System.Serializable]
    public class PlayerInfo
    {
        public AgentSoccer Agent;
        [HideInInspector]
        public Vector3 StartingPos;
        [HideInInspector]
        public Quaternion StartingRot;
        [HideInInspector]
        public Rigidbody Rb;
    }

    public int MaxEnvironmentSteps = 25000;

    public bool strongRandomization = true;

    public float ballStallPenaltyScale = 0.00005f;

    public GameObject ball;
    [HideInInspector]
    public Rigidbody ballRb;
    Vector3 m_BallStartingPos;

    // List of players on this field.
    public List<PlayerInfo> AgentsList = new List<PlayerInfo>();

    public int blueScore;
    public int redScore;

    int m_ResetTimer;
    SimpleMultiAgentGroup m_BlueStrikerGroup;
    SimpleMultiAgentGroup m_BlueKeeperGroup;
    SimpleMultiAgentGroup m_RedStrikerGroup;
    SimpleMultiAgentGroup m_RedKeeperGroup;

    // VsGoalie lesson (2 attacking strikers vs the other team's lone goalie): the attacker/defender
    // roles are fixed for the round, so we can shape defense specifically. Set in ResetScene.
    bool m_VsGoalieLesson;
    Team m_DefenderTeam;      // the team whose goalie is defending

    void Start()
    {
        ballRb = ball.GetComponent<Rigidbody>();
        m_BallStartingPos = ball.transform.position;

        m_BlueStrikerGroup = new SimpleMultiAgentGroup();
        m_BlueKeeperGroup = new SimpleMultiAgentGroup();
        m_RedStrikerGroup = new SimpleMultiAgentGroup();
        m_RedKeeperGroup = new SimpleMultiAgentGroup();

        foreach (var item in AgentsList)
        {
            item.StartingPos = item.Agent.transform.position;
            item.StartingRot = item.Agent.transform.rotation;
            item.Rb = item.Agent.GetComponent<Rigidbody>();
        }
        // Group membership is (re)built per lesson in ResetScene, so the field can shrink/grow.
        ResetScene();
    }

    void FixedUpdate()
    {
        m_ResetTimer += 1;
        if (m_ResetTimer >= MaxEnvironmentSteps && MaxEnvironmentSteps > 0)
        {
            InterruptAllGroups();
            ResetScene();
        }
    }

    public void ResetBall()
    {
        // Strong mode spreads the ball across most of the pitch (kept off the ±20 goal lines);
        // otherwise a small jitter around the center spot.
        var range = strongRandomization ? new Vector2(14f, 7f) : new Vector2(2.5f, 2.5f);
        var randomPosX = Random.Range(-range.x, range.x);
        var randomPosZ = Random.Range(-range.y, range.y);

        ball.transform.position = m_BallStartingPos + new Vector3(randomPosX, 0f, randomPosZ);
        ballRb.linearVelocity = Vector3.zero;
        ballRb.angularVelocity = Vector3.zero;
    }

    public void GoalTouched(Team scoredTeam)
    {
        var concededTeam = scoredTeam == Team.Blue ? Team.Red : Team.Blue;

        if (m_VsGoalieLesson && scoredTeam == m_DefenderTeam)
        {
            AddTeamGroupReward(concededTeam, -2f);
        }
        else
        {
            AddTeamGroupReward(scoredTeam, 1f);
            AddTeamGroupReward(concededTeam, -1f);
        }

        // Mirror the goal onto each active striker's INDIVIDUAL reward.
        foreach (var item in AgentsList)
        {
            var a = item.Agent;
            if (!a.gameObject.activeSelf || a.position != AgentSoccer.Position.Striker)
                continue;
            a.AddReward(a.team == scoredTeam ? 1f : -1f);
        }

        if (scoredTeam == Team.Blue) blueScore++; else redScore++;
        Debug.Log($"Goal! Blue {blueScore} - {redScore} Red");

        EndAllGroups();
        ResetScene();
    }

    public void ResetScene()
    {
        m_ResetTimer = 0;

        // Curriculum lesson (from Python environment_parameters): 
        // 0 = 2 strikers vs empty goal,
        // 1 = 2 strikers vs a lone goalie,
        // 2 = full 3v3 self-play.
        var lesson = Academy.Instance.EnvironmentParameters.GetWithDefault("lesson", 2f);
        Team randTeam = Random.value < 0.5f ? Team.Blue : Team.Red;
        // randTeam's strikers attack
        m_VsGoalieLesson = lesson >= 1f && lesson < 2f;
        m_DefenderTeam = randTeam == Team.Blue ? Team.Red : Team.Blue;
        foreach (var item in AgentsList)
        {
            var agent = item.Agent;
            var active = ActiveInLesson(agent, lesson, randTeam);
            agent.gameObject.SetActive(active); // benched agents leave the field: no body, no decisions.

            var group = GetGroup(agent);
            if (!active)
            {
                group.UnregisterAgent(agent);
                continue;
            }
            group.RegisterAgent(agent);

            const float pitchHalfX = 19f; // goal line begins ~20.2
            const float pitchHalfZ = 8f;  // side walls at ~9.8

            Vector3 newStartPos;
            Quaternion newRot;
            if (strongRandomization && agent.position != AgentSoccer.Position.Goalie)
            {
                // Scatter non-goalie players anywhere on the pitch, facing a random direction, so the
                // striker generalizes past the near-home kickoff layout. Goalies keep their line.
                var lx = Random.Range(-pitchHalfX, pitchHalfX);
                var lz = Random.Range(-pitchHalfZ, pitchHalfZ);
                newStartPos = new Vector3(transform.position.x + lx, agent.initialPos.y, transform.position.z + lz);
                newRot = Quaternion.Euler(0, Random.Range(0f, 360f), 0);
            }
            else
            {
                // Default: small depth (x) jitter around home, clamped to the pitch, authored facing.
                var newX = agent.initialPos.x + Random.Range(-5f, 5f);
                var localX = Mathf.Clamp(newX - transform.position.x, -pitchHalfX, pitchHalfX);
                newStartPos = new Vector3(transform.position.x + localX, agent.initialPos.y, agent.initialPos.z);
                newRot = Quaternion.Euler(0, agent.rotSign * Random.Range(80f, 100f), 0);
            }
            agent.transform.SetPositionAndRotation(newStartPos, newRot);

            item.Rb.linearVelocity = Vector3.zero;
            item.Rb.angularVelocity = Vector3.zero;
        }
        Academy.Instance.StatsRecorder.Add("Blue Score", blueScore);
        Academy.Instance.StatsRecorder.Add("Red Score", redScore);

        //Reset Ball
        ResetBall();
    }

    SimpleMultiAgentGroup GetGroup(AgentSoccer agent)
    {
        if (agent.team == Team.Blue)
        {
            return agent.position == AgentSoccer.Position.Goalie
                ? m_BlueKeeperGroup
                : m_BlueStrikerGroup;
        }

        return agent.position == AgentSoccer.Position.Goalie
            ? m_RedKeeperGroup
            : m_RedStrikerGroup;
    }

    void AddTeamGroupReward(Team team, float reward)
    {
        if (team == Team.Blue)
        {
            m_BlueStrikerGroup.AddGroupReward(reward);
            m_BlueKeeperGroup.AddGroupReward(reward);
        }
        else
        {
            m_RedStrikerGroup.AddGroupReward(reward);
            m_RedKeeperGroup.AddGroupReward(reward);
        }
    }

    void EndAllGroups()
    {
        m_BlueStrikerGroup.EndGroupEpisode();
        m_BlueKeeperGroup.EndGroupEpisode();
        m_RedStrikerGroup.EndGroupEpisode();
        m_RedKeeperGroup.EndGroupEpisode();
    }

    void InterruptAllGroups()
    {
        m_BlueStrikerGroup.GroupEpisodeInterrupted();
        m_BlueKeeperGroup.GroupEpisodeInterrupted();
        m_RedStrikerGroup.GroupEpisodeInterrupted();
        m_RedKeeperGroup.GroupEpisodeInterrupted();
    }

    // Who is on the field for a given curriculum lesson.
    static bool ActiveInLesson(AgentSoccer agent, float lesson, Team team = Team.Blue)
    {
        var striker = agent.team == team && agent.position == AgentSoccer.Position.Striker;
        if (lesson < 1f)   // Lesson 0: only the attacking strikers, empty goal.
            return striker;
        if (lesson < 2f)   // Lesson 1: strikers vs one defending goalie.
            return striker || (agent.team != team && agent.position == AgentSoccer.Position.Goalie);
        return true;       // Lesson 2: full 3v3.
    }
}
