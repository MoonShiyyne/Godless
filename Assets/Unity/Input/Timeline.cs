using System.Collections.Generic;
using Godless.Sim.Core;
using Godless.Sim.Deltas;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Godless.Unity
{
    /// <summary>
    /// A slider over history. S07's tell — raise a hill, then scrub back to
    /// before you raised it — and the first form of S54a's timeline scrub.
    ///
    ///   drag the slider      scrub          , and .        step a tick
    ///   Home / End           the beginning, or now
    ///
    /// Scrubbing never touches the live world. It shows a HistoryView — a copy
    /// walked backward and forward through the delta log — and the renderer
    /// redraws only the voxels that differ between where the view was and
    /// where it is now. Returning to the present swaps the live world back.
    /// </summary>
    [RequireComponent(typeof(WorldBootstrap))]
    public sealed class Timeline : MonoBehaviour
    {
        WorldBootstrap _boot;
        HistoryView _view;
        Rect _panel;
        readonly List<Int3> _changed = new List<Int3>();

        public bool IsScrubbed { get { return _view != null; } }
        public long ShownTick { get { return _view != null ? _view.Tick : Present; } }

        long Present { get { return _boot.World != null ? _boot.World.Clock.Tick : 0; } }

        /// <summary>True if a screen point (origin bottom-left) is over the timeline.</summary>
        public bool Covers(Vector2 screenPoint)
        {
            return _panel.Contains(new Vector2(screenPoint.x, Screen.height - screenPoint.y));
        }

        void Awake() { _boot = GetComponent<WorldBootstrap>(); }

        void Update()
        {
            if (_boot.World == null) return;
            Keyboard keys = Keyboard.current;
            if (keys == null) return;

            long now = ShownTick;
            if (keys.commaKey.wasPressedThisFrame) Seek(now - 1);
            if (keys.periodKey.wasPressedThisFrame) Seek(now + 1);
            if (keys.homeKey.wasPressedThisFrame) Seek(0);
            if (keys.endKey.wasPressedThisFrame) Seek(Present);
        }

        /// <summary>Shows the world as it stood at the end of the given tick.</summary>
        public void Seek(long tick)
        {
            var world = _boot.World;
            if (world == null) return;
            long present = world.Clock.Tick;
            if (tick < 0) tick = 0;
            if (tick > present) tick = present;

            if (tick == present)
            {
                if (_view == null) return;
                // Walk the view back up to now, to learn what differs, then
                // hand the renderer the live world and redraw exactly that.
                _changed.Clear();
                _view.Seek(present, _changed);
                _boot.View.Show(world.Voxels.Store);
                Redraw();
                _view = null;
                return;
            }

            if (_view == null)
            {
                _view = new HistoryView(world.Voxels.Store, world.Voxels.Log, present);
                _boot.View.Show(_view.Store);
            }

            _changed.Clear();
            _view.Seek(tick, _changed);
            Redraw();
        }

        void Redraw()
        {
            for (int i = 0; i < _changed.Count; i++) _boot.View.MarkDirty(_changed[i]);
        }

        void OnGUI()
        {
            var world = _boot.World;
            if (world == null) return;

            long present = world.Clock.Tick;
            long shown = ShownTick;

            _panel = new Rect(12f, Screen.height - 70f, Screen.width - 24f, 58f);
            GUI.Box(_panel, GUIContent.none);

            string when = shown == present
                ? "the present — tick " + present
                : "tick " + shown + " of " + present + "   (" + (present - shown) + " ticks ago)";
            GUI.Label(new Rect(_panel.x + 10f, _panel.y + 6f, _panel.width - 160f, 20f),
                      "History: " + when + "    , . to step, Home / End");

            float value = GUI.HorizontalSlider(
                new Rect(_panel.x + 10f, _panel.y + 32f, _panel.width - 170f, 16f),
                shown, 0f, Mathf.Max(1f, present));

            if (GUI.Button(new Rect(_panel.xMax - 150f, _panel.y + 24f, 140f, 26f),
                           IsScrubbed ? "Return to present" : "Present"))
                Seek(present);

            long target = (long)Mathf.Round(value);
            if (target != shown) Seek(target);
        }
    }
}
