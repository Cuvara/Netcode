namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// gateway -> client (2). Also the gateway's generic error frame: a failed
    /// precondition on any request other than <c>enter_world</c> comes back here
    /// with <see cref="Ok"/> false.
    /// </summary>
    public sealed class AuthResponse : IWireMessage
    {
        public bool Ok { get; set; }

        public string UserId { get; set; } = string.Empty;

        /// <summary>
        /// One of the gateway's closed error set (<c>invalid token</c>,
        /// <c>session expired</c>, <c>rate limited</c>, <c>internal error</c>, ...).
        /// Never internal error text.
        /// </summary>
        public string Error { get; set; } = string.Empty;
    }
}
