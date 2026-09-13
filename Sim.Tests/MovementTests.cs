using System.IO;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Drives;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    /// <summary>
    /// People in the world. S2G.
    ///
    /// The tell: at dawn the village walks out to the edge of its clearing,
    /// the builders climb onto the walls, and at night whoever has no roof
    /// lies down round the fire.
    /// </summary>
    public class MovementTests
    {
        readonly Xunit.Abstractions.ITestOutputHelper _out;
        public MovementTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

        static ContentDatabase Shipped()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Shipped();

        static SimWorld Settled(ulong seed)
        {
            WorldChoice choice = WorldChoice.Pick(Content, "green-shore");
            SimWorld world = SettlementInvariants.Settled(Content, choice)(seed);
            Assert.NotEmpty(world.Settlements);
            return world;
        }

        static double Distance(int ax, int az, int bx, int bz)
        {
            double dx = ax - bx, dz = az - bz;
            return System.Math.Sqrt(dx * dx + dz * dz);
        }

        [Fact]
        public void GatherersStandAtWhatTheyAreWorking()
        {
            SimWorld world = Settled(7);
            Settlement s = world.Settlements[0];
            DepositMap deposits = world.Island.Deposits;

            int checkedWorkers = 0;
            for (int day = 0; day < 40 && checkedWorkers < 10; day++)
            {
                for (int t = 0; t < world.Clock.TicksPerDay; t++) world.Tick();
                if (world.Clock.TickOfDay == world.Clock.TicksPerDay - 1) continue;

                foreach (Agent a in s.People)
                {
                    if (a.WorkingAt < 0 || !a.Doing.StartsWith("felling")) continue;
                    if (a.GoalX == s.Hearth.X && a.GoalZ == s.Hearth.Z) continue;   // the tree's side was sea
                    // Arrived, or on the way: never somewhere unrelated.
                    double toWork = Distance(a.GoalX, a.GoalZ, deposits.X(a.WorkingAt), deposits.Z(a.WorkingAt));
                    Assert.True(toWork <= 2.0, a.Doing + " is headed " + toWork.ToString("0") + " voxels from the tree");
                    checkedWorkers++;
                }
            }
            _out.WriteLine(checkedWorkers + " fellers checked");
            Assert.True(checkedWorkers > 0, "nobody felled anything in forty days");
        }

        [Fact]
        public void AtNightThoseWithoutARoofLieDownRoundTheFire()
        {
            SimWorld world = Settled(7);
            Settlement s = world.Settlements[0];
            while (world.Clock.TickOfDay != world.Clock.TicksPerDay - 2) world.Tick();
            world.Tick();   // the night tick
            Assert.Equal(world.Clock.TicksPerDay - 1, world.Clock.TickOfDay);

            int open = 0;
            foreach (Agent a in s.People)
            {
                if (a.ShelteredLastNight) continue;
                open++;
                Assert.StartsWith("asleep", a.Doing);
                Assert.True(Distance(a.GoalX, a.GoalZ, s.Hearth.X, s.Hearth.Z) <= 12.0);
            }
            Assert.True(open > 0, "the first night everyone is in the open");
        }

        [Fact]
        public void WalkingWearsTheGround()
        {
            SimWorld world = Settled(7);
            for (int i = 0; i < 20 * world.Clock.TicksPerDay; i++) world.Tick();
            Settlement s = world.Settlements[0];
            Assert.NotNull(s.Traffic);
            Assert.True(s.Traffic.Max() > 0.0);
        }

        [Fact]
        public void TheSameSeedPutsEveryoneInTheSamePlace()
        {
            SimWorld a = Settled(5), b = Settled(5);
            for (int i = 0; i < 30 * a.Clock.TicksPerDay; i++) { a.Tick(); b.Tick(); }
            Assert.Equal(a.Settlements[0].Digest(), b.Settlements[0].Digest());
            for (int i = 0; i < a.Settlements[0].People.Count; i++)
            {
                Assert.Equal(a.Settlements[0].People[i].X, b.Settlements[0].People[i].X);
                Assert.Equal(a.Settlements[0].People[i].Z, b.Settlements[0].People[i].Z);
            }
        }

        [Fact]
        public void AWalkNeverEndsInTheSea()
        {
            SimWorld world = Settled(3);
            Settlement s = world.Settlements[0];
            Agent a = s.People[0];

            // Aim straight out to sea from the fire: the walker stops at the shore.
            int seaX = s.Hearth.X, seaZ = s.Hearth.Z;
            while (seaX > 0 && world.Island.IsLand(seaX, seaZ)) seaX--;
            seaX = System.Math.Max(0, seaX - 20);
            for (int i = 0; i < 40; i++) Movement.Toward(a, null, seaX, seaZ, null, world.Island);
            Assert.True(world.Island.IsLand(a.X, a.Z));
        }

        [Fact]
        public void PathsAreTheSameWithReusedBuffers()
        {
            SimWorld world = Settled(7);
            ConstraintFields fields;
            ParcelGrid grid = Founding.Survey(world, Content, BiomeTable.FromContent(Content), out fields);
            Settlement s = world.Settlements[0];
            var first = ParcelPath.Find(grid, s.HearthParcelX, s.HearthParcelZ, s.HearthParcelX + 12, s.HearthParcelZ + 9);
            ParcelPath.Find(grid, s.HearthParcelX, s.HearthParcelZ, s.HearthParcelX - 20, s.HearthParcelZ + 3);
            var again = ParcelPath.Find(grid, s.HearthParcelX, s.HearthParcelZ, s.HearthParcelX + 12, s.HearthParcelZ + 9);
            Assert.Equal(first, again);
        }
    }
}
