namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// One entry of <see cref="EntitySnapshot.Stats"/> (wire <c>StatValue</c>, ADR-30): a content
    /// stat id and its current value. Ids are content (ADR-19), allocated from 1 so a proto3
    /// zero can never read as a real stat.
    /// </summary>
    public readonly struct StatValue
    {
        public StatValue(uint statId, int value)
        {
            StatId = statId;
            Value = value;
        }

        /// <summary>Content stat id, 1 or greater.</summary>
        public uint StatId { get; }

        /// <summary>Current value (<c>sint32</c> on the wire).</summary>
        public int Value { get; }

        public override string ToString() => $"{StatId}={Value}";
    }
}
