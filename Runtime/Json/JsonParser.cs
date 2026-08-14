using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Cuvara.Netcode.Json
{
    /// <summary>
    /// Minimal recursive-descent JSON parser, sufficient for the wire protocol and
    /// nothing more.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately strict about structure and deliberately total about content:
    /// malformed input throws <see cref="JsonParseException"/>, which the read loop
    /// turns into a connection error, while unknown members parse fine and are
    /// simply never read. That matches how both servers treat a peer's extra
    /// fields, and is what lets the server add a field without breaking us.
    /// </para>
    /// <para>
    /// Numbers are parsed with <see cref="CultureInfo.InvariantCulture"/>. Anything
    /// else would make the wire format depend on the device locale, which is a real
    /// failure mode on mobile, not a theoretical one.
    /// </para>
    /// </remarks>
    public static class JsonParser
    {
        /// <summary>
        /// Nesting limit. The protocol's deepest shape is
        /// envelope -> payload -> entities -> entity = 4; 32 leaves room to grow
        /// while keeping a hostile or corrupt frame from exhausting the stack.
        /// </summary>
        private const int MaxDepth = 32;

        public static JsonValue Parse(string text)
        {
            if (text == null)
            {
                throw new JsonParseException("JSON input is null");
            }

            var index = 0;
            var value = ParseValue(text, ref index, 0);
            SkipWhitespace(text, ref index);
            if (index != text.Length)
            {
                throw new JsonParseException($"Trailing characters at index {index}");
            }

            return value;
        }

        private static JsonValue ParseValue(string text, ref int index, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new JsonParseException("JSON nesting too deep");
            }

            SkipWhitespace(text, ref index);
            if (index >= text.Length)
            {
                throw new JsonParseException("Unexpected end of JSON input");
            }

            var c = text[index];
            switch (c)
            {
                case '{':
                    return ParseObject(text, ref index, depth);
                case '[':
                    return ParseArray(text, ref index, depth);
                case '"':
                    return JsonValue.FromString(ParseString(text, ref index));
                case 't':
                    Expect(text, ref index, "true");
                    return JsonValue.FromBool(true);
                case 'f':
                    Expect(text, ref index, "false");
                    return JsonValue.FromBool(false);
                case 'n':
                    Expect(text, ref index, "null");
                    return JsonValue.Null;
                default:
                    return JsonValue.FromNumber(ParseNumber(text, ref index));
            }
        }

        private static JsonValue ParseObject(string text, ref int index, int depth)
        {
            index++; // '{'
            var members = new Dictionary<string, JsonValue>();

            SkipWhitespace(text, ref index);
            if (index < text.Length && text[index] == '}')
            {
                index++;
                return JsonValue.FromObject(members);
            }

            while (true)
            {
                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != '"')
                {
                    throw new JsonParseException($"Expected member name at index {index}");
                }

                var name = ParseString(text, ref index);
                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != ':')
                {
                    throw new JsonParseException($"Expected ':' at index {index}");
                }

                index++;
                // A duplicate member keeps the last value, matching both servers'
                // decoders; rejecting it would be stricter than the peers we talk to.
                members[name] = ParseValue(text, ref index, depth + 1);

                SkipWhitespace(text, ref index);
                if (index >= text.Length)
                {
                    throw new JsonParseException("Unterminated JSON object");
                }

                if (text[index] == ',')
                {
                    index++;
                    continue;
                }

                if (text[index] == '}')
                {
                    index++;
                    return JsonValue.FromObject(members);
                }

                throw new JsonParseException($"Expected ',' or '}}' at index {index}");
            }
        }

        private static JsonValue ParseArray(string text, ref int index, int depth)
        {
            index++; // '['
            var items = new List<JsonValue>();

            SkipWhitespace(text, ref index);
            if (index < text.Length && text[index] == ']')
            {
                index++;
                return JsonValue.FromArray(items);
            }

            while (true)
            {
                items.Add(ParseValue(text, ref index, depth + 1));

                SkipWhitespace(text, ref index);
                if (index >= text.Length)
                {
                    throw new JsonParseException("Unterminated JSON array");
                }

                if (text[index] == ',')
                {
                    index++;
                    continue;
                }

                if (text[index] == ']')
                {
                    index++;
                    return JsonValue.FromArray(items);
                }

                throw new JsonParseException($"Expected ',' or ']' at index {index}");
            }
        }

        private static string ParseString(string text, ref int index)
        {
            index++; // opening quote
            var builder = new StringBuilder();

            while (true)
            {
                if (index >= text.Length)
                {
                    throw new JsonParseException("Unterminated JSON string");
                }

                var c = text[index];
                if (c == '"')
                {
                    index++;
                    return builder.ToString();
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    index++;
                    continue;
                }

                index++;
                if (index >= text.Length)
                {
                    throw new JsonParseException("Unterminated JSON escape");
                }

                var esc = text[index];
                index++;
                switch (esc)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (index + 4 > text.Length)
                        {
                            throw new JsonParseException("Truncated \\u escape");
                        }

                        var hex = text.Substring(index, 4);
                        if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            throw new JsonParseException($"Invalid \\u escape '{hex}'");
                        }

                        // Surrogate pairs need no special handling: each half is a
                        // valid UTF-16 code unit and appending both in order
                        // reconstitutes the pair.
                        builder.Append((char)code);
                        index += 4;
                        break;
                    default:
                        throw new JsonParseException($"Invalid escape '\\{esc}'");
                }
            }
        }

        private static double ParseNumber(string text, ref int index)
        {
            var start = index;
            if (index < text.Length && (text[index] == '-' || text[index] == '+'))
            {
                index++;
            }

            while (index < text.Length)
            {
                var c = text[index];
                var isNumeric = (c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-';
                if (!isNumeric)
                {
                    break;
                }

                index++;
            }

            var slice = text.Substring(start, index - start);
            if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                throw new JsonParseException($"Invalid number '{slice}' at index {start}");
            }

            return value;
        }

        private static void Expect(string text, ref int index, string literal)
        {
            if (index + literal.Length > text.Length ||
                string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
            {
                throw new JsonParseException($"Expected '{literal}' at index {index}");
            }

            index += literal.Length;
        }

        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length)
            {
                var c = text[index];
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r')
                {
                    return;
                }

                index++;
            }
        }
    }
}
