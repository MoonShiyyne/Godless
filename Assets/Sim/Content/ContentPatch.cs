using System;
using System.Collections.Generic;
using System.Globalization;

namespace Godless.Sim.Content
{
    public enum PatchOp { Replace, Add, Remove }

    /// <summary>
    /// A path-addressed edit to a document that some other mod owns.
    ///
    /// This is the whole point of the mod architecture, and Part 22 is blunt
    /// about why: RimWorld's real lesson is not XML, it is that mods can
    /// patch other mods' data by path without editing their files. Two mods
    /// that both adjust a biome must be able to coexist without either
    /// shipping a copy of the other's content.
    ///
    /// Paths are slash-separated member names, with integer indices into
    /// arrays and "-" meaning the end of an array:
    ///     materials/0        the first material
    ///     materials/-        append here
    ///     rainfall           a scalar member
    /// </summary>
    public sealed class ContentPatch
    {
        public PatchOp Op { get; private set; }
        public string TargetType { get; private set; }
        public string TargetId { get; private set; }
        public string Path { get; private set; }
        public JsonValue Value { get; private set; }
        public string SourceMod { get; private set; }

        ContentPatch() { }

        public static ContentPatch Parse(JsonValue v, string sourceMod)
        {
            string opText = v["op"].AsString("replace");
            PatchOp op;
            switch (opText)
            {
                case "replace": op = PatchOp.Replace; break;
                case "add": op = PatchOp.Add; break;
                case "remove": op = PatchOp.Remove; break;
                default: throw new ContentException(
                    "mod '" + sourceMod + "' uses unknown patch op '" + opText + "'");
            }

            string target = v["target"].AsString(null);
            if (string.IsNullOrEmpty(target) || target.IndexOf('/') < 0)
                throw new ContentException(
                    "mod '" + sourceMod + "' has a patch with no valid 'target' (expected \"type/id\")");

            int slash = target.IndexOf('/');
            return new ContentPatch
            {
                Op = op,
                TargetType = target.Substring(0, slash),
                TargetId = target.Substring(slash + 1),
                Path = v["path"].AsString(""),
                Value = v["value"],
                SourceMod = sourceMod,
            };
        }

        public string TargetKey { get { return TargetType + "/" + TargetId; } }

        public JsonValue ApplyTo(JsonValue document)
        {
            var segments = SplitPath(Path);
            return Apply(document, segments, 0);
        }

        static List<string> SplitPath(string path)
        {
            var parts = new List<string>();
            if (string.IsNullOrEmpty(path)) return parts;
            foreach (string p in path.Split('/'))
                if (p.Length > 0) parts.Add(p);
            return parts;
        }

        JsonValue Apply(JsonValue node, List<string> path, int depth)
        {
            if (depth == path.Count)
            {
                if (Op == PatchOp.Remove)
                    throw new ContentException("mod '" + SourceMod + "' cannot remove a whole document via a patch path");
                return Value;
            }

            string seg = path[depth];
            bool last = depth == path.Count - 1;

            if (node.Kind == JsonKind.Array || seg == "-" || IsIndex(seg))
            {
                if (node.Kind != JsonKind.Array && !(Op == PatchOp.Add && seg == "-"))
                    throw new ContentException("mod '" + SourceMod + "' patched '" + TargetKey +
                                               "' at '" + Path + "': expected an array at '" + seg + "'");

                if (seg == "-")
                {
                    if (!last || Op != PatchOp.Add)
                        throw new ContentException("mod '" + SourceMod + "' used '-' outside an append");
                    return node.WithItemInserted(node.Count, Value);
                }

                int index = int.Parse(seg, NumberStyles.Integer, CultureInfo.InvariantCulture);
                if (index < 0 || index >= node.Count)
                    throw new ContentException("mod '" + SourceMod + "' patched '" + TargetKey +
                                               "' at index " + seg + ", which is out of range");

                if (last)
                {
                    switch (Op)
                    {
                        case PatchOp.Remove: return node.WithoutItem(index);
                        case PatchOp.Add: return node.WithItemInserted(index, Value);
                        default: return node.WithItem(index, Value);
                    }
                }
                return node.WithItem(index, Apply(node[index], path, depth + 1));
            }

            if (last)
            {
                switch (Op)
                {
                    case PatchOp.Remove:
                        if (node.IndexOfKey(seg) < 0)
                            throw new ContentException("mod '" + SourceMod + "' removed '" + Path +
                                                       "' from '" + TargetKey + "', which is not there");
                        return node.WithoutMember(seg);
                    case PatchOp.Add:
                        if (node.IndexOfKey(seg) >= 0)
                            throw new ContentException("mod '" + SourceMod + "' added '" + Path + "' to '" +
                                                       TargetKey + "', which already exists — use replace");
                        return node.WithMember(seg, Value);
                    default:
                        if (node.IndexOfKey(seg) < 0)
                            throw new ContentException("mod '" + SourceMod + "' replaced '" + Path + "' in '" +
                                                       TargetKey + "', which is not there — use add");
                        return node.WithMember(seg, Value);
                }
            }

            JsonValue child = node[seg];
            if (child.IsNull && node.IndexOfKey(seg) < 0)
                throw new ContentException("mod '" + SourceMod + "' patched '" + TargetKey + "' at '" + Path +
                                           "', but '" + seg + "' does not exist");
            return node.WithMember(seg, Apply(child, path, depth + 1));
        }

        static bool IsIndex(string s)
        {
            if (s.Length == 0) return false;
            for (int i = 0; i < s.Length; i++)
                if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }
    }
}
