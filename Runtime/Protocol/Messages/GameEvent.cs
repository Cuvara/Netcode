using System;

namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// The kind of an edge-triggered occurrence carried on a snapshot. Mirrors
    /// <c>GameEventType</c> in <c>wire.proto</c>; the numeric values are on the wire and
    /// are FROZEN.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this channel exists.</b> Every other field on a snapshot is level-triggered
    /// state, which is the right shape for state and the wrong shape for an occurrence.
    /// "This entity took 12 damage" is not recoverable from two HP values a tick apart: a
    /// heal and a hit in the same tick net out, a delta may omit the entity entirely, and an
    /// entity that leaves the AOI mid-fight simply stops reporting. A client inferring damage
    /// numbers from HP deltas is wrong in exactly the cases a player notices.
    /// </para>
    /// <para>
    /// <b>Ignore an unrecognised value rather than guessing.</b> Events are presentation, so
    /// dropping an unknown one costs a missing number while guessing costs a wrong one.
    /// </para>
    /// </remarks>
    public enum GameEventType
    {
        /// <summary>Not sent / unknown. A consumer must ignore the event.</summary>
        Unspecified = 0,

        /// <summary>
        /// <see cref="GameEvent.Target"/> took <see cref="GameEvent.Amount"/> damage, AFTER
        /// mitigation — the number to float off a head, not a pre-defense roll.
        /// </summary>
        Damage = 1,

        /// <summary>Target was healed Amount.</summary>
        Heal = 2,

        /// <summary>
        /// Target died, now. Not redundant with
        /// <see cref="Shared.GameLogic.Components.EntityAction.Dead"/>, which is a state
        /// that persists as long as the corpse does — a client arriving afterwards sees
        /// Dead and cannot tell the death just happened. A death animation, a sound and a
        /// kill feed all need this edge.
        /// </summary>
        Death = 3,

        /// <summary>Source resolved a cast of <see cref="GameEvent.AbilityId"/>.</summary>
        AbilityCast = 4,

        /// <summary>Target gained Amount experience. Only ever sent to the subject.</summary>
        XpGain = 5,

        /// <summary>Target reached level Amount. Only ever sent to the subject.</summary>
        LevelUp = 6,
    }

    /// <summary>Presentation flags on a <see cref="GameEvent"/>.</summary>
    /// <remarks>
    /// A bitfield rather than more <see cref="GameEventType"/> values because these COMBINE,
    /// and because an unrecognised BIT can be ignored while still showing a correct number —
    /// where an unrecognised enum value means dropping the whole event.
    /// </remarks>
    [Flags]
    public enum GameEventFlags : uint
    {
        None = 0,
        Critical = 1u << 0,

        /// <summary>The target was immune, or the effect was fully mitigated.</summary>
        Immune = 1u << 1,

        /// <summary>A periodic tick rather than a direct action.</summary>
        Periodic = 1u << 2,
    }

    /// <summary>
    /// One edge-triggered occurrence, exactly as it arrived on the wire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Handles here are the SAME per-connection handles the surrounding snapshot uses</b>,
    /// and they are resolved by the same table. A handle may name an entity that is NOT in
    /// this snapshot's entity list — a delta only carries entities whose state changed, and
    /// the killer of something in your view need not have moved. That is legal and expected.
    /// A handle you have no binding for is not: that means lost interning state, and the
    /// correct response is to request a keyframe, exactly as for an entity.
    /// </para>
    /// <para>
    /// <b>Zero means "no such participant, or not visible to you".</b> A player who can see
    /// the victim but not the attacker still receives the damage number with no source —
    /// which is deliberate, because the alternative is a health bar that drops with no
    /// explanation.
    /// </para>
    /// <para>
    /// <b>Events are never re-sent and a keyframe does not replay them.</b> A keyframe
    /// restates the world's state because a client may have missed a delta; it does not
    /// restate its history. A client that missed the snapshot carrying an event has missed
    /// the event, by design — a damage number arriving late is worse than one that never
    /// arrives.
    /// </para>
    /// </remarks>
    public sealed class GameEvent
    {
        public GameEventType Type { get; set; }

        /// <summary>Handle of the causer, 0 for none / not visible.</summary>
        public uint Source { get; set; }

        /// <summary>Handle of the subject, 0 for none / not visible.</summary>
        public uint Target { get; set; }

        /// <summary>
        /// Full id of the causer. Populated ONLY on the legacy JSON encoding, which has no
        /// handle table; empty on Protobuf. Prefer the handle and fall back to this, the same
        /// precedence <see cref="EntitySnapshot.Id"/>/<see cref="EntitySnapshot.Handle"/> use.
        /// </summary>
        public string SourceId { get; set; } = string.Empty;

        /// <inheritdoc cref="SourceId"/>
        public string TargetId { get; set; } = string.Empty;

        /// <summary>
        /// Magnitude, meaning per <see cref="Type"/>: damage dealt, health restored,
        /// experience gained, level reached.
        /// </summary>
        public int Amount { get; set; }

        /// <summary>Ability involved, 0 for none.</summary>
        public uint AbilityId { get; set; }

        public GameEventFlags Flags { get; set; }

        public override string ToString() =>
            $"{Type}(src={Source}{(SourceId.Length > 0 ? "/" + SourceId : "")} " +
            $"dst={Target}{(TargetId.Length > 0 ? "/" + TargetId : "")} " +
            $"amount={Amount} ability={AbilityId} flags={Flags})";
    }
}
