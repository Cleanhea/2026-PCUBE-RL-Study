using UnityEditor;
using UnityEngine;

namespace RacingBotCup.EditorTools
{
    /// <summary>
    /// Creates the tags and layer the harness depends on.
    ///
    /// Done through <see cref="SerializedObject"/> rather than by editing
    /// ProjectSettings/TagManager.asset by hand: the Editor keeps that file cached in memory, so a
    /// text edit made while Unity is running is invisible until a restart — and can be overwritten
    /// the next time Unity saves. Running on load also means a fresh clone is set up automatically.
    /// </summary>
    [InitializeOnLoad]
    public static class ProjectSetup
    {
        const string k_TagManagerPath = "ProjectSettings/TagManager.asset";

        /// <summary>Layers 0–7 are reserved by Unity, so user layers start here.</summary>
        const int k_FirstUserLayer = 8;

        static readonly string[] k_RequiredTags =
        {
            Track.TrackTags.Track,
            Track.TrackTags.OffTrack,
            Track.TrackTags.Obstacle,
            Track.TrackTags.Ramp,
        };

        static ProjectSetup()
        {
            // Deferred: on the very first import the asset database may not be ready yet.
            EditorApplication.delayCall += () => Apply(false);
        }

        [MenuItem("RacingBotCup/Repair Project Settings", priority = 40)]
        public static void ApplyFromMenu()
        {
            Apply(true);
        }

        static void Apply(bool verbose)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath(k_TagManagerPath);
            if (assets == null || assets.Length == 0)
            {
                Debug.LogError($"[RacingBotCup] Could not open {k_TagManagerPath}.");
                return;
            }

            var tagManager = new SerializedObject(assets[0]);
            var changed = false;

            changed |= EnsureTags(tagManager);
            changed |= EnsureLayer(tagManager, Track.RacingLayers.VehicleLayerName);

            if (changed)
            {
                tagManager.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
                Debug.Log("[RacingBotCup] Project tags and layers updated.");
            }
            else if (verbose)
            {
                Debug.Log("[RacingBotCup] Project tags and layers were already correct.");
            }
        }

        static bool EnsureTags(SerializedObject tagManager)
        {
            var tags = tagManager.FindProperty("tags");
            if (tags == null)
            {
                return false;
            }

            var changed = false;

            foreach (var required in k_RequiredTags)
            {
                var exists = false;
                for (var i = 0; i < tags.arraySize; i++)
                {
                    if (tags.GetArrayElementAtIndex(i).stringValue == required)
                    {
                        exists = true;
                        break;
                    }
                }

                if (exists)
                {
                    continue;
                }

                tags.InsertArrayElementAtIndex(tags.arraySize);
                tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = required;
                changed = true;
            }

            return changed;
        }

        static bool EnsureLayer(SerializedObject tagManager, string layerName)
        {
            var layers = tagManager.FindProperty("layers");
            if (layers == null)
            {
                return false;
            }

            for (var i = 0; i < layers.arraySize; i++)
            {
                if (layers.GetArrayElementAtIndex(i).stringValue == layerName)
                {
                    return false;
                }
            }

            for (var i = k_FirstUserLayer; i < layers.arraySize; i++)
            {
                var slot = layers.GetArrayElementAtIndex(i);
                if (!string.IsNullOrEmpty(slot.stringValue))
                {
                    continue;
                }

                slot.stringValue = layerName;
                return true;
            }

            Debug.LogError(
                $"[RacingBotCup] No free user layer for '{layerName}'. " +
                "Free one in Project Settings > Tags and Layers, then run RacingBotCup > Repair Project Settings.");
            return false;
        }
    }
}
