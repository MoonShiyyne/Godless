using Godless.Sim.Core;
using Godless.Sim.Harness;
using Godless.Sim.Life;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Godless.Unity
{
    /// <summary>
    /// The god's hand (v2 M1): a bar of powers along the bottom, and the
    /// mouse to use the one chosen.
    ///
    ///   Raise, Lower                         hold to paint the ground (shift lowers while Raise is chosen)
    ///   People, Deer, Sheep, Wolves          click to set them down (people come as a band with a camp)
    ///   Smite, Bless, Curse                  click on creatures
    ///   Fire, Rain, Water                    click on the land
    ///   - and =                              brush size
    ///
    /// Everything real happens in the sim: VoxelRaycast finds the column and
    /// the power goes through the world's command queue (v2 M0), which lands
    /// it at the start of the next step — or at once while paused — with the
    /// god.* record everything it touches cites. This class only turns a mouse
    /// into commands. (Named for what it was in v1; the scene holds it by name.)
    /// </summary>
    [RequireComponent(typeof(WorldBootstrap))]
    public sealed class TerrainEditor : MonoBehaviour
    {
        [SerializeField, Range(1, 24)] int radius = 7;
        [SerializeField, Range(1, 16)] int strength = 3;
        [Tooltip("Seconds between strokes while the button is held.")]
        [SerializeField] float strokeInterval = 0.08f;

        enum Power { Raise, Lower, People, Deer, Sheep, Wolves, Smite, Bless, Curse, Fire, Rain, Water }
        static readonly string[] Labels = { "Raise", "Lower", "People", "Deer", "Sheep", "Wolves", "Smite", "Bless", "Curse", "Fire", "Rain", "Water" };
        static readonly string[] SpeciesOf = { null, null, "human", "deer", "sheep", "wolf", null, null, null, null, null, null };
        static readonly int[] CountOf = { 0, 0, 10, 6, 7, 4, 0, 0, 0, 0, 0, 0 };

        Power _power = Power.Raise;
        WorldBootstrap _boot;
        Timeline _timeline;
        GodHand _hand;
        SimWorld _handWorld;
        float _nextStroke;
        Rect _bar;

        public int Strokes { get; private set; }
        public string Status { get; private set; }

        /// <summary>What the keys are, for the HUD to say out loud.</summary>
        public static string Keys { get { return "choose a power below, then click   shift-click lowers   - = brush size"; } }

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
            if (world == null || _boot.Phase != WorldBootstrap.SetupPhase.Playing) return;   // no god before there is a world

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
            Status = Labels[(int)_power] + (_power == Power.Raise || _power == Power.Lower ? ", brush " + radius : "");

            Vector2 pointer = mouse.position.ReadValue();
            if (_bar.Contains(new Vector2(pointer.x, Screen.height - pointer.y))) return;   // clicking the bar is not an act

            bool shift = keys != null && (keys.leftShiftKey.isPressed || keys.rightShiftKey.isPressed);
            if (_power == Power.Raise || _power == Power.Lower)
            {
                if (!mouse.leftButton.isPressed || Time.unscaledTime < _nextStroke) return;
                _nextStroke = Time.unscaledTime + strokeInterval;
                StrokeAt(pointer, _power == Power.Lower || shift);
                return;
            }
            if (mouse.leftButton.wasPressedThisFrame) Use(pointer);
        }

        /// <summary>One stroke of the brush at a screen point (origin bottom-left). False if nothing was picked.</summary>
        public bool StrokeAt(Vector2 pointer, bool lower)
        {
            GodHand hand = Hand();
            if (hand == null) return false;
            if (_timeline != null && (_timeline.IsScrubbed || _timeline.Covers(pointer))) return false;
            VoxelRaycast.Hit hit;
            if (!PickGround(pointer, out hit)) return false;
            Submit(lower ? (IGodCommand)new LowerGround(hand, hit.Voxel, radius, strength) : new RaiseGround(hand, hit.Voxel, radius, strength));
            Strokes++;
            return true;
        }

        /// <summary>The chosen power at a screen point. False if nothing was picked or there is no life to act on.</summary>
        public bool Use(Vector2 pointer)
        {
            LifeSystem life = _boot.Life;
            if (life == null || Hand() == null) return false;
            if (_timeline != null && (_timeline.IsScrubbed || _timeline.Covers(pointer))) return false;
            VoxelRaycast.Hit hit;
            if (!PickGround(pointer, out hit)) return false;
            Int3 at = hit.Voxel;
            IGodCommand act = null;
            switch (_power)
            {
                case Power.People: case Power.Deer: case Power.Sheep: case Power.Wolves:
                {
                    int s = life.Life.Species.IndexOf(SpeciesOf[(int)_power]);
                    if (s >= 0) act = new Spawn(life, s, at, CountOf[(int)_power]);
                    break;
                }
                case Power.Smite: act = new Smite(life, at, 3); break;
                case Power.Bless: act = new Godless.Sim.Life.Touch(life, at, true, 6); break;
                case Power.Curse: act = new Godless.Sim.Life.Touch(life, at, false, 6); break;
                case Power.Fire: act = new Fire(life, _boot.Parcels, at, 12); break;
                case Power.Rain: act = new Rain(life, at, 40); break;
                case Power.Water: act = new Water(_boot.Parcels, at, Mathf.Max(3, radius)); break;
            }
            if (act == null) return false;
            Submit(act);
            return true;
        }

        void Submit(IGodCommand act)
        {
            SimWorld world = _boot.World;
            world.Commands.Submit(act, world.Clock.Tick);
            if (_boot.Pacer == null || _boot.Pacer.IsPaused) world.Commands.ApplyNow(world);
            _boot.ShowChanges();
        }

        bool PickGround(Vector2 pointer, out VoxelRaycast.Hit hit)
        {
            hit = default(VoxelRaycast.Hit);
            GodHand hand = Hand();
            Camera cam = Camera.main;
            if (hand == null || cam == null) return false;
            // Water stops the ray too, so clicking the sea finds the column under it.
            bool[] solid = hand.Solid;
            ushort water = hand.Water;
            Ray ray = cam.ScreenPointToRay(pointer);
            return VoxelRaycast.Cast(_boot.World.Voxels.Store, ray.origin.x, ray.origin.y, ray.origin.z,
                                     ray.direction.x, ray.direction.y, ray.direction.z, 3000,
                                     id => id < solid.Length && (solid[id] || (id == water && water != VoxelTypes.AirId)), out hit);
        }

        GUIStyle _button, _chosen;

        void OnGUI()
        {
            if (_boot.World == null || _boot.Phase != WorldBootstrap.SetupPhase.Playing) return;
            if (_button == null)
            {
                _button = new GUIStyle(GUI.skin.button) { fontSize = 13 };
                _chosen = new GUIStyle(_button) { fontStyle = FontStyle.Bold };
                _chosen.normal.textColor = _chosen.hover.textColor = new Color(1f, 0.85f, 0.4f);
            }
            float gap = 4f, h = 30f;
            float w = Mathf.Min(70f, (Screen.width - 40f - gap * (Labels.Length - 1)) / Labels.Length);
            float total = Labels.Length * (w + gap) - gap;
            float x = (Screen.width - total) * 0.5f, y = Screen.height - 150f;
            _bar = new Rect(x - 6f, y - 6f, total + 12f, h + 12f);
            GUI.Box(_bar, GUIContent.none);
            for (int i = 0; i < Labels.Length; i++)
            {
                bool chosen = (int)_power == i;
                Color was = GUI.backgroundColor;
                if (chosen) GUI.backgroundColor = new Color(1f, 0.7f, 0.3f);
                if (GUI.Button(new Rect(x + i * (w + gap), y, w, h), Labels[i], chosen ? _chosen : _button)) _power = (Power)i;
                GUI.backgroundColor = was;
            }
        }
    }
}
