using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Core;

namespace Godless.Sim.Harness
{
    /// <summary>
    /// One act of the god: raise the ground, send rain, smite a creature. It
    /// writes its own god.* record and cites it on everything it changes (L3).
    /// </summary>
    public interface IGodCommand
    {
        /// <summary>The record kind it writes, e.g. god.raised-ground.</summary>
        Symbol Kind { get; }

        /// <summary>Does the act at the world's present tick. Returns its record.</summary>
        RecordId Apply(SimWorld world);
    }

    /// <summary>A command as it went: when it was asked for, when it landed, and its record.</summary>
    public struct CommandEntry
    {
        public IGodCommand Command;
        public long SubmittedAt, AppliedAt;
        public RecordId Record;
    }

    /// <summary>
    /// Every god power goes through here (v2 M0). A power is submitted from
    /// anywhere — the Editor, a tool, a test — and lands at the start of the
    /// world's next step, before any system runs, so every system that step
    /// already sees it: one step, a tenth of a second at 1x. v1's brush wrote
    /// the ground and nothing that planned on it heard for a year; the queue
    /// is the one door, so nothing can go round it.
    ///
    /// While the world is paused the Editor can land what is waiting at once
    /// with <see cref="ApplyNow"/>: the act lands in the present tick.
    ///
    /// Everything applied is kept in order, which is the god's half of a
    /// replay: the same seed and the same commands at the same ticks give the
    /// same world (L2).
    /// </summary>
    public sealed class CommandQueue
    {
        readonly List<CommandEntry> _pending = new List<CommandEntry>();
        readonly List<CommandEntry> _applied = new List<CommandEntry>();

        public int Pending { get { return _pending.Count; } }

        /// <summary>Every command applied, in the order it landed.</summary>
        public IReadOnlyList<CommandEntry> Applied { get { return _applied; } }

        public void Submit(IGodCommand command, long now)
        {
            if (command == null) return;
            _pending.Add(new CommandEntry { Command = command, SubmittedAt = now, AppliedAt = -1 });
        }

        /// <summary>Lands everything waiting, in the order it was submitted, at the world's present tick.</summary>
        public int ApplyNow(SimWorld world)
        {
            // Taken off the queue before any lands: an act on a closed present
            // runs the world's next step first, and that step must not land
            // them a second time.
            var landing = new List<CommandEntry>(_pending);
            _pending.Clear();
            for (int i = 0; i < landing.Count; i++)
            {
                CommandEntry e = landing[i];
                e.Record = e.Command.Apply(world);
                e.AppliedAt = world.Clock.Tick;
                _applied.Add(e);
            }
            return landing.Count;
        }
    }
}
