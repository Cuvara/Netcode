using System.Globalization;
using System.Text;

namespace Cuvara.Netcode.Json
{
    /// <summary>
    /// Minimal JSON writer producing the compact, <c>snake_case</c> shape both
    /// servers parse. No pretty printing, no reflection.
    /// </summary>
    /// <remarks>
    /// Field presence mirrors Go's <c>omitempty</c> only where a server cares;
    /// writing a defaulted field the peer would have omitted is harmless, because
    /// both server decoders treat a present-but-default field and an absent one
    /// identically.
    /// </remarks>
    public sealed class JsonBuilder
    {
        private readonly StringBuilder _sb = new StringBuilder(128);
        private bool _needsComma;

        public JsonBuilder BeginObject()
        {
            SeparateIfNeeded();
            _sb.Append('{');
            _needsComma = false;
            return this;
        }

        public JsonBuilder EndObject()
        {
            _sb.Append('}');
            _needsComma = true;
            return this;
        }

        public JsonBuilder Name(string name)
        {
            SeparateIfNeeded();
            AppendQuoted(name);
            _sb.Append(':');
            _needsComma = false;
            return this;
        }

        public JsonBuilder String(string name, string value)
        {
            Name(name);
            AppendQuoted(value ?? string.Empty);
            _needsComma = true;
            return this;
        }

        public JsonBuilder Number(string name, long value)
        {
            Name(name);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            _needsComma = true;
            return this;
        }

        /// <summary>
        /// Writes a float with round-trip precision, invariant culture. A
        /// non-finite value is written as 0: JSON has no NaN or Infinity literal,
        /// so emitting one would produce a frame the server cannot parse — and
        /// the server would reject the value anyway.
        /// </summary>
        public JsonBuilder Number(string name, float value)
        {
            Name(name);
            var finite = float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;
            _sb.Append(finite.ToString("R", CultureInfo.InvariantCulture));
            _needsComma = true;
            return this;
        }

        public JsonBuilder Bool(string name, bool value)
        {
            Name(name);
            _sb.Append(value ? "true" : "false");
            _needsComma = true;
            return this;
        }

        /// <summary>Appends already-serialized JSON as the value of a member.</summary>
        public JsonBuilder Raw(string name, string json)
        {
            Name(name);
            _sb.Append(json);
            _needsComma = true;
            return this;
        }

        public override string ToString() => _sb.ToString();

        private void SeparateIfNeeded()
        {
            if (_needsComma)
            {
                _sb.Append(',');
            }
        }

        private void AppendQuoted(string value)
        {
            _sb.Append('"');
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\b': _sb.Append("\\b"); break;
                    case '\f': _sb.Append("\\f"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            _sb.Append("\\u");
                            _sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
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
    }
}
