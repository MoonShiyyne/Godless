using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Culture;
using Godless.Sim.World;

namespace Godless.Sim.Build
{
    /// <summary>
    /// A split grammar, loaded from content, that turns a genome into a
    /// blueprint. S18.
    ///
    /// Part 05's structural grammar: "divides the volume into rooms, floors,
    /// and openings against functional requirements". The rules are content
    /// (L5), in a small shape language over boxes:
    ///
    ///   split   cut a box along an axis into parts, absolute or "~weight"
    ///   repeat  cut it into equal parts of about a size, or a count of them
    ///   comp    take its faces — front, back, ends, sides, top, bottom, inside
    ///   roof    a gable along the box's long axis, pitch and overhang
    ///   posts   columns at the corners and every so often along the edges
    ///   centre  a smaller box in the middle, on the floor
    ///   fill    give every cell a role
    ///   choose  one rule or another, on an expression
    ///
    /// Axes are box-relative: u runs along a box's length, w across it, v up.
    /// A wall slab from comp has its own u along the wall, so one facade rule
    /// serves every face. Every size is an expression over the genes, the
    /// palette's modules, the lot, the intent and the box, so the numbers that
    /// make a culture's buildings are the only thing that differs between two
    /// cultures — the vocabulary is shared, the sentences are not (Part 01).
    /// </summary>
    public sealed class Grammar
    {
        /// <summary>Horizontal margin around the building's box, for eaves.</summary>
        public const int Margin = 3;

        const int MaxDepth = 48;
        const int MaxSteps = 200000;

        internal Dictionary<string, Op> Rules;
        internal List<Symbol> RolesWritten;
        internal Dictionary<string, Expr> Lets;
        internal List<string> LetOrder;

        public Symbol Id { get; internal set; }
        public string Name { get; internal set; }

        /// <summary>The intent kind this grammar answers, by name.</summary>
        public string Builds { get; internal set; }
        public string Tell { get; internal set; }
        public string Axiom { get; internal set; }

        /// <summary>Every role this grammar can write, in stable-hash order. What a tileset has to answer for.</summary>
        public IReadOnlyList<Symbol> Roles { get { return RolesWritten; } }

        /// <summary>
        /// Runs the grammar for one genome on a lot. The lot is the most room
        /// the building may take; the grammar decides how much of it to use.
        /// </summary>
        public Blueprint Build(Genome genome, Palette palette, int lotWidth, int lotDepth, int budgetVoxels)
        {
            var run = new Run(this, genome, palette, lotWidth, lotDepth, budgetVoxels);
            return run.Execute();
        }

        /// <summary>
        /// Runs the grammar with some of its lets decided from outside (S2O): a
        /// dwelling program that knows the family needs nine beds sets
        /// "capacity" to nine, and the grammar builds the rest of the house
        /// around that. A let not in <paramref name="overrides"/> is computed as usual.
        /// </summary>
        public Blueprint Build(Genome genome, Palette palette, int lotWidth, int lotDepth, int budgetVoxels,
                               IReadOnlyDictionary<string, double> overrides)
        {
            var run = new Run(this, genome, palette, lotWidth, lotDepth, budgetVoxels) { Overrides = overrides };
            return run.Execute();
        }

        /// <summary>
        /// A part of a building rather than a building (S2P): "wing" or
        /// "storey". Empty for a whole building. A part is never what an intent
        /// asks for; it is what a dwelling program adds to a home that stands.
        /// </summary>
        public string PartOf { get; internal set; }

        /// <summary>A let's value for a genome, without building. For tools and tests.</summary>
        public double Evaluate(string let, Genome genome, Palette palette, int lotWidth, int lotDepth, int budgetVoxels)
        {
            return new Run(this, genome, palette, lotWidth, lotDepth, budgetVoxels).Resolve(let);
        }

        // ── the rule tree ───────────────────────────────────────────────────

        internal enum OpKind { Sequence, Ref, Split, Repeat, Comp, Roof, Posts, Centre, Fill, Choose }

        internal sealed class Op
        {
            public OpKind Kind;
            public string Ref;                    // Ref
            public List<Op> Children;             // Sequence
            public char Axis;                     // Split, Repeat
            public List<Part> Parts;              // Split
            public Expr Size, Count;              // Repeat
            public Expr Thickness;                // Comp
            public Op Front, Back, Ends, Sides, Top, Bottom, Inside;   // Comp
            public Expr Pitch, Overhang;          // Roof
            public Symbol Role, Gable;            // Roof, Posts, Fill
            public Expr Spacing;                  // Posts
            public Expr U, V, W;                  // Centre
            public Op Rule, Then, Else;           // Repeat, Centre, Choose
            public Expr If;                       // Choose
        }

        internal sealed class Part
        {
            public Expr Size;       // absolute
            public double Weight;   // relative, when Size is null
            public Op Rule;
        }

        // ── one build ───────────────────────────────────────────────────────

        struct Box
        {
            public int X, Y, Z, U, V, W;
            public bool UAlongX;

            /// <summary>Which face of a comp this box came from: 0 front, 1 back, 2 and 3 the ends; -1 none.</summary>
            public int Face;
        }

        sealed class Run : IExprScope
        {
            readonly Grammar _g;
            readonly Genome _genome;
            readonly Palette _palette;
            readonly int _lotWidth, _lotDepth, _budget;
            public IReadOnlyDictionary<string, double> Overrides;
            readonly Dictionary<string, double> _memo = new Dictionary<string, double>();
            readonly HashSet<string> _resolving = new HashSet<string>();
            Blueprint _bp;
            Box _scope;
            int _index, _count, _steps;

            public Run(Grammar g, Genome genome, Palette palette, int lotWidth, int lotDepth, int budget)
            {
                _g = g; _genome = genome; _palette = palette;
                _lotWidth = lotWidth; _lotDepth = lotDepth; _budget = budget;
            }

            public Blueprint Execute()
            {
                int width = Clamp(Round(Resolve("width")), 3, _lotWidth);
                int depth = Clamp(Round(Resolve("depth")), 3, _lotDepth);
                int height = Clamp(Round(Resolve("height")), 1, 150);

                _bp = new Blueprint(width + 2 * Margin, height, depth + 2 * Margin);
                if (_g.Lets.ContainsKey("capacity")) _bp.Capacity = Clamp(Round(Resolve("capacity")), 0, 1000);

                var root = new Box { X = Margin, Y = 0, Z = Margin, U = width, V = height, W = depth, UAlongX = true, Face = -1 };
                Apply(_g.Rules[_g.Axiom], root, 0, 1, 0);
                return _bp;
            }

            public double Resolve(string name)
            {
                switch (name)
                {
                    case "scope.u": return _scope.U;
                    case "scope.v": return _scope.V;
                    case "scope.w": return _scope.W;
                    case "face": return _scope.Face;
                    case "index": return _index;
                    case "count": return _count;
                    case "lot.width": return _lotWidth;
                    case "lot.depth": return _lotDepth;
                    case "intent.budget": return _budget;
                    case "palette.floor": return _palette.FloorHeightVoxels;
                    case "palette.bay": return _palette.BayWidthVoxels;
                    case "palette.door.w": return _palette.DoorWidthVoxels;
                    case "palette.door.h": return _palette.DoorHeightVoxels;
                }

                if (name.StartsWith("gene.", System.StringComparison.Ordinal))
                {
                    double v = _genome[Symbol.For(name)];
                    return double.IsNaN(v) ? 0.0 : v;
                }

                double given;
                if (Overrides != null && Overrides.TryGetValue(name, out given)) return given;

                double memo;
                if (_memo.TryGetValue(name, out memo)) return memo;
                Expr let;
                if (!_g.Lets.TryGetValue(name, out let)) return 0.0;   // refused at load; defensive only
                if (!_resolving.Add(name)) return 0.0;                   // cycles refused at load
                double value = let.Eval(this);
                _resolving.Remove(name);
                _memo[name] = value;
                return value;
            }

            double Eval(Expr e, Box box, int index, int count)
            {
                Box saveScope = _scope; int saveIndex = _index, saveCount = _count;
                _scope = box; _index = index; _count = count;
                double v = e.Eval(this);
                _scope = saveScope; _index = saveIndex; _count = saveCount;
                return v;
            }

            int Size(Expr e, Box box, int index, int count)
            {
                int v = Round(Eval(e, box, index, count));
                return v < 0 ? 0 : v;
            }

            void Apply(Op op, Box box, int index, int count, int depth)
            {
                if (op == null || box.U <= 0 || box.V <= 0 || box.W <= 0) return;
                if (depth > MaxDepth || ++_steps > MaxSteps)
                    throw new GrammarException("grammar '" + _g.Name + "' recursed or ran too long");

                switch (op.Kind)
                {
                    case OpKind.Sequence:
                        foreach (Op child in op.Children) Apply(child, box, index, count, depth + 1);
                        return;
                    case OpKind.Ref:
                        Apply(_g.Rules[op.Ref], box, index, count, depth + 1);
                        return;
                    case OpKind.Fill:
                        for (int v = 0; v < box.V; v++)
                            for (int w = 0; w < box.W; w++)
                                for (int u = 0; u < box.U; u++) Put(box, u, v, w, op.Role);
                        return;
                    case OpKind.Choose:
                        Apply(Eval(op.If, box, index, count) > 0.0 ? op.Then : op.Else, box, index, count, depth + 1);
                        return;
                    case OpKind.Split: DoSplit(op, box, index, count, depth); return;
                    case OpKind.Repeat: DoRepeat(op, box, index, count, depth); return;
                    case OpKind.Comp: DoComp(op, box, index, count, depth); return;
                    case OpKind.Roof: DoRoof(op, box, index, count); return;
                    case OpKind.Posts: DoPosts(op, box, index, count); return;
                    case OpKind.Centre:
                    {
                        int u = System.Math.Min(Size(op.U, box, index, count), box.U);
                        int w = System.Math.Min(Size(op.W, box, index, count), box.W);
                        int v = op.V == null ? box.V : System.Math.Min(Size(op.V, box, index, count), box.V);
                        Box c = Sub(box, 'u', (box.U - u) / 2, u);
                        c = Sub(c, 'w', (box.W - w) / 2, w);
                        c = Sub(c, 'v', 0, v);
                        Apply(op.Rule, c, index, count, depth + 1);
                        return;
                    }
                }
            }

            void DoSplit(Op op, Box box, int index, int count, int depth)
            {
                int length = Length(box, op.Axis);
                var sizes = new int[op.Parts.Count];
                int used = 0;
                double weights = 0.0;
                for (int i = 0; i < op.Parts.Count; i++)
                {
                    Part p = op.Parts[i];
                    if (p.Size == null) { weights += p.Weight; continue; }
                    int s = Size(p.Size, box, i, op.Parts.Count);
                    if (used + s > length) s = length - used;   // absolute parts that do not fit are cut
                    sizes[i] = s;
                    used += s;
                }

                // The remainder goes to the "~weight" parts, whole cells first
                // by weight and any leftover to the earliest.
                int rest = length - used;
                if (weights > 0.0 && rest > 0)
                {
                    int given = 0;
                    for (int i = 0; i < op.Parts.Count; i++)
                        if (op.Parts[i].Size == null) { sizes[i] = (int)(rest * op.Parts[i].Weight / weights); given += sizes[i]; }
                    for (int i = 0; i < op.Parts.Count && given < rest; i++)
                        if (op.Parts[i].Size == null && op.Parts[i].Weight > 0.0) { sizes[i]++; given++; }
                }

                int offset = 0;
                for (int i = 0; i < op.Parts.Count; i++)
                {
                    if (sizes[i] > 0) Apply(op.Parts[i].Rule, Sub(box, op.Axis, offset, sizes[i]), i, op.Parts.Count, depth + 1);
                    offset += sizes[i];
                }
            }

            void DoRepeat(Op op, Box box, int index, int count, int depth)
            {
                int length = Length(box, op.Axis);
                if (length <= 0) return;
                int n = op.Count != null
                    ? Size(op.Count, box, index, count)
                    : Round(length / (double)System.Math.Max(1, Size(op.Size, box, index, count)));
                if (n < 1) n = 1;
                if (n > length) n = length;

                int each = length / n, extra = length % n, offset = 0;
                for (int i = 0; i < n; i++)
                {
                    int s = each + (i < extra ? 1 : 0);
                    Apply(op.Rule, Sub(box, op.Axis, offset, s), i, n, depth + 1);
                    offset += s;
                }
            }

            /// <summary>
            /// Faces keep the index and count of the box they came from — so a
            /// facade still knows which storey it is on — and add their own
            /// face number, readable as "face".
            /// </summary>
            void DoComp(Op op, Box box, int index, int count, int depth)
            {
                int t = op.Thickness == null ? 1 : System.Math.Max(1, Size(op.Thickness, box, index, count));
                Op front = op.Front ?? op.Sides, back = op.Back ?? op.Sides, ends = op.Ends ?? op.Sides;

                if (op.Bottom != null) Apply(op.Bottom, Face(Sub(box, 'v', 0, System.Math.Min(t, box.V)), -1), index, count, depth + 1);
                if (op.Top != null) Apply(op.Top, Face(Sub(box, 'v', System.Math.Max(0, box.V - t), System.Math.Min(t, box.V)), -1), index, count, depth + 1);

                int v0 = op.Bottom != null ? t : 0;
                int v1 = box.V - (op.Top != null ? t : 0);
                Box walls = Sub(box, 'v', v0, System.Math.Max(0, v1 - v0));

                if (front != null) Apply(front, Face(Sub(walls, 'w', 0, System.Math.Min(t, box.W)), 0), index, count, depth + 1);
                if (back != null && box.W > t) Apply(back, Face(Sub(walls, 'w', box.W - t, t), 1), index, count, depth + 1);
                if (ends != null && box.W > 2 * t)
                {
                    Box inner = Sub(walls, 'w', t, box.W - 2 * t);
                    Apply(ends, Face(Turn(Sub(inner, 'u', 0, System.Math.Min(t, box.U))), 2), index, count, depth + 1);
                    if (box.U > t) Apply(ends, Face(Turn(Sub(inner, 'u', box.U - t, t)), 3), index, count, depth + 1);
                }
                if (op.Inside != null && box.U > 2 * t && box.W > 2 * t)
                    Apply(op.Inside, Face(Sub(Sub(walls, 'u', t, box.U - 2 * t), 'w', t, box.W - 2 * t), -1), index, count, depth + 1);
            }

            static Box Face(Box b, int face) { b.Face = face; return b; }

            /// <summary>
            /// A gable: the ridge along u, falling to eaves on both sides of w
            /// at <c>pitch</c> cells up per cell in, with <c>overhang</c> cells
            /// of eave. The triangles under it at each end take the gable role.
            /// Flat when the pitch is zero. Stepped, because the medium is — and
            /// Part 19 says a stepped roofline reads better than a smooth one.
            /// </summary>
            void DoRoof(Op op, Box box, int index, int count)
            {
                double pitch = Eval(op.Pitch, box, index, count);
                if (pitch < 0.0) pitch = 0.0;
                int o = op.Overhang == null ? 0 : Size(op.Overhang, box, index, count);

                for (int k = -o; k < box.W + o; k++)
                {
                    int e = System.Math.Min(k + o, box.W - 1 + o - k);
                    int top = (int)SimMath.Floor(e * pitch);
                    int bottom = e == 0 ? top : (int)SimMath.Floor((e - 1) * pitch) + 1;
                    if (bottom > top) bottom = top;
                    for (int v = bottom; v <= top && v < box.V; v++)
                        for (int u = -o; u < box.U + o; u++) Put(box, u, v, k, op.Role);

                    if (!op.Gable.IsNone && k >= 0 && k < box.W)
                        for (int v = 0; v < bottom && v < box.V; v++)
                        {
                            Put(box, 0, v, k, op.Gable);
                            Put(box, box.U - 1, v, k, op.Gable);
                        }
                }
            }

            void DoPosts(Op op, Box box, int index, int count)
            {
                int spacing = System.Math.Max(1, Size(op.Spacing, box, index, count));
                for (int u = 0; u < box.U; u++)
                {
                    bool atU = u % spacing == 0 || u == box.U - 1;
                    for (int w = 0; w < box.W; w++)
                    {
                        bool atW = w % spacing == 0 || w == box.W - 1;
                        bool edgeU = u == 0 || u == box.U - 1, edgeW = w == 0 || w == box.W - 1;
                        if (!((edgeW && atU) || (edgeU && atW))) continue;
                        for (int v = 0; v < box.V; v++) Put(box, u, v, w, op.Role);
                    }
                }
            }

            // ── boxes ───────────────────────────────────────────────────────

            static int Length(Box b, char axis) { return axis == 'u' ? b.U : (axis == 'v' ? b.V : b.W); }

            static Box Sub(Box b, char axis, int offset, int size)
            {
                Box c = b;
                if (axis == 'v') { c.Y += offset; c.V = size; }
                else if (axis == 'u') { if (b.UAlongX) c.X += offset; else c.Z += offset; c.U = size; }
                else { if (b.UAlongX) c.Z += offset; else c.X += offset; c.W = size; }
                return c;
            }

            /// <summary>Same cells, with u and w exchanged: an end wall's own length becomes its u.</summary>
            static Box Turn(Box b)
            {
                Box c = b;
                c.U = b.W; c.W = b.U; c.UAlongX = !b.UAlongX;
                return c;
            }

            void Put(Box b, int u, int v, int w, Symbol role)
            {
                int x = b.UAlongX ? b.X + u : b.X + w;
                int z = b.UAlongX ? b.Z + w : b.Z + u;
                _bp.Set(x, b.Y + v, z, role);
            }
        }

        static int Round(double v) { return (int)SimMath.Round(v); }
        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }

    public sealed class GrammarException : System.Exception
    {
        public GrammarException(string message) : base(message) { }
    }
}
