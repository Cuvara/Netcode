using System.Collections.Generic;

namespace Cuvara.Netcode.Json
{
    /// <summary>
    /// A parsed JSON value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because the legacy JSON encoding has to be readable without
    /// adding a JSON package to the project, and because <c>JsonUtility</c> cannot
    /// express the envelope shape (an opaque nested payload whose schema depends
    /// on a sibling field) nor 64-bit ticks reliably.
    /// </para>
    /// <para>
    /// It allocates per frame, which is acceptable only because JSON is the legacy
    /// path: Protobuf is the intended encoding and does not go through here.
    /// </para>
    /// <para>
    /// Accessors are total — a missing key or a wrong kind returns the supplied
    /// default rather than throwing. Wire messages omit defaulted fields
    /// (<c>omitempty</c>), so "absent" is normal and must not be an error.
    /// </para>
    /// </remarks>
    public sealed class JsonValue
    {
        private static readonly IReadOnlyList<JsonValue> EmptyArray = new List<JsonValue>();

        private readonly Dictionary<string, JsonValue> _members;
        private readonly List<JsonValue> _items;
        private readonly string _text;
        private readonly double _number;
        private readonly bool _flag;

        private JsonValue(JsonKind kind, Dictionary<string, JsonValue> members, List<JsonValue> items,
            string text, double number, bool flag)
        {
            Kind = kind;
            _members = members;
            _items = items;
            _text = text;
            _number = number;
            _flag = flag;
        }

        public JsonKind Kind { get; }

        public static JsonValue Null { get; } = new JsonValue(JsonKind.Null, null, null, null, 0d, false);

        public static JsonValue FromBool(bool value) =>
            new JsonValue(JsonKind.Bool, null, null, null, 0d, value);

        public static JsonValue FromNumber(double value) =>
            new JsonValue(JsonKind.Number, null, null, null, value, false);

        public static JsonValue FromString(string value) =>
            new JsonValue(JsonKind.String, null, null, value ?? string.Empty, 0d, false);

        public static JsonValue FromObject(Dictionary<string, JsonValue> members) =>
            new JsonValue(JsonKind.Object, members ?? new Dictionary<string, JsonValue>(), null, null, 0d, false);

        public static JsonValue FromArray(List<JsonValue> items) =>
            new JsonValue(JsonKind.Array, null, items ?? new List<JsonValue>(), null, 0d, false);

        /// <summary>Items of an array value; empty for anything else.</summary>
        public IReadOnlyList<JsonValue> Items => _items ?? EmptyArray;

        /// <summary>Look up a member of an object value.</summary>
        public bool TryGetMember(string name, out JsonValue value)
        {
            if (_members != null && _members.TryGetValue(name, out value))
            {
                return true;
            }

            value = Null;
            return false;
        }

        public string GetString(string name, string fallback = "")
        {
            return TryGetMember(name, out var v) && v.Kind == JsonKind.String ? v._text : fallback;
        }

        public bool GetBool(string name, bool fallback = false)
        {
            return TryGetMember(name, out var v) && v.Kind == JsonKind.Bool ? v._flag : fallback;
        }

        /// <summary>
        /// Reads a member as a 64-bit integer. Ticks are <c>uint64</c> on the wire
        /// but are represented as <c>long</c> here: a 15 Hz tick counter needs
        /// nineteen digits of headroom it will never use, and <c>long</c> is what
        /// every consumer can hold without a cast.
        /// </summary>
        public long GetLong(string name, long fallback = 0L)
        {
            return TryGetMember(name, out var v) && v.Kind == JsonKind.Number ? (long)v._number : fallback;
        }

        public uint GetUInt(string name, uint fallback = 0u)
        {
            if (!TryGetMember(name, out var v) || v.Kind != JsonKind.Number || v._number < 0d)
            {
                return fallback;
            }

            return (uint)v._number;
        }

        public int GetInt(string name, int fallback = 0)
        {
            return TryGetMember(name, out var v) && v.Kind == JsonKind.Number ? (int)v._number : fallback;
        }

        public float GetFloat(string name, float fallback = 0f)
        {
            return TryGetMember(name, out var v) && v.Kind == JsonKind.Number ? (float)v._number : fallback;
        }

        /// <summary>The value's own text, for a string element inside an array.</summary>
        public string AsString(string fallback = "")
        {
            return Kind == JsonKind.String ? _text : fallback;
        }

        /// <summary>Members of an object value, for iterating an unknown shape.</summary>
        public IReadOnlyList<JsonValue> GetArray(string name)
        {
            return TryGetMember(name, out var v) && v.Kind == JsonKind.Array ? v.Items : EmptyArray;
        }
    }
}
