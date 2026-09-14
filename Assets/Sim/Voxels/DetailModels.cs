using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Core;

namespace Godless.Sim.Voxels
{
    /// <summary>
    /// A small thing drawn in detail cells: a bed, a heap of rubble, and later
    /// a person or an animal. S2R.
    ///
    /// The world grid is half a metre, which is right for walls and hills and
    /// too coarse for anything a person owns: a bed at that scale is a crate.
    /// A detail model divides each world voxel into
    /// <see cref="DetailModelTable.CellsPerVoxel"/> cells an edge (12.5 cm),
    /// drawn in content as layers of characters. Some of its colours are slots
    /// — "frame" — filled at placement with a material, so a bed made in an oak
    /// house is oak and one made in a pine house is pine.
    /// </summary>
    public sealed class DetailModel
    {
        public Symbol Id { get; internal set; }
        public string Name { get; internal set; }
        public string Tell { get; internal set; }

        /// <summary>Size in detail cells.</summary>
        public int SizeX { get; internal set; }
        public int SizeY { get; internal set; }
        public int SizeZ { get; internal set; }

        /// <summary>Paint per cell, 0 empty, else an index into Paints plus one; x-major within z within y.</summary>
        internal byte[] Cells;

        internal readonly List<DetailPaint> PaintList = new List<DetailPaint>();
        public IReadOnlyList<DetailPaint> Paints { get { return PaintList; } }

        /// <summary>Slot names in the order a placement supplies their materials.</summary>
        internal readonly List<string> SlotList = new List<string>();
        public IReadOnlyList<string> Slots { get { return SlotList; } }

        public int CellCount
        {
            get { int n = 0; foreach (byte c in Cells) if (c != 0) n++; return n; }
        }

        /// <summary>The paint at a cell of the model as authored, 0 for empty.</summary>
        public int At(int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 || x >= SizeX || y >= SizeY || z >= SizeZ) return 0;
            return Cells[(y * SizeZ + z) * SizeX + x];
        }

        /// <summary>
        /// The paint at a cell of the model turned a quarter at a time clockwise
        /// from above; (x, z) are in the turned frame, whose size swaps on odd turns.
        /// </summary>
        public int AtTurned(int x, int y, int z, int turn)
        {
            switch (((turn % 4) + 4) % 4)
            {
                case 1: return At(z, y, SizeZ - 1 - x);
                case 2: return At(SizeX - 1 - x, y, SizeZ - 1 - z);
                case 3: return At(SizeX - 1 - z, y, x);
                default: return At(x, y, z);
            }
        }

        public int TurnedSizeX(int turn) { return (turn & 1) == 1 ? SizeZ : SizeX; }
        public int TurnedSizeZ(int turn) { return (turn & 1) == 1 ? SizeX : SizeZ; }
    }

    /// <summary>One colour a model is painted with: a fixed colour, or a slot filled by a material at placement.</summary>
    public struct DetailPaint
    {
        /// <summary>The slot's index in the model, or -1 for a fixed colour.</summary>
        public int Slot;
        public int Hue, Saturation, Value;
    }

    /// <summary>Every detail model content declares. S2R.</summary>
    public sealed class DetailModelTable
    {
        /// <summary>Detail cells along a world voxel's edge: 12.5 cm cells in half-metre voxels.</summary>
        public const int CellsPerVoxel = 4;

        readonly DetailModel[] _models;
        readonly List<string> _problems;

        DetailModelTable(DetailModel[] models, List<string> problems) { _models = models; _problems = problems; }

        public int Count { get { return _models.Length; } }
        public DetailModel this[int index] { get { return _models[index]; } }
        public IReadOnlyList<string> Problems { get { return _problems; } }

        public DetailModel Find(Symbol id)
        {
            foreach (DetailModel m in _models) if (m.Id == id) return m;
            return null;
        }

        public DetailModel Find(string name) { return Find(Symbol.For("model." + name)); }

        public static DetailModelTable FromContent(ContentDatabase content)
        {
            var problems = new List<string>();
            var models = new List<DetailModel>();
            foreach (string id in content.Ids("model"))
            {
                string fault;
                DetailModel m = Load(id, content.Get("model", id), out fault);
                if (fault != null) { problems.Add("model '" + id + "' " + fault + "."); continue; }
                models.Add(m);
            }
            models.Sort((a, b) => a.Id.CompareTo(b.Id));
            return new DetailModelTable(models.ToArray(), problems);
        }

        static DetailModel Load(string id, JsonValue doc, out string fault)
        {
            fault = null;
            var m = new DetailModel
            {
                Id = Symbol.For("model." + id),
                Name = id,
                Tell = doc["tell"].AsString("").Trim(),
                SizeX = doc["size"]["x"].AsInt32(0),
                SizeY = doc["size"]["y"].AsInt32(0),
                SizeZ = doc["size"]["z"].AsInt32(0),
            };
            if (m.Tell.Length == 0) { fault = "declares no tell"; return m; }
            if (m.SizeX < 1 || m.SizeY < 1 || m.SizeZ < 1 || m.SizeX > 32 || m.SizeY > 32 || m.SizeZ > 32)
            { fault = "has a size outside 1..32 cells on some axis"; return m; }

            // Paints: a character each, a fixed colour or a slot.
            var key = new Dictionary<char, int>();
            JsonValue paints = doc["paint"];
            foreach (string k in paints.Keys)
            {
                if (k.Length != 1 || k == ".") { fault = "paints '" + k + "'; a paint is one character other than '.'"; return m; }
                JsonValue p = paints[k];
                var paint = new DetailPaint { Slot = -1 };
                string slot = p["slot"].AsString(null);
                if (slot != null)
                {
                    int s = m.SlotList.IndexOf(slot);
                    if (s < 0) { m.SlotList.Add(slot); s = m.SlotList.Count - 1; }
                    paint.Slot = s;
                }
                paint.Hue = p["hue"].AsInt32(0);
                paint.Saturation = p["saturation"].AsInt32(0);
                paint.Value = p["value"].AsInt32(50);
                m.PaintList.Add(paint);
                key[k[0]] = m.PaintList.Count;
            }

            // Layers bottom up; rows along z; characters along x.
            m.Cells = new byte[m.SizeX * m.SizeY * m.SizeZ];
            JsonValue layers = doc["layers"];
            if (layers.Count != m.SizeY) { fault = "has " + layers.Count + " layers and a height of " + m.SizeY; return m; }
            for (int y = 0; y < m.SizeY; y++)
            {
                JsonValue rows = layers[y];
                if (rows.Count != m.SizeZ) { fault = "layer " + y + " has " + rows.Count + " rows and a depth of " + m.SizeZ; return m; }
                for (int z = 0; z < m.SizeZ; z++)
                {
                    string row = rows[z].AsString("");
                    if (row.Length != m.SizeX) { fault = "layer " + y + " row " + z + " is " + row.Length + " wide, not " + m.SizeX; return m; }
                    for (int x = 0; x < m.SizeX; x++)
                    {
                        char c = row[x];
                        if (c == '.') continue;
                        int p;
                        if (!key.TryGetValue(c, out p)) { fault = "paints with '" + c + "', which it does not declare"; return m; }
                        m.Cells[(y * m.SizeZ + z) * m.SizeX + x] = (byte)p;
                    }
                }
            }
            return m;
        }
    }
}
