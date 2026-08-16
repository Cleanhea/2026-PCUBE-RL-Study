using System;
using System.Collections;
using System.Collections.Generic;
using RacingBotCup.Agent;
using RacingBotCup.Racing;
using RacingBotCup.Track;
using RacingBotCup.Vehicle;
using Unity.InferenceEngine;
using Unity.MLAgents;
using UnityEngine;

namespace RacingBotCup.Eval
{
    /// <summary>
    /// Runs the whole evaluation at once: every circuit in the seed set laid out on a grid, each
    /// with the baseline and the competitor's car running side by side.
    ///
    /// Racing them together rather than in separate passes halves the wall clock, and — the part
    /// that actually matters — makes the comparison visible. You can watch where the policy gains
    /// on the bot and where it throws it away. Collisions between the two are disabled, so the
    /// baseline is effectively a ghost: it shares the circuit without ever affecting the run.
    /// </summary>
    public sealed class EvaluationRunner : MonoBehaviour
    {
        /// <summary>Spacing between environments. Comfortably wider than the largest circuit.</summary>
        const float k_EnvironmentSpacing = 1600f;

        const int k_StepsPerYield = 200;
        const int k_WatchStepsPerYield = 1;
        const float k_SettleSeconds = 0.5f;

        public sealed class Request
        {
            public GameObject CarPrefab;
            public GameObject AgentPrefab;
            public ModelAsset Model;
            public SeedSet Seeds;
            public TrackMaterials Materials;
            public string ParticipantId = "unknown";
            public string Note;

            /// <summary>
            /// Run at roughly real time so the laps can be watched. Costs wall-clock time but
            /// changes nothing about the simulation, so the scores come out the same either way.
            /// </summary>
            public bool WatchMode;
        }

        public readonly struct Progress
        {
            public readonly int Finished;
            public readonly int Total;
            public readonly float ElapsedSimSeconds;

            public Progress(int finished, int total, float elapsedSimSeconds)
            {
                Finished = finished;
                Total = total;
                ElapsedSimSeconds = elapsedSimSeconds;
            }

            public override string ToString()
            {
                return $"{Finished}/{Total} environments finished ({ElapsedSimSeconds:F0}s simulated)";
            }
        }

        /// <summary>One circuit with its two cars.</summary>
        sealed class Environment
        {
            public int Seed;
            public GameObject Root;
            public RacerRig Baseline;
            public RacerRig Challenger;
            public RaceContext BaselineContext;
            public RaceContext ChallengerContext;
            public RunResult BaselineResult;
            public RunResult ChallengerResult;

            public bool Done => BaselineResult != null && ChallengerResult != null;
        }

        public IEnumerator Run(
            Request request,
            Action<SubmissionPayload> onComplete,
            Action<Progress> onProgress = null,
            Action<string> onError = null)
        {
            if (request.CarPrefab == null || request.AgentPrefab == null)
            {
                onError?.Invoke("Assign both a car prefab and an agent prefab before running.");
                yield break;
            }

            var previousFixedDelta = Time.fixedDeltaTime;
            var previousRunInBackground = Application.runInBackground;
            var previousAutoStep = !Academy.IsInitialized || Academy.Instance.AutomaticSteppingEnabled;
            var previousSimulationMode = Physics.simulationMode;
            var previousSkidMarks = SkidMarks.GloballyEnabled;

            Time.fixedDeltaTime = CarSpec.FixedDeltaTime;
            Academy.Instance.AutomaticSteppingEnabled = false;
            Application.runInBackground = true;
            Physics.simulationMode = SimulationMode.Script;

            // Unwatched, a full evaluation simulates ten minutes of racing in about twenty seconds.
            // Twenty cars laying tyre marks through that would cost real frame time to draw
            // something no one is looking at.
            SkidMarks.GloballyEnabled = request.WatchMode;

            var arena = new GameObject("EvaluationArena");
            var environments = new List<Environment>();
            var dt = CarSpec.FixedDeltaTime;
            var stepsPerYield = request.WatchMode ? k_WatchStepsPerYield : k_StepsPerYield;

            try
            {
                var seeds = request.Seeds.Seeds;
                for (var i = 0; i < seeds.Length; i++)
                {
                    var environment = BuildEnvironment(request, seeds[i], GridPosition(i), arena.transform);
                    if (environment == null)
                    {
                        onError?.Invoke("Could not build an environment. Check the console.");
                        yield break;
                    }

                    environments.Add(environment);
                }

                Physics.SyncTransforms();
                Settle(environments, dt);

                foreach (var environment in environments)
                {
                    environment.BaselineContext.Reset();
                    environment.ChallengerContext.Reset();
                    environment.Baseline.Driver.BeginRun();
                    environment.Challenger.Driver.BeginRun();
                }

                var maxSteps = Mathf.CeilToInt(RaceRules.BaselineTimeoutSeconds / dt);

                for (var step = 0; step < maxSteps; step++)
                {
                    var finished = 0;
                    var anyDecision = false;

                    foreach (var environment in environments)
                    {
                        AdvanceCar(environment, isBaseline: true, dt, ref anyDecision);
                        AdvanceCar(environment, isBaseline: false, dt, ref anyDecision);

                        if (environment.Done)
                        {
                            finished++;
                        }
                    }

                    // One Academy step for every agent that asked, no matter how many are running.
                    if (anyDecision)
                    {
                        Academy.Instance.EnvironmentStep();
                    }

                    foreach (var environment in environments)
                    {
                        if (environment.BaselineResult == null)
                        {
                            environment.Baseline.Car.Step(dt);
                        }

                        if (environment.ChallengerResult == null)
                        {
                            environment.Challenger.Car.Step(dt);
                        }
                    }

                    Physics.Simulate(dt);

                    if (finished >= environments.Count)
                    {
                        break;
                    }

                    if (step % stepsPerYield == stepsPerYield - 1)
                    {
                        onProgress?.Invoke(new Progress(finished, environments.Count, step * dt));
                        yield return null;
                    }
                }

                // Anything still running when the clock ran out timed out.
                foreach (var environment in environments)
                {
                    environment.BaselineResult ??= Timeout(environment, environment.BaselineContext, "BaselineBot");
                    environment.ChallengerResult ??= Timeout(environment, environment.ChallengerContext, "Agent");
                }
            }
            finally
            {
                foreach (var environment in environments)
                {
                    environment.Baseline?.Driver.EndRun();
                    environment.Challenger?.Driver.EndRun();
                }

                if (arena != null)
                {
                    Destroy(arena);
                }

                Time.fixedDeltaTime = previousFixedDelta;
                Application.runInBackground = previousRunInBackground;
                Physics.simulationMode = previousSimulationMode;
                SkidMarks.GloballyEnabled = previousSkidMarks;
                if (Academy.IsInitialized)
                {
                    Academy.Instance.AutomaticSteppingEnabled = previousAutoStep;
                }
            }

            onComplete?.Invoke(BuildPayload(request, environments));
        }

        // ------------------------------------------------------------------
        // Per-step advance
        // ------------------------------------------------------------------

        void AdvanceCar(Environment environment, bool isBaseline, float dt, ref bool anyDecision)
        {
            var result = isBaseline ? environment.BaselineResult : environment.ChallengerResult;
            if (result != null)
            {
                return;
            }

            var context = isBaseline ? environment.BaselineContext : environment.ChallengerContext;
            var rig = isBaseline ? environment.Baseline : environment.Challenger;

            context.Refresh(dt);

            if (context.LapCompletedThisTick)
            {
                Finish(environment, isBaseline, RunStatus.Finished, context, rig);
                return;
            }

            if (context.OffTrackDuration >= RaceRules.OffTrackDnfSeconds)
            {
                Finish(environment, isBaseline, RunStatus.DidNotFinish, context, rig);
                return;
            }

            // The agent's clock is capped at a multiple of the baseline's, but only once the
            // baseline has actually set a time on this circuit.
            if (!isBaseline &&
                environment.BaselineResult is { Status: RunStatus.Finished } baseline &&
                context.ElapsedTime > baseline.Time * RaceRules.TimeoutMultiplier)
            {
                Finish(environment, false, RunStatus.TimedOut, context, rig);
                return;
            }

            rig.Driver.Tick();
            anyDecision |= rig.Driver is RacerAgent;
        }

        static void Finish(
            Environment environment,
            bool isBaseline,
            RunStatus status,
            RaceContext context,
            RacerRig rig)
        {
            // Back off the part of the final step taken after the line, so the lap time is a
            // continuous function of where the car actually was rather than a multiple of 20 ms.
            var adjustment = status == RunStatus.Finished
                ? (1f - context.Checkpoints.LapCrossingFraction) * CarSpec.FixedDeltaTime
                : 0f;

            var result = new RunResult
            {
                Seed = environment.Seed,
                Driver = rig.Driver.DriverName,
                Status = status,
                Time = context.ElapsedTime - adjustment,
                CheckpointsPassed = context.Checkpoints.Passed,
                CheckpointCount = context.Checkpoints.Count,
                DistanceTraveled = context.Checkpoints.TraveledDistance,
            };

            if (isBaseline)
            {
                environment.BaselineResult = result;
            }
            else
            {
                environment.ChallengerResult = result;
            }

            rig.Car.SetInput(0f, 0f);
        }

        static RunResult Timeout(Environment environment, RaceContext context, string driver)
        {
            return new RunResult
            {
                Seed = environment.Seed,
                Driver = driver,
                Status = RunStatus.TimedOut,
                Time = context.ElapsedTime,
                CheckpointsPassed = context.Checkpoints.Passed,
                CheckpointCount = context.Checkpoints.Count,
                DistanceTraveled = context.Checkpoints.TraveledDistance,
            };
        }

        // ------------------------------------------------------------------
        // Setup
        // ------------------------------------------------------------------

        static Vector3 GridPosition(int index)
        {
            const int columns = 4;
            return new Vector3(
                index % columns * k_EnvironmentSpacing,
                0f,
                index / columns * k_EnvironmentSpacing);
        }

        Environment BuildEnvironment(Request request, int seed, Vector3 origin, Transform parent)
        {
            var root = new GameObject($"Env_{seed}");
            root.transform.SetParent(parent, false);
            root.transform.position = origin;

            var trackObject = new GameObject("Circuit");
            trackObject.transform.SetParent(root.transform, false);

            var track = trackObject.AddComponent<TrackInstance>();
            track.Seed = seed;
            CopyMaterials(track, request.Materials);
            track.Rebuild();

            var baseline = RacerBuilder.BuildBaseline(request.CarPrefab, root.transform);
            var challenger = RacerBuilder.BuildAgent(
                request.CarPrefab,
                request.AgentPrefab,
                request.Model,
                manualStepping: true,
                parent: root.transform);

            if (baseline == null || challenger == null)
            {
                return null;
            }

            baseline.MakeGhost();

            var environment = new Environment
            {
                Seed = seed,
                Root = root,
                Baseline = baseline,
                Challenger = challenger,
            };

            environment.BaselineContext = baseline.PlaceOnTrack(track.Model);
            environment.ChallengerContext = challenger.PlaceOnTrack(track.Model);

            IgnoreCollisionsBetween(baseline.Root, challenger.Root);
            return environment;
        }

        static void CopyMaterials(TrackInstance track, TrackMaterials materials)
        {
            if (materials == null)
            {
                return;
            }

            track.Materials.Road = materials.Road;
            track.Materials.Runoff = materials.Runoff;
            track.Materials.Ground = materials.Ground;
        }

        /// <summary>
        /// The baseline shares the circuit with the competitor's car, so they must pass through one
        /// another. Without this the ghost would be a rolling roadblock.
        /// </summary>
        static void IgnoreCollisionsBetween(GameObject a, GameObject b)
        {
            var first = a.GetComponentsInChildren<Collider>(true);
            var second = b.GetComponentsInChildren<Collider>(true);

            foreach (var left in first)
            {
                foreach (var right in second)
                {
                    Physics.IgnoreCollision(left, right, true);
                }
            }
        }

        static void Settle(List<Environment> environments, float dt)
        {
            var steps = Mathf.CeilToInt(k_SettleSeconds / dt);
            for (var i = 0; i < steps; i++)
            {
                foreach (var environment in environments)
                {
                    environment.Baseline.Car.SetInput(0f, 0f);
                    environment.Challenger.Car.SetInput(0f, 0f);
                    environment.Baseline.Car.Step(dt);
                    environment.Challenger.Car.Step(dt);
                }

                Physics.Simulate(dt);
            }
        }

        // ------------------------------------------------------------------
        // Scoring
        // ------------------------------------------------------------------

        SubmissionPayload BuildPayload(Request request, List<Environment> environments)
        {
            var tracks = new List<TrackScore>(environments.Count);
            foreach (var environment in environments)
            {
                if (environment.BaselineResult is not { Status: RunStatus.Finished })
                {
                    Debug.LogError(
                        $"[RacingBotCup] The baseline bot failed on seed {environment.Seed} " +
                        $"({environment.BaselineResult?.Status}). This seed cannot be scored and " +
                        "should be replaced in eval_seeds.json.");
                }

                tracks.Add(ScoreAggregator.BuildTrackScore(
                    environment.Seed, environment.BaselineResult, environment.ChallengerResult));
            }

            var payload = new SubmissionPayload
            {
                ParticipantId = string.IsNullOrWhiteSpace(request.ParticipantId)
                    ? "unknown"
                    : request.ParticipantId.Trim(),
                SubmittedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                SeedSetId = request.Seeds.Id,
                Seeds = request.Seeds.Seeds,
                Note = request.Note,
                Score = ScoreAggregator.Aggregate(tracks),
                Integrity = new IntegrityBlock
                {
                    CarSpecHash = CarSpec.SpecHash,
                    TrackGeneratorVersion = TrackGenerator.Version,
                    ScoreModuleVersion = ScoreAggregator.Version,
                    BaselineBotVersion = BaselineBot.Version,
                    RulesHash = RaceRules.Hash,
                    AgentHash = AgentFingerprint(request.AgentPrefab, request.Model),
                },
            };

            SubmissionCodec.Sign(payload);
            return payload;
        }

        /// <summary>
        /// Identifies the entry. With sensors now free-form there is no config to hash, so this is
        /// the prefab and model that produced the score.
        /// </summary>
        static string AgentFingerprint(GameObject agentPrefab, ModelAsset model)
        {
            var prefabName = agentPrefab == null ? "none" : agentPrefab.name;
            var modelName = model == null ? "heuristic" : model.name;
            return $"{prefabName}/{modelName}";
        }
    }
}
