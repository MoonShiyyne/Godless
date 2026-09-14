using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Core;
using Godless.Sim.Deltas;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;
using Godless.Sim.World;

namespace Godless.Sim.Build
{
    /// <summary>A heap of what a building was, waiting to be carried off or built over. S2T.</summary>
    public struct RubbleCell
    {
        public Int3 At;
        public int Material;     // index into the settlement's material table, or -1
        public int Detail;       // the detail instance drawing it
        public RecordId Fell;    // the collapse that left it here
    }

    /// <summary>
    /// Buildings coming down. S2T.
    ///
    /// Whatever brings a building down — ground dug from under it, a god's
    /// hand, and later a flood, a fire or neglect — it comes down the same way:
    /// every voxel of it, and of every wing and storey added to it, leaves its
    /// place and falls, the high ones scattering further, and the heap slumps
    /// until it lies the way a heap lies. Each fallen voxel is rubble: ground
    /// that is walked on and built on, drawn as a broken chunk of what it was,
    /// and salvage that gives back exactly that material. The beds fall with
    /// it. The family loses its roof and its claim, and the next house may be
    /// built on the heap — Part 16's "debris becomes terrain", arriving early.
    /// </summary>
    public static class Collapse
    {
        public static readonly Symbol CollapsedKind = Symbol.For("structure.collapsed");
        public static readonly Symbol UnderminedKind = Symbol.For("structure.undermined");
        public static readonly Symbol RubbleVoxel = Symbol.For("voxel.rubble");

        /// <summary>
        /// Brings a building down, with everything added to it. Returns the
        /// voxels that fell. <paramref name="cause"/> is whatever did it; the
        /// collapse record cites it, and every fallen voxel cites the collapse.
        /// </summary>
        public static int BringDown(SimWorld world, Settlement s, Project project, RecordId cause, bool[] ground,
                                    MaterialTable materials, DetailModelTable models, ParcelGrid grid = null)
        {
            if (project == null || project.Destroyed) return 0;
            long tick = world.Clock.Tick;
            VoxelTypes types = world.VoxelTypes;
            ushort rubble;
            if (!types.TryGetId(RubbleVoxel, out rubble)) rubble = VoxelTypes.AirId;

            // The building and every part stacked on or leaning against it.
            var parts = new List<Project>();
            Gather(project, parts);

            RecordId fell = world.Annals.Write(tick, CollapsedKind, s.Id, Centre(project), cause, parts.Count, 0,
                                               new[] { Symbol.For("part." + (project.PartKind.Length == 0 ? "house" : project.PartKind)) });

            // What is standing of it: each world voxel once, highest parts last.
            var seen = new HashSet<long>();
            var falling = new List<KeyValuePair<Int3, ushort>>();
            int floorY = int.MaxValue;
            foreach (Project part in parts)
            {
                Blueprint plan = part.Plan;
                for (int y = 0; y < plan.Height; y++)
                    for (int z = 0; z < plan.Depth; z++)
                        for (int x = 0; x < plan.Width; x++)
                        {
                            ushort type = part.Built.At(x, y, z);
                            if (type == VoxelTypes.AirId) continue;
                            Int3 at = Construction.World(part, x, y, z);
                            if (!ChunkStore.InBounds(at.X, at.Y, at.Z) || world.Voxels.Get(at) != type) continue;
                            if (!seen.Add(Key(at))) continue;
                            falling.Add(new KeyValuePair<Int3, ushort>(at, type));
                            if (at.Y < floorY) floorY = at.Y;
                        }

                // The beds come down with it, as a voxel of broken frame each.
                foreach (Furnishing.Bed bed in part.Beds)
                {
                    DetailInstance inst = world.Details.Get(bed.Instance);
                    if (inst == null) continue;
                    ushort frame = inst.Slots.Count > 0 ? inst.Slots[0] : VoxelTypes.AirId;
                    world.Details.Remove(bed.Instance, tick, fell);
                    if (frame != VoxelTypes.AirId) falling.Add(new KeyValuePair<Int3, ushort>(bed.Centre, frame));
                }
            }

            // Everything leaves its place before anything lands, so nothing lands on a wall about to fall.
            foreach (var f in falling)
                if (world.Voxels.Get(f.Key) == f.Value) world.Voxels.Set(f.Key, VoxelTypes.AirId, tick, fell);

            // Lowest first, so the heap builds up from the ground.
            falling.Sort((a, b) =>
            {
                int c = a.Key.Y.CompareTo(b.Key.Y);
                if (c != 0) return c;
                c = a.Key.Z.CompareTo(b.Key.Z);
                return c != 0 ? c : a.Key.X.CompareTo(b.Key.X);
            });

            int x0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue;
            ulong seed = StableHash.Combine(world.Streams.WorldSeed, (ulong)project.Site.Record.Index);
            foreach (var f in falling)
            {
                Int3 landed;
                if (!Land(world, ground, rubble, f.Key, floorY, seed, out landed)) continue;
                world.Voxels.Set(landed, rubble, tick, fell);

                int material = materials != null ? materials.IndexOf(types.SymbolOf(f.Value)) : -1;
                int detail = -1;
                DetailModel chunk = models != null ? models.Find("rubble-" + (1 + (int)(Hash(seed, landed) % 3))) : null;
                if (chunk != null)
                    detail = world.Details.Place(chunk, landed.X * DetailModelTable.CellsPerVoxel, landed.Y * DetailModelTable.CellsPerVoxel,
                                                 landed.Z * DetailModelTable.CellsPerVoxel, (int)(Hash(seed, landed) >> 8) & 3,
                                                 new[] { f.Value }, tick, fell);
                s.RubbleList.Add(new RubbleCell { At = landed, Material = material, Detail = detail, Fell = fell });

                if (landed.X < x0) x0 = landed.X; if (landed.X > x1) x1 = landed.X;
                if (landed.Z < z0) z0 = landed.Z; if (landed.Z > z1) z1 = landed.Z;
            }

            Forget(world, s, parts, fell);
            if (grid != null && x0 <= x1) grid.Refresh(world.Voxels.Store, System.Math.Max(0, x0 - 4), System.Math.Max(0, z0 - 4),
                                                        System.Math.Min(ChunkStore.SizeX - 1, x1 + 4), System.Math.Min(ChunkStore.SizeZ - 1, z1 + 4));
            return falling.Count;
        }

        static void Gather(Project p, List<Project> into)
        {
            into.Add(p);
            foreach (Project added in p.Additions) Gather(added, into);
        }

        /// <summary>
        /// Where a falling voxel comes to rest: its own column or near it —
        /// further the higher it fell from — on top of whatever is there, then
        /// rolling off anything more than a voxel higher than a neighbour, as a
        /// heap does.
        /// </summary>
        static bool Land(SimWorld world, bool[] ground, ushort rubble, Int3 from, int floorY, ulong seed, out Int3 landed)
        {
            ulong h = Hash(seed, from);
            int reach = System.Math.Min(3, System.Math.Max(0, (from.Y - floorY) / 4));
            int x = from.X + (int)(h % (ulong)(2 * reach + 1)) - reach;
            int z = from.Z + (int)((h >> 16) % (ulong)(2 * reach + 1)) - reach;

            for (int roll = 0; roll < 4; roll++)
            {
                int top = Top(world, ground, rubble, x, z);
                int bestX = x, bestZ = z, best = top;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), nz = z + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if (!ChunkStore.InBounds(nx, 0, nz)) continue;
                    int t = Top(world, ground, rubble, nx, nz);
                    if (t < best - 1) { best = t; bestX = nx; bestZ = nz; }
                }
                if (bestX == x && bestZ == z) break;
                x = bestX; z = bestZ;
            }

            int y = Top(world, ground, rubble, x, z) + 1;
            landed = new Int3(x, y, z);
            return ChunkStore.InBounds(x, y, z) && y < ChunkStore.SizeY - 1;
        }

        /// <summary>The highest ground, standing building or rubble in a column.</summary>
        static int Top(SimWorld world, bool[] ground, ushort rubble, int x, int z)
        {
            if (!ChunkStore.InBounds(x, 0, z)) return ChunkStore.SizeY;
            for (int y = ChunkStore.SizeY - 2; y >= 0; y--)
            {
                ushort v = world.Voxels.Get(x, y, z);
                if (v == VoxelTypes.AirId) continue;
                if (v == rubble || (v < ground.Length && ground[v])) return y;
            }
            return 0;
        }

        /// <summary>Takes a fallen building out of the settlement: its beds, its roof over a family, its ground.</summary>
        static void Forget(SimWorld world, Settlement s, List<Project> parts, RecordId fell)
        {
            foreach (Project part in parts)
            {
                if (part.Complete) s.ShelterCapacity -= part.Plan.Capacity;
                if (s.ShelterCapacity < 0) s.ShelterCapacity = 0;
                part.Destroyed = true;
                part.Complete = false;
                if (part.Host != null) part.Host.Additions.Remove(part);
                s.Projects.Remove(part);
                s.RuinList.Add(part);
                s.ReleaseClaims(part.Site.Record);
                if (part.Intent != null && part.Intent.Outstanding && s.Intents != null)
                    s.Intents.Abandon(part.Intent, world.Clock.Tick, world.Annals, fell);
                foreach (Household h in s.Households) h.Homes.Remove(part);
            }
        }

        /// <summary>
        /// Salvage (S2T): a tick's worth of the nearest rubble of a material,
        /// carried to the yard. Returns the voxels taken; 0 when there is none.
        /// </summary>
        public static int Salvage(VoxelWorld voxels, DetailLayer details, Settlement s, int material, int most, long tick)
        {
            int taken = 0;
            while (taken < most)
            {
                int best = -1;
                long bestD = long.MaxValue;
                for (int i = 0; i < s.RubbleList.Count; i++)
                {
                    if (s.RubbleList[i].Material != material) continue;
                    long dx = s.RubbleList[i].At.X - s.Hearth.X, dz = s.RubbleList[i].At.Z - s.Hearth.Z;
                    // Top of the heap first: a voxel with rubble on it waits.
                    long d = dx * dx + dz * dz - (long)s.RubbleList[i].At.Y * 1000000;
                    if (d < bestD) { bestD = d; best = i; }
                }
                if (best < 0) break;
                TakeRubble(voxels, details, s, best, tick);
                s.Stock.Add(material, 1);
                taken++;
            }
            return taken;
        }

        /// <summary>Whether any rubble of this material lies in the settlement.</summary>
        public static bool HasRubble(Settlement s, int material)
        {
            foreach (RubbleCell c in s.RubbleList) if (c.Material == material) return true;
            return false;
        }

        /// <summary>
        /// Clears rubble lying in a rectangle of columns into the yard — the
        /// ground a house is about to stand on. Returns the voxels cleared.
        /// </summary>
        public static int ClearRubble(VoxelWorld voxels, DetailLayer details, Settlement s, int x0, int z0, int x1, int z1, long tick)
        {
            int cleared = 0;
            for (int i = s.RubbleList.Count - 1; i >= 0; i--)
            {
                RubbleCell c = s.RubbleList[i];
                if (c.At.X < x0 || c.At.X > x1 || c.At.Z < z0 || c.At.Z > z1) continue;
                if (c.Material >= 0) s.Stock.Add(c.Material, 1);
                TakeRubble(voxels, details, s, i, tick);
                cleared++;
            }
            return cleared;
        }

        /// <summary>Carrying off a heap cites the collapse that left it (L3): there is rubble to take because the building fell.</summary>
        static void TakeRubble(VoxelWorld voxels, DetailLayer details, Settlement s, int index, long tick)
        {
            RubbleCell c = s.RubbleList[index];
            s.RubbleList.RemoveAt(index);
            voxels.Set(c.At, VoxelTypes.AirId, tick, c.Fell);
            if (c.Detail >= 0 && details != null) details.Remove(c.Detail, tick, c.Fell);
        }

        /// <summary>
        /// Whether a standing building still has ground under it: the share of
        /// its floor's columns with anything solid in the two voxels below.
        /// </summary>
        public static double Support(SimWorld world, Project p)
        {
            if (p.Ground != null && p.Ground.Strategy == GroundStrategy.Stilt) return 1.0;
            Blueprint plan = p.Plan;
            int supported = 0, columns = 0;
            for (int z = Grammar.Margin; z < plan.Depth - Grammar.Margin; z++)
                for (int x = Grammar.Margin; x < plan.Width - Grammar.Margin; x++)
                {
                    Int3 at = Construction.World(p, x, 0, z);
                    if (!ChunkStore.InBounds(at.X, at.Y, at.Z)) continue;
                    columns++;
                    if (world.Voxels.Get(at.X, at.Y - 1, at.Z) != VoxelTypes.AirId
                        || world.Voxels.Get(at.X, at.Y - 2, at.Z) != VoxelTypes.AirId) supported++;
                }
            return columns == 0 ? 1.0 : (double)supported / columns;
        }

        /// <summary>
        /// The most recent change under a building's floor: whatever dug its
        /// ground away, which is what its collapse will cite.
        /// </summary>
        public static RecordId Underminer(SimWorld world, Project p)
        {
            Int3 a = Construction.World(p, Grammar.Margin, 0, Grammar.Margin);
            Int3 b = Construction.World(p, p.Plan.Width - 1 - Grammar.Margin, 0, p.Plan.Depth - 1 - Grammar.Margin);
            IReadOnlyList<VoxelDelta> log = world.Voxels.Log.All();
            for (int i = log.Count - 1; i >= 0 && i >= log.Count - 20000; i--)
            {
                if (log[i].NewType != VoxelTypes.AirId) continue;
                Int3 at = ChunkStore.PositionOf(log[i].ChunkIndex, log[i].VoxelIndex);
                if (at.Y >= a.Y || at.Y < a.Y - 3 || at.X < a.X || at.X > b.X || at.Z < a.Z || at.Z > b.Z) continue;
                return log[i].Cause;
            }
            return RecordId.None;
        }

        static Int3 Centre(Project p) { return Construction.World(p, p.Plan.Width / 2, 0, p.Plan.Depth / 2); }

        static long Key(Int3 at) { return ((long)at.Y << 40) | ((long)at.Z << 20) | (long)at.X; }

        static ulong Hash(ulong seed, Int3 at) { return StableHash.Combine(seed, (ulong)Key(at)); }
    }

    /// <summary>
    /// Once a day, every standing building checks there is still ground under
    /// it; one that has lost most of it comes down, citing what dug it away. S2T.
    /// </summary>
    public sealed class SupportSystem : ISimSystem
    {
        public static readonly Symbol SystemId = Symbol.For("system.support");

        /// <summary>Below this share of its floor on solid ground, a building falls.</summary>
        public const double Fails = 0.4;

        readonly bool[] _ground;
        readonly MaterialTable _materials;
        readonly DetailModelTable _models;
        readonly ParcelGrid _grid;

        public SupportSystem(bool[] ground, MaterialTable materials, DetailModelTable models, ParcelGrid grid)
        {
            _ground = ground; _materials = materials; _models = models; _grid = grid;
        }

        public Symbol Id { get { return SystemId; } }

        public void Tick(SimWorld world)
        {
            if (!world.Clock.IsFirstTickOfDay) return;
            foreach (Settlement s in world.Settlements)
            {
                var falling = new List<Project>();
                foreach (Project p in s.Projects)
                    if (p.Complete && p.Host == null && Collapse.Support(world, p) < Fails) falling.Add(p);
                foreach (Project p in falling)
                {
                    RecordId why = Collapse.Underminer(world, p);
                    RecordId undermined = world.Annals.Write(world.Clock.Tick, Collapse.UnderminedKind, s.Id,
                                                             Construction.World(p, p.Plan.Width / 2, 0, p.Plan.Depth / 2),
                                                             why.Exists ? why : p.Site.Record);
                    Collapse.BringDown(world, s, p, undermined, _ground, _materials, _models, _grid);
                }
            }
        }
    }
}
