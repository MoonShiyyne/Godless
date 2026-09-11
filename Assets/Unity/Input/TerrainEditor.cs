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
            if (world == null) return;

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
            Status = "brush radius " + radius + "   (click raise, shift-click lower, [ ] size)";

            if (!mouse.leftButton.isPressed) return;
            Vector2 pointer = mouse.position.ReadValue();
            if (_timeline != null && _timeline.Covers(pointer)) return;
            if (Time.unscaledTime < _nextStroke) return;
            _nextStroke = Time.unscaledTime + strokeInterval;

            Camera cam = Camera.main;
            if (cam == null) return;
            Ray ray = cam.ScreenPointToRay(pointer);

            // Water stops the ray too, so clicking the sea finds the column
            // under it — the brush then builds up from the seabed.
            VoxelRaycast.Hit hit;
            if (!VoxelRaycast.Cast(world.Voxels.Store, ray.origin.x, ray.origin.y, ray.origin.z,
                                   ray.direction.x, ray.direction.y, ray.direction.z, 3000,
                                   id => id < _solid.Length && (_solid[id] || id == _water), out hit))
                return;

            bool lower = keys != null && (keys.leftShiftKey.isPressed || keys.rightShiftKey.isPressed);

            world.Clock.Advance();
            long tick = world.Clock.Tick;
            RecordId stroke = world.Annals.Write(tick, Symbol.For(lower ? "god.lowered-ground" : "god.raised-ground"),
                                                 Symbol.None, hit.Voxel, RecordId.None, radius, strength);

            _changed.Clear();
            if (lower)
                TerrainBrush.Lower(world.Voxels, _solid, hit.Voxel.X, hit.Voxel.Z, radius, strength,
                                   _water, IslandMap.SeaLevel, tick, stroke, _changed);
            else
                TerrainBrush.Raise(world.Voxels, _solid, hit.Voxel.X, hit.Voxel.Z, radius, strength,
                                   _stone, tick, stroke, _changed);

            for (int i = 0; i < _changed.Count; i++) _boot.View.MarkDirty(_changed[i]);
            world.Voxels.EndTick(tick);
            Strokes++;
        }
    }
}
