using System.IO;
using RacingBotCup.Agent;
using RacingBotCup.Eval;
using RacingBotCup.Track;
using RacingBotCup.UI;
using RacingBotCup.Vehicle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RacingBotCup.EditorTools
{
    /// <summary>
    /// Builds the training and evaluation scenes from code.
    ///
    /// Both scenes come out ready to use: the training scene has a circuit and a car already on the
    /// start line, and the evaluation scene has one component with everything wired but the two
    /// fields a competitor fills in. Generating them from a menu means the setup is readable,
    /// diffable, and one click away from being restored.
    /// </summary>
    public static class SceneBootstrap
    {
        public const string SceneDirectory = "Assets/RacingBotCup/Scenes";
        public const string TrainingScenePath = SceneDirectory + "/Training.unity";
        public const string EvaluationScenePath = SceneDirectory + "/Evaluation.unity";

        const string k_ConfigDirectory = "Assets/RacingBotCup/Config";
        const string k_SeedSetPath = k_ConfigDirectory + "/eval_seeds.json";
        const string k_SubmissionConfigPath = k_ConfigDirectory + "/SubmissionConfig.asset";

        const string k_MaterialFolder = "Assets/PolygonStreetRacer/Materials/Roads/";

        // Picked by sampling the textures: Mat_03 is the darkest asphalt. The run-off takes its sand
        // colour from the swatch strip, which is the same in all of them. Grass is not in the pack,
        // so it is generated — see GeneratedMaterials.
        const string k_RoadMaterialPath = k_MaterialFolder + "PolygonStreetRacer_Road_Mat_03.mat";
        const string k_RunoffMaterialPath = k_MaterialFolder + "PolygonStreetRacer_Road_Mat_01.mat";

        /// <summary>Circuit the training scene opens on. Any practice seed does.</summary>
        const int k_DefaultTrainingSeed = 4242;

        [MenuItem("RacingBotCup/Build Scenes", priority = 0)]
        public static void BuildScenesFromMenu()
        {
            BuildScenes();
            EditorUtility.DisplayDialog(
                "RacingBot Cup",
                $"Created:\n{TrainingScenePath}\n{EvaluationScenePath}",
                "OK");
        }

        /// <summary>Rebuilds both scenes. Separate from the menu entry so automation can call it
        /// without a modal dialog blocking the editor.</summary>
        public static void BuildScenes()
        {
            Directory.CreateDirectory(SceneDirectory);
            Directory.CreateDirectory(k_ConfigDirectory);

            if (PrefabBaker.LoadCarPrefab() == null)
            {
                PrefabBaker.RebuildPrefabs();
            }

            EnsureSubmissionConfig();
            BuildTrainingScene();
            BuildEvaluationScene();

            AssetDatabase.Refresh();
        }

        static void BuildTrainingScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AddEnvironment();

            var track = CreateTrack(k_DefaultTrainingSeed);
            var car = CreateCar(track);

            var arena = new GameObject("TrainingArena");
            var component = arena.AddComponent<TrainingArena>();

            var serialized = new SerializedObject(component);
            serialized.FindProperty("m_Track").objectReferenceValue = track;
            serialized.FindProperty("m_Car").objectReferenceValue = car;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, TrainingScenePath);
        }

        static void BuildEvaluationScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AddEnvironment();

            var host = new GameObject("RaceEvaluator");
            var evaluator = host.AddComponent<RaceEvaluator>();

            var serialized = new SerializedObject(evaluator);
            serialized.FindProperty("m_CarPrefab").objectReferenceValue = PrefabBaker.LoadCarPrefab();
            serialized.FindProperty("m_AgentPrefab").objectReferenceValue = PrefabBaker.LoadAgentPrefab();
            serialized.FindProperty("m_SeedSetAsset").objectReferenceValue = LoadAsset<TextAsset>(k_SeedSetPath);
            serialized.FindProperty("m_SubmissionConfig").objectReferenceValue =
                LoadAsset<SubmissionConfig>(k_SubmissionConfigPath);
            serialized.FindProperty("m_RunOnStart").boolValue = true;
            AssignMaterials(serialized);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, EvaluationScenePath);
        }

        /// <summary>Creates a circuit and bakes its geometry into the scene.</summary>
        static TrackInstance CreateTrack(int seed)
        {
            var trackObject = new GameObject("Circuit");
            var track = trackObject.AddComponent<TrackInstance>();

            var serialized = new SerializedObject(track);
            serialized.FindProperty("m_Seed").intValue = seed;
            AssignMaterials(serialized);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            track.Rebuild();
            return track;
        }

        /// <summary>Drops the car on the start line with the sample agent already attached.</summary>
        static CarController CreateCar(TrackInstance track)
        {
            var carPrefab = PrefabBaker.LoadCarPrefab();
            var agentPrefab = PrefabBaker.LoadAgentPrefab();

            if (carPrefab == null || agentPrefab == null)
            {
                Debug.LogError("[RacingBotCup] Prefabs are missing. Run RacingBotCup > Rebuild Prefabs.");
                return null;
            }

            var carObject = (GameObject)PrefabUtility.InstantiatePrefab(carPrefab);
            carObject.name = "RaceCar";

            var agentObject = (GameObject)PrefabUtility.InstantiatePrefab(agentPrefab);
            agentObject.name = "Agent";
            agentObject.transform.SetParent(carObject.transform, false);

            var pose = track.Model.GetStartPose(RaceRules.StartHeightOffset);
            carObject.transform.SetPositionAndRotation(pose.position, pose.rotation);

            return carObject.GetComponent<CarController>();
        }

        static void AddEnvironment()
        {
            var lightObject = new GameObject("Directional Light");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
            lightObject.transform.rotation = Quaternion.Euler(48f, 140f, 0f);

            var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.farClipPlane = 3000f;
            cameraObject.transform.SetPositionAndRotation(
                new Vector3(0f, 40f, -60f),
                Quaternion.Euler(25f, 0f, 0f));

            // Following the car is the default: watching a lap from a fixed overhead camera means
            // watching a few pixels move.
            cameraObject.AddComponent<ChaseCamera>();
            cameraObject.AddComponent<RaceHud>();
        }

        static void EnsureSubmissionConfig()
        {
            if (AssetDatabase.LoadAssetAtPath<SubmissionConfig>(k_SubmissionConfigPath) != null)
            {
                return;
            }

            var config = ScriptableObject.CreateInstance<SubmissionConfig>();
            AssetDatabase.CreateAsset(config, k_SubmissionConfigPath);
        }

        static void AssignMaterials(SerializedObject serialized)
        {
            var materials = serialized.FindProperty("m_Materials");
            if (materials == null)
            {
                return;
            }

            materials.FindPropertyRelative("Road").objectReferenceValue =
                LoadAsset<Material>(k_RoadMaterialPath);
            materials.FindPropertyRelative("Runoff").objectReferenceValue =
                LoadAsset<Material>(k_RunoffMaterialPath);
            materials.FindPropertyRelative("Ground").objectReferenceValue =
                GeneratedMaterials.LoadOrCreateGrass();
        }

        /// <summary>Loads the circuit materials for tools that build a track outside a scene.</summary>
        public static TrackMaterials LoadMaterials()
        {
            return new TrackMaterials
            {
                Road = LoadAsset<Material>(k_RoadMaterialPath),
                Runoff = LoadAsset<Material>(k_RunoffMaterialPath),
                Ground = GeneratedMaterials.LoadOrCreateGrass(),
            };
        }

        static T LoadAsset<T>(string path) where T : Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
            {
                Debug.LogWarning($"[RacingBotCup] Missing asset at {path}; the scene will fall back to defaults.");
            }

            return asset;
        }
    }
}
