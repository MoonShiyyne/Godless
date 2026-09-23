using System.Collections.Generic;
using Godless.Meshing;
using Godless.Sim.Life;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Godless.Unity
{
    /// <summary>
    /// Draws every creature (v2 M1): a small voxel figure per creature from its
    /// species' models, standing, walking in two frames, bent to graze or
    /// work, or sitting — facing the way it last moved. People wear their
    /// band's colour, so a band reads as one group across a valley.
    ///
    /// Positions come from the sim once a step and are eased between steps by
    /// how far the pacer is toward the next one, so movement is smooth at any
    /// speed without the sim ever knowing a frame exists. Figures are drawn
    /// instanced, a batch per model and colour, so two thousand cost a few
    /// draw calls.
    ///
    /// Hover a creature to read it.
    /// </summary>
    [RequireComponent(typeof(WorldBootstrap))]
    public sealed class CreatureView : MonoBehaviour
    {
        [Tooltip("Never drawn shorter than this many pixels, so herds stay readable from further out.")]
        [SerializeField] float minPixelHeight = 10f;

        [Tooltip("How many times life size a figure may be drawn to stay readable from far away.")]
        [SerializeField] float maxEnlarge = 4f;

        [SerializeField] bool show = true;

        const int Batch = 1023;
        enum Pose { Stand, WalkA, WalkB, Act, Rest }

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
        DetailModel[][] _models;   // species × pose
        float[] _heightCells;      // species: model height in cells

        // Where each row was last step and is this step, keyed by row with the creature's id to spot a reused row.
        double[] _fromX = new double[0], _fromZ = new double[0], _toX = new double[0], _toZ = new double[0];
        ulong[] _idAt = new ulong[0];
        long _tick = -1;

        readonly Dictionary<int, Mesh> _meshes = new Dictionary<int, Mesh>();
        readonly Dictionary<int, List<Matrix4x4>> _batches = new Dictionary<int, List<Matrix4x4>>();
        readonly List<Matrix4x4> _chunk = new List<Matrix4x4>(Batch);

        string _hovered;
        Vector2 _hoverAt;

        public int Drawn { get; private set; }
        public string Hovered { get { return _hovered; } }

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
            Living life = _boot.World != null ? _boot.World.Life : null;
            if (!show || life == null || _boot.Parcels == null) return;
            if (_material == null && !Build(life)) return;
            Camera cam = Camera.main;
            if (cam == null) return;

            Creatures c = life.Creatures;
            if (_boot.World.Clock.Tick != _tick) Advance(c);

            float t = _boot.Pacer.IsPaused ? 1f : Mathf.Clamp01((float)_boot.Pacer.Behind);
            float pixelScale = 2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Screen.height;
            Vector2 mouse = Mouse.current != null ? Mouse.current.position.ReadValue() : new Vector2(-1000f, -1000f);
            float closest = 16f;
            int hoveredRow = -1;
            foreach (List<Matrix4x4> list in _batches.Values) list.Clear();

            int n = Mathf.Min(c.Length, _toX.Length);
            for (int i = 0; i < n; i++)
            {
                if (!c.Alive[i] || _idAt[i] != c.Id[i]) continue;
                float x = (float)(_fromX[i] + (_toX[i] - _fromX[i]) * t), z = (float)(_fromZ[i] + (_toZ[i] - _fromZ[i]) * t);
                bool moving = (_toX[i] - _fromX[i]) * (_toX[i] - _fromX[i]) + (_toZ[i] - _fromZ[i]) * (_toZ[i] - _fromZ[i]) > 0.0004;
                int gx = Mathf.Clamp((int)x, 0, ChunkStore.SizeX - 1), gz = Mathf.Clamp((int)z, 0, ChunkStore.SizeZ - 1);
                var at = new Vector3(x, _boot.Parcels.GroundAt(gx, gz) + 1f, z);

                int s = c.Species[i];
                Pose pose = moving ? ((((int)(Time.time * 6f)) + i) % 2 == 0 ? Pose.WalkA : Pose.WalkB)
                          : c.Doing[i] == Doing.Grazing || c.Doing[i] == Doing.Fighting ? Pose.Act
                          : c.Doing[i] == Doing.Resting ? Pose.Rest : Pose.Stand;

                float distance = Vector3.Distance(cam.transform.position, at);
                float height = _heightCells[s] / DetailModelTable.CellsPerVoxel;
                float enlarge = Mathf.Clamp(distance * pixelScale * minPixelHeight / Mathf.Max(0.5f, height), 1f, maxEnlarge);
                float cells = enlarge / DetailModelTable.CellsPerVoxel;
                float yaw = Mathf.Atan2((float)c.FaceX[i], (float)c.FaceZ[i]) * Mathf.Rad2Deg;

                int look = life.Species[s].Person ? (int)(c.Id[i] % 16UL) : 0;
                int colour = life.Species[s].Person ? (c.Band[i] >= 0 ? c.Band[i] % 16 : 15) : 0;
                int flags = c.Flags[i] & 3;
                int key = (((s * 5 + (int)pose) * 16 + colour) * 16 + look) * 4 + flags;
                List<Matrix4x4> list;
                if (!_batches.TryGetValue(key, out list)) { list = new List<Matrix4x4>(); _batches[key] = list; }
                list.Add(Matrix4x4.TRS(at, Quaternion.Euler(0f, yaw, 0f), new Vector3(cells, cells, cells)));

                Vector3 screen = cam.WorldToScreenPoint(at + Vector3.up * height * enlarge * 0.5f);
                if (screen.z > 0f)
                {
                    float d = Vector2.Distance(mouse, new Vector2(screen.x, screen.y));
                    if (d < closest) { closest = d; hoveredRow = i; _hoverAt = new Vector2(screen.x, Screen.height - screen.y); }
                }
            }
            if (hoveredRow >= 0) _hovered = Describe(life, hoveredRow);

            foreach (KeyValuePair<int, List<Matrix4x4>> batch in _batches)
            {
                if (batch.Value.Count == 0) continue;
                Mesh mesh = MeshFor(life, batch.Key);
                if (mesh == null) continue;
                for (int start = 0; start < batch.Value.Count; start += Batch)
                {
                    _chunk.Clear();
                    int end = Mathf.Min(batch.Value.Count, start + Batch);
                    for (int k = start; k < end; k++) _chunk.Add(batch.Value[k]);
                    Graphics.DrawMeshInstanced(mesh, 0, _material, _chunk, null, UnityEngine.Rendering.ShadowCastingMode.On, true);
                    Drawn += _chunk.Count;
                }
            }
        }

        /// <summary>A new step: where each creature was becomes where it is coming from.</summary>
        void Advance(Creatures c)
        {
            _tick = _boot.World.Clock.Tick;
            if (_toX.Length < c.Length)
            {
                int size = Mathf.Max(c.Length, _toX.Length * 2);
                System.Array.Resize(ref _fromX, size); System.Array.Resize(ref _fromZ, size);
                System.Array.Resize(ref _toX, size); System.Array.Resize(ref _toZ, size);
                System.Array.Resize(ref _idAt, size);
            }
            for (int i = 0; i < c.Length; i++)
            {
                if (!c.Alive[i]) { _idAt[i] = 0; continue; }
                bool same = _idAt[i] == c.Id[i];
                _fromX[i] = same ? _toX[i] : c.X[i];
                _fromZ[i] = same ? _toZ[i] : c.Z[i];
                // A leap (the god set it down, or a stale row) is not a walk.
                double jump = (c.X[i] - _fromX[i]) * (c.X[i] - _fromX[i]) + (c.Z[i] - _fromZ[i]) * (c.Z[i] - _fromZ[i]);
                if (jump > 16.0) { _fromX[i] = c.X[i]; _fromZ[i] = c.Z[i]; }
                _toX[i] = c.X[i];
                _toZ[i] = c.Z[i];
                _idAt[i] = c.Id[i];
            }
        }

        bool Build(Living life)
        {
            Shader shader = Shader.Find("Godless/VoxelVertexColor");
            if (shader == null) { Debug.LogWarning("Godless: VoxelVertexColor shader missing; creatures are not drawn."); show = false; return false; }
            DetailModelTable table = DetailModelTable.FromContent(_boot.World.Content);
            _models = new DetailModel[life.Species.Count][];
            _heightCells = new float[life.Species.Count];
            for (int s = 0; s < life.Species.Count; s++)
            {
                Species sp = life.Species[s];
                DetailModel stand = table.Find(sp.StandModel);
                if (stand == null) { Debug.LogWarning("Godless: no '" + sp.StandModel + "' model for " + sp.Name + "; it is not drawn."); }
                DetailModel walkA = sp.WalkModels.Count > 0 ? table.Find(sp.WalkModels[0]) : null;
                DetailModel walkB = sp.WalkModels.Count > 1 ? table.Find(sp.WalkModels[1]) : null;
                _models[s] = new[] { stand, walkA ?? stand, walkB ?? walkA ?? stand, table.Find(sp.ActModel) ?? stand, table.Find(sp.RestModel) ?? stand };
                _heightCells[s] = stand != null ? stand.SizeY : 8f;
            }
            _material = new UnityEngine.Material(shader) { name = "Creatures (runtime)", enableInstancing = true };
            _material.SetFloat("_Cuttable", 0f);
            return true;
        }

        /// <summary>A figure in one pose and colouring, meshed once and kept; centred on its feet.</summary>
        Mesh MeshFor(Living life, int key)
        {
            Mesh mesh;
            if (_meshes.TryGetValue(key, out mesh)) return mesh;
            int flags = key % 4, look = (key / 4) % 16, colour = (key / 64) % 16, pose = (key / 1024) % 5, species = key / 1024 / 5;
            DetailModel model = _models[species][pose];
            if (model == null) { _meshes[key] = null; return null; }

            Color32 clothes = BandColour(colour);
            Color32 skin = Skins[look % Skins.Length], hair = Hairs[(look / Skins.Length) % Hairs.Length];
            MeshData data = DetailMesher.Build(model, 0, slot =>
            {
                string name = slot < model.Slots.Count ? model.Slots[slot] : "";
                Color32 col = name == "skin" ? skin : name == "hair" ? hair : clothes;
                return new VoxelVisual { Opaque = true, R = col.r, G = col.g, B = col.b };
            });

            var offset = new Vector3(model.SizeX * 0.5f, 0f, model.SizeZ * 0.5f);
            var positions = new List<Vector3>(data.VertexCount);
            var normals = new List<Vector3>(data.VertexCount);
            var colours = new List<Color32>(data.VertexCount);
            for (int v = 0; v < data.VertexCount; v++)
            {
                positions.Add(new Vector3(data.Positions[v * 3], data.Positions[v * 3 + 1], data.Positions[v * 3 + 2]) - offset);
                normals.Add(new Vector3(data.Normals[v * 3], data.Normals[v * 3 + 1], data.Normals[v * 3 + 2]));
                var col = new Color32(data.Colors[v * 4], data.Colors[v * 4 + 1], data.Colors[v * 4 + 2], data.Colors[v * 4 + 3]);
                // Blessed shine gold; cursed go dark and red.
                if ((flags & Creatures.Blessed) != 0) col = Color32.Lerp(col, new Color32(255, 215, 90, 255), 0.45f);
                if ((flags & Creatures.Cursed) != 0) col = Color32.Lerp(col, new Color32(120, 10, 20, 255), 0.55f);
                colours.Add(col);
            }
            mesh = new Mesh { name = model.Id + " (runtime)" };
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetColors(colours);
            mesh.SetTriangles(data.Indices, 0);
            mesh.RecalculateBounds();
            _meshes[key] = mesh;
            return mesh;
        }

        /// <summary>A band's own colour, far apart on the wheel in founding order.</summary>
        public static Color32 BandColour(int band)
        {
            float hue = (0.03f + band * 0.381966f) % 1f;
            return Color.HSVToRGB(hue, 0.65f, 0.85f);
        }

        string Describe(Living life, int i)
        {
            Creatures c = life.Creatures;
            Species sp = life.Species[c.Species[i]];
            float years = (_boot.World.Clock.Tick - c.Born[i]) / (float)_boot.World.Clock.TicksInYears(1);
            var text = new System.Text.StringBuilder();
            text.Append(sp.Singular).Append(", ").Append(years.ToString("0")).Append(years < 1.5f ? " year old" : " years old");
            if (c.Has(i, Creatures.Blessed)) text.Append(", blessed");
            if (c.Has(i, Creatures.Cursed)) text.Append(", cursed");
            text.Append("\n").Append(Words(c.Doing[i]));
            text.Append("\nhunger ").Append((c.Hunger[i] * 100).ToString("0")).Append("%   health ").Append((c.Health[i] / sp.Health * 100).ToString("0")).Append('%');
            Band band = life.BandOf(c.Band[i]);
            if (band != null) text.Append("\nband ").Append(band.Number + 1).Append(", ").Append(band.Members).Append(band.Settled ? " people, settled" : " people");
            return text.ToString();
        }

        static string Words(Doing d)
        {
            switch (d)
            {
                case Doing.Grazing: return "eating";
                case Doing.Seeking: return "looking for food";
                case Doing.Hunting: return "hunting";
                case Doing.Fleeing: return "running away";
                case Doing.Fighting: return "fighting";
                case Doing.Resting: return "resting";
                case Doing.Following: return "keeping up with the herd";
                case Doing.Roaming: return "travelling";
                case Doing.Wandering: return "wandering";
                default: return "standing";
            }
        }

        void OnGUI()
        {
            if (string.IsNullOrEmpty(_hovered)) return;
            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 12, wordWrap = false };
            Vector2 size = style.CalcSize(new GUIContent(_hovered));
            GUI.Box(new Rect(_hoverAt.x + 14f, _hoverAt.y - size.y * 0.5f, size.x + 8f, size.y + 4f), _hovered, style);
        }
    }
}
