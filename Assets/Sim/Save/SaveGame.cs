using System.Collections.Generic;
using System.IO;
using Godless.Sim.Annals;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Deltas;
using Godless.Sim.Harness;
using Godless.Sim.Voxels;
using Godless.Sim.World;

namespace Godless.Sim.Save
{
    public sealed class SaveException : System.Exception
    {
        public SaveException(string message) : base(message) { }
    }

    /// <summary>
    /// Reading and writing a world. S0C.
    ///
    /// A save is not a dump of every voxel. The simulation is deterministic,
    /// so the island is a function of its seed, and everything that happened
    /// afterwards is already recorded as deltas (S04). The save is therefore
    /// the seed, the clock, the delta log and the annals — and loading is
    /// regenerate, replay, restore. That keeps a save proportional to the
    /// history rather than to the map.
    ///
    /// Two things in the header are the point of the format:
    ///
    /// The voxel type table. Runtime voxel ids are assigned by sorting the
    /// declared symbols, so they shift the moment a mod adds a type. The save
    /// stores the id-to-symbol mapping it was written with and remaps on
    /// load. Without it, installing a mod would turn every wall in an
    /// existing world into a different material — silently.
    ///
    /// The world digest. Load reconstructs and then checks its own work, so
    /// a generator change between versions produces a clear refusal instead
    /// of a world that is subtly not the one you saved.
    /// </summary>
    public static class SaveGame
    {
        const uint Magic = 0x534C4447; // "GDLS" little-endian
        const uint Version = 1;

        public static void Write(Stream stream, SimWorld world, bool containsCodeMod = false)
        {
            var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);

            w.Write(Magic);
            w.Write(Version);
            w.Write(world.Seed);
            w.Write(world.Clock.Tick);
            w.Write(world.Content.Digest());
            w.Write(containsCodeMod);
            w.Write(world.Island != null);
            w.Write(world.Voxels.Store.Digest());
            w.Write(world.Annals.Digest());

            // Voxel type table: what each id meant when this was written.
            w.Write(world.VoxelTypes.Count);
            for (int id = 0; id < world.VoxelTypes.Count; id++)
                w.Write(world.VoxelTypes.SymbolOf((ushort)id).Hash);

            IReadOnlyList<AnnalRecord> records = world.Annals.All();
            w.Write(records.Count);
            foreach (AnnalRecord r in records)
            {
                w.Write(r.Tick);
                w.Write(r.Kind.Hash);
                w.Write(r.Subject.Hash);
                w.Write(r.Place.X); w.Write(r.Place.Y); w.Write(r.Place.Z);
                w.Write(r.Cause.Index);
                w.Write(r.ValueA); w.Write(r.ValueB);
                w.Write((ushort)r.Participants.Count);
                for (int i = 0; i < r.Participants.Count; i++) w.Write(r.Participants[i].Hash);
            }

            IReadOnlyList<VoxelDelta> deltas = world.Voxels.Log.All();
            w.Write(deltas.Count);
            foreach (VoxelDelta d in deltas)
            {
                w.Write(d.Tick);
                w.Write(d.ChunkIndex);
                w.Write(d.VoxelIndex);
                w.Write(d.OldType);
                w.Write(d.NewType);
                w.Write(d.Cause.Index);
            }

            w.Flush();
        }

        /// <summary>
        /// Rebuilds the world: regenerate the island from the seed, remap the
        /// saved voxel ids through their symbols, replay the deltas, restore
        /// the record, then verify against the digests the save carried.
        /// </summary>
        public static SimWorld Read(Stream stream, ContentDatabase content,
                                    BiomeTable biomes, VoxelTypes voxelTypes)
        {
            var r = new BinaryReader(stream, System.Text.Encoding.UTF8, true);

            if (r.ReadUInt32() != Magic) throw new SaveException("not a Godless save");

            uint version = r.ReadUInt32();
            if (version != Version)
                throw new SaveException("save is version " + version + " and this build reads version " + Version);

            ulong seed = r.ReadUInt64();
            long tick = r.ReadInt64();
            ulong contentDigest = r.ReadUInt64();
            bool codeMod = r.ReadBoolean();
            bool hasIsland = r.ReadBoolean();
            ulong expectedWorld = r.ReadUInt64();
            ulong expectedAnnals = r.ReadUInt64();

            int savedTypeCount = r.ReadInt32();
            var savedTypes = new ulong[savedTypeCount];
            for (int i = 0; i < savedTypeCount; i++) savedTypes[i] = r.ReadUInt64();

            // Remap saved ids to current ids through their symbols. This is
            // the whole reason the table is in the file.
            var remap = new ushort[savedTypeCount];
            for (int savedId = 0; savedId < savedTypeCount; savedId++)
            {
                Symbol type = Symbol.FromHash(savedTypes[savedId]);
                ushort current;
                if (!voxelTypes.TryGetId(type, out current))
                    throw new SaveException("this save uses voxel type " + type
                        + ", which the loaded content does not declare. A mod that was "
                        + "installed when it was written is missing.");
                remap[savedId] = current;
            }

            var world = new SimWorld(seed, content, voxelTypes);
            if (hasIsland)
                world.Island = IslandGenerator.Generate(world.Voxels.Store, world.Streams, biomes, voxelTypes);

            int recordCount = r.ReadInt32();
            for (int i = 0; i < recordCount; i++)
            {
                long rTick = r.ReadInt64();
                Symbol kind = Symbol.FromHash(r.ReadUInt64());
                Symbol subject = Symbol.FromHash(r.ReadUInt64());
                var place = new Int3(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                int cause = r.ReadInt32();
                long a = r.ReadInt64(), b = r.ReadInt64();

                int participantCount = r.ReadUInt16();
                var participants = new Symbol[participantCount];
                for (int p = 0; p < participantCount; p++) participants[p] = Symbol.FromHash(r.ReadUInt64());

                world.Annals.Write(rTick, kind, subject, place, new RecordId(cause), a, b, participants);
            }

            int deltaCount = r.ReadInt32();
            for (int i = 0; i < deltaCount; i++)
            {
                long dTick = r.ReadInt64();
                ushort chunk = r.ReadUInt16();
                ushort voxel = r.ReadUInt16();
                ushort oldType = r.ReadUInt16();
                ushort newType = r.ReadUInt16();
                int cause = r.ReadInt32();

                world.Voxels.ReplaySaved(dTick, chunk, voxel,
                                         Remap(remap, oldType), Remap(remap, newType),
                                         new RecordId(cause));
            }

            world.Clock.RewindTo(tick);

            if (content.Digest() != contentDigest)
                throw new SaveException("the content has changed since this save was written. "
                    + "Loading it would give you a different world wearing the same name.");

            ulong actualWorld = world.Voxels.Store.Digest();
            if (actualWorld != expectedWorld)
                throw new SaveException("the reconstructed world does not match the one that was saved "
                    + "(expected " + expectedWorld.ToString("x16") + ", got " + actualWorld.ToString("x16")
                    + "). The generator has changed since this save was written.");

            if (world.Annals.Digest() != expectedAnnals)
                throw new SaveException("the restored record does not match the one that was saved.");

            world.ContainsCodeMod = codeMod;
            return world;
        }

        static ushort Remap(ushort[] table, ushort savedId)
        {
            return savedId < table.Length ? table[savedId] : VoxelTypes.AirId;
        }
    }
}
