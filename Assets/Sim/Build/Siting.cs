using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Culture;
using Godless.Sim.Settlements;
using Godless.Sim.World;

namespace Godless.Sim.Build
{
    /// <summary>Where a building went, and how well the ground suited it.</summary>
    public sealed class Site
    {
        public int ParcelX { get; internal set; }
        public int ParcelZ { get; internal set; }

        /// <summary>Parcels across and deep that the building takes.</summary>
        public int ParcelsWide { get; internal set; }
        public int ParcelsDeep { get; internal set; }

        public double Score { get; internal set; }

        /// <summary>The site.chosen record. Caused by the intent, so the chain runs back to the nights in the open.</summary>
        public RecordId Record { get; internal set; }

        /// <summary>Ground level where the building sits: the highest ground under its footprint.</summary>
        public int Ground { get; internal set; }
    }

    /// <summary>One kind of building's idea of good ground, as content declares it.</summary>
    public sealed class SitingRule
    {
        public Symbol Id { get; internal set; }
        public string Name { get; internal set; }
        public string Tell { get; internal set; }

        /// <summary>The intent kind it sites, by name.</summary>
        public string For { get; internal set; }

        /// <summary>Above zero, the parcel is allowed at all. Land, dry enough, not somebody's already.</summary>
        internal Expr Allow;

        /// <summary>How good it is. Higher wins; the genome is in the expression.</summary>
        internal Expr Score;

        /// <summary>How far from the hearth the settlement will look, in parcels.</summary>
        public int SearchRadius { get; internal set; }

        /// <summary>
        /// How a building would rather sit on the ground it gets, and what a
        /// crowded family would rather do (S2O, S2P): weights over the genome,
        /// by name. Missing ones weigh nothing.
        /// </summary>
        internal readonly Dictionary<string, Expr> Prefer = new Dictionary<string, Expr>();

        public static readonly string[] Preferences =
            { "doorToFire", "doorToSun", "doorDownhill", "backIntoSlope", "nearKin", "nearWork", "wing", "storey", "apart" };

        /// <summary>A preference's weight for a genome, or zero if content gives none.</summary>
        public double Weight(string name, Genome genome)
        {
            Expr e;
            if (!Prefer.TryGetValue(name, out e)) return 0.0;
            return e.Eval(new GenomeScope(genome));
        }

        sealed class GenomeScope : IExprScope
        {
            readonly Genome _g;
            public GenomeScope(Genome g) { _g = g; }
            public double Resolve(string name)
            {
                if (_g == null || !name.StartsWith("gene.", System.StringComparison.Ordinal)) return 0.0;
                double v = _g[Symbol.For(name)];
                return double.IsNaN(v) ? 0.0 : v;
            }
        }
    }

    /// <summary>
    /// Site scoring. S15.
    ///
    /// Part 05, stage one: influence maps over the coarse grid — slope,
    /// drainage and flood history, sun, wind, distance to water and to what
    /// the building serves — "weighted by the genome, a defensive culture
    /// weights the ridge, a mercantile one weights the crossing".
    ///
    /// The weighing is content, in the same expression language the grammar
    /// uses, so which ground a culture calls good is data and not a branch in
    /// C#. The scorer reads the fields S1F computed, the parcel grid, the
    /// settlement's own claims and the genome, and picks the best parcel that
    /// the building fits on.
    /// </summary>
    public sealed class SitingTable
    {
        readonly SitingRule[] _rules;
        readonly List<string> _problems;

        SitingTable(SitingRule[] rules, List<string> problems) { _rules = rules; _problems = problems; }

        public int Count { get { return _rules.Length; } }
        public SitingRule this[int index] { get { return _rules[index]; } }
        public IReadOnlyList<SitingRule> All { get { return _rules; } }
        public IReadOnlyList<string> Problems { get { return _problems; } }

        public SitingRule For(string intentKind)
        {
            foreach (SitingRule r in _rules) if (r.For == intentKind) return r;
            return null;
        }

        /// <summary>The facts about a parcel content expressions may read.</summary>
        public static readonly string[] Known =
        {
            "field.sun", "field.snow", "field.damp", "field.exposure", "field.flood",
            "parcel.slope", "parcel.height", "parcel.elevation", "parcel.water", "parcel.hearth", "parcel.land", "parcel.wet",
            "intent.budget",
        };

        /// <summary>Checks an expression reads only parcel facts and genes that exist; the fault in words, or null.</summary>
        public static string Check(Expr e, GeneTable genes) { return CheckNames(e, genes); }

        public static SitingTable FromContent(ContentDatabase content, GeneTable genes, IntentKindTable intents = null)
        {
            var problems = new List<string>();
            var loaded = new List<SitingRule>();

            foreach (string id in content.Ids("siting"))
            {
                JsonValue doc = content.Get("siting", id);
                string tell = doc["tell"].AsString("").Trim();
                string builds = doc["for"].AsString("");
                string fault = tell.Length == 0 ? "declares no tell" : null;
                if (fault == null && intents != null)
                {
                    bool known = false;
                    for (int i = 0; i < intents.Count; i++) if (intents[i].Name == builds) known = true;
                    if (!known) fault = "sites '" + builds + "', which no intent kind raises";
                }

                Expr allow = null, score = null;
                if (fault == null)
                {
                    try { allow = Expr.Parse(doc["allow"].AsString("1")); }
                    catch (ExprException e) { fault = "has a broken 'allow': " + e.Message; }
                }
                if (fault == null)
                {
                    try { score = Expr.Parse(doc["score"].AsString("0")); }
                    catch (ExprException e) { fault = "has a broken 'score': " + e.Message; }
                }
                if (fault == null) fault = CheckNames(allow, genes) ?? CheckNames(score, genes);

                var prefer = new Dictionary<string, Expr>();
                JsonValue preferDoc = doc["prefer"];
                foreach (string key in preferDoc.Keys)
                {
                    if (fault != null) break;
                    if (System.Array.IndexOf(SitingRule.Preferences, key) < 0)
                    { fault = "prefers '" + key + "', which is not something a building can prefer"; break; }
                    try
                    {
                        Expr e = Expr.Parse(preferDoc[key].AsString("0"));
                        var names = new List<string>();
                        e.Names(names);
                        foreach (string n in names)
                            if (!n.StartsWith("gene.", System.StringComparison.Ordinal) || genes.IndexOf(Symbol.For(n)) < 0)
                            { fault = "weighs '" + key + "' by '" + n + "', which is not a gene"; break; }
                        prefer[key] = e;
                    }
                    catch (ExprException e) { fault = "has a broken preference '" + key + "': " + e.Message; }
                }

                if (fault != null) { problems.Add("siting '" + id + "' " + fault + "."); continue; }
                var rule = new SitingRule
                {
                    Id = Symbol.For("siting." + id),
                    Name = id,
                    Tell = tell,
                    For = builds,
                    Allow = allow,
                    Score = score,
                    SearchRadius = doc["searchRadiusParcels"].AsInt32(12),
                };
                foreach (string key in SitingRule.Preferences)
                {
                    Expr e;
                    if (prefer.TryGetValue(key, out e)) rule.Prefer[key] = e;
                }
                loaded.Add(rule);
            }

            loaded.Sort((a, b) => a.Id.CompareTo(b.Id));
            return new SitingTable(loaded.ToArray(), problems);
        }

        static string CheckNames(Expr e, GeneTable genes)
        {
            var names = new List<string>();
            e.Names(names);
            foreach (string n in names)
            {
                if (System.Array.IndexOf(Known, n) >= 0) continue;
                if (n.StartsWith("gene.", System.StringComparison.Ordinal))
                {
                    if (genes.IndexOf(Symbol.For(n)) < 0) return "reads '" + n + "', which no gene declares";
                    continue;
                }
                return "reads '" + n + "', which is not a field, a parcel fact or a gene";
            }
            return null;
        }
    }

    public static class SiteScorer
    {
        public static readonly Symbol ChosenKind = Symbol.For("site.chosen");

        /// <summary>
        /// The best parcel the building fits on, or null when the settlement
        /// has nowhere left it will build. Chooses, records and claims.
        /// </summary>
        public static Site Choose(Settlement settlement, BuildIntent intent, Blueprint plan, SitingRule rule,
                                  ParcelGrid grid, ConstraintFields fields, Genome genome,
                                  long tick, Annalist annals)
        {
            List<Site> sites = Candidates(settlement, intent, plan, rule, grid, fields, genome, 1);
            if (sites.Count == 0) return null;
            Commit(settlement, intent, sites[0], grid, tick, annals);
            return sites[0];
        }

        /// <summary>
        /// The best few parcels a footprint fits on, best first, without
        /// claiming anything (S2O): the dwelling program weighs each against
        /// how the house would sit there before it commits to one.
        /// </summary>
        public static List<Site> Candidates(Settlement settlement, BuildIntent intent, Blueprint plan, SitingRule rule,
                                            ParcelGrid grid, ConstraintFields fields, Genome genome, int keep,
                                            bool[] reachable = null)
        {
            int wide = Parcels(plan.Width - 2 * Grammar.Margin);
            int deep = Parcels(plan.Depth - 2 * Grammar.Margin);

            var scope = new ParcelScope { Fields = fields, Grid = grid, Genome = genome, Budget = intent.BudgetVoxels };
            int hx = intent.ParcelX, hz = intent.ParcelZ;
            scope.HearthX = settlement.HearthParcelX;
            scope.HearthZ = settlement.HearthParcelZ;

            // Ground you can still walk to from the fire without crossing
            // somebody's house. Flooded once, rather than pathed per candidate.
            if (reachable == null) reachable = Reachable(settlement, grid);

            // S2P: a culture that would sooner build out than up leaves room to:
            // two parcels of daylight round a new house instead of one, which is
            // the yard its wings will go into. A culture that builds up packs tight.
            int gap = rule.Weight("wing", genome) > rule.Weight("storey", genome) + 0.1 ? 2 : 1;

            var best = new List<Site>();
            for (int pz = hz - rule.SearchRadius; pz <= hz + rule.SearchRadius; pz++)
                for (int px = hx - rule.SearchRadius; px <= hx + rule.SearchRadius; px++)
                {
                    if (!Fits(settlement, grid, px, pz, wide, deep, reachable, gap)) continue;

                    scope.X = px; scope.Z = pz;
                    if (rule.Allow.Eval(scope) <= 0.0) continue;

                    // The score is the worst parcel of the footprint, not the
                    // best: a house is only as well sited as its worst corner.
                    double score = double.MaxValue;
                    for (int dz = 0; dz < deep; dz++)
                        for (int dx = 0; dx < wide; dx++)
                        {
                            scope.X = px + dx; scope.Z = pz + dz;
                            double here = rule.Score.Eval(scope);
                            if (here < score) score = here;
                        }

                    // Keep the best few, strictly better first, so ties keep scan order.
                    if (best.Count == keep && score <= best[best.Count - 1].Score) continue;
                    var site = new Site { ParcelX = px, ParcelZ = pz, ParcelsWide = wide, ParcelsDeep = deep, Score = score };
                    int at = best.Count;
                    while (at > 0 && best[at - 1].Score < score) at--;
                    best.Insert(at, site);
                    if (best.Count > keep) best.RemoveAt(best.Count - 1);
                }
            return best;
        }

        /// <summary>Settles a site's floor level, records the choice and claims the ground.</summary>
        public static void Commit(Settlement settlement, BuildIntent intent, Site site, ParcelGrid grid, long tick, Annalist annals)
        {
            int wide = site.ParcelsWide, deep = site.ParcelsDeep;
            int ground = 0;
            for (int dz = 0; dz < deep; dz++)
                for (int dx = 0; dx < wide; dx++)
                {
                    int g = grid.MaxGround(site.ParcelX + dx, site.ParcelZ + dz);
                    if (g > ground) ground = g;
                }
            site.Ground = ground + 1;

            var place = new Int3(site.ParcelX * ParcelGrid.Size + wide * ParcelGrid.Size / 2, site.Ground,
                                 site.ParcelZ * ParcelGrid.Size + deep * ParcelGrid.Size / 2);
            site.Record = annals.Write(tick, ChosenKind, settlement.Id, place, intent.Record,
                                       (long)(site.Score * 1000.0), wide * deep, new[] { intent.Kind.Id });

            for (int dz = 0; dz < deep; dz++)
                for (int dx = 0; dx < wide; dx++) settlement.ClaimParcel(site.ParcelX + dx, site.ParcelZ + dz, site.Record);
        }

        /// <summary>One parcel's siting score for a rule, as the scorer computes it (S2P: wings are scored the same way).</summary>
        public static double ScoreParcel(Settlement settlement, SitingRule rule, ParcelGrid grid, ConstraintFields fields,
                                         Genome genome, int px, int pz)
        {
            var scope = new ParcelScope { Fields = fields, Grid = grid, Genome = genome, X = px, Z = pz,
                                          HearthX = settlement.HearthParcelX, HearthZ = settlement.HearthParcelZ };
            return rule.Score.Eval(scope);
        }

        /// <summary>A parcel's facts as an expression scope: fields, the grid, the genome (S2I: what crops are weighed on).</summary>
        public static IExprScope Facts(Settlement settlement, ParcelGrid grid, ConstraintFields fields, Genome genome, int px, int pz)
        {
            return new ParcelScope { Fields = fields, Grid = grid, Genome = genome, X = px, Z = pz,
                                     HearthX = settlement.HearthParcelX, HearthZ = settlement.HearthParcelZ };
        }

        /// <summary>Walkable-from-the-fire parcels, once per planning pass (S1B).</summary>
        public static bool[] ReachableFromFire(Settlement settlement, ParcelGrid grid) { return Reachable(settlement, grid); }

        static int Parcels(int voxels) { return (voxels + ParcelGrid.Size - 1) / ParcelGrid.Size; }

        /// <summary>
        /// S1B: a building needs ground of its own, a parcel of daylight
        /// between it and its neighbours, and a way to the fire that does not
        /// go through somebody else's house.
        /// </summary>
        static bool Fits(Settlement settlement, ParcelGrid grid, int px, int pz, int wide, int deep, bool[] reachable, int gap = 1)
        {
            for (int dz = 0; dz < deep; dz++)
                for (int dx = 0; dx < wide; dx++)
                {
                    int x = px + dx, z = pz + dz;
                    // A parcel with a puddle in it is still ground you can
                    // build on; a parcel that is a quarter water is not.
                    if (!ParcelGrid.InBounds(x, z) || !grid.IsLand(x, z) || grid.WetColumns(x, z) > 4) return false;
                }

            // The footprint and the ring round it: unclaimed, so houses do not
            // grow into each other and the gaps between them stay walkable.
            for (int dz = -gap; dz < deep + gap; dz++)
                for (int dx = -gap; dx < wide + gap; dx++)
                    if (settlement.IsClaimed(px + dx, pz + dz)) return false;

            for (int dz = -1; dz <= deep; dz++)
                for (int dx = -1; dx <= wide; dx++)
                {
                    int x = px + dx, z = pz + dz;
                    bool inside = dx >= 0 && dz >= 0 && dx < wide && dz < deep;
                    if (inside || !ParcelGrid.InBounds(x, z)) continue;
                    if (reachable[z * ParcelGrid.Width + x]) return true;   // a way in from the settlement
                }
            return false;
        }

        /// <summary>
        /// Every parcel a person can walk to from the hearth without crossing
        /// a claim. One flood from the fire, rather than a path per candidate.
        /// </summary>
        static bool[] Reachable(Settlement settlement, ParcelGrid grid)
        {
            var seen = new bool[ParcelGrid.Width * ParcelGrid.Depth];
            var queue = new Queue<int>();
            int start = settlement.HearthParcelZ * ParcelGrid.Width + settlement.HearthParcelX;
            seen[start] = true;
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                int at = queue.Dequeue();
                int ax = at % ParcelGrid.Width, az = at / ParcelGrid.Width;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int nx = ax + dx, nz = az + dz;
                        if (!ParcelGrid.InBounds(nx, nz)) continue;
                        int next = nz * ParcelGrid.Width + nx;
                        if (seen[next] || !grid.IsLand(nx, nz) || settlement.IsClaimed(nx, nz)) continue;

                        double climb = grid.Height[nx, nz] - grid.Height[ax, az];
                        if (climb < 0.0) climb = -climb;
                        if (climb >= ParcelPath.Impassable || grid.Slope[nx, nz] >= ParcelPath.Impassable) continue;

                        seen[next] = true;
                        queue.Enqueue(next);
                    }
            }
            return seen;
        }

        sealed class ParcelScope : IExprScope
        {
            public ConstraintFields Fields;
            public ParcelGrid Grid;
            public Genome Genome;
            public int X, Z, HearthX, HearthZ, Budget;

            public double Resolve(string name)
            {
                switch (name)
                {
                    case "field.sun": return Fields.Sun[X, Z];
                    case "field.snow": return Fields.SnowLoad[X, Z];
                    case "field.damp": return Fields.Damp[X, Z];
                    case "field.exposure": return Fields.Exposure[X, Z];
                    case "field.flood": return Fields.FloodRisk[X, Z];
                    case "parcel.slope": return Grid.Slope[X, Z];
                    case "parcel.height": return Grid.Height[X, Z];
                    case "parcel.elevation": return Grid.Height[X, Z] - (Fields != null ? Fields.SeaLevel : World.IslandMap.DefaultSeaLevel);
                    case "parcel.water": return Grid.WaterDistance[X, Z];
                    case "parcel.land": return Grid.IsLand(X, Z) ? 1.0 : 0.0;
                    case "parcel.wet": return Grid.WetColumns(X, Z);
                    case "intent.budget": return Budget;
                    case "parcel.hearth":
                    {
                        double dx = X - HearthX, dz = Z - HearthZ;
                        return SimMath.Sqrt(dx * dx + dz * dz);
                    }
                }
                if (name.StartsWith("gene.", System.StringComparison.Ordinal))
                {
                    if (Genome == null) return 0.5;
                    double v = Genome[Symbol.For(name)];
                    return double.IsNaN(v) ? 0.0 : v;
                }
                return 0.0;
            }
        }
    }
}
