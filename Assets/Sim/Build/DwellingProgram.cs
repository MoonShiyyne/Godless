using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Core;
using Godless.Sim.Culture;
using Godless.Sim.Settlements;
using Godless.Sim.World;

namespace Godless.Sim.Build
{
    /// <summary>
    /// What a home has to be, before anyone decides what it looks like. S2O.
    ///
    /// The grammar knows how to build a house and the siting rule knows good
    /// ground; neither knew who the house was for. A program is the brief: the
    /// families it must hold and the room they will grow into, how many storeys
    /// the culture allows, who they are kin to. The search then tries every way
    /// round on the best few sites and keeps the one that answers the brief
    /// best — door to the fire or to the sun, stepping out downhill, backed
    /// into the slope, next to the family they came from — and writes down why.
    /// </summary>
    public sealed class DwellingProgram
    {
        /// <summary>The families it is for, largest first.</summary>
        public readonly List<Household> Families = new List<Household>();

        /// <summary>A crowded family with a home, whose room this is for (S2P); null when it is for the roofless.</summary>
        public Household Crowded { get; private set; }

        /// <summary>Beds it must hold, the families' own and the room they will grow into.</summary>
        public int Beds { get; private set; }

        /// <summary>Where the family they split from lives, as a parcel; -1 when nowhere.</summary>
        public int KinX { get; private set; } = -1;
        public int KinZ { get; private set; } = -1;

        /// <summary>The grammar lets the program decides; null to let the genome decide everything.</summary>
        public IReadOnlyDictionary<string, double> Overrides { get; private set; }

        /// <summary>
        /// The brief for the next home. For the roofless first — as many
        /// families under one roof as the culture would share — and otherwise
        /// for the most crowded family nearest where the pressure was felt.
        /// Null when no family is waiting, which is a settlement without
        /// families: the genome alone then sizes the house, as before.
        /// </summary>
        public static DwellingProgram For(Settlement s, BuildIntent intent)
        {
            if (s.Households.Count == 0) return null;
            Households.Settle(s);
            var p = new DwellingProgram();
            double communal = Gene(s.Genome, "gene.communal_ratio");

            var roofless = new List<Household>();
            foreach (Household h in s.Households) if (!h.Housed) roofless.Add(h);
            roofless.Sort((a, b) => b.Size != a.Size ? b.Size.CompareTo(a.Size) : a.Number.CompareTo(b.Number));

            int beds = 0;
            if (roofless.Count > 0)
            {
                // A communal culture puts several families under one long roof.
                int share = (int)SimMath.Round(SimMath.Lerp(6.0, 28.0, communal));
                foreach (Household h in roofless)
                {
                    if (p.Families.Count > 0 && (communal < 0.45 || beds + h.Size > share)) continue;
                    p.Families.Add(h);
                    beds += h.Size;
                }
            }
            else
            {
                Household worst = null;
                int worstOver = 0;
                double worstDistance = double.MaxValue;
                foreach (Household h in s.Households)
                {
                    if (!h.Crowded) continue;
                    int over = h.Size - h.Beds;
                    double d = Distance(h.Homes[0].Site.ParcelX, h.Homes[0].Site.ParcelZ, intent.ParcelX, intent.ParcelZ);
                    if (over > worstOver || (over == worstOver && d < worstDistance)) { worst = h; worstOver = over; worstDistance = d; }
                }
                if (worst == null) return null;
                p.Crowded = worst;
                p.Families.Add(worst);
                beds = worstOver;
            }

            // Room to grow: a quarter again, and at least one bed.
            p.Beds = beds + System.Math.Max(1, (beds + 3) / 4);

            Household first = p.Families[0];
            Household kin = null;
            if (p.Crowded != null) kin = p.Crowded;
            else if (first.Kin >= 0) foreach (Household h in s.Households) if (h.Number == first.Kin) kin = h;
            if (kin != null && kin.Homes.Count > 0)
            {
                p.KinX = kin.Homes[0].Site.ParcelX;
                p.KinZ = kin.Homes[0].Site.ParcelZ;
            }

            p.Overrides = new Dictionary<string, double> { { "capacity", p.Beds } };
            return p;
        }

        static double Gene(Genome g, string name)
        {
            if (g == null) return 0.5;
            double v = g[Symbol.For(name)];
            return double.IsNaN(v) ? 0.5 : v;
        }

        static double Distance(int ax, int az, int bx, int bz)
        {
            double dx = ax - bx, dz = az - bz;
            return SimMath.Sqrt(dx * dx + dz * dz);
        }

        // ── the search ──────────────────────────────────────────────────────

        /// <summary>One way to answer the brief: a plan turned some way round, on a site, and what it scored.</summary>
        public sealed class Candidate
        {
            public Blueprint Plan;
            public Site Site;
            public int DoorSide;
            public bool EarthBacked;
            public double Score;
            public readonly List<string> Reasons = new List<string>();
        }

        /// <summary>Candidate sites kept per footprint shape before orientation is weighed.</summary>
        public const int SitesPerShape = 6;

        /// <summary>
        /// Every quarter turn of the plan on the best few sites for its
        /// footprint, weighed against the brief. The best, or null when the
        /// house fits nowhere.
        /// </summary>
        public static Candidate Search(Settlement s, BuildIntent intent, Blueprint plan, SitingRule rule,
                                       ParcelGrid grid, ConstraintFields fields, DwellingProgram program)
        {
            Genome genome = s.Genome;
            double toFire = rule.Weight("doorToFire", genome);
            double toSun = rule.Weight("doorToSun", genome);
            double downhill = rule.Weight("doorDownhill", genome);
            double intoSlope = rule.Weight("backIntoSlope", genome);
            double nearKin = rule.Weight("nearKin", genome);

            bool[] reachable = SiteScorer.ReachableFromFire(s, grid);
            int baseDoor = plan.DoorSide();
            Candidate best = null;
            var sitesByShape = new Dictionary<long, List<Site>>();

            for (int q = 0; q < 4; q++)
            {
                Blueprint turned = plan.Rotated(q);
                long shape = ((long)turned.Width << 20) | (long)turned.Depth;
                List<Site> sites;
                if (!sitesByShape.TryGetValue(shape, out sites))
                {
                    sites = SiteScorer.Candidates(s, intent, turned, rule, grid, fields, genome, SitesPerShape, reachable);
                    sitesByShape[shape] = sites;
                }
                int door = baseDoor < 0 ? -1 : (baseDoor + q) % 4;

                foreach (Site site in sites)
                {
                    var c = new Candidate { Plan = turned, Site = site, DoorSide = door };
                    c.Score = site.Score;
                    Weigh(c, s, grid, program, toFire, toSun, downhill, intoSlope, nearKin);
                    if (best == null || c.Score > best.Score) best = c;
                }
            }
            return best;
        }

        static void Weigh(Candidate c, Settlement s, ParcelGrid grid, DwellingProgram program,
                          double toFire, double toSun, double downhill, double intoSlope, double nearKin)
        {
            Site site = c.Site;
            double cx = site.ParcelX + (site.ParcelsWide - 1) * 0.5, cz = site.ParcelZ + (site.ParcelsDeep - 1) * 0.5;
            var terms = new List<KeyValuePair<double, string>>();

            if (c.DoorSide >= 0)
            {
                int dx, dz;
                Blueprint.SideStep(c.DoorSide, out dx, out dz);

                // To the fire: how squarely the door looks at it.
                double fx = s.HearthParcelX - cx, fz = s.HearthParcelZ - cz;
                double fl = SimMath.Sqrt(fx * fx + fz * fz);
                if (fl > 0.5)
                {
                    double facing = (dx * fx + dz * fz) / fl;
                    terms.Add(new KeyValuePair<double, string>(toFire * facing, "its door faces the fire"));
                }

                // To the sun, which stands toward -z (S1F's sun field).
                terms.Add(new KeyValuePair<double, string>(toSun * -dz, "its door faces the sun"));

                // Out downhill, and back into whatever rises behind.
                double front = Beyond(grid, site, c.DoorSide), back = Beyond(grid, site, (c.DoorSide + 2) % 4);
                double here = Mean(grid, site);
                if (!double.IsNaN(front) && !double.IsNaN(back))
                {
                    double fall = SimMath.Clamp((back - front) / 4.0, -1.0, 1.0);
                    terms.Add(new KeyValuePair<double, string>(downhill * fall, "it steps out downhill"));
                }
                if (!double.IsNaN(back) && back - here >= 2.0)
                {
                    double rise = SimMath.Clamp01((back - here) / 6.0);
                    terms.Add(new KeyValuePair<double, string>(intoSlope * rise, "it is backed into the slope"));
                    if (intoSlope > 0.3) c.EarthBacked = true;
                }
            }

            if (program != null && program.KinX >= 0)
            {
                double d = Distance((int)cx, (int)cz, program.KinX, program.KinZ);
                terms.Add(new KeyValuePair<double, string>(-nearKin * (d > 30.0 ? 30.0 : d),
                                                           program.Crowded != null ? "it stands by the family's old house"
                                                                                   : "it stands near the family it came from"));
            }

            foreach (var t in terms) c.Score += t.Key;

            // The reasons worth telling: whatever pulled it most.
            terms.Sort((a, b) => b.Key.CompareTo(a.Key));
            foreach (var t in terms)
                if (t.Key >= 0.25 && c.Reasons.Count < 3 && !(t.Value.StartsWith("it stands") && t.Key < 0)) c.Reasons.Add(t.Value);
        }

        // ── S2P: more room in a home that stands ─────────────────────────

        /// <summary>A wing or a storey for a crowded family's home, placed and weighed.</summary>
        public sealed class Addition
        {
            public string Kind;          // "wing" or "storey"
            public Project Host;
            public Blueprint Plan;
            public Site Site;
            public int OffsetX, OffsetY, OffsetZ;
            public int DoorSide = -1;
            public GroundPlan Ground;
            public Project Beneath;
            public double Score;
            public readonly List<string> Reasons = new List<string>();
        }

        /// <summary>
        /// Every way to add room to the crowded family's home: a wing against
        /// each wall that is not its front, where the ground beside it is free,
        /// and a storey on top where the walls will carry one and the culture
        /// builds that high. Best first by score; empty when neither fits.
        /// </summary>
        public static Addition BestAddition(Settlement s, DwellingProgram program, SitingRule rule, ParcelGrid grid,
                                            ConstraintFields fields, GrammarTable grammars, Palette palette, TileSet tiles,
                                            MaterialTable materials, int budget)
        {
            if (program == null || program.Crowded == null || program.Crowded.Homes.Count == 0) return null;
            Project host = program.Crowded.Homes[0];
            Genome genome = s.Genome;
            Addition best = null;

            int bodyW = host.Plan.Width - 2 * Grammar.Margin, bodyD = host.Plan.Depth - 2 * Grammar.Margin;
            int hostX0 = host.Site.ParcelX * ParcelGrid.Size + host.OffsetX;
            int hostZ0 = host.Site.ParcelZ * ParcelGrid.Size + host.OffsetZ;
            var beds = new Dictionary<string, double> { { "capacity", System.Math.Min(program.Beds, 8) } };

            // A wing on each side but the front.
            Grammar wing = grammars.PartFor("shelter", "wing");
            if (wing != null)
            {
                Blueprint basePlan = wing.Build(genome, palette, 64, 64, budget, beds);
                int baseDoor = basePlan.DoorSide();
                for (int side = 0; side < 4; side++)
                {
                    if (side == host.DoorSide) continue;
                    // Its own door looks outward, away from the house.
                    int q = baseDoor < 0 ? 0 : ((side - baseDoor) % 4 + 4) % 4;
                    Blueprint plan = basePlan.Rotated(q);
                    int wW = plan.Width - 2 * Grammar.Margin, wD = plan.Depth - 2 * Grammar.Margin;

                    int dx, dz;
                    Blueprint.SideStep(side, out dx, out dz);
                    int x0 = dx > 0 ? hostX0 + bodyW : dx < 0 ? hostX0 - wW : hostX0 + (bodyW - wW) / 2;
                    int z0 = dz > 0 ? hostZ0 + bodyD : dz < 0 ? hostZ0 - wD : hostZ0 + (bodyD - wD) / 2;
                    if (x0 < 8 || z0 < 8 || x0 + wW >= Voxels.ChunkStore.SizeX - 8 || z0 + wD >= Voxels.ChunkStore.SizeZ - 8) continue;

                    int px = x0 / ParcelGrid.Size, pz = z0 / ParcelGrid.Size;
                    var site = new Site
                    {
                        ParcelX = px, ParcelZ = pz,
                        ParcelsWide = (x0 - px * ParcelGrid.Size + wW + ParcelGrid.Size - 1) / ParcelGrid.Size,
                        ParcelsDeep = (z0 - pz * ParcelGrid.Size + wD + ParcelGrid.Size - 1) / ParcelGrid.Size,
                    };
                    double score;
                    if (!WingGround(s, host, rule, grid, fields, genome, site, out score)) continue;

                    var a = new Addition
                    {
                        Kind = "wing", Host = host, Plan = plan, Site = site, DoorSide = side,
                        OffsetX = x0 - px * ParcelGrid.Size, OffsetZ = z0 - pz * ParcelGrid.Size,
                        // Judged like the house it is part of: the ground beside a
                        // home is the home's ground. Only a wing on ground far worse
                        // than the house's own loses for it.
                        Score = host.Site.Score + rule.Weight("wing", genome) - 0.5 * System.Math.Max(0.0, host.Site.Score - score - 1.0),
                    };
                    a.Ground = NegotiationTable.AtFloor(grid, x0, z0, wW, wD, host.Site.Ground,
                                                       host.Ground != null && host.Ground.Strategy == GroundStrategy.Stilt);
                    a.Reasons.Add("a wing on the " + SideName(side) + " side of the house, for a family of " + program.Crowded.Size);
                    if (best == null || a.Score > best.Score) best = a;
                }
            }

            // A storey on top.
            Grammar storey = grammars.PartFor("shelter", "storey");
            int storeys = Construction.Storeys(host.Plan);
            foreach (Project added in host.Additions) if (added.PartKind == "storey") storeys++;
            int most = 2 + (int)SimMath.Round(Gene(genome, "gene.verticality") * 2.0);
            int wallMaterial = host.Built.MaterialFor(Symbol.For("role.wall"));
            bool carries = wallMaterial >= 0 && tiles.Carries(materials[wallMaterial].Class);
            bool pending = false;
            foreach (Project added in host.Additions) if (!added.Complete) pending = true;

            if (storey != null && carries && storeys < most && !pending && host.Complete)
            {
                var size = new Dictionary<string, double>
                {
                    { "capacity", System.Math.Min(program.Beds, 8) }, { "width", bodyW }, { "depth", bodyD },
                };
                Blueprint plan = storey.Build(genome, palette, bodyW, bodyD, budget, size);
                Project top = host;
                foreach (Project added in host.Additions) if (added.PartKind == "storey") top = added;
                int roofY = Construction.RoofBase(top.Plan);
                if (roofY >= 0 && plan.Width == host.Plan.Width && plan.Depth == host.Plan.Depth)
                {
                    var a = new Addition
                    {
                        Kind = "storey", Host = host, Plan = plan,
                        Site = new Site { ParcelX = host.Site.ParcelX, ParcelZ = host.Site.ParcelZ,
                                          ParcelsWide = host.Site.ParcelsWide, ParcelsDeep = host.Site.ParcelsDeep,
                                          Score = host.Site.Score, Ground = host.Site.Ground },
                        OffsetX = host.OffsetX, OffsetZ = host.OffsetZ,
                        OffsetY = top.OffsetY + roofY,
                        Beneath = top,
                        Score = host.Site.Score + rule.Weight("storey", genome) - 0.4 * (storeys - 1),
                    };
                    a.Reasons.Add("a " + Ordinal(storeys + 1) + " storey on the house, for a family of " + program.Crowded.Size);
                    if (best == null || a.Score > best.Score) best = a;
                }
            }
            return best;
        }

        /// <summary>
        /// Ground a wing may take: land, dry, and nobody's but the host's; scored
        /// like any building's site, by its worst parcel.
        /// </summary>
        static bool WingGround(Settlement s, Project host, SitingRule rule, ParcelGrid grid, ConstraintFields fields,
                               Genome genome, Site site, out double score)
        {
            score = double.MaxValue;
            for (int dz = 0; dz < site.ParcelsDeep; dz++)
                for (int dx = 0; dx < site.ParcelsWide; dx++)
                {
                    int x = site.ParcelX + dx, z = site.ParcelZ + dz;
                    if (!ParcelGrid.InBounds(x, z) || !grid.IsLand(x, z) || grid.WetColumns(x, z) > 4) return false;
                    RecordId owner = s.ClaimOn(x, z);
                    if (owner.Exists && !OwnedByHome(host, owner)) return false;
                    double here = SiteScorer.ScoreParcel(s, rule, grid, fields, genome, x, z);
                    if (here < score) score = here;
                }

            // Daylight all round, as for any building (S1B) — except against the
            // house it is part of, which is the point of a wing.
            for (int dz = -1; dz <= site.ParcelsDeep; dz++)
                for (int dx = -1; dx <= site.ParcelsWide; dx++)
                {
                    RecordId owner = s.ClaimOn(site.ParcelX + dx, site.ParcelZ + dz);
                    if (owner.Exists && !OwnedByHome(host, owner)) return false;
                }
            return true;
        }

        static bool OwnedByHome(Project host, RecordId owner)
        {
            if (host.Site.Record == owner) return true;
            foreach (Project added in host.Additions) if (added.Site != null && added.Site.Record == owner) return true;
            return false;
        }

        static string SideName(int side) { return side == 0 ? "north" : side == 1 ? "east" : side == 2 ? "south" : "west"; }

        static string Ordinal(int n) { return n == 2 ? "second" : n == 3 ? "third" : n == 4 ? "fourth" : n + "th"; }

        /// <summary>Mean height of the parcels just outside one side of a footprint, or NaN where there are none.</summary>
        static double Beyond(ParcelGrid grid, Site site, int side)
        {
            int dx, dz;
            Blueprint.SideStep(side, out dx, out dz);
            double sum = 0.0;
            int n = 0;
            int along = dx != 0 ? site.ParcelsDeep : site.ParcelsWide;
            for (int i = 0; i < along; i++)
            {
                int px = dx > 0 ? site.ParcelX + site.ParcelsWide : dx < 0 ? site.ParcelX - 1 : site.ParcelX + i;
                int pz = dz > 0 ? site.ParcelZ + site.ParcelsDeep : dz < 0 ? site.ParcelZ - 1 : site.ParcelZ + i;
                if (!ParcelGrid.InBounds(px, pz) || !grid.IsLand(px, pz)) continue;
                sum += grid.Height[px, pz];
                n++;
            }
            return n == 0 ? double.NaN : sum / n;
        }

        static double Mean(ParcelGrid grid, Site site)
        {
            double sum = 0.0;
            int n = 0;
            for (int z = 0; z < site.ParcelsDeep; z++)
                for (int x = 0; x < site.ParcelsWide; x++) { sum += grid.Height[site.ParcelX + x, site.ParcelZ + z]; n++; }
            return sum / n;
        }
    }
}
