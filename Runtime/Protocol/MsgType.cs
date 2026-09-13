namespace Cuvara.Netcode.Protocol
{
    /// <summary>
    /// Wire message type. Values are frozen by
    /// <c>backend/shared/proto/wire.proto</c> and shared by both encodings, so a
    /// peer routes a message the same way whichever encoding it arrived in.
    /// Never renumber; only append.
    /// </summary>
    public enum MsgType : byte
    {
        /// <summary>Not a valid wire type. Both servers reject a frame carrying it.</summary>
        Unspecified = 0,

        /// <summary>client -> gateway: <c>{token}</c>.</summary>
        Auth = 1,

        /// <summary>gateway -> client: <c>{ok, user_id, error}</c>.</summary>
        AuthResp = 2,

        /// <summary>client -> gateway: <c>{map_id}</c>.</summary>
        EnterWorld = 3,

        /// <summary>gateway -> client: <c>{server_addr, join_token, transport, error}</c>.</summary>
        EnterWorldResp = 4,

        /// <summary>client -> game server: <c>{token}</c>.</summary>
        JoinToken = 5,

        /// <summary>game server -> client: <c>{ok, user_id, error}</c>.</summary>
        JoinTokenResp = 6,

        /// <summary>client -> game server, once per client input tick.</summary>
        Input = 7,

        /// <summary>game server -> client, once per server tick.</summary>
        Snapshot = 8,

        /// <summary>Either direction. Carries an optional reason.</summary>
        Disconnect = 9,

        /// <summary>client -> game server: promote the next snapshot to a keyframe.</summary>
        Resync = 10,

        /// <summary>Either direction, heartbeat probe.</summary>
        Ping = 11,

        /// <summary>Either direction, heartbeat reply.</summary>
        Pong = 12,

        /// <summary>client -> game server: request a map transfer. Not implemented client-side yet.</summary>
        TransferMap = 13,

        /// <summary>game server -> client: transfer result. Not implemented client-side yet.</summary>
        TransferMapResp = 14,

        /// <summary>server -> client: forced disconnect carrying a machine-readable reason.</summary>
        Kick = 15,

        /// <summary>
        /// client -> game server: opens the sealed-session handshake with an ephemeral
        /// X25519 public key. Gameplay hop only, and <b>Protobuf only</b>.
        /// </summary>
        /// <remarks>
        /// Absent from the JSON message set on purpose, so key material can never be
        /// rendered into a human-readable payload — which is also why a JSON client is
        /// refused by a server that requires sealing rather than served in the clear.
        /// </remarks>
        SealedClientHello = 16,

        /// <summary>
        /// game server -> client: the server's ephemeral public key and its binding over
        /// the handshake transcript. Protobuf only, for the same reason as
        /// <see cref="SealedClientHello"/>.
        /// </summary>
        SealedServerHello = 17
    }
}
