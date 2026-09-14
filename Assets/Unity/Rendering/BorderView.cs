using System.Collections.Generic;
using Godless.Sim.Settlements;
using Godless.Sim.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Godless.Unity
{
    /// <summary>
    /// Draws the town borders (S2Y): a low line along the edge of the ground
    /// each town holds, in that town's own colour, redrawn whenever the borders
    /// are. B hides and shows them.
    /// </summary>
    [RequireComponent(typeof(WorldBootstrap))]
    public sealed class BorderView : MonoBehaviour
    {
        [SerializeField] bool show = true;

        [Tooltip("How tall the border line stands, in voxels.")]
        [SerializeField] float height = 1.2f;

        [Tooltip("How far inside its own ground a town's line is drawn, in voxels, so two towns that meet show two lines.")]
        [SerializeField] float inset = 0.6f;

        WorldBootstrap _boot;
        GameObject _go;
        Mesh _mesh;
        int _version = -1, _towns = -1;

        public static string Keys { get { return "B borders"; } }

        void Awake() { _boot = GetComponent<WorldBootstrap>(); }

        void OnDestroy() { if (_mesh != null) Destroy(_mesh); }

        void LateUpdate()
        {
            Keyboard keys = Keyboard.current;
            if (keys != null && keys.bKey.wasPressedThisFrame) show = !show;
            if (_go != null) _go.SetActive(show);
            if (!show || _boot.World == null || _boot.Parcels == null || _boot.World.Settlements.Count == 0) return;

            Territory borders = _boot.World.Settlements[0].Borders;
            if (borders == null) return;
            if (borders.Version == _version && _boot.World.Settlements.Count == _towns) return;
            _version = borders.Version;
            _towns = _boot.World.Settlements.Count;
            Rebuild(borders);
        }

        /// <summary>A colour of its own for each town, far apart on the wheel, in founding order.</summary>
        public static Color TownColour(int index)
        {
            float hue = (0.08f + index * 0.381966f) % 1f;
            return Color.HSVToRGB(hue, 0.75f, 1f);
        }

        void Rebuild(Territory borders)
        {
            var positions = new List<Vector3>();
            var colours = new List<Color32>();
            var indices = new List<int>();
            ParcelGrid grid = _boot.Parcels;
            var towns = _boot.World.Settlements;
            float size = ParcelGrid.Size;
            int[] ox = { 0, 1, 0, -1 }, oz = { -1, 0, 1, 0 };

            for (int pz = 0; pz < ParcelGrid.Depth; pz++)
                for (int px = 0; px < ParcelGrid.Width; px++)
                {
                    Settlement s = borders.Owner(px, pz);
                    if (s == null) continue;
                    Color32 colour = TownColour(towns.IndexOf(s));
                    for (int side = 0; side < 4; side++)
                    {
                        int nx = px + ox[side], nz = pz + oz[side];
                        if (borders.Owner(nx, nz) == s) continue;

                        float baseY = grid.MaxGround(px, pz) + 1f;
                        if (ParcelGrid.InBounds(nx, nz)) baseY = Mathf.Max(baseY, grid.MaxGround(nx, nz) + 1f);

                        // The edge of the parcel on this side, pulled a little inside it.
                        Vector3 a, b;
                        float x0 = px * size, z0 = pz * size, x1 = x0 + size, z1 = z0 + size;
                        switch (side)
                        {
                            case 0: a = new Vector3(x0, baseY, z0 + inset); b = new Vector3(x1, baseY, z0 + inset); break;
                            case 1: a = new Vector3(x1 - inset, baseY, z0); b = new Vector3(x1 - inset, baseY, z1); break;
                            case 2: a = new Vector3(x1, baseY, z1 - inset); b = new Vector3(x0, baseY, z1 - inset); break;
                            default: a = new Vector3(x0 + inset, baseY, z1); b = new Vector3(x0 + inset, baseY, z0); break;
                        }
                        int start = positions.Count;
                        positions.Add(a);
                        positions.Add(b);
                        positions.Add(b + Vector3.up * height);
                        positions.Add(a + Vector3.up * height);
                        for (int k = 0; k < 4; k++) colours.Add(colour);
                        indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
                        indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
                    }
                }

            if (_go == null)
            {
                _go = new GameObject("Town borders");
                _mesh = new Mesh { name = "Town borders", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
                var renderer = _go.AddComponent<MeshRenderer>();
                // Unlit and two-sided with vertex colour: a line, not a wall.
                Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                renderer.sharedMaterial = new UnityEngine.Material(shader) { name = "Borders (runtime)" };
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            _mesh.Clear();
            _mesh.SetVertices(positions);
            _mesh.SetColors(colours);
            _mesh.SetTriangles(indices, 0);
            _mesh.RecalculateBounds();
        }
    }
}
