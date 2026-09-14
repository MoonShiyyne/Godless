using Godless.Sim.Settlements;
using Godless.Sim.Build;
using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Core;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Godless.Unity
{
    /// <summary>
    /// Raise and lower the ground with the mouse. S07.
    ///
    ///   left-click / hold          raise
    ///   shift + left-click / hold  lower
    ///   [ and ]                    brush radius
    ///
    /// Everything real happens in the sim: VoxelRaycast finds the column,
    /// TerrainBrush moves it, and every voxel is written through
    /// VoxelWorld.Set under an annal record naming the stroke. This class
    /// only turns a mouse into those calls.
    ///
    /// Each stroke advances the clock before it writes. History snapshots at
    /// the end of a tick, and a change landing on a tick already snapshotted
    /// would be refused — the guard that stroke used to trip.
    /// </summary>
    [RequireComponent(typeof(WorldBootstrap))]
    public sealed class TerrainEditor : MonoBehaviour
    {
        [SerializeField, Range(1, 24)] int radius = 7;
        [SerializeField, Range(1, 16)] int strength = 3;
        [Tooltip("Seconds between strokes while the button is held.")]
        [SerializeField] float strokeInterval = 0.08f;

        WorldBootstrap _boot;
        Timeline _timeline;
        bool[] _solid;
        ushort _stone, _water;
        float _nextStroke;
        readonly List<Int3> _changed = new List<Int3>();

        public int Strokes { get; private set; }
        public string Status { get; private set; }

        void Awake()
        {
            _boot = GetComponent<WorldBootstrap>();
            _timeline = GetComponent<Timeline>();
        }

        void Update()
        {
            var world = _boot.World;
            if (world == null || _boot.Phase != WorldBootstrap.SetupPhase.Playing) return;   // no god before there is anyone

            if (_solid == null)
            {
                _solid = TerrainBrush.SolidTable(world.Content, world.VoxelTypes);
                _stone = world.VoxelTypes.IdOf(Symbol.For("voxel.granite"));
                _water = world.VoxelTypes.IdOf(Symbol.For("voxel.water"));
            }

            Keyboard keys = Keyboard.current;
            if (keys != null)
            {
                if (keys.leftBracketKey.wasPressedThisFrame) radius = Mathf.Max(1, radius - 1);
                if (keys.rightBracketKey.wasPressedThisFrame) radius = Mathf.Min(24, radius + 1);
            }

            Mouse mouse = Mouse.current;
            if (mouse == null) return;

            // The god acts in the present. The past can be watched, not edited.
            if (_timeline != null && _timeline.IsScrubbed)
            {
                Status = "viewing the past — return to the present to act";
                return;
            }
            Status = "brush radius " + radius + "   (click raise, shift-click lower, [ ] size, X bring down the building under the pointer)";

            // S2T: a god's hand on a building.
            if (keys != null && keys.xKey.wasPressedThisFrame) DemolishAt(mouse.position.ReadValue());

            if (!mouse.leftButton.isPressed) return;
            if (Time.unscaledTime < _nextStroke) return;
            _nextStroke = Time.unscaledTime + strokeInterval;

            bool lower = keys != null && (keys.leftShiftKey.isPressed || keys.rightShiftKey.isPressed);
            StrokeAt(mouse.position.ReadValue(), lower);
        }

        /// <summary>
        /// Brings down whatever building stands under a screen point, and
        /// everything added to it (S2T). The same collapse any cause uses; this
        /// one's cause is the god. Returns false when nothing was hit.
        /// </summary>
        public bool DemolishAt(Vector2 pointer)
        {
            var world = _boot.World;
            Settlement town = _boot.Town;
            if (world == null || town == null || _solid == null) return false;
            Camera cam = Camera.main;
            if (cam == null) return false;
            Ray ray = cam.ScreenPointToRay(pointer);

            VoxelRaycast.Hit hit;
            if (!VoxelRaycast.Cast(world.Voxels.Store, ray.origin.x, ray.origin.y, ray.origin.z,
                                   ray.direction.x, ray.direction.y, ray.direction.z, 3000,
                                   id => id != VoxelTypes.AirId && id != _water, out hit))
                return false;

            Project target = null;
            foreach (Project p in town.Projects)
            {
                if (p.Host != null) continue;
                Int3 a = Construction.World(p, 0, 0, 0);
                Int3 b = Construction.World(p, p.Plan.Width - 1, p.Plan.Height - 1, p.Plan.Depth - 1);
                bool inside = hit.Voxel.X >= a.X && hit.Voxel.X <= b.X && hit.Voxel.Z >= a.Z && hit.Voxel.Z <= b.Z && hit.Voxel.Y >= a.Y - 1 && hit.Voxel.Y <= b.Y;
                foreach (Project added in p.Added)
                {
                    Int3 c = Construction.World(added, 0, 0, 0);
                    Int3 d = Construction.World(added, added.Plan.Width - 1, added.Plan.Height - 1, added.Plan.Depth - 1);
                    if (hit.Voxel.X >= c.X && hit.Voxel.X <= d.X && hit.Voxel.Z >= c.Z && hit.Voxel.Z <= d.Z && hit.Voxel.Y >= c.Y - 1 && hit.Voxel.Y <= d.Y) inside = true;
                }
                if (inside) { target = p; break; }
            }
            if (target == null) return false;

            world.Clock.Advance();
            long tick = world.Clock.Tick;
            int before = world.Voxels.Log.Count;
            RecordId by = world.Annals.Write(tick, Symbol.For("god.brought-down"), Symbol.None, hit.Voxel, RecordId.None);
            BiomeTable biomes = BiomeTable.FromContent(world.Content);
            Collapse.BringDown(world, town, target, by, _solid, MaterialTable.FromContent(world.Content, biomes),
                               DetailModelTable.FromContent(world.Content), _boot.Parcels);

            IReadOnlyList<Godless.Sim.Deltas.VoxelDelta> log = world.Voxels.Log.All();
            for (int i = before; i < log.Count; i++) _boot.View.MarkDirty(ChunkStore.PositionOf(log[i].ChunkIndex, log[i].VoxelIndex));
            world.Voxels.EndTick(tick);
            return true;
        }

        /// <summary>
        /// One stroke of the brush at a screen point (origin bottom-left).
        /// Separate from Update so the whole edit path — camera ray, pick,
        /// brush, annal record, remesh — can be driven without a physical
        /// mouse. Synthetic device events share Mouse.current with the real
        /// one, so a test that queues a press is overwritten the moment the
        /// user's hand moves. Returns false if nothing was picked.
        /// </summary>
        public bool StrokeAt(Vector2 pointer, bool lower)
        {
            var world = _boot.World;
            if (world == null || _solid == null) return false;
            if (_timeline != null && (_timeline.IsScrubbed || _timeline.Covers(pointer))) return false;

            Camera cam = Camera.main;
            if (cam == null) return false;
            Ray ray = cam.ScreenPointToRay(pointer);

            // Water stops the ray too, so clicking the sea finds the column
            // under it — the brush then builds up from the seabed.
            VoxelRaycast.Hit hit;
            if (!VoxelRaycast.Cast(world.Voxels.Store, ray.origin.x, ray.origin.y, ray.origin.z,
                                   ray.direction.x, ray.direction.y, ray.direction.z, 3000,
                                   id => id < _solid.Length && (_solid[id] || id == _water), out hit))
                return false;

            world.Clock.Advance();
            long tick = world.Clock.Tick;
            RecordId stroke = world.Annals.Write(tick, Symbol.For(lower ? "god.lowered-ground" : "god.raised-ground"),
                                                 Symbol.None, hit.Voxel, RecordId.None, radius, strength);

            _changed.Clear();
            if (lower)
                TerrainBrush.Lower(world.Voxels, _solid, hit.Voxel.X, hit.Voxel.Z, radius, strength,
                                   _water, world.Island != null ? world.Island.SeaLevel : IslandMap.DefaultSeaLevel,
                                   tick, stroke, _changed);
            else
                TerrainBrush.Raise(world.Voxels, _solid, hit.Voxel.X, hit.Voxel.Z, radius, strength,
                                   _stone, tick, stroke, _changed, _boot.Ground);

            for (int i = 0; i < _changed.Count; i++) _boot.View.MarkDirty(_changed[i]);
            world.Voxels.EndTick(tick);
            Strokes++;
            return true;
        }
    }
}
