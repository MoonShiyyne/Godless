using System.Collections.Generic;
using Godless.Sim.Core;
using Godless.Sim.Economy;
using Godless.Sim.Voxels;
using Godless.Sim.World;

namespace Godless.Sim.Build
{
    /// <summary>
    /// A blueprint made of real materials: what each cell is, what it costs,
    /// and what the settlement had to settle for. S19.
    /// </summary>
    public sealed class Structure
    {
        readonly ushort[] _voxels;
        readonly short[] _material;
        readonly Blueprint _plan;
        readonly List<string> _compromises = new List<string>();
        readonly List<Symbol> _roleOrder = new List<Symbol>();
        readonly List<int> _roleMaterial = new List<int>();

        internal Structure(Blueprint plan, MaterialTable materials)
        {
            _plan = plan;
            _voxels = new ushort[plan.Width * plan.Height * plan.Depth];
            _material = new short[_voxels.Length];
            for (int i = 0; i < _material.Length; i++) _material[i] = -1;
            Cost = new long[materials.Count];
        }

        public Blueprint Plan { get { return _plan; } }

        /// <summary>Voxels of each material the finished building takes.</summary>
        public long[] Cost { get; private set; }

        /// <summary>What it could not do as the rules would have it — out of stock, or too alike.</summary>
        public IReadOnlyList<string> Compromises { get { return _compromises; } }

        /// <summary>Roles that had no material at all. A building with one of these cannot be built.</summary>
        public IReadOnlyList<Symbol> Missing { get; internal set; }

        public ushort At(int x, int y, int z)
        {
            return _plan.InBounds(x, y, z) ? _voxels[(y * _plan.Depth + z) * _plan.Width + x] : VoxelTypes.AirId;
        }

        internal void Set(int x, int y, int z, ushort type, int material)
        {
            int cell = (y * _plan.Depth + z) * _plan.Width + x;
            _voxels[cell] = type;
            _material[cell] = (short)material;
        }

        /// <summary>The material a cell was realized in, by cell index; -1 for air.</summary>
        public int MaterialAtCell(int cell) { return _material[cell]; }

        internal void Note(string compromise) { _compromises.Add(compromise); }

        internal void Chose(Symbol role, int material) { _roleOrder.Add(role); _roleMaterial.Add(material); }

        /// <summary>The material this building uses for a role, or -1.</summary>
        public int MaterialFor(Symbol role)
        {
            for (int i = 0; i < _roleOrder.Count; i++) if (_roleOrder[i] == role) return _roleMaterial[i];
            return -1;
        }

        /// <summary>How many distinct materials it wears.</summary>
        public int MaterialCount
        {
            get { int n = 0; for (int m = 0; m < Cost.Length; m++) if (Cost[m] > 0) n++; return n; }
        }

        public long TotalVoxels
        {
            get { long n = 0; for (int m = 0; m < Cost.Length; m++) n += Cost[m]; return n; }
        }

        public ulong Digest()
        {
            var d = new Digest();
            for (int i = 0; i < _voxels.Length; i++) d.AddShort(_voxels[i]);
            for (int m = 0; m < Cost.Length; m++) d.Add(Cost[m]);
            return d.Value;
        }
    }

    /// <summary>
    /// Turns a blueprint into voxels of the materials a settlement actually
    /// holds. S19.
    ///
    /// Part 05: "WFC over a tileset constrained to materials the settlement
    /// physically holds in stock. Same grammar, different stock, different
    /// building." WFC answers what it looks like and never whether it exists —
    /// the blueprint already settled that.
    ///
    /// Two passes. First the building's own small palette: each role takes the
    /// first class the stock can supply, reusing a material already chosen
    /// before adding a new one, so the tileset's cap holds and a house in the
    /// highlands is slate and granite while the same house in the woods is oak
    /// and thatch. The roof is then held to a real contrast against the walls,
    /// because a roof that reads as the wall is not a roof at camera distance.
    ///
    /// Then the cells: each starts with every material its role allows and is
    /// collapsed in order of fewest choices, weighted toward the role's own
    /// material and toward its neighbours' — with the tileset's adjacency
    /// rules propagated between them. Nothing rests on thatch, and earth walls
    /// get a course of something harder under them.
    /// </summary>
    public static class Realizer
    {
        /// <param name="catchment">
        /// What the land round the settlement offers, when known. Used only
        /// where the stock has nothing at all: a plan made in an empty yard
        /// should still name something the place can actually supply.
        /// </param>
        public static Structure Realize(Blueprint plan, TileSet tiles, MaterialTable materials,
                                        MaterialStock stock, Palette palette, VoxelTypes types, RngStream rng,
                                        Catchment catchment = null)
        {
            var structure = new Structure(plan, materials);
            var missing = new List<Symbol>();

            // Roles present, most cells first: the biggest surface sets the
            // building's character and everything else answers to it.
            var roles = new List<Symbol>();
            var counts = new List<int>();
            foreach (Symbol role in plan.Roles)
            {
                if (role.IsNone) continue;
                RoleTile tile = tiles.Find(role);
                if (tile == null || tile.Open) continue;
                roles.Add(role);
                counts.Add(plan.Count(role));
            }
            SortByCount(roles, counts);

            var chosen = new List<int>();       // material indices already used
            var roleMaterial = new Dictionary<ulong, int>();
            Symbol wall = Symbol.For("role.wall");
            int wallMaterial = -1;

            foreach (Symbol role in roles)
            {
                RoleTile tile = tiles.Find(role);
                int need = plan.Count(role);
                int pick = Choose(tile, need, tiles, materials, stock, palette, chosen, role, wallMaterial, structure, catchment);
                if (pick < 0) { missing.Add(role); continue; }
                roleMaterial[role.Hash] = pick;
                structure.Chose(role, pick);
                if (!chosen.Contains(pick)) chosen.Add(pick);
                if (role == wall) wallMaterial = pick;
            }

            structure.Missing = missing;
            Weave(plan, tiles, materials, stock, types, rng, structure, roleMaterial);
            return structure;
        }

        static void SortByCount(List<Symbol> roles, List<int> counts)
        {
            for (int i = 1; i < roles.Count; i++)
                for (int j = i; j > 0; j--)
                {
                    bool swap = counts[j] > counts[j - 1]
                             || (counts[j] == counts[j - 1] && roles[j].CompareTo(roles[j - 1]) < 0);
                    if (!swap) break;
                    Symbol r = roles[j]; roles[j] = roles[j - 1]; roles[j - 1] = r;
                    int c = counts[j]; counts[j] = counts[j - 1]; counts[j - 1] = c;
                }
        }

        /// <summary>
        /// One role's material: the first class the settlement can supply,
        /// preferring what it already builds with, what it holds enough of,
        /// and — for a roof — something that reads against the walls.
        /// </summary>
        /// <remarks>
        /// <see cref="UsableYield"/> is the rate below which a material is
        /// treated as absent rather than scarce: a tenth of a voxel a tick is
        /// a day's walk for one stone.
        /// </remarks>
        /// <summary>Voxels a tick of labour must bring in for a material to count as available at all.</summary>
        public const double UsableYield = 0.02;

        static int Choose(RoleTile tile, int need, TileSet tiles, MaterialTable materials, MaterialStock stock,
                          Palette palette, List<int> chosen, Symbol role, int wallMaterial, Structure structure,
                          Catchment catchment)
        {
            // Enough of it first, across every class the role allows; only
            // then whatever there is any of. A handful of stone is a footing,
            // not a wall, and a role that took it would leave the building
            // unfinishable while the mud it could have used sat in the yard.
            int pick = Pick(tile, need, tiles, materials, stock, palette, chosen, role, wallMaterial, true);
            if (pick < 0) pick = Pick(tile, need, tiles, materials, stock, palette, chosen, role, wallMaterial, false);
            if (pick >= 0)
            {
                if (role == Symbol.For("role.roof") && wallMaterial >= 0
                    && TileSet.Contrast(palette, materials, pick, wallMaterial) < tiles.MinRoofWallContrast)
                    structure.Note("the roof reads too close to the walls: " + materials[pick].Name
                                   + " on " + materials[wallMaterial].Name + "; nothing in stock reads better");
                return pick;
            }

            // Nothing in hand at all: plan in whatever the land gives most
            // readily, not in the first material it gives at all.
            //
            // A trickle counts as "offered", and a roof planned in a stone
            // that arrives at a tenth of a voxel a day is a roof that never
            // gets built — the whole settlement walks to the highland and
            // back while the reed it could have used grows beside the yard.
            // Abundance decides; the tileset's order only breaks ties, which
            // is the same rule the stocked case already follows.
            int last = -1;
            double best = -1.0;
            foreach (Symbol cls in tile.Classes)
                foreach (int m in tiles.MaterialsOf(materials, cls))
                {
                    if (last < 0) last = m;
                    if (catchment == null || !catchment.Offers(m)) continue;
                    double yield = catchment.YieldPerLabourTick(m);

                    // On an island with real deposits, a quick material that
                    // runs out a fifth of the way up the wall is worth a fifth
                    // of its rate (S2F): seventy voxels of sand on the beach
                    // do not make a sand house.
                    if (catchment.HasDeposits && need > 0 && catchment.Sources(m) < need)
                        yield *= (double)catchment.Sources(m) / need;

                    // A few source columns at the far edge of the haul range
                    // round to nothing per tick. Counting those as available
                    // is how a house ends up needing four voxels of a stone
                    // that will never arrive, and standing unfinished for
                    // four hundred days with its walls up.
                    if (yield < UsableYield) continue;
                    if (yield > best) { best = yield; last = m; }
                }
            if (last >= 0)
            {
                structure.Note(role + " has nothing in stock; planned in " + materials[last].Name);
                return last;
            }
            return -1;
        }

        /// <summary>
        /// What the settlement has most of, across every class the role
        /// allows. The tileset's order is a preference and breaks ties; it is
        /// not a ranking that outvotes the yard, or every settlement on the
        /// island builds in whatever material happens to be listed first and
        /// the stock stops meaning anything (measured with S1G: biomes
        /// separated at 55% until this changed).
        /// </summary>
        static int Pick(RoleTile tile, int need, TileSet tiles, MaterialTable materials, MaterialStock stock,
                        Palette palette, List<int> chosen, Symbol role, int wallMaterial, bool wantEnough)
        {
            bool isRoof = role == Symbol.For("role.roof");
            int best = -1;
            long bestScore = long.MinValue;

            for (int rank = 0; rank < tile.Classes.Count; rank++)
            {
                foreach (int m in tiles.MaterialsOf(materials, tile.Classes[rank]))
                {
                    long held = stock.Of(m);
                    if (wantEnough ? held < need : held <= 0) continue;

                    long score = held * 100;
                    score += (tile.Classes.Count - rank) * 20;                   // the tileset's preference, gently
                    if (chosen.Contains(m)) score += 500000;                     // keep the palette tight
                    else if (chosen.Count >= tiles.MaxMaterials) score -= 1000000; // a fourth material is a last resort
                    if (isRoof && wallMaterial >= 0)
                        score += TileSet.Contrast(palette, materials, m, wallMaterial) >= tiles.MinRoofWallContrast ? 800000 : 0;
                    score = score * 1000 - m;                                    // ties by table order
                    if (score > bestScore) { bestScore = score; best = m; }
                }
            }
            return best;
        }

        /// <summary>
        /// The cells. Domains start as every material the role allows and the
        /// settlement holds; the tileset's rules cut them down, and what is
        /// left collapses toward the role's own material and its neighbours'.
        /// </summary>
        static void Weave(Blueprint plan, TileSet tiles, MaterialTable materials, MaterialStock stock, VoxelTypes types,
                          RngStream rng, Structure structure, Dictionary<ulong, int> roleMaterial)
        {
            int w = plan.Width, h = plan.Height, d = plan.Depth;
            var domains = new List<int>[w * h * d];

            for (int y = 0; y < h; y++)
                for (int z = 0; z < d; z++)
                    for (int x = 0; x < w; x++)
                    {
                        Symbol role = plan.At(x, y, z);
                        if (role.IsNone) continue;
                        RoleTile tile = tiles.Find(role);
                        if (tile == null || tile.Open) continue;
                        int preferred;
                        if (!roleMaterial.TryGetValue(role.Hash, out preferred)) continue;

                        var domain = new List<int>();
                        foreach (Symbol cls in tile.Classes)
                            foreach (int m in tiles.MaterialsOf(materials, cls))
                                if ((stock.Of(m) > 0 || m == preferred) && !domain.Contains(m)) domain.Add(m);
                        if (domain.Count == 0) domain.Add(preferred);
                        domains[(y * d + z) * w + x] = domain;
                    }

            // Nothing rests on thatch: a cell carrying something above it
            // cannot be a class that does not carry.
            for (int y = 0; y < h - 1; y++)
                for (int z = 0; z < d; z++)
                    for (int x = 0; x < w; x++)
                    {
                        List<int> domain = domains[(y * d + z) * w + x];
                        if (domain == null || domains[((y + 1) * d + z) * w + x] == null) continue;
                        var kept = new List<int>();
                        foreach (int m in domain) if (tiles.Carries(materials[m].Class)) kept.Add(m);
                        if (kept.Count > 0) domains[(y * d + z) * w + x] = kept;
                    }

            // Earth wants a harder course under it where it meets the ground.
            for (int z = 0; z < d; z++)
                for (int x = 0; x < w; x++)
                {
                    int bottom = -1;
                    for (int y = 0; y < h && bottom < 0; y++) if (domains[(y * d + z) * w + x] != null) bottom = y;
                    if (bottom < 0) continue;
                    List<int> domain = domains[(bottom * d + z) * w + x];
                    var hard = new List<int>();
                    foreach (int m in domain) if (!tiles.NeedsFooting(materials[m].Class)) hard.Add(m);
                    if (hard.Count > 0 && hard.Count < domain.Count) domains[(bottom * d + z) * w + x] = hard;
                }

            // Collapse: fewest choices first, ties in grid order, so the run
            // is the same on every machine. Weights favour the role's own
            // material and whatever the neighbours already are.
            var order = new List<int>();
            for (int i = 0; i < domains.Length; i++) if (domains[i] != null) order.Add(i);
            order.Sort((a, b) =>
            {
                int c = domains[a].Count.CompareTo(domains[b].Count);
                return c != 0 ? c : a.CompareTo(b);
            });

            foreach (int i in order)
            {
                int x = i % w, z = (i / w) % d, y = i / (w * d);
                Symbol role = plan.At(x, y, z);
                int preferred = roleMaterial[role.Hash];
                List<int> domain = domains[i];

                int chosen = domain[0];
                if (domain.Count > 1)
                {
                    long total = 0;
                    var weights = new long[domain.Count];
                    for (int k = 0; k < domain.Count; k++)
                    {
                        int m = domain[k];
                        long weight = 1 + stock.Of(m);
                        if (m == preferred) weight *= 150;
                        // Runs, not speckle: a material already next door is
                        // far likelier than one that would sit alone.
                        weight *= 1 + 3 * Neighbours(structure, types, materials, m, x, y, z);
                        weights[k] = weight;
                        total += weight;
                    }
                    long roll = (long)rng.NextInt((int)System.Math.Min(total, int.MaxValue - 1));
                    for (int k = 0; k < domain.Count; k++) { roll -= weights[k]; if (roll < 0) { chosen = domain[k]; break; } }
                }

                structure.Set(x, y, z, types.IdOf(materials[chosen].Voxel), chosen);
                structure.Cost[chosen]++;
            }
        }

        /// <summary>How many already-decided neighbours are this material: a run of one thing reads as one thing.</summary>
        static int Neighbours(Structure s, VoxelTypes types, MaterialTable materials, int material, int x, int y, int z)
        {
            ushort id = types.IdOf(materials[material].Voxel);
            int n = 0;
            if (s.At(x - 1, y, z) == id) n++;
            if (s.At(x + 1, y, z) == id) n++;
            if (s.At(x, y, z - 1) == id) n++;
            if (s.At(x, y, z + 1) == id) n++;
            if (s.At(x, y - 1, z) == id) n++;
            if (s.At(x, y + 1, z) == id) n++;
            return n;
        }
    }
}
