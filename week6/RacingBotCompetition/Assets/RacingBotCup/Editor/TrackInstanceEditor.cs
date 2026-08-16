using RacingBotCup.Eval;
using RacingBotCup.Track;
using RacingBotCup.Vehicle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RacingBotCup.EditorTools
{
    /// <summary>
    /// Adds the buttons that make swapping circuits a one-click job while designing an agent.
    /// </summary>
    [CustomEditor(typeof(TrackInstance))]
    public sealed class TrackInstanceEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var track = (TrackInstance)target;

            EditorGUILayout.Space();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Randomize", GUILayout.Height(28f)))
                {
                    Apply(track, () => track.Randomize());
                }

                if (GUILayout.Button("Rebuild", GUILayout.Height(28f)))
                {
                    Apply(track, track.Rebuild);
                }
            }

            EditorGUILayout.HelpBox(
                "Randomize picks a new circuit; Rebuild regenerates the current seed. Both write the " +
                "geometry into the scene, so it stays visible without entering Play mode.\n\n" +
                "TrackInstance.Randomize() is public — call it from a curriculum script to change " +
                "circuits between training episodes.",
                MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Layout", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(track.Describe(), EditorStyles.wordWrappedMiniLabel);
        }

        static void Apply(TrackInstance track, System.Action action)
        {
            action();
            SnapCarsToStartLine(track);

            // The generated children are plain scene objects, so the scene has to be told it
            // changed or the new circuit is lost on the next reload.
            EditorUtility.SetDirty(track);
            if (!Application.isPlaying)
            {
                EditorSceneManager.MarkSceneDirty(track.gameObject.scene);
            }
        }

        /// <summary>
        /// Moves any car in the scene onto the new start line. Without this a fresh circuit leaves
        /// the car parked wherever the old one started, which looks broken even though it is not.
        /// </summary>
        static void SnapCarsToStartLine(TrackInstance track)
        {
            var pose = track.Model.GetStartPose(RaceRules.StartHeightOffset);

            foreach (var car in Object.FindObjectsByType<CarController>(FindObjectsSortMode.None))
            {
                Undo.RecordObject(car.transform, "Move car to start line");
                car.transform.SetPositionAndRotation(pose.position, pose.rotation);
                EditorUtility.SetDirty(car.transform);
            }
        }
    }
}
