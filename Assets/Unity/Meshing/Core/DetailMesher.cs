using System;
using Godless.Sim.Voxels;

namespace Godless.Meshing
{
    /// <summary>
    /// A detail model as a mesh, in detail cells, its lowest corner at the
    /// origin. S2R.
    ///
    /// The same greedy mesher the terrain uses, fed a padded block built from
    /// the model instead of a chunk: each paint is a "voxel type" whose colour
    /// is the paint's, or the material filling its slot. A renderer scales the
    /// result down by CellsPerVoxel and moves it into place.
    /// </summary>
    public static class DetailMesher
    {
        /// <param name="slotColour">The colour of the material filling a slot, by slot index.</param>
        public static MeshData Build(DetailModel model, int turn, Func<int, VoxelVisual> slotColour)
        {
            int sx = model.TurnedSizeX(turn), sy = model.SizeY, sz = model.TurnedSizeZ(turn);
            var pad = new ushort[ChunkMesher.PaddedVolume];
            for (int y = 0; y < sy; y++)
                for (int z = 0; z < sz; z++)
                    for (int x = 0; x < sx; x++)
                    {
                        int paint = model.AtTurned(x, y, z, turn);
                        if (paint != 0) pad[ChunkMesher.PaddedIndex(x, y, z)] = (ushort)paint;
                    }

            var table = new VoxelVisual[model.Paints.Count + 1];
            for (int p = 0; p < model.Paints.Count; p++)
            {
                DetailPaint paint = model.Paints[p];
                if (paint.Slot >= 0)
                {
                    VoxelVisual v = slotColour(paint.Slot);
                    table[p + 1] = new VoxelVisual { Opaque = true, R = v.R, G = v.G, B = v.B };
                    continue;
                }
                byte r, g, b;
                VoxelVisuals.HslToRgb(paint.Hue, paint.Saturation, paint.Value, out r, out g, out b);
                table[p + 1] = new VoxelVisual { Opaque = true, R = r, G = g, B = b };
            }

            var mesh = new MeshData();
            ChunkMesher.BuildFromPadded(pad, 0, 0, 0, VoxelVisuals.FromTable(table), mesh, new MeshData());
            return mesh;
        }
    }
}
