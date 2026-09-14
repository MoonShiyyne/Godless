using System.Collections.Generic;
using Godless.Meshing;
using Godless.Sim.Voxels;
using UnityEngine;

namespace Godless.Unity
{
    /// <summary>
    /// Draws the detail layer: beds, rubble, and whatever else content draws in
    /// detail cells. S2R.
    ///
    /// One mesh per world chunk that holds any, rebuilt when the layer reports
    /// the chunk changed. Each model is meshed once per turn and material, then
    /// copied into place, so a village of beds costs a handful of meshings.
    /// </summary>
    [RequireComponent(typeof(WorldBootstrap))]
    public sealed class DetailRenderer : MonoBehaviour
    {
        [SerializeField] UnityEngine.Material material;

        WorldBootstrap _boot;
        DetailModelTable _models;
        VoxelVisuals _visuals;
        readonly Dictionary<string, MeshData> _meshes = new Dictionary<string, MeshData>();
        readonly Dictionary<int, (GameObject go, Mesh mesh)> _chunks = new Dictionary<int, (GameObject, Mesh)>();

        /// <summary>Detail instances drawn, for the stats overlay.</summary>
        public int Instances { get; private set; }

        void Awake() { _boot = GetComponent<WorldBootstrap>(); }

        void LateUpdate()
        {
            if (_boot.World == null) return;
            if (_models == null)
            {
                _models = DetailModelTable.FromContent(_boot.World.Content);
                _visuals = VoxelVisuals.FromContent(_boot.World.Content, _boot.World.VoxelTypes);
                if (material == null)
                {
                    Shader shader = Shader.Find("Godless/VoxelVertexColor") ?? Shader.Find("Standard");
                    material = new UnityEngine.Material(shader) { name = "Details (runtime)" };
                }
            }

            foreach (int chunk in _boot.World.Details.TakeChangedChunks()) Rebuild(chunk);
            Instances = _boot.World.Details.Count;
        }

        void Rebuild(int chunk)
        {
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var colours = new List<Color32>();
            var indices = new List<int>();
            float scale = 1f / DetailModelTable.CellsPerVoxel;

            foreach (DetailInstance inst in _boot.World.Details.InChunk(chunk))
            {
                DetailModel model = _models.Find(inst.Model);
                if (model == null) continue;
                MeshData mesh = MeshFor(model, inst);
                int start = positions.Count;
                var origin = new Vector3(inst.X * scale, inst.Y * scale, inst.Z * scale);
                for (int v = 0; v < mesh.VertexCount; v++)
                {
                    positions.Add(origin + new Vector3(mesh.Positions[v * 3], mesh.Positions[v * 3 + 1], mesh.Positions[v * 3 + 2]) * scale);
                    normals.Add(new Vector3(mesh.Normals[v * 3], mesh.Normals[v * 3 + 1], mesh.Normals[v * 3 + 2]));
                    colours.Add(new Color32(mesh.Colors[v * 4], mesh.Colors[v * 4 + 1], mesh.Colors[v * 4 + 2], mesh.Colors[v * 4 + 3]));
                }
                foreach (int i in mesh.Indices) indices.Add(start + i);
            }

            (GameObject go, Mesh mesh) view;
            if (!_chunks.TryGetValue(chunk, out view))
            {
                if (indices.Count == 0) return;
                var go = new GameObject("Details " + chunk);
                go.transform.SetParent(transform, false);
                var m = new Mesh { name = "Details " + chunk, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                go.AddComponent<MeshFilter>().sharedMesh = m;
                go.AddComponent<MeshRenderer>().sharedMaterial = material;
                view = (go, m);
                _chunks[chunk] = view;
            }

            view.mesh.Clear();
            view.mesh.SetVertices(positions);
            view.mesh.SetNormals(normals);
            view.mesh.SetColors(colours);
            view.mesh.SetTriangles(indices, 0);
            view.mesh.RecalculateBounds();
            view.go.SetActive(indices.Count > 0);
        }

        MeshData MeshFor(DetailModel model, DetailInstance inst)
        {
            var key = new System.Text.StringBuilder(model.Name).Append('/').Append(inst.Turn);
            foreach (ushort s in inst.Slots) key.Append('/').Append(s);
            string k = key.ToString();
            MeshData mesh;
            if (_meshes.TryGetValue(k, out mesh)) return mesh;
            mesh = DetailMesher.Build(model, inst.Turn, slot => slot < inst.Slots.Count ? _visuals[inst.Slots[slot]] : default(VoxelVisual));
            _meshes[k] = mesh;
            return mesh;
        }
    }
}
