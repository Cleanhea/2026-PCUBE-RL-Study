using System.Collections.Generic;
using UnityEngine;

namespace RacingBotCup.Vehicle
{
    /// <summary>
    /// Black streaks on the tarmac wherever a tyre is sliding.
    ///
    /// Purely cosmetic, and deliberately kept that way: it reads <see cref="WheelCollider"/> ground
    /// hits and never writes to them, and it runs in <c>LateUpdate</c> rather than a physics
    /// callback. Evaluation drives the simulation by hand with <c>Physics.Simulate</c>, which does
    /// not raise <c>FixedUpdate</c> at all — so anything scored had better not live there.
    ///
    /// The marks also make the car legible without instrumentation: a policy that scrubs speed by
    /// sliding through every corner leaves a trail that says so.
    /// </summary>
    [RequireComponent(typeof(CarController))]
    public sealed class SkidMarks : MonoBehaviour
    {
        /// <summary>
        /// Turned off while evaluation runs unwatched. Ten environments at several hundred times
        /// real speed would lay down hundreds of metres of trail per frame that nobody will see.
        /// </summary>
        public static bool GloballyEnabled = true;

        // Measured on the baseline bot: a clean lap sits at 0.00-0.36 forward slip and 0.00-0.06
        // sideways. Anything under those is ordinary tyre deformation under power, not a slide, and
        // marking it paints a black line down the whole circuit. A standing start hits 1.28.
        [Tooltip("휠스핀·락업 판정 기준 (전후 슬립)")]
        [SerializeField] float m_ForwardSlipThreshold = 0.6f;

        [Tooltip("드리프트 판정 기준 (좌우 슬립)")]
        [SerializeField] float m_SidewaysSlipThreshold = 0.25f;

        [Tooltip("자국이 남아 있는 시간 (초)")]
        [SerializeField] float m_Duration = 12f;

        [Tooltip("자국의 폭 (m). 타이어 폭 정도")]
        [SerializeField] float m_Width = 0.22f;

        [Tooltip("비워 두면 반투명 검정 재질을 자동으로 만듭니다")]
        [SerializeField] Material m_Material;

        /// <summary>Height above the contact point, to keep the ribbon out of the road surface.</summary>
        const float k_Lift = 0.02f;

        const float k_MinVertexDistance = 0.25f;

        CarController m_Car;
        WheelCollider[] m_Wheels;
        TrailRenderer[] m_Active;
        readonly List<TrailRenderer> m_Fading = new List<TrailRenderer>();
        Material m_Resolved;
        Vector3 m_LastBodyPosition;
        bool m_Tracking;

        void Awake()
        {
            m_Car = GetComponent<CarController>();
            m_Wheels = GetComponentsInChildren<WheelCollider>();
            m_Active = new TrailRenderer[m_Wheels.Length];
            m_Resolved = m_Material != null ? m_Material : CreateFallbackMaterial();
        }

        void LateUpdate()
        {
            if (m_Active == null)
            {
                return;
            }

            // A car put back on the start line teleports. Without this the mark it was in the middle
            // of laying gets stretched across the whole circuit, from where the car was to where it
            // now is.
            var position = transform.position;
            if (m_Tracking && (position - m_LastBodyPosition).sqrMagnitude > TeleportThresholdSquared())
            {
                Clear();
            }

            m_LastBodyPosition = position;
            m_Tracking = true;

            for (var i = 0; i < m_Wheels.Length; i++)
            {
                UpdateWheel(i);
            }

            // Autodestruct has already taken these away; drop the dead references so a long run does
            // not accumulate a list entry per slide.
            m_Fading.RemoveAll(trail => trail == null);
        }

        void UpdateWheel(int index)
        {
            var wheel = m_Wheels[index];
            if (wheel == null)
            {
                return;
            }

            if (!GloballyEnabled || !wheel.GetGroundHit(out var hit))
            {
                Release(index);
                return;
            }

            var slipping =
                Mathf.Abs(hit.sidewaysSlip) > m_SidewaysSlipThreshold ||
                Mathf.Abs(hit.forwardSlip) > m_ForwardSlipThreshold;

            if (!slipping)
            {
                Release(index);
                return;
            }

            // The ribbon lies in the plane the transform's forward axis is normal to, so pointing
            // forward at the surface normal lays it flat on the road instead of billboarding it
            // edge-on towards the camera.
            var pose = hit.point + hit.normal * k_Lift;
            var facing = Quaternion.LookRotation(hit.normal, transform.forward);

            var trail = m_Active[index];
            if (trail == null)
            {
                // One trail per slide, not one per wheel. A single reused trail keeps its old points
                // when emission resumes, and TrailRenderer joins them up — which paints a straight
                // black line across everything between the last corner and this one.
                trail = CreateTrail(pose, facing);
                m_Active[index] = trail;
            }
            else
            {
                trail.transform.SetPositionAndRotation(pose, facing);
            }

            trail.emitting = true;
        }

        /// <summary>Ends the mark this wheel was laying and leaves it to fade out on its own.</summary>
        void Release(int index)
        {
            var trail = m_Active[index];
            if (trail == null)
            {
                return;
            }

            m_Active[index] = null;
            trail.emitting = false;

            // A slide that lasted a single frame never travelled far enough to record two points, so
            // there is no ribbon to fade — and a hard lap flicks past the threshold constantly.
            // Left alive these outnumber the visible marks roughly thirty to one.
            if (trail.positionCount < 2)
            {
                Destroy(trail.gameObject);
                return;
            }

            m_Fading.Add(trail);
        }

        void OnDestroy()
        {
            // The marks are not children of the car, so they outlive it unless we say otherwise.
            Clear();
        }

        /// <summary>
        /// One shared, never-moving parent for every car's marks, so the hierarchy does not fill up
        /// with loose objects.
        /// </summary>
        static Transform Container()
        {
            if (s_Container == null)
            {
                s_Container = new GameObject("SkidMarks").transform;
            }

            return s_Container;
        }

        static Transform s_Container;

        /// <summary>Wipes every mark this car has laid down. Called on a teleport.</summary>
        public void Clear()
        {
            for (var i = 0; i < m_Active.Length; i++)
            {
                if (m_Active[i] != null)
                {
                    Destroy(m_Active[i].gameObject);
                    m_Active[i] = null;
                }
            }

            foreach (var trail in m_Fading)
            {
                if (trail != null)
                {
                    Destroy(trail.gameObject);
                }
            }

            m_Fading.Clear();
        }

        float TeleportThresholdSquared()
        {
            // Generous: anything the car covers legitimately between two frames is well under this,
            // and a reset moves it by at least the length of the grid.
            var plausible = Mathf.Max(5f, m_Car.Speed * Time.deltaTime * 3f);
            return plausible * plausible;
        }

        TrailRenderer CreateTrail(Vector3 position, Quaternion rotation)
        {
            // Deliberately NOT a child of the car. Writing to a transform inside a rigidbody's
            // hierarchy every frame invites Unity to re-sync that body into PhysX, and this
            // component must not be able to move the numbers a competitor is scored on. The marks
            // are world-space anyway, so a static container is the honest place for them.
            var go = new GameObject("SkidMark");
            go.transform.SetParent(Container(), false);
            go.transform.SetPositionAndRotation(position, rotation);

            // Self-cleaning rather than TrailRenderer.autodestruct: that only fires once the ribbon
            // has emptied itself, which never happens for a mark that recorded nothing. A delayed
            // Destroy is unconditional, and by m_Duration the ribbon has fully faded anyway.
            Destroy(go, m_Duration + 1f);

            var trail = go.AddComponent<TrailRenderer>();
            trail.time = m_Duration;
            trail.startWidth = m_Width;
            trail.endWidth = m_Width;
            trail.minVertexDistance = k_MinVertexDistance;
            trail.numCapVertices = 0;
            trail.alignment = LineAlignment.View;
            trail.textureMode = LineTextureMode.Stretch;
            trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            trail.receiveShadows = false;
            trail.sharedMaterial = m_Resolved;
            trail.emitting = false;
            return trail;
        }

        /// <summary>
        /// A transparent black ribbon, built in code so the component works on a car that was
        /// dropped into a scene without the material asset wired up.
        /// </summary>
        static Material CreateFallbackMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            if (shader == null)
            {
                return null;
            }

            var material = new Material(shader) { name = "SkidMark (runtime)" };
            ConfigureAsSkidMark(material);
            return material;
        }

        /// <summary>
        /// Puts a URP Unlit material into its transparent mode. Shared with the editor tool that
        /// bakes the material asset, so the asset and the fallback cannot drift apart.
        /// </summary>
        public static void ConfigureAsSkidMark(Material material)
        {
            var colour = new Color(0.05f, 0.05f, 0.06f, 0.55f);

            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_ZWrite", 0f);
            material.SetOverrideTag("RenderType", "Transparent");
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");

            foreach (var property in new[] { "_BaseColor", "_Color" })
            {
                if (material.HasProperty(property))
                {
                    material.SetColor(property, colour);
                }
            }
        }
    }
}
