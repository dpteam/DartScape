using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DartScape.Web.Http
{
    /// <summary>
    /// A minimal JSON writer and the handful of parsing helpers the admin API needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// .NET Framework 4.0 has no JSON support in the box, and the project takes no
    /// third-party libraries, so this is hand-written. It is deliberately not a general
    /// serializer: responses are built by explicit calls, which makes the wire format
    /// obvious at the call site and impossible to change by accident when a field is
    /// renamed.
    /// </para>
    /// <para>
    /// The builder tracks whether a comma is needed, so a caller can append values in a
    /// loop without worrying about separators вЂ” the single most common source of broken
    /// hand-written JSON.
    /// </para>
    /// </remarks>
    public sealed class JsonBuilder
    {
        private readonly StringBuilder _sb;
        private readonly Stack<bool> _needsComma = new Stack<bool>();

        public JsonBuilder(int capacity = 512)
        {
            _sb = new StringBuilder(capacity);
        }

        /// <summary>Emits the separator a value needs, then marks that the next one will too.</summary>
        private void BeforeValue()
        {
            if (_needsComma.Count == 0) return;      // top level: no separator ever

            if (_needsComma.Peek()) _sb.Append(',');
            else _needsComma.Pop();
            _needsComma.Push(true);
        }

        public JsonBuilder BeginObject()
        {
            BeforeValue();
            _sb.Append('{');
            _needsComma.Push(false);
            return this;
        }

        public JsonBuilder EndObject()
        {
            _sb.Append('}');
            if (_needsComma.Count > 0) _needsComma.Pop();
            return this;
        }

        public JsonBuilder BeginArray()
        {
            BeforeValue();
            _sb.Append('[');
            _needsComma.Push(false);
            return this;
        }

        public JsonBuilder EndArray()
        {
            _sb.Append(']');
            if (_needsComma.Count > 0) _needsComma.Pop();
            return this;
        }

        public JsonBuilder Name(string name)
        {
            BeforeValue();
            AppendQuoted(name);
            _sb.Append(':');

            // The value that follows is not a separate element, so the container must not
            // insert another comma before it.
            _needsComma.Pop();
            _needsComma.Push(false);
            return this;
        }

        public JsonBuilder Value(string value)
        {
            BeforeValue();
            if (value == null) _sb.Append("null");
            else AppendQuoted(value);
            return this;
        }

        public JsonBuilder Value(long value)
        {
            BeforeValue();
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonBuilder Value(double value)
        {
            BeforeValue();

            if (double.IsNaN(value) || double.IsInfinity(value)) _sb.Append('0');
            else _sb.Append(value.ToString("0.###", CultureInfo.InvariantCulture));

            return this;
        }

        public JsonBuilder Value(bool value)
        {
            BeforeValue();
            _sb.Append(value ? "true" : "false");
            return this;
        }

        public JsonBuilder Null()
        {
            BeforeValue();
            _sb.Append("null");
            return this;
        }

        public JsonBuilder Raw(string json)
        {
            BeforeValue();
            _sb.Append(json ?? "null");
            return this;
        }

        public JsonBuilder Prop(string name, string value)
        {
            return Name(name).Value(value);
        }

        public JsonBuilder Prop(string name, long value)
        {
            return Name(name).Value(value);
        }

        public JsonBuilder Prop(string name, double value)
        {
            return Name(name).Value(value);
        }

        public JsonBuilder Prop(string name, bool value)
        {
            return Name(name).Value(value);
        }

        private void AppendQuoted(string value)
        {
            _sb.Append('"');

            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    case '\b': _sb.Append("\\b"); break;
                    case '\f': _sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == 0x7F)
                        {
                            _sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            _sb.Append(c);
                        }
                        break;
                }
            }

            _sb.Append('"');
        }

        public override string ToString()
        {
            return _sb.ToString();
        }
    }

    /// <summary>
    /// Reads a single value out of a small JSON object.
    /// </summary>
    /// <remarks>
    /// The admin API receives objects with a handful of string fields. This finds a
    /// top-level key and returns its raw value, which is all any endpoint needs; a full
    /// parser would be more code and more ways to be wrong.
    /// </remarks>
    public static class JsonReader
    {
        /// <summary>Returns the string value of <paramref name="key"/>, or <paramref name="fallback"/>.</summary>
        public static string GetString(string json, string key, string fallback = null)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return fallback;

            int index = FindKey(json, key);
            if (index < 0) return fallback;

            index = SkipWhitespace(json, index);
            if (index >= json.Length) return fallback;

            if (json[index] == '"')
            {
                return ReadString(json, ref index);
            }

            int start = index;
            while (index < json.Length && json[index] != ',' && json[index] != '}' && json[index] != ']') index++;

            string raw = json.Substring(start, index - start).Trim();
            return raw.Length == 0 || raw == "null" ? fallback : raw;
        }

        /// <summary>Returns the integer value of <paramref name="key"/>, or <paramref name="fallback"/>.</summary>
        public static int GetInt(string json, string key, int fallback)
        {
            string raw = GetString(json, key, null);
            if (raw == null) return fallback;

            int parsed;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        /// <summary>Returns the boolean value of <paramref name="key"/>.</summary>
        public static bool GetBool(string json, string key, bool fallback)
        {
            string raw = GetString(json, key, null);
            if (raw == null) return fallback;

            return raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1";
        }

        private static int FindKey(string json, string key)
        {
            // Look for "key" followed by a colon, at any depth. Keys are unique enough in
            // these payloads that a flat search is safe and much simpler than a parser.
            string needle = "\"" + key + "\"";
            int index = json.IndexOf(needle, StringComparison.Ordinal);

            while (index >= 0)
            {
                int after = SkipWhitespace(json, index + needle.Length);
                if (after < json.Length && json[after] == ':') return after + 1;

                index = json.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
            }

            return -1;
        }

        private static int SkipWhitespace(string json, int index)
        {
            while (index < json.Length && (json[index] == ' ' || json[index] == '\t' ||
                                           json[index] == '\r' || json[index] == '\n'))
            {
                index++;
            }
            return index;
        }

        private static string ReadString(string json, ref int index)
        {
            index++;      // opening quote
            var sb = new StringBuilder(32);

            while (index < json.Length && json[index] != '"')
            {
                char c = json[index];

                if (c == '\\' && index + 1 < json.Length)
                {
                    index++;
                    switch (json[index])
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'u':
                            if (index + 4 < json.Length)
                            {
                                int code;
                                if (int.TryParse(json.Substring(index + 1, 4), NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture, out code))
                                {
                                    sb.Append((char)code);
                                    index += 4;
                                }
                            }
                            break;
                        default: sb.Append(json[index]); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }

                index++;
            }

            index++;      // closing quote
            return sb.ToString();
        }
    }
}
