using System.Collections.Generic;
using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using Xunit;

namespace Godless.Sim.Tests
{
    public class ClaimTests
    {
        static ContentDatabase Load()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Assets", "Content"))) dir = dir.Parent;
            return ContentLoader.Load(new DirectoryContentSource(Path.Combine(dir.FullName, "Assets", "Content"))).Database;
        }

        static readonly ContentDatabase Content = Load();

        sealed class Village
        {
            public SimWorld World;
            public Settlement Town;
            public ParcelGrid Grid;

            public Village(ulong seed = 7, int stocked = 6000)
            {
                BiomeTable biomes = BiomeTable.FromContent(Content);
                VoxelTypes types = VoxelTypes.FromContent(Content);
                World = new SimWorld(seed, Content, types);
                World.Island = TestIslands.Generate(World.Voxels.Store, World.Streams, biomes, types);

                ConstraintFields fields;
                Grid = Founding.Survey(World, Content, biomes, out fields);
                int px, pz;
                Assert.True(Founding.StandInSite(Grid, World.Island, biomes, Symbol.None, out px, out pz));

                Town = Founding.Begin(World, Content, Grid, biomes, "test", 20, px, pz, null);
                MaterialTable materials = MaterialTable.FromContent(Content, biomes);
                for (int m = 0; m < materials.Count; m++) if (Town.Catchment.Offers(m)) Town.Stock.Add(m, stocked);
                Founding.AddSystems(World, Content, Grid, fields, biomes);
                World.BeginHistory();
            }

            public void Live(int days) { for (int i = 0; i < days * 4; i++) World.Tick(); }
        }

        static readonly Village Built = MakeVillage();

        static Village MakeVillage()
        {
            var v = new Village();
            v.Live(400);
            return v;
        }

        [Fact]
        public void TheVillageBuiltSomething()
        {
            Assert.True(Built.Town.Projects.Count >= 2, Built.Town.Projects.Count + " houses");
        }

        /// <summary>
        /// S1B: no house grows into its neighbour. Two claims of different
        /// owners never touch, even at a corner, so there is daylight and a way
        /// between them.
        /// </summary>
        [Fact]
        public void HousesDoNotTouchEachOther()
        {
            var owners = new Dictionary<int, int>();
            foreach (int parcel in Built.Town.Claims)
                owners[parcel] = Built.Town.ClaimOn(parcel % ParcelGrid.Width, parcel / ParcelGrid.Width).Index;

            foreach (KeyValuePair<int, int> a in owners)
            {
                int ax = a.Key % ParcelGrid.Width, az = a.Key / ParcelGrid.Width;
                foreach (KeyValuePair<int, int> b in owners)
                {
                    if (a.Value == b.Value) continue;
                    int bx = b.Key % ParcelGrid.Width, bz = b.Key / ParcelGrid.Width;
                    int dx = System.Math.Abs(ax - bx), dz = System.Math.Abs(az - bz);
                    Assert.True(System.Math.Max(dx, dz) > 1,
                                "claims of different owners touch at (" + ax + "," + az + ") and (" + bx + "," + bz + ")");
                }
            }
        }

        /// <summary>And there is always a way from the fire to every house that goes through nobody's home.</summary>
        [Fact]
        public void EveryHouseCanBeReachedFromTheFire()
        {
            foreach (Project project in Built.Town.Projects)
            {
                bool found = false;
                for (int dz = -1; dz <= project.Site.ParcelsDeep && !found; dz++)
                    for (int dx = -1; dx <= project.Site.ParcelsWide && !found; dx++)
                    {
                        int px = project.Site.ParcelX + dx, pz = project.Site.ParcelZ + dz;
                        // The ground kept round the fire is walked across (S2Z).
                        if ((Built.Town.IsClaimed(px, pz) && !Built.Town.IsCommons(px, pz)) || !ParcelGrid.InBounds(px, pz)) continue;
                        List<int> path = ParcelPath.Find(Built.Grid, Built.Town.HearthParcelX, Built.Town.HearthParcelZ, px, pz,
                                                         (x, z) => Built.Town.IsClaimed(x, z) && !Built.Town.IsCommons(x, z));
                        if (path.Count > 0) found = true;
                    }
                Assert.True(found, "no way from the fire to the house at (" + project.Site.ParcelX + ", " + project.Site.ParcelZ + ")");
            }
        }

        [Fact]
        public void NobodyBuildsOnTheFire()
        {
            // Held by the founding, or since S2Z by the commons kept round the fire the founders lit.
            RecordId owner = Built.Town.ClaimOn(Built.Town.HearthParcelX, Built.Town.HearthParcelZ);
            Assert.True(owner == Built.Town.Founded || Built.Town.IsCommons(Built.Town.HearthParcelX, Built.Town.HearthParcelZ));
            foreach (Project project in Built.Town.Projects)
            {
                bool covers = Built.Town.HearthParcelX >= project.Site.ParcelX
                           && Built.Town.HearthParcelX < project.Site.ParcelX + project.Site.ParcelsWide
                           && Built.Town.HearthParcelZ >= project.Site.ParcelZ
                           && Built.Town.HearthParcelZ < project.Site.ParcelZ + project.Site.ParcelsDeep;
                Assert.False(covers, "a house was built on the hearth");
            }
        }

        /// <summary>With the ground all taken, a settlement asks and gets no site rather than stacking houses.</summary>
        [Fact]
        public void WithNoRoomLeftNothingIsSited()
        {
            var village = new Village();
            for (int pz = 0; pz < ParcelGrid.Depth; pz++)
                for (int px = 0; px < ParcelGrid.Width; px++)
                    if (!village.Town.IsClaimed(px, pz)) village.Town.ClaimParcel(px, pz, village.Town.Founded);

            village.Live(60);
            Assert.Empty(village.Town.Projects);
            Assert.NotEmpty(village.Town.Intents.Intents);   // it did ask
        }
    }
}
