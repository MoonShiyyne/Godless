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

        [Tooltip("Which world in Assets/Content to generate. Empty is the built-in island with every biome. See `sim maps`.")]
        [SerializeField] string map = "green-shore";

        [Tooltip("Place the main camera over the island on start.")]
        [SerializeField] bool frameCamera = true;

        [SerializeField] bool showStats = true;

        [Header("Settlement (stratum 1)")]
        [Tooltip("Put twenty people on the island and let them build.")]
        [SerializeField] bool settle = true;

        [SerializeField] int people = 20;

        [Tooltip("Simulated days a real second at 1x. Speed never reaches the simulation; it only decides how many whole ticks run this frame.")]
        [SerializeField] float daysPerSecondAt1x = 2f;

        [Tooltip("Speed to start at, as an index into TickPacer.Multipliers (0 paused, 1 is 1x).")]
        [SerializeField] int startSpeed = 1;

        [Tooltip("Ticks a frame when the renderer is keeping up. Lower if a fast speed makes the frame stutter.")]
        [SerializeField] int ticksPerFrame = 16;

        public SimWorld World { get; private set; }
        public WorldRenderer View { get; private set; }

        /// <summary>The island's planning grid, or null in a world nobody settled.</summary>
        public ParcelGrid Parcels { get; private set; }

        public Settlement Town { get; private set; }

        /// <summary>How fast the world runs while somebody is watching. Never seen by the simulation.</summary>
        public TickPacer Pacer { get; private set; }

        /// <summary>What the ground is made of, by place and height — the god's brush builds from this.</summary>
        public GroundPalette Ground { get; private set; }

        /// <summary>The map this world was generated on. Never null once the world exists.</summary>
        public WorldPreset Map { get; private set; }

        float _smoothedFrame = 1f / 60f;
        float _worstFrame;
        float _loadSeconds;
        int _deltaCursor;

        void Start()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();

            string root = Path.Combine(Application.dataPath, "Content");
            LoadResult content = ContentLoader.Load(new DirectoryContentSource(root));
            foreach (string warning in content.Warnings) Debug.LogWarning("content: " + warning);

            VoxelTypes types = VoxelTypes.FromContent(content.Database);

            WorldChoice choice;
            try { choice = WorldChoice.Pick(content.Database, map == null ? "" : map.Trim()); }
            catch (ContentException e)
            {
                // A misspelled map in the inspector is a content problem, not
                // a crash: say what there is and fall back to the plain island.
                Debug.LogWarning("Godless: " + e.Message);
                choice = WorldChoice.Pick(content.Database, "");
            }

            BiomeTable biomes = choice.Biomes;
            if (biomes.Count == 0)
            {
                // L5's tell, seen from the Unity side: no content is not a crash.
                Debug.Log("No biomes declared in " + root + " — booting with nothing to build.");
                return;
            }
            foreach (string problem in choice.Maps.Problems) Debug.LogWarning("content: " + problem);

            World = new SimWorld((ulong)seed, content.Database, types);
            Pacer = new TickPacer(daysPerSecondAt1x, World.Clock.TicksPerDay, startSpeed);
            World.Island = IslandGenerator.Generate(World.Voxels.Store, World.Streams, biomes, types, choice.Preset);
            Ground = GroundPalette.From(World.Island, biomes, types);
            Map = choice.Preset;

            // The untouched island is history's baseline (S04): everything the
            // player does is replayed forward from here when scrubbing back.
            World.BeginHistory();

            View = GetComponent<WorldRenderer>();
            View.Bind(World.Voxels.Store, VoxelVisuals.FromContent(content.Database, types));

            if (settle) Settle(content.Database, biomes);

            _loadSeconds = (float)watch.Elapsed.TotalSeconds;
            Debug.Log("Godless: " + Map.Title + " (" + Map.Name + "), seed " + seed
                      + ", island generated in " + _loadSeconds.ToString("0.00") + "s"
                      + "\n" + Map.Tell);

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

            var centre = new Vector3(ChunkStore.SizeX * 0.5f,
                                     World.Island != null ? World.Island.SeaLevel : IslandMap.DefaultSeaLevel,
                                     ChunkStore.SizeZ * 0.5f);
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
        ///
        /// How many ticks is the pacer's business and nobody else's — the
        /// world does not know what speed it is being watched at.
        /// </summary>
        void RunSim(float dt)
        {
            if (World == null || Town == null || Pacer == null) return;

            Pacer.Advance(dt);
            int ticks = Pacer.Take(FrameBudget());
            if (ticks > 0) RunTicks(ticks);
        }

        /// <summary>
        /// Ticks this frame can afford. A fast speed must not outrun the
        /// mesher: the queue is the honest signal that the picture is falling
        /// behind the world, and the ticks it cannot afford stay owed rather
        /// than being lost.
        /// </summary>
        int FrameBudget()
        {
            int waiting = View.ChunksQueued + View.ChunksInFlight;
            if (waiting > 24) return 1;
            if (waiting > 8) return Mathf.Max(1, ticksPerFrame / 4);
            return ticksPerFrame;
        }

        /// <summary>
        /// Runs whole ticks and hands the renderer what changed. Public so a
        /// tool can wind the world on (S29's style plate) without pretending
        /// to be a frame.
        /// </summary>
        public void RunTicks(int ticks)
        {
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
                text += "\n\n" + Pacer.Label + (Pacer.Behind > 2.0 ? "  (the picture is behind the world)" : "")
                      + "   year " + World.Clock.Year + " day " + World.Clock.DayOfYear
                      + "   " + Town.People.Count + " people, " + Town.ShelterCapacity + " sleeping places"
                      + "\nhouses: " + standing + " standing, " + under + " going up"
                      + "   intents " + Town.Intents.Intents.Count;
            }

            if (GetComponent<SimSpeed>() != null) text += "\n" + SimSpeed.Keys;

            TerrainEditor editor = GetComponent<TerrainEditor>();
            if (editor != null) text += "\n" + editor.Status + "\nstrokes " + editor.Strokes;

            GUI.Label(new Rect(12, 10, 620, 150), text);
        }
    }
}
