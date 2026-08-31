using System.Collections.Generic;
using Unity.MLAgents;
using UnityEngine;

// spawn/reset the players and ball, handle a goal (score + group reward + reset)
public class SoccerEnvController : MonoBehaviour
{
    // Each active agent receives both shares, so the terminal goal reward still
    // totals +1/-1 per agent while remaining visible to the individual return.
    const float k_GroupGoalRewardShare = 0.5f;
    const float k_IndividualGoalRewardShare = 0.5f;

    // Playable bounds, in local units from the field centre. Also the scale AgentSoccer
    // normalises the keeper's line observations against.
    public const float PitchHalfX = 18f; // goal line begins ~20.2
    public const float PitchHalfZ = 7f;  // side walls at ~9.8

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

    public int MaxEnvironmentSteps = 2000;

    public bool strongRandomization = true;

    public GameObject ball;
    [HideInInspector]
    public Rigidbody ballRb;
    Vector3 m_BallStartingPos;
    float previousBallX;

    // List of players on this field.
    public List<PlayerInfo> AgentsList = new List<PlayerInfo>();

    public int blueScore;
    public int redScore;

    public float progressRewardScale = 0.01f;
    public float keeperDefenseRewardScale = 0.01f;
    public float shotReward = 0.05f;

    float lesson;
    int m_ResetTimer;
    SimpleMultiAgentGroup m_BlueStrikers;
    SimpleMultiAgentGroup m_BlueKeeper;

    SimpleMultiAgentGroup m_RedStrikers;
    SimpleMultiAgentGroup m_RedKeeper;

    // VsGoalie lesson (2 attacking strikers vs the other team's lone goalie): the attacker/defender
    // roles are fixed for the round, so we can shape defense specifically. Set in ResetScene.
    Team m_DefenderTeam;      // the team whose goalie is defending

    void Start()
    {
        ballRb = ball.GetComponent<Rigidbody>();
        m_BallStartingPos = ball.transform.position;

        m_BlueStrikers = new SimpleMultiAgentGroup();
        m_BlueKeeper = new SimpleMultiAgentGroup();
        m_RedStrikers = new SimpleMultiAgentGroup();
        m_RedKeeper = new SimpleMultiAgentGroup();

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
        var ballX = ball.transform.position.x;
        var deltaX = ballX - previousBallX;

        // Only strikers receive whole-pitch attacking progress. Giving this reward to the
        // keeper paid it for escorting the ball all the way into the opponent's goal.
        AddStrikerGroupReward(Team.Blue, deltaX * progressRewardScale);
        AddStrikerGroupReward(Team.Red, -deltaX * progressRewardScale);

        // Keeper shaping is defensive and saturates at midfield. It rewards recovering a
        // threatening ball from the own goal line toward halfway, but gives nothing for
        // carrying an already-safe ball farther into the opponent's half.
        AddKeeperGroupReward(
            Team.Blue,
            (KeeperBallSafety(Team.Blue, ballX) - KeeperBallSafety(Team.Blue, previousBallX))
            * keeperDefenseRewardScale);
        AddKeeperGroupReward(
            Team.Red,
            (KeeperBallSafety(Team.Red, ballX) - KeeperBallSafety(Team.Red, previousBallX))
            * keeperDefenseRewardScale);

        previousBallX = ballX;
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

        previousBallX = ball.transform.position.x;
    }

    public void GoalTouched(Team scoredTeam)
    {
        var concededTeam = scoredTeam == Team.Blue ? Team.Red : Team.Blue;
        bool ownGoal = scoredTeam == m_DefenderTeam;

        if (lesson < 2f)
        {
            if (ownGoal)
            {
                // Lesson 0: -1.2
                // Lesson 1: -2.0
                float penalty = lesson < 1f ? -1.2f : -2.0f;

                // 50% group + 50% individual
                AddStrikerTerminalReward(concededTeam, penalty);

                Debug.Log("Own Goal!");
            }
            else
            {
                // Attacking strikers scored.
                // +1.0 = +0.5 group + +0.5 individual
                AddStrikerTerminalReward(scoredTeam, 1.0f);

                if (lesson >= 1f)
                {
                    // Defending keeper conceded.
                    // -1.0 = -0.5 group + -0.5 individual
                    AddKeeperTerminalReward(concededTeam, -1.0f);
                }

                Debug.Log("Goal!");
            }
        }
        else
        {
            // Lesson 2: existing 3v3 behavior.
            AddTeamGoalReward(scoredTeam, 1.0f);
            AddTeamGoalReward(concededTeam, -1.0f);
        }

        if (scoredTeam == Team.Blue)
            blueScore++;
        else
            redScore++;

        EndAllGroups();
        ResetScene();
    }

    public void ResetScene()
    {
        m_ResetTimer = 0;
        lesson = Academy.Instance.EnvironmentParameters.GetWithDefault("lesson", 2f);

        Team randTeam = Random.value < 0.5f ? Team.Blue : Team.Red;

        // randTeam's strikers attack
        m_DefenderTeam = randTeam == Team.Blue ? Team.Red : Team.Blue;

        foreach (var item in AgentsList)
        {
            var agent = item.Agent;
            var active = ActiveInLesson(agent, lesson, randTeam);

            agent.gameObject.SetActive(active);

            var group = GetGroup(agent);

            if (!active)
            {
                group.UnregisterAgent(agent);
                continue;
            }

            group.RegisterAgent(agent);
            agent.ResetEpisodeBudgets();

            Vector3 newStartPos;
            Quaternion newRot;

            if (strongRandomization &&
                agent.position != AgentSoccer.Position.Goalie)
            {
                var lx = Random.Range(-PitchHalfX, PitchHalfX);
                var lz = Random.Range(-PitchHalfZ, PitchHalfZ);

                newStartPos = new Vector3(
                    transform.position.x + lx,
                    item.StartingPos.y,
                    transform.position.z + lz
                );

                newRot = Quaternion.Euler(
                    0f,
                    Random.Range(0f, 360f),
                    0f
                );
            }
            else
            {
                var newX =
                    item.StartingPos.x +
                    Random.Range(-5f, 5f);

                var localX = Mathf.Clamp(
                    newX - transform.position.x,
                    -PitchHalfX,
                    PitchHalfX
                );

                newStartPos = new Vector3(
                    transform.position.x + localX,
                    item.StartingPos.y,
                    item.StartingPos.z
                );

                newRot = Quaternion.Euler(
                    0f,
                    agent.rotSign * Random.Range(80f, 100f),
                    0f
                );
            }

            agent.transform.SetPositionAndRotation(
                newStartPos,
                newRot
            );

            item.Rb.linearVelocity = Vector3.zero;
            item.Rb.angularVelocity = Vector3.zero;
        }

        Academy.Instance.StatsRecorder.Add(
            "Blue Score",
            blueScore
        );

        Academy.Instance.StatsRecorder.Add(
            "Red Score",
            redScore
        );

        ResetBall();
    }

    SimpleMultiAgentGroup GetGroup(AgentSoccer agent)
    {
        if (agent.team == Team.Blue)
        {
            return agent.position == AgentSoccer.Position.Goalie
                ? m_BlueKeeper
                : m_BlueStrikers;
        }

        return agent.position == AgentSoccer.Position.Goalie
            ? m_RedKeeper
            : m_RedStrikers;
    }

    void AddTeamGroupReward(Team team, float reward)
    {
        if (team == Team.Blue)
        {
            m_BlueStrikers.AddGroupReward(reward);
            m_BlueKeeper.AddGroupReward(reward);
        }
        else
        {
            m_RedStrikers.AddGroupReward(reward);
            m_RedKeeper.AddGroupReward(reward);
        }
    }

    void AddStrikerGroupReward(Team team, float reward)
    {
        if (team == Team.Blue)
            m_BlueStrikers.AddGroupReward(reward);
        else
            m_RedStrikers.AddGroupReward(reward);
    }

    void AddKeeperGroupReward(Team team, float reward)
    {
        if (team == Team.Blue)
            m_BlueKeeper.AddGroupReward(reward);
        else
            m_RedKeeper.AddGroupReward(reward);
    }
    void AddStrikerTerminalReward(Team team, float totalReward)
    {
        // POCA group reward
        AddStrikerGroupReward(
            team,
            totalReward * k_GroupGoalRewardShare
        );

        // Individual reward:
        // TensorBoard Environment/Cumulative Reward와
        // curriculum measure: reward에 보이도록 함.
        float individualReward =
            totalReward * k_IndividualGoalRewardShare;

        foreach (var item in AgentsList)
        {
            var agent = item.Agent;

            if (agent.gameObject.activeSelf &&
                agent.team == team &&
                agent.position == AgentSoccer.Position.Striker)
            {
                agent.AddReward(individualReward);
            }
        }
    }

    void AddKeeperTerminalReward(Team team, float totalReward)
    {
        // POCA group reward
        AddKeeperGroupReward(
            team,
            totalReward * k_GroupGoalRewardShare
        );

        // Individual keeper reward
        float individualReward =
            totalReward * k_IndividualGoalRewardShare;

        foreach (var item in AgentsList)
        {
            var agent = item.Agent;

            if (agent.gameObject.activeSelf &&
                agent.team == team &&
                agent.position == AgentSoccer.Position.Goalie)
            {
                agent.AddReward(individualReward);
            }
        }
    }

    float KeeperBallSafety(Team team, float ballX)
    {
        var centerX = transform.position.x;
        var ownLineX = centerX + (team == Team.Blue ? -PitchHalfX : PitchHalfX);
        var distanceFromOwnLine = team == Team.Blue
            ? ballX - ownLineX
            : ownLineX - ballX;
        return Mathf.Clamp(distanceFromOwnLine, 0f, PitchHalfX);
    }

    void AddTeamGoalReward(Team team, float totalReward)
    {
        AddTeamGroupReward(team, totalReward * k_GroupGoalRewardShare);

        var individualReward = totalReward * k_IndividualGoalRewardShare;
        foreach (var item in AgentsList)
        {
            var agent = item.Agent;
            if (agent.gameObject.activeSelf && agent.team == team)
            {
                agent.AddReward(individualReward);
            }
        }
    }

    void EndAllGroups()
    {
        m_BlueStrikers.EndGroupEpisode();
        m_BlueKeeper.EndGroupEpisode();

        m_RedStrikers.EndGroupEpisode();
        m_RedKeeper.EndGroupEpisode();
    }

    void InterruptAllGroups()
    {
        m_BlueStrikers.GroupEpisodeInterrupted();
        m_BlueKeeper.GroupEpisodeInterrupted();
        m_RedStrikers.GroupEpisodeInterrupted();
        m_RedKeeper.GroupEpisodeInterrupted();
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