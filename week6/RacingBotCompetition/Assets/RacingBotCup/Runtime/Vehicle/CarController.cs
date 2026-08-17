using System;
using UnityEngine;

namespace RacingBotCup.Vehicle
{
    /// <summary>
    /// The one and only vehicle in the competition. Physics comes entirely from
    /// <see cref="CarSpec"/>; nothing here is tunable per competitor.
    ///
    /// Every driver — the baseline bot, an ML-Agents policy, or a human on the keyboard —
    /// reaches the car through the single <see cref="SetInput"/> door.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class CarController : MonoBehaviour
    {
        [Serializable]
        public sealed class Wheel
        {
            public WheelCollider Collider;
            public Transform Visual;
            public bool IsSteering;
            public float TorqueShare = 0.25f;

            [NonSerialized] public float AppliedGripMultiplier = float.NaN;
        }

        [SerializeField] Wheel[] m_Wheels = Array.Empty<Wheel>();

        Rigidbody m_Body;
        float m_SteerInput;
        float m_ThrottleInput;
        float m_CurrentSteerAngle;
        float m_OffRoadBrakeForce;

        /// <summary>Assigned by the track/harness. Null means "assume tarmac everywhere".</summary>
        public ISurfaceProvider SurfaceProvider { get; set; }

        public Rigidbody Body => m_Body;

        /// <summary>Signed forward speed in m/s (negative when reversing).</summary>
        public float ForwardSpeed { get; private set; }

        /// <summary>Speed magnitude in m/s.</summary>
        public float Speed { get; private set; }

        public Vector3 LocalVelocity { get; private set; }

        public Vector3 LocalAngularVelocity { get; private set; }

        /// <summary>Current road-wheel angle normalised to -1..1 of <see cref="CarSpec.MaxSteerAngle"/>.</summary>
        public float SteerAngleNormalized => m_CurrentSteerAngle / CarSpec.MaxSteerAngle;

        public float SteerAngleDegrees => m_CurrentSteerAngle;

        /// <summary>Angle in degrees between the body's forward axis and its velocity.</summary>
        public float SlipAngle { get; private set; }

        public int WheelsOffTrack { get; private set; }

        public bool AllWheelsOffTrack => m_Wheels.Length > 0 && WheelsOffTrack >= m_Wheels.Length;

        /// <summary>
        /// True while this car is part of a live run. Sequential evaluation leaves each finished
        /// circuit's cars sitting where they stopped rather than destroying them, so at any moment
        /// there can be several cars in the scene that satisfy every other "is this a real racing
        /// car" check — this is what tells a spectating camera which one is not simply parked.
        /// </summary>
        public bool IsRacing { get; set; }

        public float LastSteerInput => m_SteerInput;

        public float LastThrottleInput => m_ThrottleInput;

        void Awake()
        {
            m_Body = GetComponent<Rigidbody>();
            m_Body.mass = CarSpec.Mass;
            m_Body.centerOfMass = CarSpec.CenterOfMass;
            m_Body.maxAngularVelocity = CarSpec.MaxAngularSpeed;

            foreach (var wheel in m_Wheels)
            {
                ConfigureWheel(wheel);
            }
        }

        /// <summary>Wires the wheel list. Called by <see cref="CarFactory"/> during rig assembly.</summary>
        public void Configure(Wheel[] wheels)
        {
            m_Wheels = wheels ?? Array.Empty<Wheel>();
            if (m_Body != null)
            {
                foreach (var wheel in m_Wheels)
                {
                    ConfigureWheel(wheel);
                }
            }
        }

        static void ConfigureWheel(Wheel wheel)
        {
            var collider = wheel.Collider;
            if (collider == null)
            {
                return;
            }

            collider.mass = CarSpec.WheelMass;
            collider.suspensionDistance = CarSpec.SuspensionDistance;
            collider.forceAppPointDistance = CarSpec.ForceAppPointDistance;

            var suspension = collider.suspensionSpring;
            suspension.spring = CarSpec.SuspensionSpring;
            suspension.damper = CarSpec.SuspensionDamper;
            suspension.targetPosition = CarSpec.SuspensionTargetPosition;
            collider.suspensionSpring = suspension;

            wheel.AppliedGripMultiplier = float.NaN;
            ApplyGrip(wheel, 1f);
        }

        static void ApplyGrip(Wheel wheel, float multiplier)
        {
            if (Mathf.Approximately(wheel.AppliedGripMultiplier, multiplier))
            {
                return;
            }

            var forward = wheel.Collider.forwardFriction;
            forward.extremumSlip = CarSpec.ForwardExtremumSlip;
            forward.extremumValue = CarSpec.ForwardExtremumValue;
            forward.asymptoteSlip = CarSpec.ForwardAsymptoteSlip;
            forward.asymptoteValue = CarSpec.ForwardAsymptoteValue;
            forward.stiffness = CarSpec.ForwardStiffness * multiplier;
            wheel.Collider.forwardFriction = forward;

            var sideways = wheel.Collider.sidewaysFriction;
            sideways.extremumSlip = CarSpec.SidewaysExtremumSlip;
            sideways.extremumValue = CarSpec.SidewaysExtremumValue;
            sideways.asymptoteSlip = CarSpec.SidewaysAsymptoteSlip;
            sideways.asymptoteValue = CarSpec.SidewaysAsymptoteValue;
            sideways.stiffness = CarSpec.SidewaysStiffness * multiplier;
            wheel.Collider.sidewaysFriction = sideways;

            wheel.AppliedGripMultiplier = multiplier;
        }

        /// <summary>
        /// The single control surface. Both values are clamped to -1..1.
        /// Positive throttle accelerates; negative brakes, then reverses once nearly stopped.
        /// </summary>
        public void SetInput(float steer, float throttle)
        {
            m_SteerInput = Mathf.Clamp(steer, -1f, 1f);
            m_ThrottleInput = Mathf.Clamp(throttle, -1f, 1f);
        }

        /// <summary>Drops the car at a pose with all motion cleared. Used between evaluation runs.</summary>
        public void ResetTo(Vector3 position, Quaternion rotation)
        {
            m_Body.linearVelocity = Vector3.zero;
            m_Body.angularVelocity = Vector3.zero;
            m_Body.position = position;
            m_Body.rotation = rotation;
            transform.SetPositionAndRotation(position, rotation);

            m_SteerInput = 0f;
            m_ThrottleInput = 0f;
            m_CurrentSteerAngle = 0f;
            ForwardSpeed = 0f;
            Speed = 0f;
            LocalVelocity = Vector3.zero;
            LocalAngularVelocity = Vector3.zero;
            SlipAngle = 0f;
            WheelsOffTrack = 0;
            m_OffRoadBrakeForce = 0f;

            foreach (var wheel in m_Wheels)
            {
                if (wheel.Collider == null)
                {
                    continue;
                }

                wheel.Collider.motorTorque = 0f;
                wheel.Collider.brakeTorque = 0f;
                wheel.Collider.steerAngle = 0f;
                ApplyGrip(wheel, 1f);
            }
        }

        /// <summary>
        /// When true this component stops driving itself from <c>FixedUpdate</c> and expects
        /// <see cref="Step"/> to be called explicitly. The evaluation harness sets this so that
        /// input, vehicle forces and <c>Physics.Simulate</c> happen in a fixed, known order —
        /// leaving it to Unity's update loop would make lap times depend on script execution order.
        /// </summary>
        public bool ExternalStepping { get; set; }

        void FixedUpdate()
        {
            if (!ExternalStepping)
            {
                Step(Time.fixedDeltaTime);
            }
        }

        /// <summary>Applies one step of vehicle forces. Call immediately before simulating physics.</summary>
        public void Step(float deltaTime)
        {
            UpdateTelemetry();
            UpdateSurface();
            ApplySteering(deltaTime);
            ApplyDrive();
            ApplyAero();
            ApplyOffRoadDrag();
            UpdateVisuals();
        }

        void UpdateTelemetry()
        {
            var velocity = m_Body.linearVelocity;
            Speed = velocity.magnitude;
            LocalVelocity = transform.InverseTransformDirection(velocity);
            LocalAngularVelocity = transform.InverseTransformDirection(m_Body.angularVelocity);
            ForwardSpeed = LocalVelocity.z;

            SlipAngle = Speed < 0.5f
                ? 0f
                : Mathf.Atan2(LocalVelocity.x, Mathf.Abs(LocalVelocity.z)) * Mathf.Rad2Deg;
        }

        void UpdateSurface()
        {
            var provider = SurfaceProvider;
            var offCount = 0;
            var brakeForceSum = 0f;

            foreach (var wheel in m_Wheels)
            {
                if (wheel.Collider == null)
                {
                    continue;
                }

                var sample = provider == null
                    ? SurfaceSample.Tarmac
                    : provider.SampleSurface(wheel.Collider.transform.position);

                if (!sample.OnTrack)
                {
                    offCount++;
                }

                brakeForceSum += sample.OffRoadBrakeForce;

                ApplyGrip(wheel, sample.GripMultiplier);
            }

            WheelsOffTrack = offCount;
            m_OffRoadBrakeForce = brakeForceSum;
        }

        void ApplySteering(float deltaTime)
        {
            var speedFactor = Mathf.Lerp(
                1f,
                CarSpec.SteerAtHighSpeed,
                Mathf.Clamp01(Speed / CarSpec.SteerFalloffSpeed));

            var target = m_SteerInput * CarSpec.MaxSteerAngle * speedFactor;
            var maxDelta = CarSpec.SteerRateDegPerSec * deltaTime;
            m_CurrentSteerAngle = Mathf.MoveTowards(m_CurrentSteerAngle, target, maxDelta);

            foreach (var wheel in m_Wheels)
            {
                if (wheel.IsSteering && wheel.Collider != null)
                {
                    wheel.Collider.steerAngle = m_CurrentSteerAngle;
                }
            }
        }

        void ApplyDrive()
        {
            float motor;
            float brake;

            if (m_ThrottleInput >= 0f)
            {
                motor = m_ThrottleInput * CarSpec.MaxMotorTorque;
                brake = 0f;
            }
            else if (ForwardSpeed > CarSpec.ReverseEngageSpeed)
            {
                // Still rolling forward: a negative command is the brake pedal.
                motor = 0f;
                brake = -m_ThrottleInput * CarSpec.MaxBrakeTorque;
            }
            else
            {
                motor = m_ThrottleInput * CarSpec.MaxMotorTorque * CarSpec.ReverseTorqueScale;
                brake = 0f;
            }

            foreach (var wheel in m_Wheels)
            {
                if (wheel.Collider == null)
                {
                    continue;
                }

                wheel.Collider.motorTorque = motor * wheel.TorqueShare * 4f;
                wheel.Collider.brakeTorque = brake;
            }
        }

        void ApplyAero()
        {
            var speedSquared = Speed * Speed;
            m_Body.AddForce(-transform.up * (CarSpec.DownforceCoefficient * speedSquared));

            if (Speed > 0.01f)
            {
                var dragDirection = -m_Body.linearVelocity / Speed;
                m_Body.AddForce(dragDirection * (CarSpec.AeroDragCoefficient * speedSquared));
            }
        }

        /// <summary>
        /// Resistive force from driving on the grass beyond the run-off band. Summed across
        /// however many wheels are out there, so two wheels off costs half what four does.
        /// </summary>
        void ApplyOffRoadDrag()
        {
            if (m_OffRoadBrakeForce <= 0f || Speed < 0.01f)
            {
                return;
            }

            var dragDirection = -m_Body.linearVelocity / Speed;
            m_Body.AddForce(dragDirection * m_OffRoadBrakeForce);
        }

        void UpdateVisuals()
        {
            foreach (var wheel in m_Wheels)
            {
                if (wheel.Collider == null || wheel.Visual == null)
                {
                    continue;
                }

                wheel.Collider.GetWorldPose(out var position, out var rotation);
                wheel.Visual.SetPositionAndRotation(position, rotation);
            }
        }
    }
}
