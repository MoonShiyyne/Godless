using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Culture;

namespace Godless.Sim.Build
{
    /// <summary>
    /// Every grammar the loaded content declares, each checked in full before
    /// it is allowed to build anything. S18.
    ///
    /// A grammar that names a gene nobody declared, a rule nobody wrote, or a
    /// let that depends on itself is refused at load with a sentence saying
    /// which — a modder should read that in `sim content`, not discover it as
    /// a village of empty boxes.
    /// </summary>
    public sealed class GrammarTable
    {
        static readonly HashSet<string> Fixed = new HashSet<string>
        {
            "lot.width", "lot.depth", "intent.budget",
            "palette.floor", "palette.bay", "palette.door.w", "palette.door.h",
        };

        static readonly HashSet<string> ScopeNames = new HashSet<string> { "scope.u", "scope.v", "scope.w", "index", "count", "face" };

        readonly Grammar[] _grammars;
        readonly List<string> _problems;

        GrammarTable(Grammar[] grammars, List<string> problems) { _grammars = grammars; _problems = problems; }

        public int Count { get { return _grammars.Length; } }
        public Grammar this[int index] { get { return _grammars[index]; } }
        public IReadOnlyList<Grammar> All { get { return _grammars; } }
        public IReadOnlyList<string> Problems { get { return _problems; } }

        /// <summary>The grammar that builds this intent kind, or null. The first in stable-hash order.</summary>
        public Grammar For(string intentKind)
        {
            foreach (Grammar g in _grammars) if (g.Builds == intentKind && string.IsNullOrEmpty(g.PartOf)) return g;
            return null;
        }

        /// <summary>The grammar for a part — a wing, a storey — added to a building of this kind (S2P), or null.</summary>
        public Grammar PartFor(string intentKind, string part)
        {
            foreach (Grammar g in _grammars) if (g.Builds == intentKind && g.PartOf == part) return g;
            return null;
        }

        public static GrammarTable FromContent(ContentDatabase content, GeneTable genes, System.Collections.Generic.ICollection<string> builds = null)
        {
            var problems = new List<string>();
            var loaded = new List<Grammar>();

            foreach (string id in content.Ids("grammar"))
            {
                var loader = new Loader(id, genes);
                Grammar g = loader.Load(content.Get("grammar", id), builds);
                if (loader.Fault != null) { problems.Add("grammar '" + id + "' " + loader.Fault + "."); continue; }
                loaded.Add(g);
            }

            loaded.Sort((a, b) => a.Id.CompareTo(b.Id));
            return new GrammarTable(loaded.ToArray(), problems);
        }

        sealed class Loader
        {
            readonly string _id;
            readonly GeneTable _genes;
            readonly List<string> _ruleRefs = new List<string>();
            readonly List<string> _ruleNames = new List<string>();   // names read inside rules
            readonly List<Symbol> _roles = new List<Symbol>();       // roles the rules can write
            public string Fault;

            public Loader(string id, GeneTable genes) { _id = id; _genes = genes; }

            void Fail(string why) { if (Fault == null) Fault = why; }

            public Grammar Load(JsonValue doc, System.Collections.Generic.ICollection<string> builds)
            {
                var g = new Grammar
                {
                    Id = Symbol.For("grammar." + _id),
                    Name = _id,
                    Builds = doc["builds"].AsString(""),
                    PartOf = doc["part"].AsString(""),
                    Tell = doc["tell"].AsString("").Trim(),
                    Axiom = doc["axiom"].AsString("Lot"),
                    Rules = new Dictionary<string, Grammar.Op>(),
                    Lets = new Dictionary<string, Expr>(),
                    LetOrder = new List<string>(),
                    RolesWritten = new List<Symbol>(),
                };

                if (g.Tell.Length == 0) Fail("declares no tell");
                if (builds != null && !builds.Contains(g.Builds)) Fail("builds '" + g.Builds + "', which nothing asks for");

                JsonValue lets = doc["let"];
                for (int i = 0; i < lets.Keys.Count && Fault == null; i++)
                {
                    string name = lets.Keys[i];
                    g.Lets[name] = ExprOf(lets[name], "let '" + name + "'");
                    g.LetOrder.Add(name);
                }
                foreach (string required in new[] { "width", "depth", "height" })
                    if (!g.Lets.ContainsKey(required)) Fail("has no let '" + required + "'; the building's box needs one");

                JsonValue rules = doc["rules"];
                for (int i = 0; i < rules.Keys.Count && Fault == null; i++)
                    g.Rules[rules.Keys[i]] = OpOf(rules[rules.Keys[i]], rules.Keys[i]);

                if (Fault != null) return g;
                if (!g.Rules.ContainsKey(g.Axiom)) Fail("starts from rule '" + g.Axiom + "', which it does not define");
                foreach (string r in _ruleRefs) if (!g.Rules.ContainsKey(r)) Fail("refers to rule '" + r + "', which it does not define");

                // Names inside rules may read the box; names inside lets may not,
                // because a let is one number for the whole building.
                foreach (string n in _ruleNames) CheckName(n, g, true);
                foreach (string let in g.LetOrder)
                {
                    var names = new List<string>();
                    g.Lets[let].Names(names);
                    foreach (string n in names) CheckName(n, g, false);
                }
                CheckCycles(g);
                _roles.Sort((a, b) => a.CompareTo(b));
                g.RolesWritten = _roles;
                return g;
            }


            void CheckName(string n, Grammar g, bool inRule)
            {
                if (Fixed.Contains(n) || g.Lets.ContainsKey(n)) return;
                if (ScopeNames.Contains(n)) { if (!inRule) Fail("uses '" + n + "' in a let, where there is no box to read"); return; }
                if (n.StartsWith("gene.", System.StringComparison.Ordinal))
                {
                    if (_genes.IndexOf(Symbol.For(n)) < 0) Fail("reads '" + n + "', which no gene declares");
                    return;
                }
                Fail("reads '" + n + "', which is not a let, a gene or a known name");
            }

            void CheckCycles(Grammar g)
            {
                var state = new Dictionary<string, int>();   // 1 visiting, 2 done
                foreach (string let in g.LetOrder) Visit(let, g, state, new List<string>());
            }

            void Visit(string let, Grammar g, Dictionary<string, int> state, List<string> path)
            {
                int s;
                state.TryGetValue(let, out s);
                if (s == 2 || Fault != null) return;
                if (s == 1) { Fail("has lets that depend on each other in a circle: " + string.Join(" -> ", path) + " -> " + let); return; }
                state[let] = 1;
                path.Add(let);
                var names = new List<string>();
                g.Lets[let].Names(names);
                foreach (string n in names) if (g.Lets.ContainsKey(n)) Visit(n, g, state, path);
                path.RemoveAt(path.Count - 1);
                state[let] = 2;
            }

            Expr ExprOf(JsonValue v, string where)
            {
                string text = v.Kind == JsonKind.Number ? v.NumberLexeme : v.AsString(null);
                if (text == null) { Fail("has no expression for " + where); return null; }
                try { return Expr.Parse(text); }
                catch (ExprException e) { Fail("has a broken expression for " + where + ": " + e.Message); return null; }
            }

            Expr RuleExpr(JsonValue v, string where)
            {
                if (v.IsNull) return null;
                Expr e = ExprOf(v, where);
                if (e != null) e.Names(_ruleNames);
                return e;
            }

            Grammar.Op OpOf(JsonValue v, string rule)
            {
                if (Fault != null) return null;
                switch (v.Kind)
                {
                    case JsonKind.Null: return null;
                    case JsonKind.String:
                        _ruleRefs.Add(v.AsString(""));
                        return new Grammar.Op { Kind = Grammar.OpKind.Ref, Ref = v.AsString("") };
                    case JsonKind.Array:
                    {
                        var seq = new Grammar.Op { Kind = Grammar.OpKind.Sequence, Children = new List<Grammar.Op>() };
                        for (int i = 0; i < v.Count; i++) { Grammar.Op c = OpOf(v[i], rule); if (c != null) seq.Children.Add(c); }
                        return seq;
                    }
                    case JsonKind.Object: break;
                    default: Fail("has something that is not a rule in '" + rule + "'"); return null;
                }

                string op = v["op"].AsString("");
                string where = "'" + op + "' in rule '" + rule + "'";
                var o = new Grammar.Op();
                switch (op)
                {
                    case "split":
                        o.Kind = Grammar.OpKind.Split;
                        o.Axis = AxisOf(v, where);
                        o.Parts = new List<Grammar.Part>();
                        JsonValue parts = v["parts"];
                        for (int i = 0; i < parts.Count; i++)
                        {
                            JsonValue p = parts[i];
                            var part = new Grammar.Part { Rule = OpOf(p["rule"], rule) };
                            string size = p["size"].Kind == JsonKind.String ? p["size"].AsString("") : null;
                            if (size != null && size.StartsWith("~", System.StringComparison.Ordinal))
                            {
                                double w;
                                if (!DeterministicDecimal.TryParse(size.Substring(1).Trim(), out w) || w < 0.0)
                                    Fail("has a bad weight '" + size + "' in " + where);
                                part.Weight = w;
                            }
                            else part.Size = RuleExpr(p["size"], where);
                            o.Parts.Add(part);
                        }
                        break;
                    case "repeat":
                        o.Kind = Grammar.OpKind.Repeat;
                        o.Axis = AxisOf(v, where);
                        o.Size = RuleExpr(v["size"], where);
                        o.Count = RuleExpr(v["count"], where);
                        if (o.Size == null && o.Count == null) Fail("needs a size or a count for " + where);
                        o.Rule = OpOf(v["rule"], rule);
                        break;
                    case "comp":
                        o.Kind = Grammar.OpKind.Comp;
                        o.Thickness = RuleExpr(v["thickness"], where);
                        o.Front = OpOf(v["front"], rule); o.Back = OpOf(v["back"], rule);
                        o.Ends = OpOf(v["ends"], rule); o.Sides = OpOf(v["sides"], rule);
                        o.Top = OpOf(v["top"], rule); o.Bottom = OpOf(v["bottom"], rule);
                        o.Inside = OpOf(v["inside"], rule);
                        break;
                    case "roof":
                        o.Kind = Grammar.OpKind.Roof;
                        o.Pitch = RuleExpr(v["pitch"], where);
                        if (o.Pitch == null) Fail("needs a pitch for " + where);
                        o.Overhang = RuleExpr(v["overhang"], where);
                        o.Role = RoleOf(v["role"].AsString("roof"));
                        o.Gable = RoleOf(v["gable"].AsString(null));
                        break;
                    case "posts":
                        o.Kind = Grammar.OpKind.Posts;
                        o.Spacing = RuleExpr(v["spacing"], where);
                        if (o.Spacing == null) Fail("needs a spacing for " + where);
                        o.Role = RoleOf(v["role"].AsString("post"));
                        break;
                    case "centre":
                        o.Kind = Grammar.OpKind.Centre;
                        o.U = RuleExpr(v["u"], where); o.V = RuleExpr(v["v"], where); o.W = RuleExpr(v["w"], where);
                        if (o.U == null || o.W == null) Fail("needs u and w for " + where);
                        o.Rule = OpOf(v["rule"], rule);
                        break;
                    case "fill":
                        o.Kind = Grammar.OpKind.Fill;
                        o.Role = RoleOf(v["role"].AsString(null));
                        if (o.Role.IsNone) Fail("needs a role for " + where);
                        break;
                    case "choose":
                        o.Kind = Grammar.OpKind.Choose;
                        o.If = RuleExpr(v["if"], where);
                        if (o.If == null) Fail("needs an 'if' for " + where);
                        o.Then = OpOf(v["then"], rule);
                        o.Else = OpOf(v["else"], rule);
                        break;
                    default:
                        Fail("uses operation '" + op + "' in rule '" + rule + "', which the grammar does not know");
                        return null;
                }
                return o;
            }

            char AxisOf(JsonValue v, string where)
            {
                string a = v["axis"].AsString("");
                if (a == "u" || a == "v" || a == "w") return a[0];
                Fail("needs axis u, v or w for " + where);
                return 'u';
            }

            Symbol RoleOf(string name)
            {
                if (string.IsNullOrEmpty(name)) return Symbol.None;
                Symbol role = Symbol.For("role." + name);
                if (!_roles.Contains(role)) _roles.Add(role);
                return role;
            }
        }
    }
}
