using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Godless.Sim.Content
{
    public sealed class JsonParseException : System.Exception
    {
        public JsonParseException(string message, int line, int column)
            : base(message + " (line " + line.ToString(CultureInfo.InvariantCulture)
                   + ", column " + column.ToString(CultureInfo.InvariantCulture) + ")")
        {
            Line = line;
            Column = column;
        }

        public int Line { get; private set; }
        public int Column { get; private set; }
    }

    /// <summary>
    /// A strict JSON parser. Strict on purpose: content is authored by hand
    /// and by modders, so a trailing comma should be an error with a line
    /// number rather than a silently different world.
    ///
    /// One deliberate extension: // and /* */ comments are accepted, because
    /// content files carry design intent that belongs next to the data.
    /// </summary>
    public static class JsonParser
    {
        public static JsonValue Parse(string text)
        {
            int i = 0, line = 1, col = 1;
            SkipTrivia(text, ref i, ref line, ref col);
            JsonValue v = ParseValue(text, ref i, ref line, ref col, 0);
            SkipTrivia(text, ref i, ref line, ref col);
            if (i < text.Length) throw new JsonParseException("trailing content after the root value", line, col);
            return v;
        }

        const int MaxDepth = 64;

        static JsonValue ParseValue(string s, ref int i, ref int line, ref int col, int depth)
        {
            if (depth > MaxDepth) throw new JsonParseException("nesting too deep", line, col);
            if (i >= s.Length) throw new JsonParseException("unexpected end of input", line, col);

            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i, ref line, ref col, depth);
                case '[': return ParseArray(s, ref i, ref line, ref col, depth);
                case '"': return JsonValue.String(ParseString(s, ref i, ref line, ref col));
                case 't': Expect(s, ref i, ref line, ref col, "true"); return JsonValue.Bool(true);
                case 'f': Expect(s, ref i, ref line, ref col, "false"); return JsonValue.Bool(false);
                case 'n': Expect(s, ref i, ref line, ref col, "null"); return JsonValue.Null;
                default: return JsonValue.Number(ParseNumber(s, ref i, ref line, ref col));
            }
        }

        static JsonValue ParseObject(string s, ref int i, ref int line, ref int col, int depth)
        {
            Advance(s, ref i, ref line, ref col); // '{'
            var keys = new List<string>();
            var values = new List<JsonValue>();

            SkipTrivia(s, ref i, ref line, ref col);
            if (i < s.Length && s[i] == '}') { Advance(s, ref i, ref line, ref col); return JsonValue.Object(keys, values); }

            while (true)
            {
                SkipTrivia(s, ref i, ref line, ref col);
                if (i >= s.Length || s[i] != '"')
                    throw new JsonParseException("expected a quoted member name", line, col);

                int keyLine = line, keyCol = col;
                string key = ParseString(s, ref i, ref line, ref col);
                for (int k = 0; k < keys.Count; k++)
                    if (string.Equals(keys[k], key, System.StringComparison.Ordinal))
                        throw new JsonParseException("duplicate member '" + key + "'", keyLine, keyCol);

                SkipTrivia(s, ref i, ref line, ref col);
                if (i >= s.Length || s[i] != ':') throw new JsonParseException("expected ':'", line, col);
                Advance(s, ref i, ref line, ref col);

                SkipTrivia(s, ref i, ref line, ref col);
                keys.Add(key);
                values.Add(ParseValue(s, ref i, ref line, ref col, depth + 1));

                SkipTrivia(s, ref i, ref line, ref col);
                if (i >= s.Length) throw new JsonParseException("unterminated object", line, col);
                if (s[i] == ',') { Advance(s, ref i, ref line, ref col); continue; }
                if (s[i] == '}') { Advance(s, ref i, ref line, ref col); return JsonValue.Object(keys, values); }
                throw new JsonParseException("expected ',' or '}'", line, col);
            }
        }

        static JsonValue ParseArray(string s, ref int i, ref int line, ref int col, int depth)
        {
            Advance(s, ref i, ref line, ref col); // '['
            var items = new List<JsonValue>();

            SkipTrivia(s, ref i, ref line, ref col);
            if (i < s.Length && s[i] == ']') { Advance(s, ref i, ref line, ref col); return JsonValue.Array(items); }

            while (true)
            {
                SkipTrivia(s, ref i, ref line, ref col);
                items.Add(ParseValue(s, ref i, ref line, ref col, depth + 1));
                SkipTrivia(s, ref i, ref line, ref col);
                if (i >= s.Length) throw new JsonParseException("unterminated array", line, col);
                if (s[i] == ',') { Advance(s, ref i, ref line, ref col); continue; }
                if (s[i] == ']') { Advance(s, ref i, ref line, ref col); return JsonValue.Array(items); }
                throw new JsonParseException("expected ',' or ']'", line, col);
            }
        }

        static string ParseString(string s, ref int i, ref int line, ref int col)
        {
            Advance(s, ref i, ref line, ref col); // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new JsonParseException("unterminated string", line, col);
                char c = s[i];
                if (c == '"') { Advance(s, ref i, ref line, ref col); return sb.ToString(); }
                if (c == '\\')
                {
                    Advance(s, ref i, ref line, ref col);
                    if (i >= s.Length) throw new JsonParseException("unterminated escape", line, col);
                    char e = s[i];
                    Advance(s, ref i, ref line, ref col);
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new JsonParseException("truncated \\u escape", line, col);
                            int cp = 0;
                            for (int k = 0; k < 4; k++)
                            {
                                int d = HexDigit(s[i]);
                                if (d < 0) throw new JsonParseException("bad hex digit in \\u escape", line, col);
                                cp = (cp << 4) | d;
                                Advance(s, ref i, ref line, ref col);
                            }
                            sb.Append((char)cp);
                            break;
                        default: throw new JsonParseException("unknown escape '\\" + e + "'", line, col);
                    }
                    continue;
                }
                if (c < 0x20) throw new JsonParseException("raw control character in string", line, col);
                sb.Append(c);
                Advance(s, ref i, ref line, ref col);
            }
        }

        static string ParseNumber(string s, ref int i, ref int line, ref int col)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) Advance(s, ref i, ref line, ref col);
            bool any = false;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') { Advance(s, ref i, ref line, ref col); any = true; }
            if (i < s.Length && s[i] == '.')
            {
                Advance(s, ref i, ref line, ref col);
                while (i < s.Length && s[i] >= '0' && s[i] <= '9') { Advance(s, ref i, ref line, ref col); any = true; }
            }
            if (any && i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                Advance(s, ref i, ref line, ref col);
                if (i < s.Length && (s[i] == '-' || s[i] == '+')) Advance(s, ref i, ref line, ref col);
                while (i < s.Length && s[i] >= '0' && s[i] <= '9') Advance(s, ref i, ref line, ref col);
            }
            if (!any) throw new JsonParseException("expected a value", line, col);
            return s.Substring(start, i - start);
        }

        static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        static void Expect(string s, ref int i, ref int line, ref int col, string word)
        {
            for (int k = 0; k < word.Length; k++)
            {
                if (i >= s.Length || s[i] != word[k])
                    throw new JsonParseException("expected '" + word + "'", line, col);
                Advance(s, ref i, ref line, ref col);
            }
        }

        static void Advance(string s, ref int i, ref int line, ref int col)
        {
            if (i < s.Length && s[i] == '\n') { line++; col = 1; } else col++;
            i++;
        }

        static void SkipTrivia(string s, ref int i, ref int line, ref int col)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '﻿')
                {
                    Advance(s, ref i, ref line, ref col);
                }
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    while (i < s.Length && s[i] != '\n') Advance(s, ref i, ref line, ref col);
                }
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    Advance(s, ref i, ref line, ref col);
                    Advance(s, ref i, ref line, ref col);
                    while (i < s.Length && !(s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/'))
                        Advance(s, ref i, ref line, ref col);
                    if (i >= s.Length) throw new JsonParseException("unterminated block comment", line, col);
                    Advance(s, ref i, ref line, ref col);
                    Advance(s, ref i, ref line, ref col);
                }
                else return;
            }
        }
    }
}
