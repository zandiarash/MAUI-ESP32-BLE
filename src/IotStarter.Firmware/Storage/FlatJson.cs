// Compiled by the firmware and, linked, by the desktop unit tests: conservative C# only.
#nullable disable

using System.Collections;
using System.Text;

namespace IotStarter.Firmware.Storage
{
    /// <summary>
    /// Strict parser for flat JSON objects: string keys; string, boolean, number or null values.
    /// Nested objects and arrays are rejected. Used for HTTP request bodies and the settings file,
    /// where a small fixed shape is expected and anything else must be refused without crashing.
    /// </summary>
    public class FlatJsonObject
    {
        // Values: string, bool, FlatJsonNumber, or null marker.
        private readonly Hashtable _values;

        private FlatJsonObject(Hashtable values)
        {
            _values = values;
        }

        private static readonly object NullValue = new object();

        public bool Contains(string key)
        {
            return _values.Contains(key);
        }

        public bool TryGetString(string key, out string value)
        {
            object raw = _values.Contains(key) ? _values[key] : null;
            value = raw as string;
            return value != null;
        }

        public bool TryGetBool(string key, out bool value)
        {
            object raw = _values.Contains(key) ? _values[key] : null;
            if (raw is bool)
            {
                value = (bool)raw;
                return true;
            }

            value = false;
            return false;
        }

        /// <summary>Reads a non-negative integer number (no fraction or exponent).</summary>
        public bool TryGetInt(string key, out int value)
        {
            value = 0;
            object raw = _values.Contains(key) ? _values[key] : null;
            FlatJsonNumber number = raw as FlatJsonNumber;
            if (number == null)
            {
                return false;
            }

            string text = number.Text;
            if (text.Length == 0 || text.Length > 9)
            {
                return false;
            }

            int result = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < '0' || c > '9')
                {
                    return false;
                }

                result = (result * 10) + (c - '0');
            }

            value = result;
            return true;
        }

        /// <summary>Parses <paramref name="json"/>; returns null if it is not a flat JSON object.</summary>
        public static FlatJsonObject Parse(string json)
        {
            if (json == null)
            {
                return null;
            }

            Hashtable values = new Hashtable();
            int pos = 0;
            SkipWhitespace(json, ref pos);
            if (!Expect(json, ref pos, '{'))
            {
                return null;
            }

            SkipWhitespace(json, ref pos);
            if (Peek(json, pos) == '}')
            {
                pos++;
                return TrailingOk(json, pos) ? new FlatJsonObject(values) : null;
            }

            while (true)
            {
                SkipWhitespace(json, ref pos);
                string key = ReadString(json, ref pos);
                if (key == null)
                {
                    return null;
                }

                SkipWhitespace(json, ref pos);
                if (!Expect(json, ref pos, ':'))
                {
                    return null;
                }

                SkipWhitespace(json, ref pos);
                object value;
                if (!ReadValue(json, ref pos, out value))
                {
                    return null;
                }

                // Last duplicate wins, as in most JSON parsers.
                values[key] = value;

                SkipWhitespace(json, ref pos);
                char c = Peek(json, pos);
                if (c == ',')
                {
                    pos++;
                    continue;
                }

                if (c == '}')
                {
                    pos++;
                    return TrailingOk(json, pos) ? new FlatJsonObject(values) : null;
                }

                return null;
            }
        }

        private static bool ReadValue(string json, ref int pos, out object value)
        {
            value = null;
            char c = Peek(json, pos);
            if (c == '"')
            {
                string s = ReadString(json, ref pos);
                value = s;
                return s != null;
            }

            if (Match(json, ref pos, "true"))
            {
                value = true;
                return true;
            }

            if (Match(json, ref pos, "false"))
            {
                value = false;
                return true;
            }

            if (Match(json, ref pos, "null"))
            {
                value = NullValue;
                return true;
            }

            if (c == '-' || (c >= '0' && c <= '9'))
            {
                int start = pos;
                pos++;
                while (pos < json.Length)
                {
                    char d = json[pos];
                    if ((d >= '0' && d <= '9') || d == '.' || d == 'e' || d == 'E' || d == '+' || d == '-')
                    {
                        pos++;
                    }
                    else
                    {
                        break;
                    }
                }

                value = new FlatJsonNumber(json.Substring(start, pos - start));
                return true;
            }

            // Objects, arrays and anything else are not accepted.
            return false;
        }

        private static string ReadString(string json, ref int pos)
        {
            if (Peek(json, pos) != '"')
            {
                return null;
            }

            pos++;
            StringBuilder sb = new StringBuilder();
            while (pos < json.Length)
            {
                char c = json[pos++];
                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c < 0x20)
                {
                    return null;
                }

                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (pos >= json.Length)
                {
                    return null;
                }

                char e = json[pos++];
                switch (e)
                {
                    case '"':
                        sb.Append('"');
                        break;
                    case '\\':
                        sb.Append('\\');
                        break;
                    case '/':
                        sb.Append('/');
                        break;
                    case 'b':
                        sb.Append('\b');
                        break;
                    case 'f':
                        sb.Append('\f');
                        break;
                    case 'n':
                        sb.Append('\n');
                        break;
                    case 'r':
                        sb.Append('\r');
                        break;
                    case 't':
                        sb.Append('\t');
                        break;
                    case 'u':
                        if (pos + 4 > json.Length)
                        {
                            return null;
                        }

                        int code = 0;
                        for (int i = 0; i < 4; i++)
                        {
                            int h = HexValue(json[pos++]);
                            if (h < 0)
                            {
                                return null;
                            }

                            code = (code << 4) | h;
                        }

                        sb.Append((char)code);
                        break;
                    default:
                        return null;
                }
            }

            return null;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9')
            {
                return c - '0';
            }

            if (c >= 'a' && c <= 'f')
            {
                return c - 'a' + 10;
            }

            if (c >= 'A' && c <= 'F')
            {
                return c - 'A' + 10;
            }

            return -1;
        }

        private static bool Match(string json, ref int pos, string literal)
        {
            if (pos + literal.Length > json.Length)
            {
                return false;
            }

            for (int i = 0; i < literal.Length; i++)
            {
                if (json[pos + i] != literal[i])
                {
                    return false;
                }
            }

            pos += literal.Length;
            return true;
        }

        private static bool Expect(string json, ref int pos, char c)
        {
            if (Peek(json, pos) != c)
            {
                return false;
            }

            pos++;
            return true;
        }

        private static char Peek(string json, int pos)
        {
            return pos < json.Length ? json[pos] : '\0';
        }

        private static void SkipWhitespace(string json, ref int pos)
        {
            while (pos < json.Length)
            {
                char c = json[pos];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    pos++;
                }
                else
                {
                    break;
                }
            }
        }

        private static bool TrailingOk(string json, int pos)
        {
            SkipWhitespace(json, ref pos);
            return pos == json.Length;
        }
    }

    /// <summary>Raw JSON number text.</summary>
    public class FlatJsonNumber
    {
        public FlatJsonNumber(string text)
        {
            Text = text;
        }

        public string Text { get; private set; }
    }

    /// <summary>Builds JSON text with correct string escaping.</summary>
    public class JsonWriter
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private bool _needComma;

        public JsonWriter BeginObject()
        {
            Separator();
            _sb.Append('{');
            _needComma = false;
            return this;
        }

        public JsonWriter EndObject()
        {
            _sb.Append('}');
            _needComma = true;
            return this;
        }

        public JsonWriter BeginArray(string name)
        {
            Name(name);
            _sb.Append('[');
            _needComma = false;
            return this;
        }

        public JsonWriter EndArray()
        {
            _sb.Append(']');
            _needComma = true;
            return this;
        }

        public JsonWriter String(string name, string value)
        {
            Name(name);
            if (value == null)
            {
                _sb.Append("null");
            }
            else
            {
                AppendEscaped(value);
            }

            _needComma = true;
            return this;
        }

        public JsonWriter Number(string name, int value)
        {
            Name(name);
            _sb.Append(value.ToString());
            _needComma = true;
            return this;
        }

        public JsonWriter Bool(string name, bool value)
        {
            Name(name);
            _sb.Append(value ? "true" : "false");
            _needComma = true;
            return this;
        }

        public override string ToString()
        {
            return _sb.ToString();
        }

        private void Name(string name)
        {
            Separator();
            if (name != null)
            {
                AppendEscaped(name);
                _sb.Append(':');
            }
        }

        private void Separator()
        {
            if (_needComma)
            {
                _sb.Append(',');
                _needComma = false;
            }
        }

        private void AppendEscaped(string value)
        {
            _sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"':
                        _sb.Append("\\\"");
                        break;
                    case '\\':
                        _sb.Append("\\\\");
                        break;
                    case '\n':
                        _sb.Append("\\n");
                        break;
                    case '\r':
                        _sb.Append("\\r");
                        break;
                    case '\t':
                        _sb.Append("\\t");
                        break;
                    default:
                        if (c < 0x20)
                        {
                            _sb.Append("\\u00");
                            _sb.Append(HexDigit(c >> 4));
                            _sb.Append(HexDigit(c & 0xF));
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

        private static char HexDigit(int value)
        {
            return (char)(value < 10 ? '0' + value : 'a' + value - 10);
        }
    }
}
