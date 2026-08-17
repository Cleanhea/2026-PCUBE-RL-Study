using System;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace RacingBotCup.Vehicle
{
    /// <summary>
    /// The sealed vehicle specification. Every competitor drives exactly this car —
    /// "차는 똑같고, 뇌만 다르다".
    ///
    /// Do not edit these values. <see cref="SpecHash"/> is derived from them by reflection and
    /// travels inside every submission, so a changed constant produces a different hash and the
    /// result no longer matches everyone else's.
    /// </summary>
    public static class CarSpec
    {
        public const string Version = "1.0.0";

        // ---- Chassis -------------------------------------------------------
        public const float Mass = 1200f;
        public const float CenterOfMassHeight = -0.30f;
        public const float CenterOfMassForward = 0.05f;
        public const float MaxAngularSpeed = 8f;

        // ---- Drivetrain ----------------------------------------------------
        /// <summary>Peak torque per driven wheel (Nm). All four wheels are driven.</summary>
        public const float MaxMotorTorque = 600f;
        public const float FrontTorqueSplit = 0.4f;
        public const float MaxBrakeTorque = 4000f;
        /// <summary>Reverse gear is deliberately weak — recovering from a spin should cost time.</summary>
        public const float ReverseTorqueScale = 0.35f;
        /// <summary>Below this forward speed (m/s) a negative throttle reverses instead of braking.</summary>
        public const float ReverseEngageSpeed = 0.8f;

        // ---- Steering ------------------------------------------------------
        public const float MaxSteerAngle = 32f;
        /// <summary>Steer authority at <see cref="SteerFalloffSpeed"/>, as a fraction of max.</summary>
        public const float SteerAtHighSpeed = 0.32f;
        public const float SteerFalloffSpeed = 40f;
        /// <summary>Slew limit (deg/s). Stops an RL policy from teleporting the wheel between frames.</summary>
        public const float SteerRateDegPerSec = 180f;

        // ---- Aero ----------------------------------------------------------
        /// <summary>Quadratic drag (N per (m/s)^2). Caps top speed near 50 m/s.</summary>
        public const float AeroDragCoefficient = 2.7f;
        /// <summary>Downforce (N per (m/s)^2), applied through the centre of mass.</summary>
        public const float DownforceCoefficient = 2.5f;

        // ---- Suspension ----------------------------------------------------
        public const float SuspensionDistance = 0.25f;
        public const float SuspensionSpring = 35000f;
        public const float SuspensionDamper = 4500f;
        public const float SuspensionTargetPosition = 0.5f;
        public const float ForceAppPointDistance = 0.2f;
        public const float WheelMass = 22f;

        // ---- Tyres ---------------------------------------------------------
        public const float ForwardExtremumSlip = 0.4f;
        public const float ForwardExtremumValue = 1.0f;
        public const float ForwardAsymptoteSlip = 0.8f;
        public const float ForwardAsymptoteValue = 0.6f;
        public const float ForwardStiffness = 2.2f;

        public const float SidewaysExtremumSlip = 0.25f;
        public const float SidewaysExtremumValue = 1.0f;
        public const float SidewaysAsymptoteSlip = 0.5f;
        public const float SidewaysAsymptoteValue = 0.75f;
        public const float SidewaysStiffness = 2.4f;

        /// <summary>
        /// Grip multiplier once a wheel leaves the road. There are no walls on this circuit
        /// (기획서 §5) — going off is punished by losing traction, not by hitting something.
        /// </summary>
        public const float OffTrackGripMultiplier = 0.30f;

        /// <summary>
        /// Extra braking force (N) applied per wheel once it crosses past the gravel run-off band
        /// onto the grass beyond. Zero anywhere on the run-off strip itself — that band still only
        /// costs grip, not speed; going fully off the circuit costs both.
        /// </summary>
        public const float OffRoadBrakeForce = 3500f;

        // ---- Simulation ----------------------------------------------------
        /// <summary>Fixed timestep enforced during evaluation so runs are reproducible.</summary>
        public const float FixedDeltaTime = 0.02f;

        static string s_SpecHash;

        /// <summary>
        /// Short, stable fingerprint of every constant above. Recomputed by reflection rather than
        /// hand-maintained, so adding or editing a field is always reflected here.
        /// </summary>
        public static string SpecHash
        {
            get
            {
                if (s_SpecHash != null)
                {
                    return s_SpecHash;
                }

                var fields = typeof(CarSpec).GetFields(BindingFlags.Public | BindingFlags.Static);
                Array.Sort(fields, (a, b) => string.CompareOrdinal(a.Name, b.Name));

                var builder = new StringBuilder();
                foreach (var field in fields)
                {
                    var value = field.GetValue(null);
                    builder.Append(field.Name).Append('=');
                    builder.Append(value is float f
                        ? f.ToString("R", CultureInfo.InvariantCulture)
                        : Convert.ToString(value, CultureInfo.InvariantCulture));
                    builder.Append(';');
                }

                using (var sha = SHA256.Create())
                {
                    var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                    var hex = new StringBuilder(16);
                    for (var i = 0; i < 8; i++)
                    {
                        hex.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
                    }

                    s_SpecHash = hex.ToString();
                }

                return s_SpecHash;
            }
        }

        public static Vector3 CenterOfMass => new Vector3(0f, CenterOfMassHeight, CenterOfMassForward);
    }
}
