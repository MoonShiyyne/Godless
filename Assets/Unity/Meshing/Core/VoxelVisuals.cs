using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Voxels;

namespace Godless.Meshing
{
    /// <summary>How one voxel type draws.</summary>
    public struct VoxelVisual
    {
        /// <summary>Hides the faces of anything touching it, and casts ambient occlusion.</summary>
        public bool Opaque;

        /// <summary>Drawn as a surface against air, in its own mesh.</summary>
        public bool Liquid;

        public byte R, G, B;
    }

    /// <summary>
    /// Voxel colours, read from content. L5 applies to presentation too: a
    /// mod that ships a new material ships its colour with it.
    ///
    /// Lightness comes from the voxel's `value` — the same number the S0A
    /// contrast rule checks — so the rule and the rendered colour cannot drift
    /// apart. Hue and saturation are the only purely visual inputs.
    /// </summary>
    public sealed class VoxelVisuals
    {
        readonly VoxelVisual[] _byId;

        VoxelVisuals(VoxelVisual[] byId) { _byId = byId; }

        public int Count { get { return _byId.Length; } }

        public VoxelVisual this[ushort id]
        {
            get { return id < _byId.Length ? _byId[id] : default(VoxelVisual); }
        }

        public static VoxelVisuals FromContent(ContentDatabase content, VoxelTypes types)
        {
            // Index 0 is air: neither opaque nor liquid, never drawn.
            var byId = new VoxelVisual[types.Count];

            foreach (string name in content.Ids("voxel"))
            {
                ushort id;
                if (!types.TryGetId(Symbol.For("voxel." + name), out id)) continue;

                JsonValue doc = content.Get("voxel", name);
                bool solid = doc["solid"].AsBool(true);

                byte r, g, b;
                HslToRgb(doc["hue"].AsInt32(0), doc["saturation"].AsInt32(0), doc["value"].AsInt32(50),
                         out r, out g, out b);

                byId[id] = new VoxelVisual
                {
                    Opaque = solid,
                    // Anything declared but not solid is drawn as a liquid
                    // surface. Air is never declared, so this is only water
                    // for now.
                    Liquid = !solid,
                    R = r, G = g, B = b,
                };
            }

            return new VoxelVisuals(byId);
        }

        /// <summary>For tests: an explicit table without content.</summary>
        public static VoxelVisuals FromTable(VoxelVisual[] byId) { return new VoxelVisuals(byId); }

        /// <summary>HSL to 8-bit RGB. Plain arithmetic, no transcendentals.</summary>
        public static void HslToRgb(int hueDegrees, int saturation, int lightness,
                                    out byte r, out byte g, out byte b)
        {
            double h = ((hueDegrees % 360) + 360) % 360 / 60.0;
            double s = saturation < 0 ? 0 : (saturation > 100 ? 1 : saturation / 100.0);
            double l = lightness < 0 ? 0 : (lightness > 100 ? 1 : lightness / 100.0);

            double c = (1.0 - System.Math.Abs(2.0 * l - 1.0)) * s;
            double hMod2 = h - 2.0 * System.Math.Floor(h / 2.0);
            double x = c * (1.0 - System.Math.Abs(hMod2 - 1.0));
            double m = l - c / 2.0;

            double r1, g1, b1;
            if (h < 1) { r1 = c; g1 = x; b1 = 0; }
            else if (h < 2) { r1 = x; g1 = c; b1 = 0; }
            else if (h < 3) { r1 = 0; g1 = c; b1 = x; }
            else if (h < 4) { r1 = 0; g1 = x; b1 = c; }
            else if (h < 5) { r1 = x; g1 = 0; b1 = c; }
            else { r1 = c; g1 = 0; b1 = x; }

            r = ToByte(r1 + m);
            g = ToByte(g1 + m);
            b = ToByte(b1 + m);
        }

        static byte ToByte(double v)
        {
            double scaled = v * 255.0 + 0.5;
            if (scaled < 0) return 0;
            if (scaled > 255) return 255;
            return (byte)scaled;
        }
    }
}
