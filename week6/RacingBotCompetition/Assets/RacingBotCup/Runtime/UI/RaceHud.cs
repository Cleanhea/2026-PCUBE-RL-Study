using RacingBotCup.Racing;
using RacingBotCup.Vehicle;
using UnityEngine;
using UnityEngine.UI;

namespace RacingBotCup.UI
{
    /// <summary>
    /// Speed and lap clock for whichever car is currently being driven.
    ///
    /// Builds its own canvas at runtime rather than relying on a prefab wired up in the scene:
    /// there is nothing to reconnect if a competitor rebuilds the scene, and no font asset to
    /// import. The car is found the same way the chase camera finds it — by height — so the readout
    /// follows the ghost or the competitor's car without being told which is which.
    /// </summary>
    public sealed class RaceHud : MonoBehaviour
    {
        /// <summary>Speed the bar treats as full scale, in m/s. Roughly the car's top speed.</summary>
        const float k_FullScale = 50f;

        [SerializeField] CarController m_Car;

        [Tooltip("비워 두면 현재 주행 중인 차를 자동으로 찾습니다")]
        [SerializeField] bool m_AutoFindCar = true;

        [Tooltip("랩 타임 표시")]
        [SerializeField] bool m_ShowTimer = true;

        static readonly Color k_Dim = new Color(1f, 1f, 1f, 0.7f);
        static readonly Color k_OffTrack = new Color(1f, 0.45f, 0.35f);
        static readonly Color k_LapDone = new Color(0.55f, 0.95f, 0.55f);

        Text m_Readout;
        Text m_Caption;
        Text m_Timer;
        Text m_LapTime;
        RectTransform m_BarFill;
        Eval.ChaseCamera m_Chase;
        RaceClock m_Clock;
        float m_NextSearch;

        void Start()
        {
            Build();
        }

        void Update()
        {
            if (m_AutoFindCar)
            {
                AcquireCar();
            }

            if (m_Car == null || m_Readout == null)
            {
                return;
            }

            var kilometresPerHour = Mathf.Abs(m_Car.ForwardSpeed) * 3.6f;
            m_Readout.text = Mathf.RoundToInt(kilometresPerHour).ToString();

            var fill = Mathf.Clamp01(Mathf.Abs(m_Car.ForwardSpeed) / k_FullScale);
            m_BarFill.anchorMax = new Vector2(fill, 1f);

            // Red once the tyres are on gravel — the readout doubles as an "you are off" light.
            var offTrack = m_Car.WheelsOffTrack > 0;
            m_Readout.color = offTrack ? k_OffTrack : Color.white;
            m_Caption.text = offTrack ? "OFF TRACK" : "km/h";

            UpdateTimer();
        }

        void UpdateTimer()
        {
            if (m_Timer == null)
            {
                return;
            }

            if (m_Clock == null)
            {
                m_Timer.text = RaceClock.Format(0f);
                m_LapTime.text = "";
                return;
            }

            // Once the lap is in, the running clock is frozen anyway — showing the finished time in
            // green is what tells you at a glance that this car is done rather than merely stopped.
            if (m_Clock.IsFinished)
            {
                m_Timer.text = RaceClock.Format(m_Clock.LastLap);
                m_Timer.color = k_LapDone;
                m_LapTime.text = "LAP COMPLETE";
                return;
            }

            m_Timer.text = RaceClock.Format(m_Clock.Elapsed);
            m_Timer.color = Color.white;

            // Training rolls straight into the next episode, so the previous lap is the only thing
            // telling you whether this one is going better.
            m_LapTime.text = m_Clock.HasLap
                ? $"LAST {RaceClock.Format(m_Clock.LastLap)}"
                : "LAP TIME";
        }

        void AcquireCar()
        {
            // Show the readouts for whatever the camera is looking at. With ten environments running
            // at once, picking any car off the scene would readily land on one in another postcode.
            if (m_Chase == null)
            {
                m_Chase = FindFirstObjectByType<Eval.ChaseCamera>();
            }

            if (m_Chase != null && m_Chase.Target != null)
            {
                SetCar(m_Chase.Target);
                return;
            }

            if (m_Car != null && m_Car.transform.position.y > -100f)
            {
                return;
            }

            if (Time.unscaledTime < m_NextSearch)
            {
                return;
            }

            m_NextSearch = Time.unscaledTime + 0.25f;

            foreach (var car in FindObjectsByType<CarController>(FindObjectsSortMode.None))
            {
                // Parked cars sit far below the circuit; skip them.
                if (car.transform.position.y > -100f)
                {
                    SetCar(car);
                    return;
                }
            }
        }

        void SetCar(CarController car)
        {
            if (m_Car == car && m_Clock != null)
            {
                return;
            }

            m_Car = car;

            // The clock is attached when the car is placed on a circuit, so a car sitting in an
            // unstarted scene legitimately has none.
            m_Clock = car != null ? car.GetComponent<RaceClock>() : null;
        }

        void Build()
        {
            var canvasObject = new GameObject("RaceHudCanvas");
            canvasObject.transform.SetParent(transform, false);

            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            BuildSpeedometer(canvasObject.transform);

            if (m_ShowTimer)
            {
                BuildTimer(canvasObject.transform);
            }
        }

        void BuildSpeedometer(Transform parent)
        {
            var panel = CreatePanel("Speedometer", parent, new Vector2(1f, 0f), new Vector2(-40f, 40f),
                new Vector2(300f, 140f));

            m_Readout = CreateText("Readout", panel, 72, TextAnchor.LowerRight);
            var readoutRect = m_Readout.rectTransform;
            readoutRect.anchorMin = new Vector2(0f, 0.3f);
            readoutRect.anchorMax = new Vector2(1f, 1f);
            readoutRect.offsetMin = new Vector2(16f, 0f);
            readoutRect.offsetMax = new Vector2(-16f, -8f);

            m_Caption = CreateText("Caption", panel, 24, TextAnchor.UpperRight);
            var captionRect = m_Caption.rectTransform;
            captionRect.anchorMin = new Vector2(0f, 0.12f);
            captionRect.anchorMax = new Vector2(1f, 0.32f);
            captionRect.offsetMin = new Vector2(16f, 0f);
            captionRect.offsetMax = new Vector2(-16f, 0f);
            m_Caption.text = "km/h";
            m_Caption.color = k_Dim;

            var barBack = CreateRect("BarBackground", panel);
            barBack.anchorMin = new Vector2(0f, 0f);
            barBack.anchorMax = new Vector2(1f, 0.12f);
            barBack.offsetMin = new Vector2(16f, 12f);
            barBack.offsetMax = new Vector2(-16f, 0f);
            barBack.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.15f);

            m_BarFill = CreateRect("BarFill", barBack);
            m_BarFill.anchorMin = Vector2.zero;
            m_BarFill.anchorMax = new Vector2(0f, 1f);
            m_BarFill.offsetMin = Vector2.zero;
            m_BarFill.offsetMax = Vector2.zero;
            m_BarFill.gameObject.AddComponent<Image>().color = new Color(0.95f, 0.78f, 0.25f, 0.95f);
        }

        void BuildTimer(Transform parent)
        {
            var panel = CreatePanel("LapTimer", parent, new Vector2(0.5f, 1f), new Vector2(0f, -32f),
                new Vector2(360f, 118f));

            m_Timer = CreateText("Time", panel, 56, TextAnchor.LowerCenter);
            var timerRect = m_Timer.rectTransform;
            timerRect.anchorMin = new Vector2(0f, 0.28f);
            timerRect.anchorMax = new Vector2(1f, 1f);
            timerRect.offsetMin = new Vector2(12f, 0f);
            timerRect.offsetMax = new Vector2(-12f, -8f);
            m_Timer.text = RaceClock.Format(0f);

            m_LapTime = CreateText("Label", panel, 22, TextAnchor.UpperCenter);
            var labelRect = m_LapTime.rectTransform;
            labelRect.anchorMin = new Vector2(0f, 0.04f);
            labelRect.anchorMax = new Vector2(1f, 0.28f);
            labelRect.offsetMin = new Vector2(12f, 0f);
            labelRect.offsetMax = new Vector2(-12f, 0f);
            m_LapTime.text = "LAP TIME";
            m_LapTime.color = k_Dim;
        }

        static RectTransform CreatePanel(
            string name, Transform parent, Vector2 anchor, Vector2 offset, Vector2 size)
        {
            var panel = CreateRect(name, parent);
            panel.anchorMin = anchor;
            panel.anchorMax = anchor;
            panel.pivot = anchor;
            panel.anchoredPosition = offset;
            panel.sizeDelta = size;

            panel.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.45f);
            return panel;
        }

        static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        static Text CreateText(string name, Transform parent, int size, TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var text = go.AddComponent<Text>();

            // Built into the engine, so there is no font asset to import and nothing to break when
            // the project is cloned fresh.
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = size;
            text.alignment = alignment;
            text.color = Color.white;
            text.raycastTarget = false;

            // Text truncates by default, and truncation drops the whole line rather than clipping
            // it — a glyph one pixel taller than its box simply vanishes. Letting it overflow keeps
            // the readout on screen whatever the window is scaled to.
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }
    }
}
