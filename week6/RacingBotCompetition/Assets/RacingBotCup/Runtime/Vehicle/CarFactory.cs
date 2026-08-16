using System.Collections.Generic;
using UnityEngine;

namespace RacingBotCup.Vehicle
{
    /// <summary>
    /// Builds a drivable rig around an art-only vehicle prefab.
    ///
    /// The PolygonStreetRacer presets are meshes and nothing else — no Rigidbody, no
    /// WheelColliders — so the physics rig is assembled here at runtime. Doing it in code rather
    /// than hand-authoring a prefab keeps every competitor's car provably identical: it is derived
    /// from <see cref="CarSpec"/> on load, not from serialised inspector values someone could nudge.
    /// </summary>
    public static class CarFactory
    {
        public const string DefaultArtPrefabPath =
            "Assets/PolygonStreetRacer/Prefabs/Vehicles/Presets/SM_Veh_Sports_Preset_01.prefab";

        static readonly (string Suffix, bool Steering)[] k_WheelSlots =
        {
            ("_Wheel_fl", true),
            ("_Wheel_fr", true),
            ("_Wheel_rl", false),
            ("_Wheel_rr", false),
        };

        /// <summary>
        /// Instantiates <paramref name="artPrefab"/> and wraps it in a physics rig.
        /// The returned car sits at the origin with identity rotation; position it afterwards
        /// with <see cref="CarController.ResetTo"/>.
        /// </summary>
        public static CarController Build(GameObject artPrefab, Transform parent = null, string name = "RaceCar")
        {
            if (artPrefab == null)
            {
                Debug.LogError("[RacingBotCup] CarFactory.Build called with a null art prefab.");
                return null;
            }

            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            var art = Object.Instantiate(artPrefab, root.transform);
            art.name = "Art";
            art.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);

            // The art ships with a few MeshColliders on body panels. Left enabled they fight the
            // WheelColliders for ground contact, so the body gets one clean box instead.
            foreach (var meshCollider in art.GetComponentsInChildren<Collider>(true))
            {
                meshCollider.enabled = false;
            }

            var body = root.AddComponent<Rigidbody>();
            body.mass = CarSpec.Mass;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            var wheels = new List<CarController.Wheel>(k_WheelSlots.Length);
            var wheelRenderers = new List<Renderer>();

            foreach (var slot in k_WheelSlots)
            {
                var visual = FindBySuffix(art.transform, slot.Suffix);
                if (visual == null)
                {
                    Debug.LogError($"[RacingBotCup] Wheel '{slot.Suffix}' not found under {artPrefab.name}. " +
                                   "The art prefab does not follow the expected naming convention.");
                    continue;
                }

                var renderer = visual.GetComponentInChildren<Renderer>();
                if (renderer == null)
                {
                    Debug.LogError($"[RacingBotCup] Wheel '{slot.Suffix}' has no renderer to measure.");
                    continue;
                }

                wheelRenderers.Add(renderer);

                var bounds = renderer.bounds;
                var radius = EstimateWheelRadius(bounds.extents);

                // A pivot preserves whatever baked orientation the wheel mesh happens to have —
                // driving the mesh transform directly would snap it to the collider's axes.
                var pivot = new GameObject($"WheelPivot{slot.Suffix}");
                pivot.transform.SetParent(root.transform, false);
                pivot.transform.SetPositionAndRotation(bounds.center, root.transform.rotation);
                visual.SetParent(pivot.transform, true);

                var colliderObject = new GameObject($"WheelCollider{slot.Suffix}");
                colliderObject.transform.SetParent(root.transform, false);
                colliderObject.transform.SetPositionAndRotation(bounds.center, root.transform.rotation);

                var wheelCollider = colliderObject.AddComponent<WheelCollider>();
                wheelCollider.radius = radius;
                wheelCollider.center = Vector3.zero;

                wheels.Add(new CarController.Wheel
                {
                    Collider = wheelCollider,
                    Visual = pivot.transform,
                    IsSteering = slot.Steering,
                    TorqueShare = (slot.Steering ? CarSpec.FrontTorqueSplit : 1f - CarSpec.FrontTorqueSplit) * 0.5f,
                });
            }

            AddBodyCollider(root, art, wheelRenderers);
            SetLayerRecursively(root, Track.RacingLayers.VehicleLayer);

            var controller = root.AddComponent<CarController>();
            controller.Configure(wheels.ToArray());
            return controller;
        }

        static void SetLayerRecursively(GameObject root, int layer)
        {
            if (layer < 0)
            {
                Debug.LogWarning($"[RacingBotCup] Layer '{Track.RacingLayers.VehicleLayerName}' is not defined. " +
                                 "Ray sensors will see the car's own body.");
                return;
            }

            foreach (var child in root.GetComponentsInChildren<Transform>(true))
            {
                child.gameObject.layer = layer;
            }
        }

        /// <summary>
        /// A wheel's smallest extent is its width; the other two are the rolling radius.
        /// Averaging them tolerates meshes that are slightly out of round.
        /// </summary>
        static float EstimateWheelRadius(Vector3 extents)
        {
            var values = new[] { extents.x, extents.y, extents.z };
            System.Array.Sort(values);
            return (values[1] + values[2]) * 0.5f;
        }

        static void AddBodyCollider(GameObject root, GameObject art, List<Renderer> wheelRenderers)
        {
            var hasBounds = false;
            var bounds = new Bounds();

            foreach (var renderer in art.GetComponentsInChildren<Renderer>(true))
            {
                if (wheelRenderers.Contains(renderer))
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (!hasBounds)
            {
                Debug.LogError("[RacingBotCup] Could not measure the vehicle body: no renderers found.");
                return;
            }

            var boxCollider = root.AddComponent<BoxCollider>();
            boxCollider.center = root.transform.InverseTransformPoint(bounds.center);
            boxCollider.size = bounds.size;
        }

        static Transform FindBySuffix(Transform root, string suffix)
        {
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child.name.EndsWith(suffix, System.StringComparison.Ordinal))
                {
                    return child;
                }
            }

            return null;
        }
    }
}
