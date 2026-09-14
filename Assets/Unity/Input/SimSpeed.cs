using Godless.Sim.Harness;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Godless.Unity
{
    /// <summary>
    /// The speed controls: space to pause, number keys for a speed, brackets
    /// to step up and down, full stop to take one day while paused.
    ///
    /// This component holds no state of its own. Everything it does is move
    /// the bootstrap's pacer, which decides how many whole ticks are due; the
    /// simulation never learns that any of this happened. Keeping it that way
    /// is what lets a faster speed be added later — or a slower one, or a
    /// scrub, or two hundred unattended years at the end of a session — with
    /// nothing to reconcile.
    /// </summary>
    [RequireComponent(typeof(WorldBootstrap))]
    public sealed class SimSpeed : MonoBehaviour
    {
        [Tooltip("Days to wind on when stepping while paused.")]
        [SerializeField] int stepDays = 1;

        WorldBootstrap _boot;

        void Awake() { _boot = GetComponent<WorldBootstrap>(); }

        /// <summary>What the keys are, for the HUD to say out loud.</summary>
        public static string Keys { get { return "space pause   1-8 speed   [ ] slower faster   . step a day"; } }

        void Update()
        {
            TickPacer pacer = _boot.Pacer;
            Keyboard keys = Keyboard.current;
            if (pacer == null || keys == null) return;

            if (keys.spaceKey.wasPressedThisFrame) pacer.TogglePause();
            if (keys.leftBracketKey.wasPressedThisFrame) pacer.Slower();
            if (keys.rightBracketKey.wasPressedThisFrame) pacer.Faster();

            if (keys.digit1Key.wasPressedThisFrame) pacer.Level = 1;
            if (keys.digit2Key.wasPressedThisFrame) pacer.Level = 2;
            if (keys.digit3Key.wasPressedThisFrame) pacer.Level = 3;
            if (keys.digit4Key.wasPressedThisFrame) pacer.Level = 4;
            if (keys.digit5Key.wasPressedThisFrame) pacer.Level = 5;
            if (keys.digit6Key.wasPressedThisFrame) pacer.Level = 6;
            if (keys.digit7Key.wasPressedThisFrame) pacer.Level = 7;
            if (keys.digit8Key.wasPressedThisFrame) pacer.Level = 8;

            // A step is the same ticks by another road: it goes through the
            // pacer's debt, so the frame budget and the mesher still get their
            // say and a held key cannot outrun the picture.
            if (keys.periodKey.wasPressedThisFrame)
                pacer.Request(stepDays * _boot.World.Clock.TicksPerDay);
        }
    }
}
