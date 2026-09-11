using System.IO;
using Godless.Meshing;
using Godless.Sim.Content;
using Godless.Sim.Harness;
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

        public SimWorld World { get; private set; }
        public WorldRenderer View { get; private set; }

        float _smoothedFrame = 1f / 60f;
        float _worstFrame;
        float _loadSeconds;

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

            // Snapshot the untouched island, so history has a baseline to
            // scrub back to (S04) before anything has happened.
            World.Voxels.EndTick(0);

            View = GetComponent<WorldRenderer>();
            View.Bind(World.Voxels.Store, VoxelVisuals.FromContent(content.Database, types));

            _loadSeconds = (float)watch.Elapsed.TotalSeconds;
            Debug.Log("Godless: seed " + seed + ", island generated in " + _loadSeconds.ToString("0.00") + "s");

            if (frameCamera) FrameCamera();
        }

        void FrameCamera()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

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

            GUI.Label(new Rect(12, 10, 520, 110), text);
        }
    }
}
