using System.IO;
using RacingBotCup.Agent;
using RacingBotCup.Track;
using RacingBotCup.Vehicle;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEditor;
using UnityEngine;

namespace RacingBotCup.EditorTools
{
    /// <summary>
    /// Bakes the two prefabs the competition is built around.
    ///
    /// The car used to be assembled at runtime, which kept it provably identical but also meant
    /// competitors never got to look at it. Baking the same <see cref="CarFactory"/> output into an
    /// asset gives them something to inspect while the physics still comes from
    /// <see cref="CarSpec"/> at load time — editing the prefab cannot change how it drives.
    /// </summary>
    public static class PrefabBaker
    {
        public const string PrefabFolder = "Assets/RacingBotCup/Prefabs";
        public const string CarPrefabPath = PrefabFolder + "/RaceCar.prefab";
        public const string AgentPrefabPath = PrefabFolder + "/RacerAgent.prefab";

        [MenuItem("RacingBotCup/Rebuild Prefabs", priority = 1)]
        public static void RebuildPrefabsFromMenu()
        {
            var ok = RebuildPrefabs();

            EditorUtility.DisplayDialog(
                "RacingBot Cup",
                ok ? $"Created:\n{CarPrefabPath}\n{AgentPrefabPath}"
                   : "Some prefabs could not be built. Check the console.",
                "OK");
        }

        /// <summary>Bakes both prefabs. Separate from the menu entry so automation can call it
        /// without a modal dialog blocking the editor.</summary>
        public static bool RebuildPrefabs()
        {
            Directory.CreateDirectory(PrefabFolder);

            var car = BakeCar();
            var agent = BakeAgent();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return car && agent;
        }

        static bool BakeCar()
        {
            var art = AssetDatabase.LoadAssetAtPath<GameObject>(CarFactory.DefaultArtPrefabPath);
            if (art == null)
            {
                Debug.LogError($"[RacingBotCup] Vehicle art not found at {CarFactory.DefaultArtPrefabPath}.");
                return false;
            }

            var built = CarFactory.Build(art, null, "RaceCar");
            if (built == null)
            {
                return false;
            }

            // Cosmetic, and outside CarFactory on purpose: the factory builds the physics rig that
            // every competitor is scored on, and nothing that only draws belongs in it.
            var skids = built.gameObject.AddComponent<SkidMarks>();
            var skidSerialized = new SerializedObject(skids);
            skidSerialized.FindProperty("m_Material").objectReferenceValue =
                GeneratedMaterials.LoadOrCreateSkidMark();
            skidSerialized.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SaveAsPrefabAsset(built.gameObject, CarPrefabPath);
            Object.DestroyImmediate(built.gameObject);
            return true;
        }

        /// <summary>
        /// The template competitors copy: a sample agent, a forward ray sensor, and a behaviour
        /// already sized to the observations the sample writes.
        /// </summary>
        static bool BakeAgent()
        {
            var root = new GameObject("RacerAgent");

            var sample = root.AddComponent<SampleRacerAgent>();

            var ray = root.AddComponent<RayPerceptionSensorComponent3D>();
            ray.SensorName = "front";
            ray.RaysPerDirection = 12;
            ray.MaxRayDegrees = 80f;
            ray.RayLength = 60f;
            ray.SphereCastRadius = 0.4f;
            ray.StartVerticalOffset = 0.6f;
            ray.EndVerticalOffset = 0.6f;
            ray.RayLayerMask = RacingLayers.SensorMask;
            ray.DetectableTags = new System.Collections.Generic.List<string>
            {
                TrackTags.Track,
                TrackTags.OffTrack,
                TrackTags.Obstacle,
                TrackTags.Ramp,
            };

            // Agent carries [RequireComponent(typeof(BehaviorParameters))], so adding the sample
            // above already created one. Adding a second leaves the prefab with two, and the one
            // the harness reads is not the one that got configured.
            var behavior = root.GetComponent<BehaviorParameters>();
            behavior.BehaviorName = "Racer";
            behavior.BrainParameters.VectorObservationSize = sample.ObservationSize;
            behavior.BrainParameters.NumStackedVectorObservations = 3;
            behavior.BrainParameters.ActionSpec = RacerAgent.ContinuousActionSpec;
            behavior.DeterministicInference = true;

            PrefabUtility.SaveAsPrefabAsset(root, AgentPrefabPath);
            Object.DestroyImmediate(root);
            return true;
        }

        public static GameObject LoadCarPrefab()
        {
            return AssetDatabase.LoadAssetAtPath<GameObject>(CarPrefabPath);
        }

        public static GameObject LoadAgentPrefab()
        {
            return AssetDatabase.LoadAssetAtPath<GameObject>(AgentPrefabPath);
        }
    }
}
