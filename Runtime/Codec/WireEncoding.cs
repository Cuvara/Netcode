namespace Cuvara.Netcode.Codec
{
    /// <summary>
    /// Which serialization a frame body uses. Both encodings share the framing and
    /// the message-type space, so they are interchangeable per connection.
    /// </summary>
    public enum WireEncoding
    {
        /// <summary>Not yet determined for this connection.</summary>
        Unknown = 0,

        /// <summary>Legacy <c>{"type":N,"payload":{...}}</c>. First body byte <c>0x7B</c>.</summary>
        Json = 1,

        /// <summary>
        /// Protobuf, generated from <c>shared/proto/wire.proto</c>. First body byte
        /// <c>0x08</c>. Not implemented in this client yet.
        /// </summary>
        Protobuf = 2
    }
}
