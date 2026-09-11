using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Godless.Meshing;
using Godless.Sim.Core;
using Godless.Sim.Voxels;
using UnityEngine;
using UnityEngine.Rendering;

namespace Godless.Unity
{
    /// <summary>
    /// Draws a ChunkStore, one GameObject per non-empty chunk. S06.
    ///
    /// The division of labour is the whole design. Per chunk, the main thread
    /// does two cheap things — copies 34^3 voxels out of the store, and later
    /// hands finished arrays to a Mesh — and a worker does everything in
    /// between: the greedy merge, the AO, and the conversion to Unity's vertex
    /// types. The store is only ever read from the main thread, so it needs no
    /// lock, and a terrain edit costs the frame a copy and an upload.
    ///
    /// Both main-thread halves run under a per-frame budget, and the renderer
    /// times itself, because S06's tell is "terrain edits remesh off the main
    /// thread without a hitch" and that is a number, not a feeling.
    /// </summary>
    public sealed class WorldRenderer : MonoBehaviour
    {
        [SerializeField] Material opaqueMaterial;
        [SerializeField] Material waterMaterial;

        [Tooltip("Most chunks copied out of the store in one frame.")]
        [SerializeField] int copiesPerFrame = 24;

        [Tooltip("Most finished meshes handed to the GPU in one frame.")]
        [SerializeField] int uploadsPerFrame = 24;

        [Tooltip("Stop scheduling for the frame once main-thread work passes this.")]
        [SerializeField] float frameBudgetMs = 4f;

        sealed class ChunkView
        {
            public GameObject Root;
            public Mesh Opaque;
            public Mesh Water;
            public MeshRenderer OpaqueRenderer;
            public MeshRenderer WaterRenderer;
            public int Quads;
        }

        sealed class Built
        {
            public int Index;
            public int Version;
            public Vector3[] OpaqueVertices, OpaqueNormals;
            public Color32[] OpaqueColors;
            public int[] OpaqueIndices;
            public Vector3[] WaterVertices, WaterNormals;
            public Color32[] WaterColors;
            public int[] WaterIndices;
            public int Quads;
        }

        ChunkStore _store;
        VoxelVisuals _visuals;
        ChunkView[] _views;
        int[] _version;
        bool[] _inFlight;
        bool[] _queued;

        readonly Queue<int> _dirty = new Queue<int>();
        readonly List<int> _deferred = new List<int>();
        readonly ConcurrentQueue<Built> _done = new ConcurrentQueue<Built>();
        readonly ConcurrentBag<ushort[]> _padPool = new ConcurrentBag<ushort[]>();
        readonly Stopwatch _frame = new Stopwatch();
        int _inFlightCount;

        public float LastFrameMs { get; private set; }
        public float WorstFrameMs { get; private set; }
        public int ChunksDrawn { get; private set; }
        public int QuadsDrawn { get; private set; }
        public int ChunksInFlight { get { return _inFlightCount; } }
        public int ChunksQueued { get { return _dirty.Count + _deferred.Count; } }

        /// <summary>True once nothing is waiting to be meshed or uploaded.</summary>
        public bool IsIdle { get { return _store != null && ChunksQueued == 0 && _inFlightCount == 0 && _done.IsEmpty; } }

        public void ResetWorstFrame() { WorstFrameMs = 0f; }

        public void Bind(ChunkStore store, VoxelVisuals visuals)
        {
            _store = store;
            _visuals = visuals;
            EnsureMaterials();

            _views = new ChunkView[ChunkStore.ChunkCount];
            _version = new int[ChunkStore.ChunkCount];
            _inFlight = new bool[ChunkStore.ChunkCount];
            _queued = new bool[ChunkStore.ChunkCount];

            for (int i = 0; i < ChunkStore.ChunkCount; i++) Enqueue(i);
        }

        /// <summary>
        /// A voxel changed. Remeshes every chunk whose padded copy contains it:
        /// its own, plus any neighbour — edge, face or corner — whose border it
        /// sits in, since a neighbour's culling and AO both read it.
        /// </summary>
        public void MarkDirty(Int3 voxel)
        {
            if (_store == null) return;
            for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int x = voxel.X + dx, y = voxel.Y + dy, z = voxel.Z + dz;
                        if (!ChunkStore.InBounds(x, y, z)) continue;
                        int index = ChunkStore.ChunkIndex(x >> 5, y >> 5, z >> 5);
                        _version[index]++;
                        Enqueue(index);
                    }
        }

        /// <summary>Remesh everything — after a scrub rebuilds the store wholesale.</summary>
        public void Rebind(ChunkStore store)
        {
            _store = store;
            for (int i = 0; i < ChunkStore.ChunkCount; i++) { _version[i]++; Enqueue(i); }
        }

        void Enqueue(int index)
        {
            if (_queued[index]) return;
            _queued[index] = true;
            _dirty.Enqueue(index);
        }

        void Update()
        {
            if (_store == null) return;
            _frame.Restart();

            int uploads = 0;
            Built built;
            while (uploads < uploadsPerFrame && _done.TryDequeue(out built))
            {
                Upload(built);
                uploads++;
                if (_frame.Elapsed.TotalMilliseconds > frameBudgetMs) break;
            }

            int copies = 0;
            while (copies < copiesPerFrame && _dirty.Count > 0
                   && _frame.Elapsed.TotalMilliseconds < frameBudgetMs)
            {
                int index = _dirty.Dequeue();
                if (_inFlight[index]) { _deferred.Add(index); continue; }
                _queued[index] = false;
                Schedule(index);
                copies++;
            }
            for (int i = 0; i < _deferred.Count; i++) _dirty.Enqueue(_deferred[i]);
            _deferred.Clear();

            _frame.Stop();
            LastFrameMs = (float)_frame.Elapsed.TotalMilliseconds;
            if (LastFrameMs > WorstFrameMs) WorstFrameMs = LastFrameMs;
        }

        void Schedule(int index)
        {
            int cy = index / (ChunkStore.ChunksX * ChunkStore.ChunksZ);
            int rem = index % (ChunkStore.ChunksX * ChunkStore.ChunksZ);
            int cz = rem / ChunkStore.ChunksX;
            int cx = rem % ChunkStore.ChunksX;

            ushort[] pad;
            if (!_padPool.TryTake(out pad)) pad = new ushort[ChunkMesher.PaddedVolume];

            // The one read of the live store, on the main thread.
            if (!ChunkMesher.CopyPadded(_store, cx, cy, cz, pad))
            {
                _padPool.Add(pad);
                Clear(index);
                return;
            }

            int version = _version[index];
            VoxelVisuals visuals = _visuals;
            _inFlight[index] = true;
            _inFlightCount++;

            Task.Run(() =>
            {
                try
                {
                    var opaque = new MeshData();
                    var water = new MeshData();
                    ChunkMesher.BuildFromPadded(pad, cx * Chunk.Size, cy * Chunk.Size, cz * Chunk.Size,
                                                visuals, opaque, water);

                    var result = new Built { Index = index, Version = version, Quads = opaque.QuadCount + water.QuadCount };
                    ToArrays(opaque, out result.OpaqueVertices, out result.OpaqueNormals,
                             out result.OpaqueColors, out result.OpaqueIndices);
                    ToArrays(water, out result.WaterVertices, out result.WaterNormals,
                             out result.WaterColors, out result.WaterIndices);
                    _done.Enqueue(result);
                }
                catch (System.Exception e)
                {
                    UnityEngine.Debug.LogException(e);
                    _done.Enqueue(new Built { Index = index, Version = -1 });
                }
                finally
                {
                    _padPool.Add(pad);
                }
            });
        }

        /// <summary>Conversion to Unity's vertex types happens on the worker too.</summary>
        static void ToArrays(MeshData m, out Vector3[] vertices, out Vector3[] normals,
                             out Color32[] colors, out int[] indices)
        {
            int n = m.VertexCount;
            vertices = new Vector3[n];
            normals = new Vector3[n];
            colors = new Color32[n];
            for (int i = 0; i < n; i++)
            {
                vertices[i] = new Vector3(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]);
                normals[i] = new Vector3(m.Normals[i * 3], m.Normals[i * 3 + 1], m.Normals[i * 3 + 2]);
                colors[i] = new Color32(m.Colors[i * 4], m.Colors[i * 4 + 1], m.Colors[i * 4 + 2], m.Colors[i * 4 + 3]);
            }
            indices = m.Indices.ToArray();
        }

        void Upload(Built b)
        {
            int index = b.Index;
            _inFlight[index] = false;
            _inFlightCount--;

            // A newer edit landed while this was building: draw what we have,
            // which is closer to the truth than what is on screen, and go again.
            if (b.Version != _version[index]) Enqueue(index);
            if (b.Version < 0) return;

            ChunkView view = _views[index] ?? (_views[index] = CreateView(index));

            ChunksDrawn -= view.Quads > 0 ? 1 : 0;
            QuadsDrawn -= view.Quads;

            Fill(view.Opaque, b.OpaqueVertices, b.OpaqueNormals, b.OpaqueColors, b.OpaqueIndices);
            Fill(view.Water, b.WaterVertices, b.WaterNormals, b.WaterColors, b.WaterIndices);
            view.OpaqueRenderer.enabled = b.OpaqueIndices.Length > 0;
            view.WaterRenderer.enabled = b.WaterIndices.Length > 0;
            view.Quads = b.Quads;

            ChunksDrawn += view.Quads > 0 ? 1 : 0;
            QuadsDrawn += view.Quads;
        }

        static void Fill(Mesh mesh, Vector3[] vertices, Vector3[] normals, Color32[] colors, int[] indices)
        {
            mesh.Clear();
            if (indices.Length == 0) return;
            mesh.indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.colors32 = colors;
            mesh.SetTriangles(indices, 0, true);
        }

        void Clear(int index)
        {
            ChunkView view = _views[index];
            if (view == null) return;
            ChunksDrawn -= view.Quads > 0 ? 1 : 0;
            QuadsDrawn -= view.Quads;
            view.Opaque.Clear();
            view.Water.Clear();
            view.OpaqueRenderer.enabled = false;
            view.WaterRenderer.enabled = false;
            view.Quads = 0;
        }

        ChunkView CreateView(int index)
        {
            var root = new GameObject("Chunk " + index);
            root.transform.SetParent(transform, false);

            var view = new ChunkView
            {
                Root = root,
                Opaque = new Mesh { name = "Chunk " + index + " opaque" },
                Water = new Mesh { name = "Chunk " + index + " water" },
            };
            view.Opaque.MarkDynamic();
            view.Water.MarkDynamic();

            root.AddComponent<MeshFilter>().sharedMesh = view.Opaque;
            view.OpaqueRenderer = root.AddComponent<MeshRenderer>();
            view.OpaqueRenderer.sharedMaterial = opaqueMaterial;

            var water = new GameObject("Water");
            water.transform.SetParent(root.transform, false);
            water.AddComponent<MeshFilter>().sharedMesh = view.Water;
            view.WaterRenderer = water.AddComponent<MeshRenderer>();
            view.WaterRenderer.sharedMaterial = waterMaterial;
            view.WaterRenderer.shadowCastingMode = ShadowCastingMode.Off;

            return view;
        }

        void EnsureMaterials()
        {
            Shader shader = Shader.Find("Godless/VoxelVertexColor");
            if (shader == null)
            {
                UnityEngine.Debug.LogWarning("Godless/VoxelVertexColor not found; falling back to Standard. "
                                             + "Vertex colours and baked AO will not show.");
                shader = Shader.Find("Standard");
            }
            if (opaqueMaterial == null) opaqueMaterial = new Material(shader) { name = "Voxel (runtime)" };
            if (waterMaterial == null) waterMaterial = new Material(shader) { name = "Water (runtime)" };
        }

        void OnDestroy()
        {
            if (_views == null) return;
            foreach (ChunkView v in _views)
            {
                if (v == null) continue;
                Destroy(v.Opaque);
                Destroy(v.Water);
            }
        }
    }
}
