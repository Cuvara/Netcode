using Cuvara.Netcode.Protocol.Messages;

namespace Cuvara.Netcode.Snapshot
{
    /// <summary>
    /// One game event with its participants' handles resolved to entity ids.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An unresolved participant is reported as an empty id, never guessed, and never
    /// forces a resync on its own.</b> That is a deliberate asymmetry with
    /// <see cref="ResolvedEntity"/>, where an unresolvable handle aborts the whole snapshot:
    /// a wrong entity STATE is a wrong world and has to be repaired, while a missing damage
    /// number is a missing damage number. Escalating a presentation-only gap into a
    /// world-wide keyframe would spend a keyframe's bandwidth for every observer to fix a
    /// floating number nobody would have missed — and would do it precisely when the link is
    /// already struggling.
    /// </para>
    /// <para>
    /// Unresolved participants are COUNTED
    /// (<see cref="SnapshotResolver.UnresolvedEventParticipants"/>) rather than logged, so
    /// the case stays visible without a log line per event.
    /// </para>
    /// </remarks>
    public readonly struct ResolvedGameEvent
    {
        public ResolvedGameEvent(
            GameEventType type, string sourceId, string targetId,
            int amount, uint abilityId, GameEventFlags flags)
        {
            Type = type;
            SourceId = sourceId;
            TargetId = targetId;
            Amount = amount;
            AbilityId = abilityId;
            Flags = flags;
        }

        public GameEventType Type { get; }

        /// <summary>
        /// Entity id of the causer, or empty for "none, not visible to this client, or not
        /// resolvable". A consumer renders the event without a source rather than dropping
        /// it: a player who sees the victim but not the attacker still needs the number.
        /// </summary>
        public string SourceId { get; }

        /// <summary>
        /// Entity id of the subject, or empty. An event with no resolvable target is the one
        /// case most consumers should skip — there is nowhere to draw it.
        /// </summary>
        public string TargetId { get; }

        public int Amount { get; }

        public uint AbilityId { get; }

        public GameEventFlags Flags { get; }

        public bool HasSource => !string.IsNullOrEmpty(SourceId);

        public bool HasTarget => !string.IsNullOrEmpty(TargetId);

        public bool IsCritical => (Flags & GameEventFlags.Critical) != 0;

        public override string ToString() =>
            $"{Type}({(HasSource ? SourceId : "-")} -> {(HasTarget ? TargetId : "-")}, " +
            $"amount={Amount}, ability={AbilityId}, flags={Flags})";
    }
}
