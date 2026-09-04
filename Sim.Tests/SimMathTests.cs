using System;
using Godless.Sim.Core;
using Xunit;

namespace Godless.Sim.Tests
{
    /// <summary>
    /// SimMath exists to be identical on every runtime, which cannot be
    /// proved from one machine. What can be proved here is that it is
    /// correct — so System.Math is used as the oracle. Sim.Tests is outside
    /// Assets/Sim, so the law guard permits it here and nowhere else.
    /// </summary>
    public class SimMathTests
    {
        const double Tol = 1e-12;

        static void Close(double expected, double actual, string what)
        {
            double err = Math.Abs(expected - actual) / Math.Max(1.0, Math.Abs(expected));
            Assert.True(err < Tol, what + ": expected " + expected + ", got " + actual + ", rel err " + err);
        }

        [Fact]
        public void Exp2_MatchesTheOracleAcrossItsRange()
        {
            for (int i = -2000; i <= 2000; i++)
            {
                double x = i * 0.5;
                Close(Math.Pow(2.0, x), SimMath.Exp2(x), "Exp2(" + x + ")");
            }
            Assert.Equal(1.0, SimMath.Exp2(0.0));
            Assert.Equal(2.0, SimMath.Exp2(1.0));
            Assert.Equal(0.5, SimMath.Exp2(-1.0));
        }

        [Fact]
        public void Log2_MatchesTheOracle_IncludingSubnormals()
        {
            for (int i = 1; i <= 4000; i++)
            {
                double x = i * 0.25;
                Close(Math.Log2(x), SimMath.Log2(x), "Log2(" + x + ")");
            }
            Assert.Equal(0.0, SimMath.Log2(1.0));
            Assert.Equal(1.0, SimMath.Log2(2.0));
            Close(Math.Log2(double.Epsilon), SimMath.Log2(double.Epsilon), "Log2(denormal min)");
            Assert.True(double.IsNegativeInfinity(SimMath.Log2(0.0)));
            Assert.True(double.IsNaN(SimMath.Log2(-1.0)));
        }

        [Fact]
        public void ExpAndLog_RoundTrip()
        {
            for (int i = -500; i <= 500; i++)
            {
                double x = i * 0.1;
                Close(Math.Exp(x), SimMath.Exp(x), "Exp(" + x + ")");
                Close(x, SimMath.Log(SimMath.Exp(x)), "Log(Exp(" + x + "))");
            }
        }

        [Fact]
        public void Pow_MatchesTheOracle_IncludingNegativeBases()
        {
            double[] bases = { 0.1, 0.5, 1.0, 2.0, 7.5, 100.0 };
            double[] exps = { -3.0, -1.5, -0.25, 0.0, 0.5, 1.0, 2.0, 3.7 };
            foreach (double b in bases)
                foreach (double e in exps)
                    Close(Math.Pow(b, e), SimMath.Pow(b, e), "Pow(" + b + "," + e + ")");

            Close(-8.0, SimMath.Pow(-2.0, 3.0), "Pow(-2,3)");
            Close(4.0, SimMath.Pow(-2.0, 2.0), "Pow(-2,2)");
            Assert.True(double.IsNaN(SimMath.Pow(-2.0, 0.5)));
        }

        [Fact]
        public void SinAndCos_MatchTheOracleAcrossSeveralTurns()
        {
            for (int i = -4000; i <= 4000; i++)
            {
                double x = i * 0.01;
                Close(Math.Sin(x), SimMath.Sin(x), "Sin(" + x + ")");
                Close(Math.Cos(x), SimMath.Cos(x), "Cos(" + x + ")");
            }
        }

        [Fact]
        public void SinAndCos_HoldTheIdentityAtScale()
        {
            for (int i = -500; i <= 500; i++)
            {
                double x = i * 1.7;
                double s = SimMath.Sin(x), c = SimMath.Cos(x);
                Close(1.0, s * s + c * c, "sin^2+cos^2 at " + x);
            }
        }

        [Fact]
        public void Atan2_MatchesTheOracleInEveryQuadrant()
        {
            double[] vals = { -8.0, -1.0, -0.3, 0.0, 0.3, 1.0, 8.0 };
            foreach (double y in vals)
                foreach (double x in vals)
                {
                    if (x == 0.0 && y == 0.0) continue;
                    Close(Math.Atan2(y, x), SimMath.Atan2(y, x), "Atan2(" + y + "," + x + ")");
                }
        }

        /// <summary>
        /// The shape scars and salience both use. Part 08: intensity halves
        /// over roughly fifteen years but never reaches zero.
        /// </summary>
        [Fact]
        public void Decay_HalvesOnItsHalfLife()
        {
            Close(255.0, SimMath.Decay(255.0, 0.0, 15.0), "no time passed");
            Close(127.5, SimMath.Decay(255.0, 15.0, 15.0), "one half-life");
            Close(63.75, SimMath.Decay(255.0, 30.0, 15.0), "two half-lives");
            Assert.True(SimMath.Decay(255.0, 400.0, 15.0) > 0.0, "decay approaches zero without reaching it");
        }

        [Fact]
        public void IsPureAndRepeatable()
        {
            for (int i = 0; i < 100; i++)
            {
                double x = i * 0.37;
                Assert.Equal(SimMath.Sin(x), SimMath.Sin(x));
                Assert.Equal(SimMath.Exp(x), SimMath.Exp(x));
                Assert.Equal(SimMath.Log2(x + 1.0), SimMath.Log2(x + 1.0));
            }
        }

        [Fact]
        public void HelpersBehave()
        {
            Assert.Equal(-3.0, SimMath.Floor(-2.5));
            Assert.Equal(-2.0, SimMath.Ceiling(-2.5));
            Assert.Equal(3.0, SimMath.Round(2.5));
            Assert.Equal(-2.0, SimMath.Round(-2.5));  // Floor(x+0.5), documented
            Assert.Equal(5.0, SimMath.Clamp(9.0, 1.0, 5.0));
            Assert.Equal(1.0, SimMath.Clamp01(4.0));
            Assert.Equal(7.5, SimMath.Lerp(5.0, 10.0, 0.5));
        }
    }
}
