namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// client -> game server (5). Must be the first frame on the gameplay socket;
    /// the game server rejects anything else with <c>Expected JoinToken message</c>.
    /// </summary>
    public sealed class JoinTokenRequest : IWireMessage
    {
        public string Token { get; set; } = string.Empty;
    }
}
