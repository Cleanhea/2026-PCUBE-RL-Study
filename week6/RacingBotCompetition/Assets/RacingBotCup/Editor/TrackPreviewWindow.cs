using RacingBotCup.Eval;
using RacingBotCup.Track;
using UnityEditor;
using UnityEngine;

namespace RacingBotCup.EditorTools
{
    /// <summary>
    /// Generates tracks in the open scene so a seed can be looked at before anyone races on it.
    ///
    /// Mostly used to sanity-check the evaluation seed set: a layout that folds back on itself or
    /// hides a hairpin the baseline cannot take would quietly break every score on that track.
    /// </summary>
    public sealed class TrackPreviewWindow : EditorWindow
    {
        const string k_PreviewRootName = "TrackPreview";

        int m_Seed = 1017;
        float m_Spacing = 900f;
        Vector2 m_Scroll;
        string m_Report = "";

        // SharpHairpin/ObstacleStraight/RampCorner. Off by default — evaluation never uses them, so
        // this matches what the eval seed set actually races on.
        bool m_EnableHazards;

        [MenuItem("RacingBotCup/Track Preview", priority = 20)]
        public static void Open()
        {
            GetWindow<TrackPreviewWindow>("Track Preview").minSize = new Vector2(360f, 300f);
        }

        void OnGUI()
        {
            m_EnableHazards = EditorGUILayout.ToggleLeft(
                "Enable hazard sections (SharpHairpin / ObstacleStraight / RampCorner)", m_EnableHazards);
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Single seed", EditorStyles.boldLabel);
            m_Seed = EditorGUILayout.IntField("Seed", m_Seed);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Generate"))
                {
                    GenerateSingle(m_Seed);
                }

                if (GUILayout.Button("Clear"))
                {
                    ClearPreview();
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Evaluation seed set", EditorStyles.boldLabel);
            m_Spacing = EditorGUILayout.FloatField("Grid spacing (m)", m_Spacing);

            if (GUILayout.Button("Generate all evaluation seeds"))
            {
                GenerateSeedSet();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Report", EditorStyles.boldLabel);

            using (var scroll = new EditorGUILayout.ScrollViewScope(m_Scroll))
            {
                m_Scroll = scroll.scrollPosition;
                EditorGUILayout.TextArea(m_Report, GUILayout.ExpandHeight(true));
            }
        }

        void GenerateSingle(int seed)
        {
            ClearPreview();

            var root = new GameObject(k_PreviewRootName);
            var track = Build(seed, root.transform, Vector3.zero, m_EnableHazards);
            m_Report = Describe(track);

            Selection.activeGameObject = root;
            SceneView.lastActiveSceneView?.FrameSelected();
        }

        void GenerateSeedSet()
        {
            ClearPreview();

            var seeds = SeedSet.Default().Seeds;
            var root = new GameObject(k_PreviewRootName);
            var columns = Mathf.CeilToInt(Mathf.Sqrt(seeds.Length));

            var report = new System.Text.StringBuilder();
            var problems = 0;

            for (var i = 0; i < seeds.Length; i++)
            {
                var offset = new Vector3((i % columns) * m_Spacing, 0f, (i / columns) * m_Spacing);
                var track = Build(seeds[i], root.transform, offset, m_EnableHazards);

                report.AppendLine(Describe(track));
                if (!track.FullyValid)
                {
                    problems++;
                }
            }

            report.Insert(0, problems == 0
                ? $"All {seeds.Length} seeds produced valid layouts.\n\n"
                : $"{problems} of {seeds.Length} seeds fell back to a relaxed layout — " +
                  "consider replacing them in eval_seeds.json.\n\n");

            m_Report = report.ToString();
            Selection.activeGameObject = root;
        }

        static GeneratedTrack Build(int seed, Transform parent, Vector3 offset, bool enableHazards)
        {
            var generated = TrackGenerator.Generate(seed, enableHazards);
            var meshRoot = TrackMeshBuilder.Build(generated.Model, SceneBootstrap.LoadMaterials());
            meshRoot.transform.SetParent(parent, false);
            meshRoot.transform.position = offset;

            if (enableHazards)
            {
                var obstacleRoot = TrackObstacleBuilder.Build(generated.Model, SceneBootstrap.LoadProps(), seed);
                obstacleRoot.transform.SetParent(parent, false);
                obstacleRoot.transform.position = offset;
            }

            return generated;
        }

        static string Describe(GeneratedTrack track)
        {
            var header = $"seed {track.Seed}: {track.Model.TotalLength:F0} m, " +
                         $"width {track.Params.BaseWidth:F1}±{track.Params.WidthVariation:F1} m, " +
                         $"{track.Layout.Count} sections, " +
                         $"{track.Attempts} attempt(s)" +
                         (track.FullyValid ? "" : $" [RELAXED: {track.ValidationNote}]");

            var builder = new System.Text.StringBuilder(header);
            builder.AppendLine();
            builder.Append("    ");

            foreach (var section in track.Model.Sections)
            {
                builder.Append($"{section.Label}({section.Length:F0}m) ");
            }

            return builder.ToString();
        }

        static void ClearPreview()
        {
            var existing = GameObject.Find(k_PreviewRootName);
            if (existing != null)
            {
                DestroyImmediate(existing);
            }
        }
    }
}
