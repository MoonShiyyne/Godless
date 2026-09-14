using System.Collections.Generic;
using Godless.Meshing;
using Godless.Sim.Build;
using Godless.Sim.Drives;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Godless.Unity
{
    /// <summary>
    /// Draws the people. S2G, and since S2V in detail cells.
    ///
    /// Everyone is a small voxel figure, 1.75 m of 12.5 cm cells, posed for
    /// what they are doing — walking, bent to a tool, sitting to eat, kneeling
    /// at the water, lying in their own bed — facing the way they last walked
    /// and dressed in its colour: brown felling, grey breaking rock, green
    /// foraging, orange building, blue drinking. The figures are content
    /// (`models/person-*.json`); skin and hair are the person's own.
    ///
    /// Positions come from the sim once a tick and are eased between ticks by
    /// how far the pacer is toward the next one, so a walk reads as a walk at
    /// any speed without the sim ever knowing a frame exists.
    ///
    /// Hover over someone to read them: what they are doing, and the needs
    /// that have them doing it.
    /// </summary>
    [RequireComponent(typeof(WorldBootstrap))]
    public sealed class PeopleView : MonoBehaviour
    {
        [Tooltip("Never drawn shorter than this many pixels, so a village stays readable from further out.")]
        [SerializeField] float minPixelHeight = 14f;

        [Tooltip("How many times life size a figure may be drawn to stay readable from far away.")]
        [SerializeField] float maxEnlarge = 5f;

        [SerializeField] bool show = true;

        const int Batch = 1023;
        const float HeightVoxels = 3.5f;

        enum Pose { Stand, WalkA, WalkB, Work, Sit, Kneel, Lie }
        static readonly string[] PoseModels = { "person-stand", "person-walk-a", "person-walk-b", "person-work", "person-sit", "person-kneel", "person-lie" };

        static readonly Color32[] Skins =
        {
            new Color32(236, 196, 160, 255), new Color32(198, 146, 104, 255), new Color32(141, 94, 62, 255), new Color32(96, 64, 44, 255),
        };
        static readonly Color32[] Hairs =
        {
            new Color32(40, 30, 24, 255), new Color32(92, 60, 34, 255), new Color32(170, 128, 70, 255), new Color32(120, 116, 110, 255),
        };

        WorldBootstrap _boot;
        UnityEngine.Material _material;
        bool[] _ground;
        NeedTable _needs;
        DetailModelTable _models;
        DetailModel[] _poses;

        readonly Dictionary<ulong, Vector3> _from = new Dictionary<ulong, Vector3>();
        readonly Dictionary<ulong, Vector3> _to = new Dictionary<ulong, Vector3>();
        readonly Dictionary<ulong, float> _yaw = new Dictionary<ulong, float>();
        readonly Dictionary<ulong, float> _bedYaw = new Dictionary<ulong, float>();
        long _tick = -1;

        readonly Dictionary<int, Mesh> _meshes = new Dictionary<int, Mesh>();
        readonly Dictionary<int, List<Matrix4x4>> _batches = new Dictionary<int, List<Matrix4x4>>();
        readonly List<Matrix4x4> _chunk = new List<Matrix4x4>(Batch);

        string _hovered;
        Vector2 _hoverAt;

        /// <summary>People drawn last frame.</summary>
        public int Drawn { get; private set; }

        void Awake() { _boot = GetComponent<WorldBootstrap>(); }

        void OnDestroy()
        {
            if (_material != null) Destroy(_material);
            foreach (Mesh m in _meshes.Values) Destroy(m);
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
            float eased = t * t * (3f - 2f * t);

            float pixelScale = 2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Screen.height;

            Vector2 mouse = Mouse.current != null ? Mouse.current.position.ReadValue() : new Vector2(-1000f, -1000f);
            float closest = 18f;

            foreach (List<Matrix4x4> list in _batches.Values) list.Clear();
            CutawayView cutaway = GetComponent<CutawayView>();

            foreach (Settlement s in _boot.World.Settlements)
                for (int i = 0; i < s.People.Count; i++)
                {
                    Agent a = s.People[i];
                    ulong id = a.Id.Hash;

                    Vector3 from, to;
                    if (!_to.TryGetValue(id, out to)) continue;
                    if (!_from.TryGetValue(id, out from)) from = to;
                    Vector3 at = Vector3.Lerp(from, to, eased);
                    bool moving = !_boot.Pacer.IsPaused && t < 0.999f && (to - from).sqrMagnitude > 0.09f;

                    float yaw;
                    Pose pose = PoseOf(a, moving, i);
                    float bedYaw;
                    // Only a walk is drawn between two places; anyone else is where they are.
                    if (!moving) at = to;
                    if (pose == Pose.Lie && _bedYaw.TryGetValue(id, out bedYaw)) yaw = bedYaw;
                    else if (!_yaw.TryGetValue(id, out yaw)) yaw = (id % 360);

                    // Upstairs, while the cutaway has taken the upstairs away.
                    if (cutaway != null && cutaway.Hides(at)) continue;

                    float distance = Vector3.Distance(cam.transform.position, at);
                    float enlarge = Mathf.Clamp(distance * pixelScale * minPixelHeight / HeightVoxels, 1f, maxEnlarge);
                    float cells = enlarge / DetailModelTable.CellsPerVoxel;

                    int colour = ColourIndex(a.Doing);
                    int look = (int)(id % (ulong)(Skins.Length * Hairs.Length));
                    int key = ((int)pose * 32 + colour) * 16 + look;
                    List<Matrix4x4> list;
                    if (!_batches.TryGetValue(key, out list)) { list = new List<Matrix4x4>(); _batches[key] = list; }
                    list.Add(Matrix4x4.TRS(at, Quaternion.Euler(0f, yaw, 0f), new Vector3(cells, cells, cells)));

                    Vector3 screen = cam.WorldToScreenPoint(at + Vector3.up * HeightVoxels * enlarge * 0.5f);
                    if (screen.z > 0f)
                    {
                        float d = Vector2.Distance(mouse, new Vector2(screen.x, screen.y));
                        if (d < closest) { closest = d; _hovered = Describe(s, a); _hoverAt = new Vector2(screen.x, Screen.height - screen.y); }
                    }
                }

            foreach (KeyValuePair<int, List<Matrix4x4>> batch in _batches)
            {
                if (batch.Value.Count == 0) continue;
                Mesh mesh = MeshFor(batch.Key);
                if (mesh == null) continue;
                for (int start = 0; start < batch.Value.Count; start += Batch)
                {
                    _chunk.Clear();
                    int end = Mathf.Min(batch.Value.Count, start + Batch);
                    for (int k = start; k < end; k++) _chunk.Add(batch.Value[k]);
                    Graphics.DrawMeshInstanced(mesh, 0, _material, _chunk, null,
                                               UnityEngine.Rendering.ShadowCastingMode.On, true);
                    Drawn += _chunk.Count;
                }
            }
        }

        /// <summary>The figure for what someone is doing; between ticks, a walk in two steps.</summary>
        static Pose PoseOf(Agent a, bool moving, int index)
        {
            if (moving) return ((int)(Time.time * 4f) + index) % 2 == 0 ? Pose.WalkA : Pose.WalkB;
            if (a.Doing != null && a.Doing.StartsWith("asleep")) return Pose.Lie;
            switch (a.Pose)
            {
                case "work": return Pose.Work;
                case "sit": return Pose.Sit;
                case "kneel": return Pose.Kneel;
                case "lie": return Pose.Lie;
                default: return Pose.Stand;
            }
        }

        /// <summary>A new tick: where they were becomes where they are coming from.</summary>
        void Advance()
        {
            _tick = _boot.World.Clock.Tick;
            ChunkStore store = _boot.World.Voxels.Store;
            _bedYaw.Clear();
            foreach (Settlement s in _boot.World.Settlements)
                foreach (Agent a in s.People)
                {
                    ulong id = a.Id.Hash;
                    int x = Mathf.Clamp(a.X, 0, ChunkStore.SizeX - 1), z = Mathf.Clamp(a.Z, 0, ChunkStore.SizeZ - 1);
                    var now = new Vector3(x + 0.5f, StandingHeight(store, x, z), z + 0.5f);

                    // Asleep in their own bed: on it, the way it lies.
                    float bedYaw;
                    Vector3 onBed;
                    if (a.Doing != null && (a.Doing.StartsWith("asleep in bed") || a.Doing.StartsWith("asleep in a spare bed"))
                        && OnBed(s, a, out onBed, out bedYaw))
                    {
                        now = onBed;
                        _bedYaw[id] = bedYaw;
                    }

                    Vector3 was;
                    bool had = _to.TryGetValue(id, out was);
                    _from[id] = had ? was : now;
                    _to[id] = now;

                    Vector3 step = now - (had ? was : now);
                    if (step.x * step.x + step.z * step.z > 0.09f) _yaw[id] = Mathf.Atan2(step.x, step.z) * Mathf.Rad2Deg;
                }
        }

        /// <summary>The lowest place in a column someone could stand: ground with two voxels of room over it, so indoors is the floor, not the roof.</summary>
        float StandingHeight(ChunkStore store, int x, int z)
        {
            for (int y = 1; y < ChunkStore.SizeY - 2; y++)
                if (_ground[store.Get(x, y - 1, z)] && !_ground[store.Get(x, y, z)] && !_ground[store.Get(x, y + 1, z)])
                    return y;
            return store.TopMatching(x, z, _ground) + 1f;
        }

        /// <summary>Where a person lies in their own bed, and which way their head points.</summary>
        bool OnBed(Settlement s, Agent a, out Vector3 at, out float yaw)
        {
            at = Vector3.zero;
            yaw = 0f;
            Furnishing.Bed bed;
            if (!Places.SleepsIn(s, a, out bed)) return false;
            DetailInstance inst = _boot.World.Details.Get(bed.Instance);
            if (inst == null) return false;
            DetailModel model = _models.Find(inst.Model);
            if (model == null) return false;

            float per = DetailModelTable.CellsPerVoxel;
            at = new Vector3((inst.X + model.TurnedSizeX(inst.Turn) * 0.5f) / per,
                             (inst.Y + model.SizeY - 1) / per,
                             (inst.Z + model.TurnedSizeZ(inst.Turn) * 0.5f) / per);
            // The bed's pillow is at its low x as authored; a figure's head is at +z.
            yaw = 270f - 90f * inst.Turn;
            return true;
        }

        bool Build()
        {
            Shader shader = Shader.Find("Godless/VoxelVertexColor");
            if (shader == null) { Debug.LogWarning("Godless: VoxelVertexColor shader missing; people are not drawn."); show = false; return false; }

            _models = DetailModelTable.FromContent(_boot.World.Content);
            _poses = new DetailModel[PoseModels.Length];
            for (int p = 0; p < PoseModels.Length; p++)
            {
                _poses[p] = _models.Find(PoseModels[p]);
                if (_poses[p] == null) { Debug.LogWarning("Godless: no '" + PoseModels[p] + "' model in content; people are not drawn."); show = false; return false; }
            }

            _material = new UnityEngine.Material(shader) { name = "People (runtime)", enableInstancing = true };
            _material.SetFloat("_Cuttable", 0f);
            _ground = TerrainBrush.SolidTable(_boot.World.Content, _boot.World.VoxelTypes);
            _needs = DriveRules.FromContent(_boot.World.Content).Needs;
            return true;
        }

        /// <summary>A figure in one pose, dressed and coloured, meshed once and kept; centred on its feet.</summary>
        Mesh MeshFor(int key)
        {
            Mesh mesh;
            if (_meshes.TryGetValue(key, out mesh)) return mesh;

            int look = key % 16, colour = (key / 16) % 32, pose = key / 16 / 32;
            DetailModel model = _poses[pose];
            Color32 clothes = Palette[colour];
            Color32 skin = Skins[look % Skins.Length], hair = Hairs[(look / Skins.Length) % Hairs.Length];

            MeshData data = DetailMesher.Build(model, 0, slot =>
            {
                string name = slot < model.Slots.Count ? model.Slots[slot] : "";
                Color32 c = name == "skin" ? skin : name == "hair" ? hair : clothes;
                return new VoxelVisual { Opaque = true, R = c.r, G = c.g, B = c.b };
            });

            var offset = new Vector3(model.SizeX * 0.5f, 0f, model.SizeZ * 0.5f);
            var positions = new List<Vector3>(data.VertexCount);
            var normals = new List<Vector3>(data.VertexCount);
            var colours = new List<Color32>(data.VertexCount);
            for (int v = 0; v < data.VertexCount; v++)
            {
                positions.Add(new Vector3(data.Positions[v * 3], data.Positions[v * 3 + 1], data.Positions[v * 3 + 2]) - offset);
                normals.Add(new Vector3(data.Normals[v * 3], data.Normals[v * 3 + 1], data.Normals[v * 3 + 2]));
                colours.Add(new Color32(data.Colors[v * 4], data.Colors[v * 4 + 1], data.Colors[v * 4 + 2], data.Colors[v * 4 + 3]));
            }
            mesh = new Mesh { name = PoseModels[pose] + " (runtime)" };
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetColors(colours);
            mesh.SetTriangles(data.Indices, 0);
            mesh.RecalculateBounds();
            _meshes[key] = mesh;
            return mesh;
        }

        /// <summary>Clothes colours, by what someone is doing.</summary>
        static readonly Color32[] Palette =
        {
            new Color32(204, 204, 204, 255),   // 0 anything else
            new Color32(140, 84, 36, 255),     // 1 felling
            new Color32(224, 199, 71, 255),    // 2 cutting reed and grass
            new Color32(158, 161, 168, 255),   // 3 breaking rock
            new Color32(199, 143, 87, 255),    // 4 digging
            new Color32(179, 128, 77, 255),    // 5 gathering
            new Color32(71, 168, 61, 255),     // 6 foraging
            new Color32(245, 140, 26, 255),    // 7 building
            new Color32(82, 77, 148, 255),     // 8 asleep
            new Color32(209, 51, 36, 255),     // 9 at the fire, warming, keeping watch
            new Color32(58, 128, 204, 255),    // 10 drinking
            new Color32(214, 170, 60, 255),    // 11 eating
            new Color32(170, 90, 170, 255),    // 12 talking
            new Color32(120, 160, 150, 255),   // 13 resting
        };

        static int ColourIndex(string doing)
        {
            if (string.IsNullOrEmpty(doing)) return 0;
            const string onTheWay = "on the way: ";
            if (doing.StartsWith(onTheWay)) doing = doing.Substring(onTheWay.Length);
            if (doing.StartsWith("felling")) return 1;
            if (doing.StartsWith("cutting")) return 2;
            if (doing.StartsWith("breaking")) return 3;
            if (doing.StartsWith("digging")) return 4;
            if (doing.StartsWith("gathering")) return 5;
            if (doing.StartsWith("foraging")) return 6;
            if (doing.StartsWith("building")) return 7;
            if (doing.StartsWith("asleep")) return 8;
            if (doing.StartsWith("at the fire") || doing.StartsWith("warming") || doing.StartsWith("keeping watch")) return 9;
            if (doing.StartsWith("drinking")) return 10;
            if (doing.StartsWith("eating")) return 11;
            if (doing.StartsWith("talking")) return 12;
            if (doing.StartsWith("resting")) return 13;
            return 0;
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
