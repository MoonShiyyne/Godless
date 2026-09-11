using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Godless.Sim.Content
{
    public enum JsonKind { Null, Bool, Number, String, Array, Object }

    /// <summary>
    /// A minimal JSON value model and parser, written here rather than taken
    /// from a package for two reasons. L1: Unity's JsonUtility is a Unity
    /// type and cannot be referenced from the sim. And a shared dependency
    /// resolved separately by the Editor and by dotnet is a second place for
    /// the two builds to disagree, which is the failure mode netstandard2.1
    /// was chosen to avoid.
    ///
    /// Objects preserve document order rather than hashing their keys. L2
    /// forbids unordered iteration in the sim, and "the mod loaded but the
    /// fields merged in a different order this run" is not a bug anyone finds
    /// quickly.
    /// </summary>
    public sealed class JsonValue
    {
        readonly JsonKind _kind;
        readonly string _text;         // String, and the raw lexeme of Number
        readonly bool _bool;
        readonly List<JsonValue> _array;
        readonly List<string> _keys;   // parallel to _values, document order
        readonly List<JsonValue> _values;

        JsonValue(JsonKind kind, string text, bool b,
                  List<JsonValue> array, List<string> keys, List<JsonValue> values)
        {
            _kind = kind; _text = text; _bool = b;
            _array = array; _keys = keys; _values = values;
        }

        public static readonly JsonValue Null =
            new JsonValue(JsonKind.Null, null, false, null, null, null);

        public static JsonValue Bool(bool v) { return new JsonValue(JsonKind.Bool, null, v, null, null, null); }
        public static JsonValue Number(string lexeme) { return new JsonValue(JsonKind.Number, lexeme, false, null, null, null); }
        public static JsonValue String(string v) { return new JsonValue(JsonKind.String, v, false, null, null, null); }
        public static JsonValue Array(List<JsonValue> items) { return new JsonValue(JsonKind.Array, null, false, items, null, null); }
        public static JsonValue Object(List<string> keys, List<JsonValue> values) { return new JsonValue(JsonKind.Object, null, false, null, keys, values); }

        public JsonKind Kind { get { return _kind; } }
        public bool IsNull { get { return _kind == JsonKind.Null; } }

        public int Count
        {
            get
            {
                if (_kind == JsonKind.Array) return _array.Count;
                if (_kind == JsonKind.Object) return _keys.Count;
                return 0;
            }
        }

        /// <summary>Object keys in document order.</summary>
        public IReadOnlyList<string> Keys
        {
            get { return _kind == JsonKind.Object ? (IReadOnlyList<string>)_keys : new List<string>(); }
        }

        public JsonValue this[int index]
        {
            get
            {
                if (_kind == JsonKind.Array && index >= 0 && index < _array.Count) return _array[index];
                return Null;
            }
        }

        public JsonValue this[string key]
        {
            get
            {
                if (_kind != JsonKind.Object) return Null;
                for (int i = 0; i < _keys.Count; i++)
                    if (string.Equals(_keys[i], key, System.StringComparison.Ordinal))
                        return _values[i];
                return Null;
            }
        }

        public bool Has(string key) { return !this[key].IsNull || IndexOfKey(key) >= 0; }

        public int IndexOfKey(string key)
        {
            if (_kind != JsonKind.Object) return -1;
            for (int i = 0; i < _keys.Count; i++)
                if (string.Equals(_keys[i], key, System.StringComparison.Ordinal)) return i;
            return -1;
        }

        public string AsString(string fallback)
        {
            if (_kind == JsonKind.String) return _text;
            if (_kind == JsonKind.Number) return _text;
            return fallback;
        }

        public bool AsBool(bool fallback) { return _kind == JsonKind.Bool ? _bool : fallback; }

        public long AsInt64(long fallback)
        {
            if (_kind != JsonKind.Number) return fallback;
            long v;
            return long.TryParse(_text, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        public int AsInt32(int fallback) { return (int)AsInt64(fallback); }

        /// <summary>The raw lexeme, for a caller that wants to interpret it itself.</summary>
        public string NumberLexeme { get { return _kind == JsonKind.Number ? _text : null; } }

        /// <summary>
        /// The number as a double, parsed without the runtime's parser.
        ///
        /// double.Parse is implemented separately by Mono and CoreCLR, and a
        /// content value read one ulp apart on two machines is the start of
        /// two different worlds. Instead the lexeme becomes an integer
        /// mantissa and a power of ten, combined by a single division — and
        /// IEEE-754 division is correctly rounded, so every runtime produces
        /// the same bits. Exact for mantissas up to 2^53 and exponents where
        /// the power of ten is itself exact, which covers any number content
        /// will actually contain.
        /// </summary>
        public double AsDouble(double fallback)
        {
            if (_kind != JsonKind.Number) return fallback;
            double parsed;
            return DeterministicDecimal.TryParse(_text, out parsed) ? parsed : fallback;
        }

        public List<JsonValue> ItemsCopy()
        {
            return _kind == JsonKind.Array ? new List<JsonValue>(_array) : new List<JsonValue>();
        }

        public JsonValue ValueAt(int index)
        {
            if (_kind != JsonKind.Object || index < 0 || index >= _values.Count) return Null;
            return _values[index];
        }

        /// <summary>Returns a copy with one key set, preserving order; appends if new.</summary>
        public JsonValue WithMember(string key, JsonValue value)
        {
            var keys = new List<string>(_kind == JsonKind.Object ? _keys : new List<string>());
            var vals = new List<JsonValue>(_kind == JsonKind.Object ? _values : new List<JsonValue>());
            int at = -1;
            for (int i = 0; i < keys.Count; i++)
                if (string.Equals(keys[i], key, System.StringComparison.Ordinal)) { at = i; break; }
            if (at >= 0) vals[at] = value; else { keys.Add(key); vals.Add(value); }
            return Object(keys, vals);
        }

        public JsonValue WithoutMember(string key)
        {
            if (_kind != JsonKind.Object) return this;
            var keys = new List<string>();
            var vals = new List<JsonValue>();
            for (int i = 0; i < _keys.Count; i++)
            {
                if (string.Equals(_keys[i], key, System.StringComparison.Ordinal)) continue;
                keys.Add(_keys[i]); vals.Add(_values[i]);
            }
            return Object(keys, vals);
        }

        public JsonValue WithItem(int index, JsonValue value)
        {
            if (_kind != JsonKind.Array) return this;
            var items = new List<JsonValue>(_array);
            if (index < 0 || index >= items.Count) return this;
            items[index] = value;
            return Array(items);
        }

        public JsonValue WithItemInserted(int index, JsonValue value)
        {
            var items = _kind == JsonKind.Array ? new List<JsonValue>(_array) : new List<JsonValue>();
            if (index < 0 || index > items.Count) index = items.Count;
            items.Insert(index, value);
            return Array(items);
        }

        public JsonValue WithoutItem(int index)
        {
            if (_kind != JsonKind.Array) return this;
            var items = new List<JsonValue>(_array);
            if (index < 0 || index >= items.Count) return this;
            items.RemoveAt(index);
            return Array(items);
        }

        /// <summary>
        /// Canonical serialisation: no whitespace, document key order kept.
        /// Two runs that built the same content produce the same bytes, which
        /// is what lets the harness digest a loaded database.
        /// </summary>
        public override string ToString()
        {
            var sb = new StringBuilder();
            Write(sb);
            return sb.ToString();
        }

        void Write(StringBuilder sb)
        {
            switch (_kind)
            {
                case JsonKind.Null: sb.Append("null"); break;
                case JsonKind.Bool: sb.Append(_bool ? "true" : "false"); break;
                case JsonKind.Number: sb.Append(_text); break;
                case JsonKind.String: WriteString(sb, _text); break;
                case JsonKind.Array:
                    sb.Append('[');
                    for (int i = 0; i < _array.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        _array[i].Write(sb);
                    }
                    sb.Append(']');
                    break;
                case JsonKind.Object:
                    sb.Append('{');
                    for (int i = 0; i < _keys.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        WriteString(sb, _keys[i]);
                        sb.Append(':');
                        _values[i].Write(sb);
                    }
                    sb.Append('}');
                    break;
            }
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}

namespace Godless.Sim.Content
{
    /// <summary>Exact, runtime-independent decimal parsing. See JsonValue.AsDouble.</summary>
    public static class DeterministicDecimal
    {
        // Every power of ten up to 10^22 is exactly representable as a double.
        static readonly double[] Pow10 =
        {
            1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11,
            1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22,
        };

        public static bool TryParse(string s, out double value)
        {
            value = 0;
            if (string.IsNullOrEmpty(s)) return false;

            int i = 0;
            bool negative = false;
            if (s[i] == '-' || s[i] == '+') { negative = s[i] == '-'; i++; }

            long mantissa = 0;
            int digits = 0, scale = 0;
            bool any = false;
            for (; i < s.Length && s[i] >= '0' && s[i] <= '9'; i++)
            {
                if (digits < 17) { mantissa = mantissa * 10 + (s[i] - '0'); if (mantissa != 0) digits++; }
                else scale--;          // beyond precision: drop digits, keep magnitude
                any = true;
            }
            if (i < s.Length && s[i] == '.')
            {
                i++;
                for (; i < s.Length && s[i] >= '0' && s[i] <= '9'; i++)
                {
                    if (digits < 17) { mantissa = mantissa * 10 + (s[i] - '0'); if (mantissa != 0) digits++; scale++; }
                    any = true;
                }
            }
            if (!any) return false;

            int exponent = 0;
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                i++;
                bool expNegative = false;
                if (i < s.Length && (s[i] == '-' || s[i] == '+')) { expNegative = s[i] == '-'; i++; }
                int start = i;
                for (; i < s.Length && s[i] >= '0' && s[i] <= '9'; i++)
                    if (exponent < 10000) exponent = exponent * 10 + (s[i] - '0');
                if (i == start) return false;
                if (expNegative) exponent = -exponent;
            }
            if (i != s.Length) return false;

            int power = exponent - scale;     // value = mantissa * 10^power
            double m = mantissa;              // exact: mantissa < 10^17 < 2^57, and digits <= 17
            if (power == 0) value = m;
            else if (power > 0 && power <= 22) value = m * Pow10[power];
            else if (power < 0 && power >= -22) value = m / Pow10[-power];
            else
            {
                // Outside the exact range: still deterministic, since each
                // step is a correctly rounded IEEE operation in a fixed order.
                value = m;
                int p = power;
                while (p > 22) { value *= 1e22; p -= 22; }
                while (p < -22) { value /= 1e22; p += 22; }
                value = p >= 0 ? value * Pow10[p] : value / Pow10[-p];
            }
            if (negative) value = -value;
            return true;
        }
    }
}
