using System;

namespace Godless.Sim.Headless
{
    /// <summary>
    /// Entry point for the headless harness (S08).
    ///
    /// Target shape, from design doc Part 27:
    ///     sim run --seed 0..200 --years 300 --assert "median pop >= 40 by year 60"
    ///
    /// Argument parsing and the assertion vocabulary arrive with S08 proper.
    /// This exists so the layering guard and the build have something to prove.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            Console.WriteLine("godless sim harness — no systems registered yet (stratum 0)");
            return 0;
        }
    }
}
