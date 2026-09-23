using Godless.Sim.Annals;
using Godless.Sim.Core;
using Godless.Sim.Harness;

namespace Godless.Sim.World
{
    /// <summary>
    /// Raise the ground in a dome round a column: the god's first terrain
    /// power, through the command queue (v2 M0). The planning grid reads the
    /// new ground at the start of the next step.
    /// </summary>
    public sealed class RaiseGround : IGodCommand
    {
        readonly GodHand _hand;
        public Int3 At { get; private set; }
        public int Radius { get; private set; }
        public int Strength { get; private set; }

        public RaiseGround(GodHand hand, Int3 at, int radius, int strength)
        {
            _hand = hand; At = at; Radius = radius; Strength = strength;
        }

        public Symbol Kind { get { return GodHand.RaisedKind; } }
        public RecordId Apply(SimWorld world) { return _hand.Raise(At, Radius, Strength); }
    }

    /// <summary>Lower the ground round a column; below the sea it fills with water (v2 M0).</summary>
    public sealed class LowerGround : IGodCommand
    {
        readonly GodHand _hand;
        public Int3 At { get; private set; }
        public int Radius { get; private set; }
        public int Depth { get; private set; }

        public LowerGround(GodHand hand, Int3 at, int radius, int depth)
        {
            _hand = hand; At = at; Radius = radius; Depth = depth;
        }

        public Symbol Kind { get { return GodHand.LoweredKind; } }
        public RecordId Apply(SimWorld world) { return _hand.Lower(At, Radius, Depth); }
    }
}
