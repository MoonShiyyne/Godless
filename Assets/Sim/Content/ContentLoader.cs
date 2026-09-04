using System;
using System.Collections.Generic;

namespace Godless.Sim.Content
{
    public sealed class LoadResult
    {
        public ContentDatabase Database { get; internal set; }
        public IReadOnlyList<ModManifest> LoadOrder { get; internal set; }
        public IReadOnlyList<string> Warnings { get; internal set; }

        /// <summary>
        /// True if any loaded mod declared itself a code mod. Part 22: this
        /// voids the determinism guarantee and must be written into the save.
        /// </summary>
        public bool ContainsCodeMod { get; internal set; }
    }

    /// <summary>
    /// Loads every mod in a source into one database. S02.
    ///
    /// Runs before any content exists, so that no content can be born
    /// hardcoded — which is the only moment L5 is free. The base game is a
    /// mod named "base" and takes no special path through this class.
    /// </summary>
    public static class ContentLoader
    {
        public static LoadResult Load(IContentSource source)
        {
            var warnings = new List<string>();
            var manifests = ReadManifests(source, warnings);
            var order = ResolveLoadOrder(manifests);

            var db = new ContentDatabase();
            var patches = new List<ContentPatch>();

            // Pass one: documents, in load order, later mods overwriting earlier.
            foreach (ModManifest mod in order)
            {
                foreach (string path in source.ListDocuments(mod.Id))
                {
                    if (string.Equals(path, ModManifest.FileName, StringComparison.Ordinal)) continue;

                    JsonValue root;
                    try { root = JsonParser.Parse(source.ReadText(mod.Id, path)); }
                    catch (JsonParseException e)
                    { throw new ContentException(mod.Id + "/" + path + ": " + e.Message); }

                    JsonValue patchList = root["$patch"];
                    if (patchList.Kind == JsonKind.Array)
                    {
                        for (int i = 0; i < patchList.Count; i++)
                            patches.Add(ContentPatch.Parse(patchList[i], mod.Id));
                        continue;
                    }

                    string type = root["type"].AsString(null);
                    string id = root["id"].AsString(null);
                    if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(id))
                        throw new ContentException(mod.Id + "/" + path +
                            ": a content document needs a 'type' and an 'id' (or a '$patch' array)");

                    db.Put(type, id, root, mod.Id);
                }
            }

            // Pass two: patches, after every document exists, so a mod can
            // patch a mod that loads after it.
            foreach (ContentPatch patch in patches)
            {
                JsonValue target;
                if (!db.TryGet(patch.TargetType, patch.TargetId, out target))
                {
                    warnings.Add("mod '" + patch.SourceMod + "' patches '" + patch.TargetKey +
                                 "', which no loaded mod defines — skipped");
                    continue;
                }
                db.Put(patch.TargetType, patch.TargetId, patch.ApplyTo(target),
                       db.OwnerOf(patch.TargetType, patch.TargetId));
            }

            bool anyCodeMod = false;
            foreach (ModManifest m in order) if (m.IsCodeMod) anyCodeMod = true;

            return new LoadResult
            {
                Database = db,
                LoadOrder = order,
                Warnings = warnings,
                ContainsCodeMod = anyCodeMod,
            };
        }

        static List<ModManifest> ReadManifests(IContentSource source, List<string> warnings)
        {
            var manifests = new List<ModManifest>();
            foreach (string modId in source.ListModIds())
            {
                string text;
                try { text = source.ReadText(modId, ModManifest.FileName); }
                catch (Exception)
                {
                    warnings.Add("'" + modId + "' has no " + ModManifest.FileName + " — not a mod, skipped");
                    continue;
                }
                manifests.Add(ModManifest.Parse(modId, text));
            }
            return manifests;
        }

        /// <summary>
        /// Deterministic topological sort. Ids are visited in ordinal order
        /// and dependencies in declared order, so the result depends only on
        /// the content, never on how the source enumerated it.
        /// </summary>
        internal static List<ModManifest> ResolveLoadOrder(List<ModManifest> manifests)
        {
            var byId = new SortedDictionary<string, ModManifest>(StringComparer.Ordinal);
            foreach (ModManifest m in manifests)
            {
                if (byId.ContainsKey(m.Id))
                    throw new ContentException("two mods claim the id '" + m.Id + "'");
                byId.Add(m.Id, m);
            }

            foreach (ModManifest m in manifests)
                foreach (string required in m.Requires)
                    if (!byId.ContainsKey(required))
                        throw new ContentException("mod '" + m.Id + "' requires '" + required + "', which is not installed");

            var order = new List<ModManifest>();
            var state = new SortedDictionary<string, int>(StringComparer.Ordinal); // 0 unvisited, 1 visiting, 2 done
            var stack = new List<string>();

            foreach (var pair in byId) Visit(pair.Key, byId, state, order, stack);
            return order;
        }

        static void Visit(string id,
                          SortedDictionary<string, ModManifest> byId,
                          SortedDictionary<string, int> state,
                          List<ModManifest> order,
                          List<string> stack)
        {
            int s;
            state.TryGetValue(id, out s);
            if (s == 2) return;
            if (s == 1)
            {
                stack.Add(id);
                throw new ContentException("mods depend on each other in a cycle: " + string.Join(" -> ", stack.ToArray()));
            }

            state[id] = 1;
            stack.Add(id);

            ModManifest m = byId[id];
            foreach (string dep in m.Requires) Visit(dep, byId, state, order, stack);
            foreach (string dep in m.LoadAfter) if (byId.ContainsKey(dep)) Visit(dep, byId, state, order, stack);

            stack.RemoveAt(stack.Count - 1);
            state[id] = 2;
            order.Add(m);
        }
    }
}
