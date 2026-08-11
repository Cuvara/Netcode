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
        Kick = 15
    }
}
