using UnityEngine;

namespace RacingBotCup.Track
{
    /// <summary>
    /// A circuit that lives in the scene.
    ///
    /// The geometry is built into the scene as real child objects, so opening the training scene
    /// shows a track straight away instead of an empty world that only becomes one when you press
    /// Play. Only the seed is serialised; the queryable <see cref="TrackModel"/> is regenerated on
    /// Awake, which is safe because a seed maps to exactly one circuit on every machine.
    /// </summary>
    [ExecuteAlways]
    public sealed class TrackInstance : MonoBehaviour
    {
        const string k_GeometryName = "Geometry";
        const string k_ObstaclesName = "Obstacles";

        [Tooltip("Which circuit this is. The same seed always produces the same track.")]
        [SerializeField] int m_Seed = 1000;

        [Tooltip("Allow SharpHairpin/ObstacleStraight/RampCorner sections. On by default for both " +
                 "training and evaluation, so the scored circuits include the same hazard mix a policy " +
                 "trains against.")]
        [SerializeField] bool m_EnableHazardSections = true;

        [SerializeField] TrackMaterials m_Materials = new TrackMaterials();
        [SerializeField] TrackPropCatalogue m_Props = new TrackPropCatalogue();

        TrackModel m_Model;
        GeneratedTrack m_Generated;

        /// <summary>The circuit's seed. Assigning rebuilds nothing on its own — call <see cref="Rebuild"/>.</summary>
        public int Seed
        {
            get => m_Seed;
            set => m_Seed = value;
        }

        /// <summary>Whether hazard sections may appear. Assigning rebuilds nothing on its own — call <see cref="Rebuild"/>.</summary>
        public bool EnableHazardSections
        {
            get => m_EnableHazardSections;
            set => m_EnableHazardSections = value;
        }

        public TrackMaterials Materials => m_Materials;

        public TrackPropCatalogue Props => m_Props;

        /// <summary>Queryable circuit: centreline, width, curvature, typed sections.</summary>
        public TrackModel Model
        {
            get
            {
                EnsureModel();
                return m_Model;
            }
        }

        public GeneratedTrack Generated
        {
            get
            {
                EnsureModel();
                return m_Generated;
            }
        }

        void Awake()
        {
            EnsureModel();
        }

        /// <summary>
        /// Regenerates the queryable model from the seed, and the geometry too if it is missing.
        /// Cheap enough to call freely — generation is a few milliseconds.
        /// </summary>
        public void EnsureModel()
        {
            if (m_Model != null)
            {
                return;
            }

            m_Generated = TrackGenerator.Generate(m_Seed, m_EnableHazardSections);
            m_Model = m_Generated.Model;
            m_Model.Rebase(transform.position);

            if (transform.Find(k_GeometryName) == null)
            {
                BuildGeometry();
            }

            if (transform.Find(k_ObstaclesName) == null)
            {
                BuildObstacles();
            }
        }

        /// <summary>Picks a new circuit at random and rebuilds. Safe to call from your own scripts.</summary>
        public void Randomize()
        {
            Randomize(Random.Range(1, 1_000_000));
        }

        /// <summary>Switches to a specific circuit and rebuilds.</summary>
        public void Randomize(int seed)
        {
            m_Seed = seed;
            Rebuild();
        }

        /// <summary>Regenerates model and geometry from the current seed.</summary>
        public void Rebuild()
        {
            m_Generated = TrackGenerator.Generate(m_Seed, m_EnableHazardSections);
            m_Model = m_Generated.Model;

            BuildGeometry();
            BuildObstacles();

            // Rebased after the mesh is built: the mesh is generated in the circuit's own space and
            // carried into the world by this transform, while the model holds world coordinates so
            // that projections and start poses need no correction.
            m_Model.Rebase(transform.position);
        }

        void BuildGeometry()
        {
            var existing = transform.Find(k_GeometryName);
            if (existing != null)
            {
                DestroySafely(existing.gameObject);
            }

            var geometry = TrackMeshBuilder.Build(m_Generated.Model, m_Materials);
            geometry.name = k_GeometryName;
            geometry.transform.SetParent(transform, false);
        }

        void BuildObstacles()
        {
            var existing = transform.Find(k_ObstaclesName);
            if (existing != null)
            {
                DestroySafely(existing.gameObject);
            }

            var obstacles = TrackObstacleBuilder.Build(m_Generated.Model, m_Props, m_Seed);
            obstacles.name = k_ObstaclesName;
            obstacles.transform.SetParent(transform, false);
        }

        static void DestroySafely(Object target)
        {
            if (Application.isPlaying)
            {
                Destroy(target);
            }
            else
            {
                DestroyImmediate(target);
            }
        }

        /// <summary>One-line summary of the layout, for inspectors and reports.</summary>
        public string Describe()
        {
            var track = Generated;
            if (track == null)
            {
                return "not generated";
            }

            var builder = new System.Text.StringBuilder();
            builder.Append($"{track.Model.TotalLength:F0} m, {track.Model.Sections.Count} sections");
            if (!track.FullyValid)
            {
                builder.Append($"  [relaxed: {track.ValidationNote}]");
            }

            builder.AppendLine();
            foreach (var section in track.Model.Sections)
            {
                builder.Append($"{section.Label}({section.Length:F0}m) ");
            }

            return builder.ToString();
        }
    }
}
