using System.Collections.Generic;
using Godless.Sim.Content;
using Godless.Sim.Core;

namespace Godless.Sim.Build
{
    /// <summary>
    /// A small arithmetic language for grammar content: every size in a
    /// grammar rule is one of these, so a rule can say "a storey is a floor
    /// module" or "the roof rises half the depth times the pitch gene"
    /// without a line of C#. S18.
    ///
    /// Numbers, names (gene.roof_pitch, palette.floor, scope.u, a let),
    /// + - * /, comparisons that give 1 or 0, and lerp, min, max, clamp,
    /// round, floor, ceil, sqrt, abs. Parsed once at load, so a broken
    /// expression is refused with the content, and evaluated with nothing but
    /// the four operations and Sqrt (L2).
    /// </summary>
    public abstract class Expr
    {
        public abstract double Eval(IExprScope scope);

        /// <summary>Every name the expression reads, for load-time checking.</summary>
        public abstract void Names(List<string> into);

        public static Expr Parse(string text)
        {
            var p = new Parser(text);
            Expr e = p.Comparison();
            p.SkipSpace();
            if (!p.AtEnd) throw new ExprException("unexpected '" + p.Rest() + "' in \"" + text + "\"");
            return e;
        }

        // ── nodes ───────────────────────────────────────────────────────────

        sealed class Num : Expr
        {
            readonly double _v;
            public Num(double v) { _v = v; }
            public override double Eval(IExprScope s) { return _v; }
            public override void Names(List<string> into) { }
        }

        sealed class Name : Expr
        {
            readonly string _name;
            public Name(string name) { _name = name; }
            public override double Eval(IExprScope s) { return s.Resolve(_name); }
            public override void Names(List<string> into) { into.Add(_name); }
        }

        sealed class Neg : Expr
        {
            readonly Expr _a;
            public Neg(Expr a) { _a = a; }
            public override double Eval(IExprScope s) { return -_a.Eval(s); }
            public override void Names(List<string> into) { _a.Names(into); }
        }

        sealed class Bin : Expr
        {
            readonly char _op; readonly string _cmp; readonly Expr _a, _b;
            public Bin(char op, string cmp, Expr a, Expr b) { _op = op; _cmp = cmp; _a = a; _b = b; }
            public override void Names(List<string> into) { _a.Names(into); _b.Names(into); }
            public override double Eval(IExprScope s)
            {
                double a = _a.Eval(s), b = _b.Eval(s);
                if (_cmp != null)
                {
                    bool r;
                    switch (_cmp)
                    {
                        case "<": r = a < b; break;
                        case ">": r = a > b; break;
                        case "<=": r = a <= b; break;
                        case ">=": r = a >= b; break;
                        case "==": r = a == b; break;
                        default: r = a != b; break;
                    }
                    return r ? 1.0 : 0.0;
                }
                switch (_op)
                {
                    case '+': return a + b;
                    case '-': return a - b;
                    case '*': return a * b;
                    default: return b == 0.0 ? 0.0 : a / b;   // a zero size, not a NaN wall
                }
            }
        }

        sealed class Call : Expr
        {
            readonly string _fn; readonly Expr[] _args;
            public Call(string fn, Expr[] args) { _fn = fn; _args = args; }
            public override void Names(List<string> into) { foreach (Expr a in _args) a.Names(into); }
            public override double Eval(IExprScope s)
            {
                double a = _args.Length > 0 ? _args[0].Eval(s) : 0.0;
                switch (_fn)
                {
                    case "lerp": return a + (_args[1].Eval(s) - a) * _args[2].Eval(s);
                    case "min": { double b = _args[1].Eval(s); return a < b ? a : b; }
                    case "max": { double b = _args[1].Eval(s); return a > b ? a : b; }
                    case "clamp": { double lo = _args[1].Eval(s), hi = _args[2].Eval(s); return a < lo ? lo : (a > hi ? hi : a); }
                    case "round": return SimMath.Round(a);
                    case "floor": return SimMath.Floor(a);
                    case "ceil": return SimMath.Ceiling(a);
                    case "sqrt": return a > 0.0 ? SimMath.Sqrt(a) : 0.0;
                    default: return a < 0.0 ? -a : a;   // abs
                }
            }

            public static readonly Dictionary<string, int> Arity = new Dictionary<string, int>
            {
                { "lerp", 3 }, { "min", 2 }, { "max", 2 }, { "clamp", 3 },
                { "round", 1 }, { "floor", 1 }, { "ceil", 1 }, { "sqrt", 1 }, { "abs", 1 },
            };
        }

        // ── parser ──────────────────────────────────────────────────────────

        sealed class Parser
        {
            readonly string _s;
            int _i;
            public Parser(string s) { _s = s ?? ""; }

            public bool AtEnd { get { return _i >= _s.Length; } }
            public string Rest() { return _s.Substring(_i); }
            public void SkipSpace() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }

            bool Eat(string tok)
            {
                SkipSpace();
                if (string.CompareOrdinal(_s, _i, tok, 0, tok.Length) != 0) return false;
                _i += tok.Length;
                return true;
            }

            public Expr Comparison()
            {
                Expr left = Sum();
                foreach (string op in new[] { "<=", ">=", "==", "!=", "<", ">" })
                    if (Eat(op)) return new Bin(' ', op, left, Sum());
                return left;
            }

            Expr Sum()
            {
                Expr left = Product();
                while (true)
                {
                    if (Eat("+")) left = new Bin('+', null, left, Product());
                    else if (Eat("-")) left = new Bin('-', null, left, Product());
                    else return left;
                }
            }

            Expr Product()
            {
                Expr left = Unary();
                while (true)
                {
                    if (Eat("*")) left = new Bin('*', null, left, Unary());
                    else if (Eat("/")) left = new Bin('/', null, left, Unary());
                    else return left;
                }
            }

            Expr Unary()
            {
                if (Eat("-")) return new Neg(Unary());
                return Atom();
            }

            Expr Atom()
            {
                SkipSpace();
                if (AtEnd) throw new ExprException("expression ends too early in \"" + _s + "\"");
                if (Eat("("))
                {
                    Expr inner = Comparison();
                    if (!Eat(")")) throw new ExprException("missing ')' in \"" + _s + "\"");
                    return inner;
                }

                char c = _s[_i];
                if (char.IsDigit(c) || c == '.')
                {
                    int start = _i;
                    while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
                    string lexeme = _s.Substring(start, _i - start);
                    double v;
                    if (!DeterministicDecimal.TryParse(lexeme, out v)) throw new ExprException("'" + lexeme + "' is not a number");
                    return new Num(v);
                }

                if (char.IsLetter(c) || c == '_')
                {
                    int start = _i;
                    while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '.')) _i++;
                    string name = _s.Substring(start, _i - start);
                    if (Eat("("))
                    {
                        int arity;
                        if (!Call.Arity.TryGetValue(name, out arity)) throw new ExprException("no function called '" + name + "'");
                        var args = new List<Expr>();
                        if (!Eat(")"))
                        {
                            do args.Add(Comparison()); while (Eat(","));
                            if (!Eat(")")) throw new ExprException("missing ')' after the arguments to " + name);
                        }
                        if (args.Count != arity) throw new ExprException(name + " takes " + arity + " argument(s), not " + args.Count);
                        return new Call(name, args.ToArray());
                    }
                    return new Name(name);
                }
                throw new ExprException("unexpected '" + c + "' in \"" + _s + "\"");
            }
        }
    }

    /// <summary>Where an expression's names come from.</summary>
    public interface IExprScope
    {
        double Resolve(string name);
    }

    public sealed class ExprException : System.Exception
    {
        public ExprException(string message) : base(message) { }
    }
}
