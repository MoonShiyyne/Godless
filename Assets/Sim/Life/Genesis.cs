using Godless.Sim.Content;
using Godless.Sim.Harness;
using Godless.Sim.World;

namespace Godless.Sim.Life
{
    /// <summary>
    /// The systems a world runs, in tick order, added one way everywhere —
    /// the Editor, `sim eval`, the batch harness and the tests — so no tool
    /// runs a different world from the one the player sees. v2 M1.
    /// </summary>
    public static class Genesis
    {
        /// <summary>Ground first (the grid follows the god's hand), then what grows back, then life. Returns the life system, for the god's powers.</summary>
        public static LifeSystem AddSystems(SimWorld world, ContentDatabase content, ParcelGrid grid, ConstraintFields fields, BiomeTable biomes)
        {
            var life = new LifeSystem(grid, biomes, SpeciesTable.FromContent(content, world.Clock.DaysPerYear), LifeRules.FromContent(content));
            world.Add(new GroundSystem(grid, fields, biomes))
                 .Add(new DepositSystem(grid))
                 .Add(life);
            world.Life = life.Life;
            return life;
        }
    }
}
