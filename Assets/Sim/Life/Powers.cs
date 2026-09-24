using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Core;
using Godless.Sim.Harness;
using Godless.Sim.Voxels;
using Godless.Sim.World;

namespace Godless.Sim.Life
{
    /// <summary>
    /// The god's first powers over life and land (v2 M1), every one through
    /// the command queue: it lands at the start of the next step, writes its
    /// god.* record, and everything it kills, burns or floods cites that
    /// record (L3). Each has a feed line in content.
    /// </summary>
    public static class Powers
    {
        public static readonly Symbol SpawnedKind = Symbol.For("god.spawned");
        public static readonly Symbol SmoteKind = Symbol.For("god.smote");
        public static readonly Symbol BlessedKind = Symbol.For("god.blessed");
        public static readonly Symbol CursedKind = Symbol.For("god.cursed");
        public static readonly Symbol FireKind = Symbol.For("god.fire");
        public static readonly Symbol RainKind = Symbol.For("god.rain");
        public static readonly Symbol WaterKind = Symbol.For("god.water");

        /// <summary>Every living creature within a radius of a column, in row order.</summary>
        public static List<int> Within(Living life, double x, double z, double radius)
        {
            var found = new List<int>();
            Creatures c = life.Creatures;
            double r2 = radius * radius;
            for (int i = 0; i < c.Length; i++)
            {
                if (!c.Alive[i]) continue;
                double dx = c.X[i] - x, dz = c.Z[i] - z;
                if (dx * dx + dz * dz <= r2) found.Add(i);
            }
            return found;
        }

        internal static void Refresh(ParcelGrid grid, Int3 at, int radius)
        {
            if (grid != null) grid.MarkDirty(at.X - radius, at.Z - radius, at.X + radius, at.Z + radius);
        }
    }

    /// <summary>Put creatures on the land: a herd of grown animals keeping together, or a band of people with a camp where they stand. Each is its own from then on.</summary>
    public sealed class Spawn : IGodCommand
    {
        readonly LifeSystem _life;
        public int Species { get; private set; }
        public Int3 At { get; private set; }
        public int Count { get; private set; }

        public Spawn(LifeSystem life, int species, Int3 at, int count) { _life = life; Species = species; At = at; Count = count; }

        public Symbol Kind { get { return Powers.SpawnedKind; } }

        public RecordId Apply(SimWorld world)
        {
            long tick = world.Present();
            Species sp = _life.Life.Species[Species];
            RecordId r = world.Annals.Write(tick, Kind, sp.Id, At, RecordId.None, Count);
            RngStream rng = world.Streams.Get("god.spawn");
            if (!_life.Passable(At.X, At.Z)) return r;
            if (sp.Person) { _life.FoundBand(world, Species, At.X, At.Z, Count, r, rng); return r; }
            _life.SetDown(world, Species, At.X, At.Z, Count, tick, rng);
            return r;
        }
    }

    /// <summary>Strike the ground: every creature close by dies.</summary>
    public sealed class Smite : IGodCommand
    {
        readonly LifeSystem _life;
        public Int3 At { get; private set; }
        public int Radius { get; private set; }

        public Smite(LifeSystem life, Int3 at, int radius = 3) { _life = life; At = at; Radius = radius; }

        public Symbol Kind { get { return Powers.SmoteKind; } }

        public RecordId Apply(SimWorld world)
        {
            long tick = world.Present();
            List<int> struck = Powers.Within(_life.Life, At.X, At.Z, Radius);
            RecordId r = world.Annals.Write(tick, Kind, Symbol.None, At, RecordId.None, struck.Count);
            foreach (int i in struck) _life.Kill(world, i, Death.Smitten, r);
            return r;
        }
    }

    /// <summary>Bless or curse every creature close by. Blessed: hardier, longer-lived, slower to hunger, quicker. Cursed: hungrier, shorter-lived, and mad — they attack whatever is nearest.</summary>
    public sealed class Touch : IGodCommand
    {
        readonly LifeSystem _life;
        public Int3 At { get; private set; }
        public int Radius { get; private set; }
        public bool Bless { get; private set; }

        public Touch(LifeSystem life, Int3 at, bool bless, int radius = 6) { _life = life; At = at; Bless = bless; Radius = radius; }

        public Symbol Kind { get { return Bless ? Powers.BlessedKind : Powers.CursedKind; } }

        public RecordId Apply(SimWorld world)
        {
            long tick = world.Present();
            List<int> touched = Powers.Within(_life.Life, At.X, At.Z, Radius);
            RecordId r = world.Annals.Write(tick, Kind, Symbol.None, At, RecordId.None, touched.Count);
            Creatures c = _life.Life.Creatures;
            foreach (int i in touched)
            {
                if (Bless) { c.Flags[i] = (byte)((c.Flags[i] | Creatures.Blessed) & ~Creatures.Cursed); c.Hunger[i] = 0.0; }
                else c.Flags[i] = (byte)((c.Flags[i] | Creatures.Cursed) & ~Creatures.Blessed);
            }
            return r;
        }
    }

    /// <summary>Fire: the trees burn out of the world, the grazing goes to nothing, and whatever cannot run dies.</summary>
    public sealed class Fire : IGodCommand
    {
        readonly LifeSystem _life;
        readonly ParcelGrid _grid;
        public Int3 At { get; private set; }
        public int Radius { get; private set; }

        public Fire(LifeSystem life, ParcelGrid grid, Int3 at, int radius = 12) { _life = life; _grid = grid; At = at; Radius = radius; }

        public Symbol Kind { get { return Powers.FireKind; } }

        public RecordId Apply(SimWorld world)
        {
            long tick = world.Present();
            RecordId r = world.Annals.Write(tick, Kind, Symbol.None, At, RecordId.None, Radius);
            int burnt = 0;
            DepositMap deposits = world.Island != null ? world.Island.Deposits : null;
            if (deposits != null)
                foreach (int f in deposits.Within(At.X, At.Z, Radius))
                {
                    if (deposits.KindOf(f).Shape != FeatureShape.Tree || deposits.Remaining(f) <= 0) continue;
                    deposits.Take(f, deposits.Remaining(f), world.Voxels, tick, r, world.Clock.TicksPerDay);
                    burnt++;
                }
            Grazing g = _life.Life.Grazing;
            int pr = Radius / ParcelGrid.Size + 1, cx = At.X / ParcelGrid.Size, cz = At.Z / ParcelGrid.Size;
            for (int dz = -pr; dz <= pr; dz++)
                for (int dx = -pr; dx <= pr; dx++)
                    if (ParcelGrid.InBounds(cx + dx, cz + dz) && dx * dx + dz * dz <= pr * pr)
                        g.Food[(cz + dz) * ParcelGrid.Width + cx + dx] = 0.0;
            foreach (int i in Powers.Within(_life.Life, At.X, At.Z, Radius * 0.5)) _life.Kill(world, i, Death.Burned, r);
            Powers.Refresh(_grid, At, Radius);
            return r;
        }
    }

    /// <summary>Rain: the grazing round about grows back fast for half a year, starting now.</summary>
    public sealed class Rain : IGodCommand
    {
        readonly LifeSystem _life;
        public Int3 At { get; private set; }
        public int Radius { get; private set; }

        public Rain(LifeSystem life, Int3 at, int radius = 40) { _life = life; At = at; Radius = radius; }

        public Symbol Kind { get { return Powers.RainKind; } }

        public RecordId Apply(SimWorld world)
        {
            long tick = world.Present();
            RecordId r = world.Annals.Write(tick, Kind, Symbol.None, At, RecordId.None, Radius);
            Grazing g = _life.Life.Grazing;
            int pr = Radius / ParcelGrid.Size, cx = At.X / ParcelGrid.Size, cz = At.Z / ParcelGrid.Size;
            for (int dz = -pr; dz <= pr; dz++)
                for (int dx = -pr; dx <= pr; dx++)
                {
                    if (!ParcelGrid.InBounds(cx + dx, cz + dz) || dx * dx + dz * dz > pr * pr) continue;
                    int p = (cz + dz) * ParcelGrid.Width + cx + dx;
                    if (g.Capacity[p] <= 0.0) continue;
                    g.RainMonths[p] = 6;
                    g.Food[p] += (g.Capacity[p] - g.Food[p]) * 0.3;
                }
            return r;
        }
    }

    /// <summary>
    /// Water: a pond sunk into the ground round a column, level at the lowest
    /// ground within it, two voxels deep. The planning grid reads it as water
    /// next step, and nothing walks through it. (Water that flows is M3's.)
    /// </summary>
    public sealed class Water : IGodCommand
    {
        readonly ParcelGrid _grid;
        public Int3 At { get; private set; }
        public int Radius { get; private set; }

        public Water(ParcelGrid grid, Int3 at, int radius = 6) { _grid = grid; At = at; Radius = radius; }

        public Symbol Kind { get { return Powers.WaterKind; } }

        public RecordId Apply(SimWorld world)
        {
            long tick = world.Present();
            RecordId r = world.Annals.Write(tick, Kind, Symbol.None, At, RecordId.None, Radius);
            ushort water;
            if (!world.VoxelTypes.TryGetId(Symbol.For("voxel.water"), out water)) return r;
            bool[] solid = TerrainBrush.SolidTable(world.Content, world.VoxelTypes);

            int level = int.MaxValue;
            var columns = new List<Int3>();
            for (int dz = -Radius; dz <= Radius; dz++)
                for (int dx = -Radius; dx <= Radius; dx++)
                {
                    int x = At.X + dx, z = At.Z + dz;
                    if (dx * dx + dz * dz > Radius * Radius || x < 1 || z < 1 || x >= ChunkStore.SizeX - 1 || z >= ChunkStore.SizeZ - 1) continue;
                    int top = TerrainBrush.TopSolid(world.Voxels.Store, solid, x, z);
                    if (top < 2) continue;
                    columns.Add(new Int3(x, top, z));
                    if (top < level) level = top;
                }
            if (columns.Count == 0) return r;
            foreach (Int3 col in columns)
            {
                // Everything above the pond's floor comes away; the pond fills to the lowest ground.
                for (int y = col.Y; y >= level - 1; y--) world.Voxels.Set(col.X, y, col.Z, VoxelTypes.AirId, tick, r);
                world.Voxels.Set(col.X, level - 1, col.Z, water, tick, r);
                world.Voxels.Set(col.X, level, col.Z, water, tick, r);
            }
            Powers.Refresh(_grid, At, Radius + 1);
            return r;
        }
    }
}
