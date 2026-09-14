using System.Collections.Generic;
using Godless.Sim.Build;
using Godless.Sim.Core;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Godless.Unity
{
    /// <summary>
    /// The cutaway: C opens every building a metre above its lowest floor, so
    /// the beds, and whoever is asleep in them, can be seen. S2V.
    ///
    /// Nothing in the world changes. A height per column goes to the voxel
    /// shader, which leaves out whatever is above it inside a building's
    /// footprint; terrain and open ground are never cut.
    /// </summary>
    [RequireComponent(typeof(WorldBootstrap))]
    public sealed class CutawayView : MonoBehaviour
    {
        [Tooltip("Voxels of wall left standing above a building's lowest floor. A voxel is half a metre.")]
        [SerializeField] int wallLeft = 2;

        [SerializeField] bool on;

        static readonly int CutMap = Shader.PropertyToID("_GodlessCutMap");
        static readonly int CutSize = Shader.PropertyToID("_GodlessCutSize");
        static readonly int CutOn = Shader.PropertyToID("_GodlessCutOn");
        static readonly Symbol FloorRole = Symbol.For("role.floor");

        /// <summary>No cut: higher than anything in the world.</summary>
        const float Uncut = 100000f;

        WorldBootstrap _boot;
        Texture2D _map;
        float[] _heights;
        readonly List<int> _written = new List<int>();
        long _signature = -1;

        public static string Keys { get { return "C cutaway"; } }

        /// <summary>Whether buildings are drawn opened.</summary>
        public bool On { get { return on; } }

        void Awake() { _boot = GetComponent<WorldBootstrap>(); }

        void OnDisable() { Shader.SetGlobalFloat(CutOn, 0f); }

        void OnDestroy()
        {
            Shader.SetGlobalFloat(CutOn, 0f);
            if (_map != null) Destroy(_map);
        }

        void Update()
        {
            Keyboard keys = Keyboard.current;
            if (keys != null && keys.cKey.wasPressedThisFrame) on = !on;

            if (!on || _boot.World == null) { Shader.SetGlobalFloat(CutOn, 0f); return; }
            if (_map == null) Create();

            long signature = Signature();
            if (signature != _signature) { _signature = signature; Rebuild(); }
            Shader.SetGlobalFloat(CutOn, 1f);
        }

        void Create()
        {
            TextureFormat format = SystemInfo.SupportsTextureFormat(TextureFormat.RFloat) ? TextureFormat.RFloat : TextureFormat.RHalf;
            _map = new Texture2D(ChunkStore.SizeX, ChunkStore.SizeZ, format, false, true)
            {
                name = "Cutaway (runtime)",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            _heights = new float[ChunkStore.SizeX * ChunkStore.SizeZ];
            for (int i = 0; i < _heights.Length; i++) _heights[i] = Uncut;
            Upload();
            Shader.SetGlobalTexture(CutMap, _map);
            Shader.SetGlobalVector(CutSize, new Vector4(ChunkStore.SizeX, ChunkStore.SizeZ, 1f / ChunkStore.SizeX, 1f / ChunkStore.SizeZ));
        }

        /// <summary>Changes whenever a building starts, grows a part, finishes or is gone.</summary>
        long Signature()
        {
            long sig = 17;
            foreach (Settlement s in _boot.World.Settlements)
            {
                sig = sig * 31 + s.Projects.Count;
                foreach (Project p in s.Projects)
                    sig = sig * 31 + (p.Complete ? 2 : p.Placed > 0 ? 1 : 0) + p.Site.Record.Index * 7L;
            }
            return sig;
        }

        void Rebuild()
        {
            foreach (int i in _written) _heights[i] = Uncut;
            _written.Clear();

            foreach (Settlement s in _boot.World.Settlements)
                foreach (Project p in s.Projects)
                {
                    if (p.Built == null || (!p.Complete && p.Placed == 0)) continue;
                    Blueprint plan = p.Plan;

                    // The lowest floor course; failing one, the lowest course built.
                    int floor = -1, lowest = -1;
                    for (int y = 0; y < plan.Height && floor < 0; y++)
                        for (int z = 0; z < plan.Depth && floor < 0; z++)
                            for (int x = 0; x < plan.Width; x++)
                            {
                                if (lowest < 0 && p.Built.At(x, y, z) != VoxelTypes.AirId) lowest = y;
                                if (plan.At(x, y, z) == FloorRole) { floor = y; break; }
                            }
                    if (floor < 0) floor = lowest;
                    if (floor < 0) continue;
                    float cut = Construction.World(p, 0, floor, 0).Y + 1 + wallLeft;

                    for (int z = 0; z < plan.Depth; z++)
                        for (int x = 0; x < plan.Width; x++)
                        {
                            bool any = false;
                            for (int y = 0; y < plan.Height && !any; y++) any = p.Built.At(x, y, z) != VoxelTypes.AirId;
                            if (!any) continue;
                            Int3 at = Construction.World(p, x, 0, z);
                            if (at.X < 0 || at.Z < 0 || at.X >= ChunkStore.SizeX || at.Z >= ChunkStore.SizeZ) continue;
                            int i = at.Z * ChunkStore.SizeX + at.X;
                            if (_heights[i] == Uncut) _written.Add(i);
                            if (cut < _heights[i]) _heights[i] = cut;
                        }
                }
            Upload();
        }

        void Upload()
        {
            if (_map.format == TextureFormat.RFloat) _map.SetPixelData(_heights, 0);
            else
            {
                var half = new ushort[_heights.Length];
                for (int i = 0; i < half.Length; i++) half[i] = Mathf.FloatToHalf(Mathf.Min(_heights[i], 60000f));
                _map.SetPixelData(half, 0);
            }
            _map.Apply(false);
        }
    }
}
