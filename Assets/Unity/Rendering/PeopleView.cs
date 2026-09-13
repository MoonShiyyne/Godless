using System.Collections.Generic;
using Godless.Sim.Drives;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Godless.Unity
{
    /// <summary>
    /// Draws the people. S2G.
    ///
    /// Everyone is a small cut-out figure coloured by what they are doing —
    /// brown felling, grey breaking rock, green foraging, orange building, red
    /// at the fire — standing where the simulation put them. Positions come
    /// from the sim once a tick and are eased between ticks by how far the
    /// pacer is toward the next one, so a walk reads as a walk at any speed
    /// without the sim ever knowing a frame exists.
    ///
    /// Hover over someone to read them: what they are doing, and the needs
    /// that have them doing it.
    /// </summary>
    [RequireComponent(typeof(WorldBootstrap))]
    public sealed class PeopleView : MonoBehaviour
    {
        [Tooltip("A person's height in voxels. A voxel is half a metre.")]
        [SerializeField] float heightVoxels = 3.6f;

        [Tooltip("Never drawn shorter than this many pixels, so a village stays readable from the whole-island view.")]
        [SerializeField] float minPixelHeight = 16f;

        [SerializeField] bool show = true;

        const int Batch = 1023;

        WorldBootstrap _boot;
        Mesh _quad;
        UnityEngine.Material _material;
        MaterialPropertyBlock _block;
        bool[] _ground;
        NeedTable _needs;

        readonly Dictionary<ulong, Vector3> _from = new Dictionary<ulong, Vector3>();
        readonly Dictionary<ulong, Vector3> _to = new Dictionary<ulong, Vector3>();
        long _tick = -1;

        readonly Matrix4x4[] _matrices = new Matrix4x4[Batch];
        readonly Vector4[] _colors = new Vector4[Batch];

        string _hovered;
        Vector2 _hoverAt;

        /// <summary>People drawn last frame.</summary>
        public int Drawn { get; private set; }

        void Awake() { _boot = GetComponent<WorldBootstrap>(); }

        void OnDestroy()
        {
            if (_material != null) Destroy(_material);
            if (_quad != null) Destroy(_quad);
        }

        void LateUpdate()
        {
            Drawn = 0;
            _hovered = null;
            if (!show || _boot.World == null || _boot.Town == null) return;
            if (_material == null && !Build()) return;

            Camera cam = Camera.main;
            if (cam == null) return;

            if (_boot.World.Clock.Tick != _tick) Advance();

            // How far the world is toward its next tick. The pacer carries the
            // fraction as debt; eased so a stride starts and ends gently.
            float t = Mathf.Clamp01((float)(_boot.Pacer.Behind));
            t = t * t * (3f - 2f * t);

            bool night = _boot.World.Clock.TickOfDay == _boot.World.Clock.TicksPerDay - 1;
            Quaternion facing = Quaternion.Euler(0f, cam.transform.eulerAngles.y, 0f);
            float pixelScale = 2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Screen.height;

            Vector2 mouse = Mouse.current != null ? Mouse.current.position.ReadValue() : new Vector2(-1000f, -1000f);
            float closest = 18f;

            int n = 0;
            foreach (Settlement s in _boot.World.Settlements)
                for (int i = 0; i < s.People.Count; i++)
                {
                    Agent a = s.People[i];
                    if (night && a.ShelteredLastNight) continue;   // indoors

                    Vector3 from, to;
                    if (!_to.TryGetValue(a.Id.Hash, out to)) continue;
                    if (!_from.TryGetValue(a.Id.Hash, out from)) from = to;
                    Vector3 at = Vector3.Lerp(from, to, t);

                    float distance = Vector3.Distance(cam.transform.position, at);
                    float height = Mathf.Max(heightVoxels, distance * pixelScale * minPixelHeight);
                    _matrices[n] = Matrix4x4.TRS(at, facing, new Vector3(height * 0.5f, height, 1f));
                    _colors[n] = ColourFor(a.Doing);

                    Vector3 screen = cam.WorldToScreenPoint(at + Vector3.up * height * 0.5f);
                    if (screen.z > 0f)
                    {
                        float d = Vector2.Distance(mouse, new Vector2(screen.x, screen.y));
                        if (d < closest) { closest = d; _hovered = Describe(s, a); _hoverAt = new Vector2(screen.x, Screen.height - screen.y); }
                    }

                    if (++n == Batch) { Flush(n); n = 0; }
                }
            if (n > 0) Flush(n);
        }

        void Flush(int count)
        {
            _block.SetVectorArray("_Color", _colors);
            Graphics.DrawMeshInstanced(_quad, 0, _material, _matrices, count, _block,
                                       UnityEngine.Rendering.ShadowCastingMode.Off, false);
            Drawn += count;
        }

        /// <summary>A new tick: where they were becomes where they are coming from.</summary>
        void Advance()
        {
            _tick = _boot.World.Clock.Tick;
            ChunkStore store = _boot.World.Voxels.Store;
            foreach (Settlement s in _boot.World.Settlements)
                foreach (Agent a in s.People)
                {
                    int x = Mathf.Clamp(a.X, 0, ChunkStore.SizeX - 1), z = Mathf.Clamp(a.Z, 0, ChunkStore.SizeZ - 1);
                    int top = store.TopMatching(x, z, _ground);
                    var now = new Vector3(x + 0.5f, top + 1f, z + 0.5f);

                    Vector3 was;
                    _from[a.Id.Hash] = _to.TryGetValue(a.Id.Hash, out was) ? was : now;
                    _to[a.Id.Hash] = now;
                }
        }

        bool Build()
        {
            Shader shader = Shader.Find("Godless/PersonSprite");
            if (shader == null) { Debug.LogWarning("Godless: PersonSprite shader missing; people are not drawn."); show = false; return false; }

            _material = new UnityEngine.Material(shader) { name = "People (runtime)", enableInstancing = true };
            _material.mainTexture = Figure();
            _block = new MaterialPropertyBlock();
            _ground = TerrainBrush.SolidTable(_boot.World.Content, _boot.World.VoxelTypes);
            _needs = DriveRules.FromContent(_boot.World.Content).Needs;

            // A quad standing on its bottom edge, so the figure's feet are on the ground.
            _quad = new Mesh { name = "Person" };
            _quad.SetVertices(new List<Vector3> { new Vector3(-0.5f, 0f, 0f), new Vector3(0.5f, 0f, 0f), new Vector3(0.5f, 1f, 0f), new Vector3(-0.5f, 1f, 0f) });
            _quad.SetUVs(0, new List<Vector2> { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) });
            _quad.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
            _quad.bounds = new Bounds(new Vector3(0f, 0.5f, 0f), Vector3.one * 2f);
            return true;
        }

        /// <summary>
        /// The figure, drawn once: eight by sixteen pixels of head, body and
        /// legs. Red is clothing (takes the activity colour), green is skin,
        /// black with alpha is the outline.
        /// </summary>
        static Texture2D Figure()
        {
            string[] rows =
            {
                "..oooo..",
                ".osssso.",
                ".osssso.",
                ".osssso.",
                "..oooo..",
                ".occcco.",
                "occcccco",
                "occcccco",
                "osccccso",
                "osccccso",
                ".occcco.",
                ".occcco.",
                ".oc..co.",
                ".oc..co.",
                ".oc..co.",
                ".oo..oo.",
            };
            var tex = new Texture2D(8, 16, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = "Person (runtime)",
            };
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 8; x++)
                {
                    char ch = rows[15 - y][x];
                    Color px = ch == 'c' ? new Color(1f, 0f, 0f, 1f)
                             : ch == 's' ? new Color(0f, 1f, 0f, 1f)
                             : ch == 'o' ? new Color(0f, 0f, 0f, 1f)
                             : new Color(0f, 0f, 0f, 0f);
                    tex.SetPixel(x, y, px);
                }
            tex.Apply();
            return tex;
        }

        static Vector4 ColourFor(string doing)
        {
            if (string.IsNullOrEmpty(doing)) return new Color(0.8f, 0.8f, 0.8f);
            if (doing.StartsWith("felling")) return new Color(0.55f, 0.33f, 0.14f);
            if (doing.StartsWith("cutting")) return new Color(0.88f, 0.78f, 0.28f);
            if (doing.StartsWith("breaking")) return new Color(0.62f, 0.63f, 0.66f);
            if (doing.StartsWith("digging")) return new Color(0.78f, 0.56f, 0.34f);
            if (doing.StartsWith("gathering")) return new Color(0.7f, 0.5f, 0.3f);
            if (doing.StartsWith("foraging")) return new Color(0.28f, 0.66f, 0.24f);
            if (doing.StartsWith("building")) return new Color(0.96f, 0.55f, 0.1f);
            if (doing.StartsWith("asleep")) return new Color(0.32f, 0.3f, 0.58f);
            if (doing.StartsWith("at the fire")) return new Color(0.82f, 0.2f, 0.14f);
            return new Color(0.85f, 0.85f, 0.85f);
        }

        string Describe(Settlement s, Agent a)
        {
            var text = new System.Text.StringBuilder();
            text.Append("person ").Append(a.Index).Append(": ").Append(string.IsNullOrEmpty(a.Doing) ? "—" : a.Doing);
            for (int n = 0; n < _needs.Count; n++)
            {
                double level = a.Level(n);
                if (level < 0.05) continue;
                text.Append("\n  ").Append(_needs[n].Name).Append(' ').Append(level.ToString("0.00"));
                if (level > _needs[n].Threshold) text.Append("  (pressing)");
            }
            return text.ToString();
        }

        /// <summary>What the pointer is over, for anything that wants to show it.</summary>
        public string Hovered { get { return _hovered; } }

        void OnGUI()
        {
            if (string.IsNullOrEmpty(_hovered)) return;
            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 12, wordWrap = false };
            Vector2 size = style.CalcSize(new GUIContent(_hovered));
            GUI.Box(new Rect(_hoverAt.x + 14f, _hoverAt.y - size.y * 0.5f, size.x + 8f, size.y + 4f), _hovered, style);
        }
    }
}
