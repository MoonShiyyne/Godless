using System;
using System.Collections.Generic;
using System.IO;

namespace Godless.Sim.Content
{
    /// <summary>
    /// Where content comes from. An interface rather than a path, so the sim
    /// never assumes a filesystem: the Editor can serve from its own asset
    /// pipeline, a test can serve from memory, and neither needs a temp dir.
    ///
    /// Implementations MUST return ids and paths in ordinal-sorted order.
    /// Directory enumeration order is filesystem-dependent, and a world that
    /// differs between two machines because one of them formatted its disk
    /// differently is the exact class of bug L2 exists to prevent.
    /// </summary>
    public interface IContentSource
    {
        IReadOnlyList<string> ListModIds();
        IReadOnlyList<string> ListDocuments(string modId);
        string ReadText(string modId, string documentPath);
    }

    /// <summary>An in-memory source. The one tests use.</summary>
    public sealed class MemoryContentSource : IContentSource
    {
        readonly SortedDictionary<string, SortedDictionary<string, string>> _mods =
            new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);

        public MemoryContentSource Add(string modId, string documentPath, string text)
        {
            SortedDictionary<string, string> docs;
            if (!_mods.TryGetValue(modId, out docs))
            {
                docs = new SortedDictionary<string, string>(StringComparer.Ordinal);
                _mods.Add(modId, docs);
            }
            docs[documentPath] = text;
            return this;
        }

        public IReadOnlyList<string> ListModIds() { return new List<string>(_mods.Keys); }

        public IReadOnlyList<string> ListDocuments(string modId)
        {
            SortedDictionary<string, string> docs;
            return _mods.TryGetValue(modId, out docs) ? new List<string>(docs.Keys) : new List<string>();
        }

        public string ReadText(string modId, string documentPath)
        {
            SortedDictionary<string, string> docs;
            if (_mods.TryGetValue(modId, out docs))
            {
                string text;
                if (docs.TryGetValue(documentPath, out text)) return text;
            }
            throw new FileNotFoundException("no document '" + documentPath + "' in mod '" + modId + "'");
        }
    }

    /// <summary>
    /// A directory of mod folders — Assets/Content in the shipped game. Uses
    /// System.IO, which is not a Unity type and so is legal under L1.
    ///
    /// A missing root is not an error. L5's tell is that deleting the base
    /// content leaves a game that boots with nothing to build, so an absent
    /// directory must load as zero mods rather than throw.
    /// </summary>
    public sealed class DirectoryContentSource : IContentSource
    {
        readonly string _root;

        public DirectoryContentSource(string root) { _root = root; }

        public IReadOnlyList<string> ListModIds()
        {
            var ids = new List<string>();
            if (!Directory.Exists(_root)) return ids;
            foreach (string dir in Directory.GetDirectories(_root))
                ids.Add(Path.GetFileName(dir));
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }

        public IReadOnlyList<string> ListDocuments(string modId)
        {
            var docs = new List<string>();
            string modRoot = Path.Combine(_root, modId);
            if (!Directory.Exists(modRoot)) return docs;

            foreach (string file in Directory.GetFiles(modRoot, "*.json", SearchOption.AllDirectories))
            {
                string rel = file.Substring(modRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                docs.Add(rel.Replace(Path.DirectorySeparatorChar, '/'));
            }
            docs.Sort(StringComparer.Ordinal);
            return docs;
        }

        public string ReadText(string modId, string documentPath)
        {
            return File.ReadAllText(Path.Combine(_root, modId, documentPath.Replace('/', Path.DirectorySeparatorChar)));
        }
    }
}
