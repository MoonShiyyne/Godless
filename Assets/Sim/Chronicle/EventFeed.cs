using System.Collections.Generic;
using Godless.Sim.Annals;
using Godless.Sim.Content;
using Godless.Sim.Core;

namespace Godless.Sim.Chronicle
{
    /// <summary>One line the player is told: what happened, where, and how much it matters (1 to 3).</summary>
    public struct FeedItem
    {
        public long Tick;
        public string Text;
        public Int3 Place;
        public int Importance;
        public RecordId Record;
        public Symbol Kind;
    }

    /// <summary>How one kind of record reads to the player, as content declares it.</summary>
    public sealed class FeedLine
    {
        public Symbol Kind { get; internal set; }
        public string Text { get; internal set; }
        public int Importance { get; internal set; }
    }

    /// <summary>
    /// What happened, told to the player as it happens (v2 M0).
    ///
    /// v1 recorded deaths, feasts and towns leaving in its annals and told the
    /// player none of it. The feed reads the annals as they grow and turns
    /// the kinds content names into lines, each with the place it happened so
    /// the camera can go there. Nothing in the simulation reads it back; it is
    /// the player's side of the record, like the chronicle hover will be.
    ///
    /// A line's text may name the record's values: {a} and {b} are its two
    /// numbers, {subject} what it is about, {x} and {z} where.
    ///
    /// The tell: raise a hill and a line says so at once, and clicking it
    /// takes you there.
    /// </summary>
    public sealed class EventFeed
    {
        readonly Dictionary<ulong, FeedLine> _lines = new Dictionary<ulong, FeedLine>();
        readonly List<string> _problems = new List<string>();
        int _read;

        public IReadOnlyList<string> Problems { get { return _problems; } }
        public int LineCount { get { return _lines.Count; } }

        public static EventFeed FromContent(ContentDatabase content)
        {
            var feed = new EventFeed();
            foreach (string id in content.Ids("feed"))
            {
                JsonValue doc = content.Get("feed", id);
                string kind = doc["kind"].AsString("");
                string text = doc["text"].AsString("");
                int importance = doc["importance"].AsInt32(1);
                if (kind.Length == 0) { feed._problems.Add("feed '" + id + "' names no record kind."); continue; }
                if (text.Trim().Length == 0) { feed._problems.Add("feed '" + id + "' has no text."); continue; }
                if (importance < 1 || importance > 3) { feed._problems.Add("feed '" + id + "' has importance " + importance + "; it must be 1, 2 or 3."); continue; }
                Symbol k = Symbol.For(kind);
                if (feed._lines.ContainsKey(k.Hash)) { feed._problems.Add("feed '" + id + "' tells '" + kind + "', which another feed line already tells."); continue; }
                feed._lines[k.Hash] = new FeedLine { Kind = k, Text = text, Importance = importance };
            }
            return feed;
        }

        /// <summary>Whether a kind of record is told to the player.</summary>
        public bool Tells(Symbol kind) { return _lines.ContainsKey(kind.Hash); }

        /// <summary>The records written since the last read that the player is told about, oldest first.</summary>
        public List<FeedItem> Read(Annalist annals)
        {
            var items = new List<FeedItem>();
            for (; _read < annals.Count; _read++)
            {
                AnnalRecord r = annals.Get(new RecordId(_read));
                FeedLine line;
                if (r == null || !_lines.TryGetValue(r.Kind.Hash, out line)) continue;
                items.Add(new FeedItem
                {
                    Tick = r.Tick, Place = r.Place, Importance = line.Importance, Record = r.Id, Kind = r.Kind,
                    Text = Fill(line.Text, r),
                });
            }
            return items;
        }

        /// <summary>Starts reading from the annals as they stand, so a world loaded or scrubbed does not replay its whole past.</summary>
        public void SkipTo(Annalist annals) { _read = annals.Count; }

        static string Fill(string text, AnnalRecord r)
        {
            return text.Replace("{a}", r.ValueA.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       .Replace("{b}", r.ValueB.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       .Replace("{subject}", r.Subject.IsNone ? "" : r.Subject.ToString())
                       .Replace("{x}", r.Place.X.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       .Replace("{z}", r.Place.Z.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
