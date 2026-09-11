using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Settlements;
using Godless.Sim.World;

namespace Godless.Sim.Build
{
    /// <summary>What may fill one role, and whether the role is a hole in the wall.</summary>
    public sealed class RoleTile
    {
        public Symbol Role { get; internal set; }
        public string Name { get; internal set; }

        /// <summary>Material classes that can serve it, best first. Empty for an opening.</summary>
        public IReadOnlyList<Symbol> Classes { get; internal set; }

        /// <summary>An opening: a window or a doorway. Left as air unless something can close it.</summary>
        public bool Open { get; internal set; }

        /// <summary>Whether the building can stand without it. A roof cannot be skipped; a door can.</summary>
        public bool Optional { get; internal set; }
    }

    /// <summary>
    /// The tileset: which materials may play which part of a building, what
    /// may rest on what, and the rules that keep a building readable at
    /// camera distance. S1D.
    ///
    /// Part 22 ranks readability as the first risk and says the countermeasures
    /// are design-side and must be committed early: a tight palette, consistent
    /// modules, hard silhouette rules. S0A did the palette and the modules;
    /// this is the rest, and it is what S19 realizes a blueprint against.
    ///
    /// It is content, so a mod's tileset can make a settlement build in
    /// something the base game never had. What it may not do is make a
    /// building nobody can read: the rules are checked, not suggested.
    /// </summary>
    public sealed class TileSet
    {
        readonly RoleTile[] _roles;
        readonly Dictionary<ulong, bool> _carries = new Dictionary<ulong, bool>();
        readonly List<Symbol> _needsFooting = new List<Symbol>();
        readonly List<string> _problems;

        TileSet(RoleTile[] roles, List<string> problems) { _roles = roles; _problems = problems; }

        public int Count { get { return _roles.Length; } }
        public RoleTile this[int index] { get { return _roles[index]; } }
        public IReadOnlyList<RoleTile> Roles { get { return _roles; } }
        public IReadOnlyList<string> Problems { get { return _problems; } }

        public string Tell { get; private set; }

        /// <summary>Materials one building may wear. More than a few and nothing contrasts with anything.</summary>
        public int MaxMaterials { get; private set; }

        /// <summary>How far apart in value a roof and its walls must read.</summary>
        public int MinRoofWallContrast { get; private set; }

        /// <summary>Whether the lowest course should differ from the wall above it, when there is a choice.</summary>
        public bool BandTheBase { get; private set; }

        public RoleTile Find(Symbol role)
        {
            foreach (RoleTile t in _roles) if (t.Role == role) return t;
            return null;
        }

        /// <summary>Whether a class can bear weight. Thatch cannot: nothing rests on a roof.</summary>
        public bool Carries(Symbol materialClass)
        {
            bool c;
            return !_carries.TryGetValue(materialClass.Hash, out c) || c;
        }

        /// <summary>Classes that wash away without a hard course under them — earth on wet ground.</summary>
        public bool NeedsFooting(Symbol materialClass) { return _needsFooting.Contains(materialClass); }

        public static TileSet FromContent(ContentDatabase content, MaterialTable materials, string id = "base")
        {
            var problems = new List<string>();
            var roles = new List<RoleTile>();
            if (!content.Contains("tileset", id)) return new TileSet(roles.ToArray(), problems);

            JsonValue doc = content.Get("tileset", id);

            // Which classes exist at all is the materials' business, so a
            // tileset that names one nothing is made of is a content bug.
            var known = new List<Symbol>();
            foreach (BuildingMaterial m in materials.All) if (!known.Contains(m.Class)) known.Add(m.Class);

            JsonValue roleDocs = doc["roles"];
            for (int i = 0; i < roleDocs.Keys.Count; i++)
            {
                string name = roleDocs.Keys[i];
                JsonValue r = roleDocs[name];
                var classes = new List<Symbol>();
                JsonValue list = r["classes"];
                for (int k = 0; k < list.Count; k++)
                {
                    string cls = list[k].AsString("");
                    Symbol c = Symbol.For("class." + cls);
                    if (!known.Contains(c)) { problems.Add("tileset role '" + name + "' asks for class '" + cls + "', which no material has."); continue; }
                    classes.Add(c);
                }
                bool open = r["open"].AsBool(false);
                if (!open && classes.Count == 0) problems.Add("tileset role '" + name + "' has nothing that can build it.");

                roles.Add(new RoleTile
                {
                    Role = Symbol.For("role." + name),
                    Name = name,
                    Classes = classes,
                    Open = open,
                    Optional = r["optional"].AsBool(false),
                });
            }
            roles.Sort((a, b) => a.Role.CompareTo(b.Role));

            var set = new TileSet(roles.ToArray(), problems)
            {
                Tell = doc["tell"].AsString("").Trim(),
                MaxMaterials = doc["silhouette"]["maxMaterials"].AsInt32(3),
                MinRoofWallContrast = doc["silhouette"]["minRoofWallContrast"].AsInt32(0),
                BandTheBase = doc["silhouette"]["bandTheBase"].AsBool(false),
            };
            if (set.Tell.Length == 0) problems.Add("tileset '" + id + "' declares no tell.");

            JsonValue carries = doc["carries"];
            for (int i = 0; i < carries.Keys.Count; i++)
            {
                Symbol c = Symbol.For("class." + carries.Keys[i]);
                if (!known.Contains(c)) { problems.Add("tileset says class '" + carries.Keys[i] + "' carries or does not, and no material has that class."); continue; }
                set._carries[c.Hash] = carries[carries.Keys[i]].AsBool(true);
            }

            JsonValue footing = doc["needsFooting"];
            for (int i = 0; i < footing.Count; i++)
            {
                Symbol c = Symbol.For("class." + footing[i].AsString(""));
                if (!known.Contains(c)) { problems.Add("tileset wants a footing under class '" + footing[i].AsString("") + "', which no material has."); continue; }
                set._needsFooting.Add(c);
            }

            return set;
        }

        /// <summary>
        /// Whether this tileset can answer for every role a grammar writes.
        /// Run when both are loaded: a role nobody can build is a village of
        /// holes, and the content author should hear about it at load.
        /// </summary>
        public IReadOnlyList<string> Answers(Grammar grammar)
        {
            var gaps = new List<string>();
            foreach (Symbol role in grammar.Roles)
                if (Find(role) == null)
                    gaps.Add("grammar '" + grammar.Name + "' writes " + role + ", which the tileset has no materials for.");
            return gaps;
        }

        /// <summary>Materials of a class, in the table's order.</summary>
        public List<int> MaterialsOf(MaterialTable materials, Symbol materialClass)
        {
            var found = new List<int>();
            for (int m = 0; m < materials.Count; m++) if (materials[m].Class == materialClass) found.Add(m);
            return found;
        }

        /// <summary>How far apart two materials read. The palette's value scale, 0-100.</summary>
        public static int Contrast(Palette palette, MaterialTable materials, int a, int b)
        {
            Material ma = palette.Find(materials[a].Voxel), mb = palette.Find(materials[b].Voxel);
            if (ma == null || mb == null) return 0;
            int d = ma.Value - mb.Value;
            return d < 0 ? -d : d;
        }
    }
}
