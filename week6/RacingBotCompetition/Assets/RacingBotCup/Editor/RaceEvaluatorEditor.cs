using RacingBotCup.Eval;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace RacingBotCup.EditorTools
{
    /// <summary>
    /// The competitor-facing panel: run, read, submit.
    /// </summary>
    [CustomEditor(typeof(RaceEvaluator))]
    public sealed class RaceEvaluatorEditor : Editor
    {
        UnityWebRequest m_Pending;
        string m_Message;
        MessageType m_MessageType = MessageType.None;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var evaluator = (RaceEvaluator)target;

            EditorGUILayout.Space();
            DrawRunButton(evaluator);
            EditorGUILayout.Space();
            DrawResults(evaluator);
            EditorGUILayout.Space();
            DrawSubmit(evaluator);

            if (!string.IsNullOrEmpty(m_Message))
            {
                EditorGUILayout.HelpBox(m_Message, m_MessageType);
            }
        }

        void DrawRunButton(RaceEvaluator evaluator)
        {
            using (new EditorGUI.DisabledScope(evaluator.IsRunning))
            {
                if (GUILayout.Button(
                        EditorApplication.isPlaying ? "평가 실행" : "평가 실행 (Play 모드 진입)",
                        GUILayout.Height(32f)))
                {
                    if (EditorApplication.isPlaying)
                    {
                        evaluator.RunEvaluation();
                    }
                    else
                    {
                        // The evaluation needs physics and ML-Agents inference, both of which only
                        // exist in play mode. RunOnStart in the scene handles the rest.
                        EditorApplication.EnterPlaymode();
                    }
                }
            }

            EditorGUILayout.LabelField("상태", evaluator.Status);
        }

        static void DrawResults(RaceEvaluator evaluator)
        {
            EditorGUILayout.LabelField("트랙별 결과", EditorStyles.boldLabel);

            if (evaluator.Results.Count == 0)
            {
                EditorGUILayout.LabelField("아직 결과가 없습니다.", EditorStyles.miniLabel);
                return;
            }

            foreach (var row in evaluator.Results)
            {
                EditorGUILayout.LabelField(row.ToString(), EditorStyles.miniLabel);
            }
        }

        void DrawSubmit(RaceEvaluator evaluator)
        {
            using (new EditorGUI.DisabledScope(m_Pending != null))
            {
                if (GUILayout.Button("결과 제출", GUILayout.Height(32f)))
                {
                    Submit(evaluator);
                }
            }

            if (!evaluator.HasResults && GUILayout.Button("최근 결과 불러오기"))
            {
                if (evaluator.TryLoadLatestResult(out var error))
                {
                    Report("최근 결과를 불러왔습니다.", MessageType.Info);
                }
                else
                {
                    Report(error, MessageType.Warning);
                }
            }
        }

        void Submit(RaceEvaluator evaluator)
        {
            var request = evaluator.BuildSubmission(out var error);
            if (request == null)
            {
                Report(error, MessageType.Warning);
                return;
            }

            m_Pending = request;
            Report("제출 중…", MessageType.None);
            request.SendWebRequest();

            // UnityWebRequest has no coroutine to yield on outside play mode, so the editor pumps
            // it from its own update loop instead.
            EditorApplication.update += PumpSubmission;
        }

        void PumpSubmission()
        {
            if (m_Pending == null)
            {
                EditorApplication.update -= PumpSubmission;
                return;
            }

            if (!m_Pending.isDone)
            {
                return;
            }

            EditorApplication.update -= PumpSubmission;

            var succeeded = FormSubmitter.Succeeded(m_Pending, out var message);
            Report(message, succeeded ? MessageType.Info : MessageType.Error);

            m_Pending.Dispose();
            m_Pending = null;
            Repaint();
        }

        void Report(string message, MessageType type)
        {
            m_Message = message;
            m_MessageType = type;
            Repaint();
        }
    }
}
