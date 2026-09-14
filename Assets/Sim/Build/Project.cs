using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Core;
using Godless.Sim.Culture;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;
using Godless.Sim.World;

namespace Godless.Sim.Build
{
    /// <summary>
    /// A building between being asked for and standing: the intent, the plan
    /// the genome made of it, the ground it claimed, and what it is to be made
    /// of. S15 creates one; S1A builds it.
    ///
    /// Everything here is decided at the moment of commissioning and never
    /// revisited. That is what makes a street a stratigraphy: a house holds
    /// the genome and the stock of the year it was begun, and the one next
    /// door holds another year's.
    /// </summary>
    public sealed class Project
    {
        // RecordId's default is record zero, not "no record", so a project
        // that has not begun has to say so explicitly. Missing this made
        // every house look already begun: no structure.begun record, its
        // voxels blamed on the settlement's founding, and the "enough in the
        // yard to start" check skipped.
        public Project() { Begun = RecordId.None; }

        public BuildIntent Intent { get; internal set; }

        /// <summary>A home rather than a store (S2H): what the families, beds and roofs count.</summary>
        public bool IsHome { get { return Intent == null || Intent.Kind.Purpose == IntentPurpose.Home; } }

        /// <summary>A store for food (S2H), standing or going up.</summary>
        public bool IsStore { get { return Intent != null && Intent.Kind.Purpose == IntentPurpose.Store; } }
        public Blueprint Plan { get; internal set; }
        public Site Site { get; internal set; }
        public Structure Built { get; internal set; }

        /// <summary>How the building meets its ground, and what the ground becomes (S16).</summary>
        public GroundPlan Ground { get; internal set; }

        /// <summary>Voxels placed so far. S1A raises it a day at a time.</summary>
        public int Placed { get; internal set; }

        /// <summary>The structure.begun record: the cause every one of its voxels carries.</summary>
        public RecordId Begun { get; internal set; }

        /// <summary>The cells to lay, bottom up. Built once, when building starts.</summary>
        internal List<int> Order;

        // What is still to lay, material by material, and the state it was counted at.
        internal long[] OwedCount;
        internal int OwedPlaced = -1;
        internal Structure OwedBuilt;

        public bool Complete { get; internal set; }

        /// <summary>Brought down (S2T). It is rubble now, and out of the settlement's buildings.</summary>
        public bool Destroyed { get; internal set; }

        /// <summary>The day its materials were last remade from what can be had (S2F), so it is not remade every tick.</summary>
        internal long RethoughtOn = -1;

        // ── S2O: what the dwelling program decided, and why ───────────────

        internal readonly List<string> ReasonList = new List<string>();

        /// <summary>Why it is this way round, here, this size: the criteria it won on, in words.</summary>
        public IReadOnlyList<string> Reasons { get { return ReasonList; } }

        /// <summary>Which side its door is on: 0 -z, 1 +x, 2 +z, 3 -x; -1 none.</summary>
        public int DoorSide { get; internal set; } = -1;

        /// <summary>Its back wall is the hillside where the hillside stands high enough (S2O).</summary>
        public bool EarthBacked { get; internal set; }

        /// <summary>The families it was planned for, by number, who move in first.</summary>
        internal readonly List<int> ForFamilies = new List<int>();

        // ── S2P: a part added to a home that stands ────────────────────────

        /// <summary>The home this is a wing or a storey of, or null for a building of its own.</summary>
        public Project Host { get; internal set; }

        /// <summary>"wing" or "storey" for a part; empty for a building.</summary>
        public string PartKind { get; internal set; } = "";

        /// <summary>Voxels its blueprint is shifted from its site's usual origin, so a part meets its host.</summary>
        internal int OffsetX, OffsetY, OffsetZ;

        // ── S2S: what is inside ─────────────────────────────────────────────

        internal readonly List<Furnishing.Bed> BedList = new List<Furnishing.Bed>();

        /// <summary>The beds laid in it when it was finished, one per sleeping place where they fit.</summary>
        public IReadOnlyList<Furnishing.Bed> Beds { get { return BedList; } }

        /// <summary>For a storey: the part whose roof comes off to make room for it — the host, or the storey below.</summary>
        internal Project Beneath;

        /// <summary>Wings and storeys added to it, in the order they were begun.</summary>
        internal readonly List<Project> Additions = new List<Project>();
        public IReadOnlyList<Project> Added { get { return Additions; } }
    }

    /// <summary>
    /// Turns open intents into projects on real ground, once a day. S15.
    ///
    /// The order is the design's: an intent plus a site plus the genome plus
    /// what is in stock resolves into a voxel blueprint (Part 03, layer 3).
    /// The site is claimed and the intent is claimed with it, so the bus stops
    /// counting it as unanswered and the settlement stops asking twice.
    /// </summary>
    public sealed class SiteSystem : ISimSystem
    {
        public static readonly Symbol SystemId = Symbol.For("system.siting");
        public const string StreamId = "build.realization";

        /// <summary>
        /// Days a settlement will go on looking for somewhere to put a
        /// building before giving up on it. An intent nobody can site blocks
        /// the ones behind it, and a settlement with nowhere left to build
        /// should be a settlement with a reason to leave (S30), not one that
        /// stands for ever holding a request it cannot answer.
        /// </summary>
        public const int GivesUpAfterDays = 240;

        /// <summary>Days between attempts to plan an intent that could not be planned.</summary>
        public const int RetryAfterDays = 5;

        readonly GrammarTable _grammars;
        readonly SitingTable _siting;
        readonly TileSet _tiles;
        readonly MaterialTable _materials;
        readonly Palette _palette;
        readonly ParcelGrid _grid;
        readonly ConstraintFields _fields;
        readonly NegotiationTable _negotiation;

        public SiteSystem(GrammarTable grammars, SitingTable siting, TileSet tiles, MaterialTable materials,
                          Palette palette, ParcelGrid grid, ConstraintFields fields, NegotiationTable negotiation = null)
        {
            _grammars = grammars; _siting = siting; _tiles = tiles;
            _materials = materials; _palette = palette; _grid = grid; _fields = fields;
            _negotiation = negotiation;
        }

        public Symbol Id { get { return SystemId; } }

        public void Tick(SimWorld world)
        {
            if (!world.Clock.IsFirstTickOfDay) return;
            RngStream rng = world.Streams.Get(StreamId);

            foreach (Settlement s in world.Settlements)
            {
                if (s.Intents == null || s.Genome == null || s.Stock == null) continue;
                foreach (BuildIntent intent in s.Intents.Intents)
                {
                    if (intent.Status != IntentStatus.Open) continue;
                    if (intent.Kind.Purpose == IntentPurpose.Farm) continue;   // fields are the farm system's (S2I)
                    if (world.Clock.Tick < intent.RetryAt) continue;
                    Project project = Plan(s, intent, world, rng);
                    if (project == null)
                    {
                        // The whole search again tomorrow finds the same nothing;
                        // a few days on, families and ground may have changed.
                        intent.RetryAt = world.Clock.Tick + RetryAfterDays * world.Clock.TicksPerDay;
                        long waited = world.Clock.Tick - intent.RaisedTick;
                        if (waited > GivesUpAfterDays * world.Clock.TicksPerDay)
                            s.Intents.Abandon(intent, world.Clock.Tick, world.Annals, s.Founded);
                        continue;
                    }
                    s.Projects.Add(project);
                    s.Intents.Claim(intent, world.Clock.Tick, world.Annals, project.Site.Record);
                }
            }
        }

        /// <summary>A wing or a storey, committed: its ground recorded and claimed, its materials settled.</summary>
        Project Add(Settlement s, BuildIntent intent, DwellingProgram.Addition a, SimWorld world, RngStream rng)
        {
            Site site = a.Site;
            var place = Construction.World(new Project { Site = site, OffsetX = a.OffsetX, OffsetY = a.OffsetY, OffsetZ = a.OffsetZ },
                                           a.Plan.Width / 2, 0, a.Plan.Depth / 2);
            site.Ground = a.Host.Site.Ground;
            site.Record = world.Annals.Write(world.Clock.Tick, SiteScorer.ChosenKind, s.Id, place, intent.Record,
                                             (long)(a.Score * 1000.0), site.ParcelsWide * site.ParcelsDeep,
                                             new[] { intent.Kind.Id, Symbol.For("part." + a.Kind) });
            if (a.Kind == "wing")
                for (int dz = 0; dz < site.ParcelsDeep; dz++)
                    for (int dx = 0; dx < site.ParcelsWide; dx++)
                        // Claimed as part of the home: one house, one owner.
                        if (!s.IsClaimed(site.ParcelX + dx, site.ParcelZ + dz))
                            s.ClaimParcel(site.ParcelX + dx, site.ParcelZ + dz, a.Host.Site.Record);

            Structure built = Realizer.Realize(a.Plan, _tiles, _materials, s.Stock, _palette, world.VoxelTypes, rng, s.Catchment);
            var project = new Project
            {
                Intent = intent, Plan = a.Plan, Site = site, Built = built, Ground = a.Ground,
                Host = a.Host, PartKind = a.Kind, DoorSide = a.DoorSide,
                OffsetX = a.OffsetX, OffsetY = a.OffsetY, OffsetZ = a.OffsetZ,
            };
            project.Beneath = a.Beneath;
            project.ReasonList.AddRange(a.Reasons);
            a.Host.Additions.Add(project);
            return project;
        }

        /// <summary>
        /// One intent, planned and sited. Null when the grammar or the rule is
        /// missing, or when there is nowhere left the settlement will build.
        /// </summary>
        public Project Plan(Settlement s, BuildIntent intent, SimWorld world, RngStream rng)
        {
            Grammar grammar = _grammars.For(intent.Kind.Name);
            SitingRule rule = _siting.For(intent.Kind.Name);
            if (grammar == null || rule == null) return null;

            // S2O: who it is for decides how big it is; the search decides which
            // way round it goes and where, and says why.
            bool home = intent.Kind.Purpose == IntentPurpose.Home;
            DwellingProgram program = home ? DwellingProgram.For(s, intent) : null;
            // Nobody to build it for: the request stays open until a family
            // needs it, or lapses. A shelter intent outlives the nights that
            // raised it, and building it anyway put up houses nobody moved into.
            if (home && program == null && s.Households.Count > 0) return null;
            Blueprint plan = program != null
                ? grammar.Build(s.Genome, _palette, ParcelGrid.Size * rule.SearchRadius, ParcelGrid.Size * rule.SearchRadius,
                                intent.BudgetVoxels, program.Overrides)
                : grammar.Build(s.Genome, _palette, ParcelGrid.Size * rule.SearchRadius,
                                ParcelGrid.Size * rule.SearchRadius, intent.BudgetVoxels);

            // A family a bed or two short does not send one or two of its own off
            // to a hut of their own: it waits for room it can add to its home —
            // so there is no house apart to search for.
            bool waitsForRoom = program != null && program.Crowded != null && program.Beds < 4;
            DwellingProgram.Candidate chosen = waitsForRoom ? null
                : DwellingProgram.Search(s, intent, plan, rule, _grid, _fields, program);

            // S2P: a crowded family would sooner add to the home it has — a wing
            // beside it, a storey on it — than move part of itself out. Which it
            // does is the culture's weighing, against the best house apart.
            DwellingProgram.Addition addition = DwellingProgram.BestAddition(s, program, rule, _grid, _fields, _grammars,
                                                                             _palette, _tiles, _materials, intent.BudgetVoxels);
            double apart = chosen == null ? double.NegativeInfinity : chosen.Score + rule.Weight("apart", s.Genome);
            if (addition != null && addition.Score >= apart) return Add(s, intent, addition, world, rng);

            if (waitsForRoom) return null;
            if (chosen == null) return null;
            plan = chosen.Plan;
            Site site = chosen.Site;
            SiteScorer.Commit(s, intent, site, _grid, world.Clock.Tick, world.Annals);

            // How it will meet the ground, and so what level its floor sits at (S16).
            GroundPlan ground = null;
            if (_negotiation != null && _negotiation.Count > 0)
            {
                ground = _negotiation.Choose(site, _grid, _fields, s.Genome,
                                             plan.Width - 2 * Grammar.Margin, plan.Depth - 2 * Grammar.Margin);
                site.Ground = ground.Floor;
            }

            Structure built = Realizer.Realize(plan, _tiles, _materials, s.Stock, _palette, world.VoxelTypes, rng, s.Catchment);
            var project = new Project
            {
                Intent = intent, Plan = plan, Site = site, Built = built, Ground = ground,
                DoorSide = chosen.DoorSide,
                EarthBacked = chosen.EarthBacked && ground != null && ground.Strategy != GroundStrategy.Stilt,
            };
            if (program != null)
            {
                foreach (Household h in program.Families) project.ForFamilies.Add(h.Number);
                if (program.Families.Count > 1) project.ReasonList.Add("a long house for " + program.Families.Count + " families");
                else if (program.Families.Count == 1)
                    project.ReasonList.Add((program.Crowded != null ? "room for a crowded family of " : "a home for a family of ")
                                           + program.Families[0].Size);
            }
            foreach (string why in chosen.Reasons)
                if (why != "it is backed into the slope" || project.EarthBacked) project.ReasonList.Add(why);
            return project;
        }
    }
}
