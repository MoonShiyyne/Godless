using Godless.Sim.Build;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;
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
    ///   - and =                    brush radius
    ///   X                          bring down the building under the pointer
    ///
    /// Everything real happens in the sim: VoxelRaycast finds the column and
    /// GodHand does the rest — the brush, the god.* record every voxel cites,
    /// and marking the planning grid, which reads the new ground at the start
    /// of the next tick. This class only turns a mouse into those calls.
    ///
    /// A stroke lands on the tick the world is at. It used to advance the
    /// clock first, so a held button ran a dozen ticks a second that no system
    /// saw; GodHand only moves the world on when history has already closed
    /// the present, and then by a whole tick with every system in it.
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
        GodHand _hand;
        SimWorld _handWorld;
        float _nextStroke;

        public int Strokes { get; private set; }
        public string Status { get; private set; }

        /// <summary>What the keys are, for the HUD to say out loud.</summary>
        public static string Keys { get { return "click raise   shift-click lower   - = brush size   X bring down the building under the pointer"; } }

        void Awake()
        {
            _boot = GetComponent<WorldBootstrap>();
            _timeline = GetComponent<Timeline>();
        }

        /// <summary>The hand for the world on screen, made again when the world is.</summary>
        GodHand Hand()
        {
            SimWorld world = _boot.World;
            if (world == null) return null;
            if (_hand == null || _handWorld != world)
            {
                _hand = new GodHand(world, _boot.Parcels, _boot.Ground);
                _handWorld = world;
            }
            return _hand;
        }

        void Update()
        {
            var world = _boot.World;
            if (world == null || _boot.Phase != WorldBootstrap.SetupPhase.Playing) return;   // no god before there is anyone

            Keyboard keys = Keyboard.current;
            if (keys != null)
            {
                if (keys.minusKey.wasPressedThisFrame || keys.numpadMinusKey.wasPressedThisFrame) radius = Mathf.Max(1, radius - 1);
                if (keys.equalsKey.wasPressedThisFrame || keys.numpadPlusKey.wasPressedThisFrame) radius = Mathf.Min(24, radius + 1);
            }

            Mouse mouse = Mouse.current;
            if (mouse == null) return;

            // The god acts in the present. The past can be watched, not edited.
            if (_timeline != null && _timeline.IsScrubbed)
            {
                Status = "viewing the past — return to the present to act";
                return;
            }
            Status = "brush radius " + radius;

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
            GodHand hand = Hand();
            Settlement town = _boot.Town;
            if (hand == null || town == null) return false;
            if (_timeline != null && _timeline.IsScrubbed) return false;

            VoxelRaycast.Hit hit;
            ushort water = hand.Water;
            if (!Pick(pointer, id => id != VoxelTypes.AirId && id != water, out hit)) return false;

            Project target = GodHand.BuildingAt(town, hit.Voxel);
            if (target == null) return false;
            hand.BringDown(town, target, hit.Voxel);
            _boot.ShowChanges();
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
            GodHand hand = Hand();
            if (hand == null) return false;
            if (_timeline != null && (_timeline.IsScrubbed || _timeline.Covers(pointer))) return false;

            // Water stops the ray too, so clicking the sea finds the column
            // under it — the brush then builds up from the seabed.
            bool[] solid = hand.Solid;
            ushort water = hand.Water;
            VoxelRaycast.Hit hit;
            if (!Pick(pointer, id => id < solid.Length && (solid[id] || (id == water && water != VoxelTypes.AirId)), out hit))
                return false;

            if (lower) hand.Lower(hit.Voxel, radius, strength);
            else hand.Raise(hit.Voxel, radius, strength);

            _boot.ShowChanges();
            Strokes++;
            return true;
        }

        bool Pick(Vector2 pointer, System.Func<ushort, bool> stops, out VoxelRaycast.Hit hit)
        {
            hit = default(VoxelRaycast.Hit);
            Camera cam = Camera.main;
            if (cam == null) return false;
            Ray ray = cam.ScreenPointToRay(pointer);
            return VoxelRaycast.Cast(_boot.World.Voxels.Store, ray.origin.x, ray.origin.y, ray.origin.z,
                                     ray.direction.x, ray.direction.y, ray.direction.z, 3000, stops, out hit);
        }
    }
}
