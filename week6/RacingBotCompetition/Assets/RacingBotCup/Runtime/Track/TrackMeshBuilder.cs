using System.Collections.Generic;
using UnityEngine;

namespace RacingBotCup.Track
{
    /// <summary>
    /// Turns a <see cref="TrackModel"/> into the geometry you actually race on: racing surface,
    /// red-and-white kerbs down both edges, a start/finish line, gravel run-off and the grass
    /// beyond it.
    ///
    /// There are still no barriers by design — leaving the circuit costs grip, not a collision
    /// (기획서 §5). The gravel band exists so that "off" is unmistakable at a glance.
    /// </summary>
    public static class TrackMeshBuilder
    {
        const float k_SurfaceSpacing = 2.5f;
        /// <summary>
        /// Width of the gravel band outside each kerb. The layout validator needs this too — two
        /// stretches of circuit that clear each other on tarmac can still have their run-off
        /// overlap, which is what made early circuits look braided from above.
        /// </summary>
        public const float RunoffWidth = 5f;
        const float k_GroundDrop = 0.03f;
        const float k_RunoffDrop = 0.015f;
        const float k_KerbLift = 0.03f;
        const float k_GroundMargin = 140f;
        const float k_GroundCellSize = 150f;

        /// <summary>Length of a single red or white kerb block.</summary>
        const float k_KerbBlockLength = 2.5f;

        const float k_KerbInnerInset = 0.3f;
        const float k_KerbOuterWidth = 1.1f;

        const float k_StartLineLength = 1.6f;
        const int k_StartLineSquares = 8;

        /// <summary>
        /// Colour of the grass beyond the run-off. Flat green on purpose: it is the one surface
        /// nobody should be looking at, and a plain field makes the tarmac and the sand read
        /// instantly against it from any height.
        /// </summary>
        public static readonly Color GrassGreen = new Color(0.29f, 0.52f, 0.24f);

        public static GameObject Build(TrackModel model, TrackMaterials materials)
        {
            var root = new GameObject($"Circuit_{model.Params.Seed}");

            var road = BuildRoad(model);
            road.transform.SetParent(root.transform, false);
            Apply(road, materials?.Road, new Color(0.20f, 0.20f, 0.21f));

            var runoff = BuildRunoff(model);
            runoff.transform.SetParent(root.transform, false);
            Apply(runoff, materials?.Runoff, new Color(0.42f, 0.36f, 0.24f));

            var ground = BuildGround(model);
            ground.transform.SetParent(root.transform, false);
            Apply(ground, materials?.Ground, GrassGreen);

            return root;
        }

        // ------------------------------------------------------------------
        // Road: surface (collides) + kerbs and start line (visual only)
        // ------------------------------------------------------------------

        static GameObject BuildRoad(TrackModel model)
        {
            var visual = new MeshAccumulator();
            var collision = new MeshAccumulator();

            AppendSurface(model, visual, collision);
            AppendKerbs(model, visual);
            AppendStartLine(model, visual);

            var go = new GameObject("Road") { tag = TrackTags.Track };
            go.AddComponent<MeshFilter>().sharedMesh = visual.ToMesh("RoadMesh");
            go.AddComponent<MeshRenderer>();

            // The collider gets the flat surface only. Kerbs sit 3 cm proud purely for the eye —
            // letting the wheels find them would change the physics for no gain.
            go.AddComponent<MeshCollider>().sharedMesh = collision.ToMesh("RoadCollision");
            return go;
        }

        static void AppendSurface(TrackModel model, MeshAccumulator visual, MeshAccumulator collision)
        {
            var segments = Mathf.Max(16, Mathf.RoundToInt(model.TotalLength / k_SurfaceSpacing));
            var step = model.TotalLength / segments;

            for (var i = 0; i < segments; i++)
            {
                var here = model.SampleAtDistance(i * step);
                var next = model.SampleAtDistance((i + 1) * step);

                var leftHere = here.Position - here.Right * (here.Width * 0.5f);
                var rightHere = here.Position + here.Right * (here.Width * 0.5f);
                var leftNext = next.Position - next.Right * (next.Width * 0.5f);
                var rightNext = next.Position + next.Right * (next.Width * 0.5f);

                var uvLeftHere = TrackAtlas.Asphalt(i * step, 0f);
                var uvRightHere = TrackAtlas.Asphalt(i * step, 1f);
                var uvLeftNext = TrackAtlas.Asphalt((i + 1) * step, 0f);
                var uvRightNext = TrackAtlas.Asphalt((i + 1) * step, 1f);

                visual.AddQuad(leftHere, leftNext, rightNext, rightHere,
                    uvLeftHere, uvLeftNext, uvRightNext, uvRightHere);
                collision.AddQuad(leftHere, leftNext, rightNext, rightHere,
                    uvLeftHere, uvLeftNext, uvRightNext, uvRightHere);
            }
        }

        /// <summary>
        /// Lays alternating red and white blocks along both edges of the entire lap.
        ///
        /// The blocks are counted around the whole circuit rather than per section, so the colours
        /// alternate continuously instead of restarting at every boundary — a restart puts two reds
        /// side by side wherever a section ends on an odd block. The count is forced even for the
        /// same reason at the start line, where the last block meets the first.
        /// </summary>
        static void AppendKerbs(TrackModel model, MeshAccumulator visual)
        {
            var blocks = Mathf.Max(4, Mathf.RoundToInt(model.TotalLength / k_KerbBlockLength));
            if (blocks % 2 == 1)
            {
                blocks++;
            }

            var blockLength = model.TotalLength / blocks;

            for (var block = 0; block < blocks; block++)
            {
                var uv = block % 2 == 0 ? TrackAtlas.Red : TrackAtlas.White;
                var from = block * blockLength;
                var to = from + blockLength;

                AppendKerbBlock(model, visual, from, to, uv, -1f);
                AppendKerbBlock(model, visual, from, to, uv, 1f);
            }
        }

        static void AppendKerbBlock(
            TrackModel model,
            MeshAccumulator visual,
            float from,
            float to,
            Vector2 uv,
            float side)
        {
            var here = model.SampleAtDistance(from);
            var next = model.SampleAtDistance(to);

            var lift = Vector3.up * k_KerbLift;

            var innerHere = here.Position + here.Right * (side * (here.Width * 0.5f - k_KerbInnerInset)) + lift;
            var outerHere = here.Position + here.Right * (side * (here.Width * 0.5f + k_KerbOuterWidth)) + lift;
            var innerNext = next.Position + next.Right * (side * (next.Width * 0.5f - k_KerbInnerInset)) + lift;
            var outerNext = next.Position + next.Right * (side * (next.Width * 0.5f + k_KerbOuterWidth)) + lift;

            // Wind consistently per side so both kerbs face upwards.
            if (side > 0f)
            {
                visual.AddQuad(innerHere, innerNext, outerNext, outerHere, uv, uv, uv, uv);
            }
            else
            {
                visual.AddQuad(outerHere, outerNext, innerNext, innerHere, uv, uv, uv, uv);
            }
        }

        /// <summary>A chequered start/finish line across the road at the timing point.</summary>
        static void AppendStartLine(TrackModel model, MeshAccumulator visual)
        {
            var start = model.SampleAtDistance(0f);
            var end = model.SampleAtDistance(k_StartLineLength);
            var lift = Vector3.up * (k_KerbLift * 0.6f);

            for (var i = 0; i < k_StartLineSquares; i++)
            {
                var fromFraction = i / (float)k_StartLineSquares - 0.5f;
                var toFraction = (i + 1) / (float)k_StartLineSquares - 0.5f;
                var uv = i % 2 == 0 ? TrackAtlas.White : TrackAtlas.Dark;

                var a = start.Position + start.Right * (fromFraction * start.Width) + lift;
                var b = end.Position + end.Right * (fromFraction * end.Width) + lift;
                var c = end.Position + end.Right * (toFraction * end.Width) + lift;
                var d = start.Position + start.Right * (toFraction * start.Width) + lift;

                visual.AddQuad(a, b, c, d, uv, uv, uv, uv);
            }
        }

        // ------------------------------------------------------------------
        // Run-off and ground
        // ------------------------------------------------------------------

        static GameObject BuildRunoff(TrackModel model)
        {
            var accumulator = new MeshAccumulator();
            var segments = Mathf.Max(16, Mathf.RoundToInt(model.TotalLength / k_SurfaceSpacing));
            var step = model.TotalLength / segments;
            var drop = Vector3.down * k_RunoffDrop;

            for (var i = 0; i < segments; i++)
            {
                var here = model.SampleAtDistance(i * step);
                var next = model.SampleAtDistance((i + 1) * step);

                for (var side = -1; side <= 1; side += 2)
                {
                    var innerHere = here.Position + here.Right * (side * (here.Width * 0.5f)) + drop;
                    var outerHere = here.Position + here.Right * (side * (here.Width * 0.5f + RunoffWidth)) + drop;
                    var innerNext = next.Position + next.Right * (side * (next.Width * 0.5f)) + drop;
                    var outerNext = next.Position + next.Right * (side * (next.Width * 0.5f + RunoffWidth)) + drop;

                    var uvInner = TrackAtlas.Runoff(i * step, 0.2f);
                    var uvOuter = TrackAtlas.Runoff(i * step, 0.8f);

                    if (side > 0)
                    {
                        accumulator.AddQuad(innerHere, innerNext, outerNext, outerHere,
                            uvInner, uvInner, uvOuter, uvOuter);
                    }
                    else
                    {
                        accumulator.AddQuad(outerHere, outerNext, innerNext, innerHere,
                            uvOuter, uvOuter, uvInner, uvInner);
                    }
                }
            }

            var go = new GameObject("Runoff") { tag = TrackTags.OffTrack };
            go.AddComponent<MeshFilter>().sharedMesh = accumulator.ToMesh("RunoffMesh");
            go.AddComponent<MeshRenderer>();
            go.AddComponent<MeshCollider>().sharedMesh = accumulator.ToMesh("RunoffCollision");
            return go;
        }

        static GameObject BuildGround(TrackModel model)
        {
            var bounds = new Bounds(model.SampleAtDistance(0f).Position, Vector3.zero);
            foreach (var sample in model.Samples)
            {
                bounds.Encapsulate(sample.Position);
            }

            bounds.Expand(k_GroundMargin);

            var y = -k_GroundDrop;
            var min = bounds.min;
            var max = bounds.max;

            // A single quad would span kilometres, and PhysX warns that triangles that large make
            // raycasts and contacts unstable — which is exactly what the wheels rely on off-track.
            var cellsX = Mathf.Clamp(Mathf.CeilToInt((max.x - min.x) / k_GroundCellSize), 1, 64);
            var cellsZ = Mathf.Clamp(Mathf.CeilToInt((max.z - min.z) / k_GroundCellSize), 1, 64);

            var vertices = new Vector3[(cellsX + 1) * (cellsZ + 1)];
            var uvs = new Vector2[vertices.Length];
            var triangles = new int[cellsX * cellsZ * 6];

            for (var z = 0; z <= cellsZ; z++)
            {
                for (var x = 0; x <= cellsX; x++)
                {
                    var index = z * (cellsX + 1) + x;
                    var u = x / (float)cellsX;
                    var v = z / (float)cellsZ;

                    vertices[index] = new Vector3(
                        Mathf.Lerp(min.x, max.x, u),
                        y,
                        Mathf.Lerp(min.z, max.z, v));

                    // Plain 0..1: the grass material is a flat colour, so there is no atlas region
                    // to aim at and nothing for a sweep to vary.
                    uvs[index] = new Vector2(u, v);
                }
            }

            var triangle = 0;
            for (var z = 0; z < cellsZ; z++)
            {
                for (var x = 0; x < cellsX; x++)
                {
                    var bottomLeft = z * (cellsX + 1) + x;
                    var bottomRight = bottomLeft + 1;
                    var topLeft = bottomLeft + cellsX + 1;
                    var topRight = topLeft + 1;

                    triangles[triangle++] = bottomLeft;
                    triangles[triangle++] = topLeft;
                    triangles[triangle++] = topRight;

                    triangles[triangle++] = bottomLeft;
                    triangles[triangle++] = topRight;
                    triangles[triangle++] = bottomRight;
                }
            }

            var mesh = new Mesh
            {
                name = "GroundMesh",
                vertices = vertices,
                uv = uvs,
                triangles = triangles,
            };

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var go = new GameObject("Ground") { tag = TrackTags.OffTrack };
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>();
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            return go;
        }

        static void Apply(GameObject target, Material material, Color fallbackColor)
        {
            var renderer = target.GetComponent<MeshRenderer>();
            if (material != null)
            {
                renderer.sharedMaterial = material;
                return;
            }

            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null)
            {
                return;
            }

            renderer.sharedMaterial = new Material(shader) { color = fallbackColor };
        }

        /// <summary>Collects quads into an indexed mesh.</summary>
        sealed class MeshAccumulator
        {
            readonly List<Vector3> m_Vertices = new List<Vector3>();
            readonly List<Vector2> m_Uvs = new List<Vector2>();
            readonly List<int> m_Triangles = new List<int>();

            public void AddQuad(
                Vector3 a, Vector3 b, Vector3 c, Vector3 d,
                Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD)
            {
                var baseIndex = m_Vertices.Count;

                m_Vertices.Add(a);
                m_Vertices.Add(b);
                m_Vertices.Add(c);
                m_Vertices.Add(d);

                m_Uvs.Add(uvA);
                m_Uvs.Add(uvB);
                m_Uvs.Add(uvC);
                m_Uvs.Add(uvD);

                m_Triangles.Add(baseIndex);
                m_Triangles.Add(baseIndex + 1);
                m_Triangles.Add(baseIndex + 2);

                m_Triangles.Add(baseIndex);
                m_Triangles.Add(baseIndex + 2);
                m_Triangles.Add(baseIndex + 3);
            }

            public Mesh ToMesh(string name)
            {
                var mesh = new Mesh
                {
                    name = name,
                    indexFormat = m_Vertices.Count > 65000
                        ? UnityEngine.Rendering.IndexFormat.UInt32
                        : UnityEngine.Rendering.IndexFormat.UInt16,
                };

                mesh.SetVertices(m_Vertices);
                mesh.SetUVs(0, m_Uvs);
                mesh.SetTriangles(m_Triangles, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                return mesh;
            }
        }
    }
}
