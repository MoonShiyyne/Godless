using System.Collections.Generic;
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
        public BuildIntent Intent { get; internal set; }
        public Blueprint Plan { get; internal set; }
        public Site Site { get; internal set; }
        public Structure Built { get; internal set; }

        /// <summary>Voxels placed so far. S1A raises it a day at a time.</summary>
        public int Placed { get; internal set; }

        public bool Complete { get; internal set; }
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

        readonly GrammarTable _grammars;
        readonly SitingTable _siting;
        readonly TileSet _tiles;
        readonly MaterialTable _materials;
        readonly Palette _palette;
        readonly ParcelGrid _grid;
        readonly ConstraintFields _fields;

        public SiteSystem(GrammarTable grammars, SitingTable siting, TileSet tiles, MaterialTable materials,
                          Palette palette, ParcelGrid grid, ConstraintFields fields)
        {
            _grammars = grammars; _siting = siting; _tiles = tiles;
            _materials = materials; _palette = palette; _grid = grid; _fields = fields;
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
                    Project project = Plan(s, intent, world, rng);
                    if (project == null) continue;
                    s.Projects.Add(project);
                    s.Intents.Claim(intent, world.Clock.Tick, world.Annals, project.Site.Record);
                }
            }
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

            Blueprint plan = grammar.Build(s.Genome, _palette, ParcelGrid.Size * rule.SearchRadius,
                                           ParcelGrid.Size * rule.SearchRadius, intent.BudgetVoxels);
            Site site = SiteScorer.Choose(s, intent, plan, rule, _grid, _fields, s.Genome, world.Clock.Tick, world.Annals);
            if (site == null) return null;

            Structure built = Realizer.Realize(plan, _tiles, _materials, s.Stock, _palette, world.VoxelTypes, rng);
            return new Project { Intent = intent, Plan = plan, Site = site, Built = built };
        }
    }
}
