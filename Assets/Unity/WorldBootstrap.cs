using System.Collections.Generic;
using System.IO;
using Godless.Meshing;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Deltas;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using UnityEngine;

namespace Godless.Unity
{
    /// <summary>
    /// Builds a world and puts it on screen. The Unity side's only entry
    /// point: everything it does is a call into /Sim, which is the point of
    /// law L1 — the game runs identically without this file.
    ///
    /// Content loads from Assets/Content through the same pipeline the
    /// headless harness uses (L5). That path is Editor-only for now; a player
    /// build will need it copied to StreamingAssets, which is ship work.
    /// </summary>
    [RequireComponent(typeof(WorldRenderer))]
    public sealed class WorldBootstrap : MonoBehaviour
    {
        [Tooltip("World seed. The same seed gives the same island on every machine.")]
        [SerializeField] long seed = 7;

        [Tooltip("Place the main camera over the island on start.")]
        [SerializeField] bool frameCamera = true;

        [SerializeField] bool showStats = true;

        [Header("Settlement (stratum 1)")]
        [Tooltip("Put twenty people on the island and let them build.")]
        [SerializeField] bool settle = true;

        [SerializeField] int people = 20;

        [Tooltip("Simulated days a second. Zero pauses; the sim is deterministic either way.")]
        [SerializeField] float daysPerSecond = 4f;

        public SimWorld World { get; private set; }
        public WorldRenderer View { get; private set; }

        /// <summary>The island's planning grid, or null in a world nobody settled.</summary>
        public ParcelGrid Parcels { get; private set; }

        public Settlement Town { get; private set; }

        float _smoothedFrame = 1f / 60f;
        float _worstFrame;
        float _loadSeconds;
        float _tickDebt;
        int _deltaCursor;

        void Start()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();

            string root = Path.Combine(Application.dataPath, "Content");
            LoadResult content = ContentLoader.Load(new DirectoryContentSource(root));
            foreach (string warning in content.Warnings) Debug.LogWarning("content: " + warning);

            VoxelTypes types = VoxelTypes.FromContent(content.Database);
            BiomeTable biomes = BiomeTable.FromContent(content.Database);
            if (biomes.Count == 0)
            {
                // L5's tell, seen from the Unity side: no content is not a crash.
                Debug.Log("No biomes declared in " + root + " — booting with nothing to build.");
                return;
            }

            World = new SimWorld((ulong)seed, content.Database, types);
            World.Island = IslandGenerator.Generate(World.Voxels.Store, World.Streams, biomes, types);

            // The untouched island is history's baseline (S04): everything the
            // player does is replayed forward from here when scrubbing back.
            World.BeginHistory();

            View = GetComponent<WorldRenderer>();
            View.Bind(World.Voxels.Store, VoxelVisuals.FromContent(content.Database, types));

            if (settle) Settle(content.Database, biomes);

            _loadSeconds = (float)watch.Elapsed.TotalSeconds;
            Debug.Log("Godless: seed " + seed + ", island generated in " + _loadSeconds.ToString("0.00") + "s");

            if (frameCamera) FrameCamera();
        }

        /// <summary>
        /// One settlement, with everything stratum 1 gives it. The site is a
        /// stand-in until S30 founds settlements by quorum.
        /// </summary>
        void Settle(ContentDatabase content, BiomeTable biomes)
        {
            ConstraintFields fields;
            Parcels = Founding.Survey(World, content, biomes, out fields);

            int px, pz;
            if (!Founding.StandInSite(Parcels, World.Island, biomes, Symbol.For("biome.temperate"), out px, out pz)
                && !Founding.StandInSite(Parcels, World.Island, biomes, Symbol.None, out px, out pz))
            {
                Debug.Log("Godless: nowhere flat and dry to settle on seed " + seed);
                return;
            }

            Town = Founding.Begin(World, content, Parcels, biomes, "first", people, px, pz, null);
            Founding.AddSystems(World, content, Parcels, fields, biomes);
            _deltaCursor = World.Voxels.Log.Count;
            Debug.Log("Godless: " + people + " people settled at parcel (" + px + ", " + pz + ")");
        }

        void FrameCamera()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            if (cam.GetComponent<GodCamera>() != null) return;   // it frames itself

            var centre = new Vector3(ChunkStore.SizeX * 0.5f, IslandMap.SeaLevel, ChunkStore.SizeZ * 0.5f);
            cam.transform.position = centre + new Vector3(0f, 250f, -330f);
            cam.transform.LookAt(centre + new Vector3(0f, 0f, 30f));
            cam.nearClipPlane = 0.5f;
            cam.farClipPlane = 2000f;
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            _smoothedFrame = Mathf.Lerp(_smoothedFrame, dt, 0.05f);
            if (Time.frameCount > 10 && dt > _worstFrame) _worstFrame = dt;

            RunSim(dt);
        }

        /// <summary>
        /// Advances the simulation in whole ticks, then tells the renderer
        /// which chunks the people changed. The sim never touches Unity and
        /// Unity never touches the voxels: it reads the delta log, which is
        /// there for exactly this (S04).
        /// </summary>
        void RunSim(float dt)
        {
            if (World == null || Town == null || daysPerSecond <= 0f) return;

            _tickDebt += dt * daysPerSecond * World.Clock.TicksPerDay;
            int ticks = Mathf.Min(Mathf.FloorToInt(_tickDebt), 64);
            if (ticks <= 0) return;
            _tickDebt -= ticks;

            for (int i = 0; i < ticks; i++) World.Tick();

            IReadOnlyList<VoxelDelta> log = World.Voxels.Log.All();
            for (; _deltaCursor < log.Count; _deltaCursor++)
                View.MarkDirty(ChunkStore.PositionOf(log[_deltaCursor].ChunkIndex, log[_deltaCursor].VoxelIndex));
        }

        void OnGUI()
        {
            if (!showStats || View == null) return;

            string text =
                (1f / _smoothedFrame).ToString("0") + " fps  (" + (_smoothedFrame * 1000f).ToString("0.0") + " ms)\n" +
                "chunks drawn " + View.ChunksDrawn + ", quads " + View.QuadsDrawn.ToString("N0") + "\n" +
                "meshing: " + View.ChunksInFlight + " in flight, " + View.ChunksQueued + " queued\n" +
                "mesher main-thread cost " + View.LastFrameMs.ToString("0.00") + " ms, worst "
                + View.WorstFrameMs.ToString("0.00") + " ms\n" +
                "island generated in " + _loadSeconds.ToString("0.00") + "s, seed " + seed;

            if (Town != null)
            {
                int standing = 0, under = 0;
                foreach (Project project in Town.Projects) { if (project.Complete) standing++; else under++; }
                text += "\n\nyear " + World.Clock.Year + " day " + World.Clock.DayOfYear
                      + "   " + Town.People.Count + " people, " + Town.ShelterCapacity + " sleeping places"
                      + "\nhouses: " + standing + " standing, " + under + " going up"
                      + "   intents " + Town.Intents.Intents.Count;
            }

            TerrainEditor editor = GetComponent<TerrainEditor>();
            if (editor != null) text += "\n" + editor.Status + "\nstrokes " + editor.Strokes;

            GUI.Label(new Rect(12, 10, 620, 150), text);
        }
    }
}
