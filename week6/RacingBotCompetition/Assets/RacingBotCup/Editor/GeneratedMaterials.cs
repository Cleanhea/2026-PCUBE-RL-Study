using RacingBotCup.Track;
using RacingBotCup.Vehicle;
using UnityEditor;
using UnityEngine;

namespace RacingBotCup.EditorTools
{
    /// <summary>
    /// The two materials the competition needs that the art pack does not provide: flat grass and a
    /// tyre mark.
    ///
    /// They are created as assets rather than instantiated at runtime so every circuit and every
    /// car shares one instance — ten environments each minting their own copy is ten materials, ten
    /// draw call batches, and ten things to look at in the profiler. They are also editable: change
    /// the colour on the asset and every track picks it up.
    /// </summary>
    public static class GeneratedMaterials
    {
        public const string Directory = "Assets/RacingBotCup/Materials";
        public const string GrassPath = Directory + "/Grass.mat";
        public const string SkidMarkPath = Directory + "/SkidMark.mat";

        /// <summary>Flat green for the ground beyond the run-off.</summary>
        public static Material LoadOrCreateGrass()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(GrassPath);
            if (existing != null)
            {
                return existing;
            }

            var material = NewLitMaterial("Grass");
            material.color = TrackMeshBuilder.GrassGreen;

            // Grass is not shiny. Left at the URP default the whole field mirrors the sun as one
            // enormous specular blob when the camera is low, which is most of the time.
            SetIfPresent(material, "_Smoothness", 0f);
            SetIfPresent(material, "_Glossiness", 0f);

            return Save(material, GrassPath);
        }

        /// <summary>Transparent black ribbon for <see cref="SkidMarks"/>.</summary>
        public static Material LoadOrCreateSkidMark()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(SkidMarkPath);
            if (existing != null)
            {
                return existing;
            }

            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            if (shader == null)
            {
                return null;
            }

            var material = new Material(shader) { name = "SkidMark" };
            SkidMarks.ConfigureAsSkidMark(material);
            return Save(material, SkidMarkPath);
        }

        static Material NewLitMaterial(string name)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            return new Material(shader) { name = name };
        }

        static void SetIfPresent(Material material, string property, float value)
        {
            if (material.HasProperty(property))
            {
                material.SetFloat(property, value);
            }
        }

        static Material Save(Material material, string path)
        {
            System.IO.Directory.CreateDirectory(Directory);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }
    }
}
