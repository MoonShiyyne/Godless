using System;
using System.Collections.Generic;

namespace Godless.Sim.Content
{
    /// <summary>
    /// A mod's declaration of itself. The base game ships as one of these
    /// (L5): there is no privileged code path, so "base" is a mod that
    /// happens to be authored by us.
    /// </summary>
    public sealed class ModManifest
    {
        public const string FileName = "mod.json";

        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Version { get; private set; }

        /// <summary>Hard dependencies. A missing one is an error.</summary>
        public IReadOnlyList<string> Requires { get; private set; }

        /// <summary>Soft ordering. Absent ids are ignored.</summary>
        public IReadOnlyList<string> LoadAfter { get; private set; }

        /// <summary>
        /// Part 22's two tiers. A code mod voids the determinism guarantee
        /// and must be flagged in the save, so the flag is carried from the
        /// manifest rather than inferred later.
        /// </summary>
        public bool IsCodeMod { get; private set; }

        ModManifest() { }

        public static ModManifest Parse(string modId, string json)
        {
            JsonValue root = JsonParser.Parse(json);

            string declared = root["id"].AsString(null);
            if (!string.IsNullOrEmpty(declared) && !string.Equals(declared, modId, StringComparison.Ordinal))
                throw new ContentException("mod '" + modId + "' declares id '" + declared +
                                           "'; the folder name is the id and they must agree");

            return new ModManifest
            {
                Id = modId,
                Name = root["name"].AsString(modId),
                Version = root["version"].AsString("0.0.0"),
                Requires = StringList(root["requires"]),
                LoadAfter = StringList(root["loadAfter"]),
                IsCodeMod = root["codeMod"].AsBool(false),
            };
        }

        static IReadOnlyList<string> StringList(JsonValue v)
        {
            var list = new List<string>();
            for (int i = 0; i < v.Count; i++)
            {
                string s = v[i].AsString(null);
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
            return list;
        }
    }

    public sealed class ContentException : Exception
    {
        public ContentException(string message) : base(message) { }
    }
}
