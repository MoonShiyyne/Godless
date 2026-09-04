using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Godless.Sim.Tests
{
    /// <summary>
    /// L1, checked at the assembly level.
    ///
    /// Tools/check-laws.sh greps the source; this catches the same violation
    /// arriving transitively through a package reference, which a grep cannot
    /// see. Two cheap instruments, two different failure modes.
    ///
    /// S01's determinism test — same seed, 300 years, byte-identical annals —
    /// replaces this file's placeholder status when the tick loop lands.
    /// </summary>
    public class LayeringTests
    {
        [Fact]
        public void SimAssembly_ReferencesNothingFromUnity()
        {
            var sim = typeof(Godless.Sim.Headless.AssemblyMarker).Assembly;

            var unity = sim.GetReferencedAssemblies()
                           .Select(a => a.Name ?? string.Empty)
                           .Where(n => n.StartsWith("Unity", StringComparison.OrdinalIgnoreCase)
                                    || n.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase)
                                    || n.StartsWith("UnityEditor", StringComparison.OrdinalIgnoreCase))
                           .ToArray();

            Assert.True(unity.Length == 0,
                "L1 violated — the sim assembly references: " + string.Join(", ", unity));
        }
    }
}
