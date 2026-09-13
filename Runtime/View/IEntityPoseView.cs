using Shared.GameLogic.Components;

namespace Cuvara.Netcode.View
{
    /// <summary>
    /// An OPTIONAL companion to <see cref="IEntityView"/> for views that want to render
    /// an entity's facing and action. Implement it alongside <see cref="IEntityView"/>;
    /// <see cref="WorldViewBinder"/> detects it and calls
    /// <see cref="SetPose"/> beside every <c>SetState</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is a separate, optional interface rather than two more arguments on
    /// <see cref="IEntityView.SetState"/>.</b> That was tried and reverted. Widening
    /// <c>SetState</c> compiles the day it lands and then bills every OTHER package that
    /// implements the interface — and there are at least three, in three independently
    /// versioned repositories. <c>com.cuvara.dots</c> broke on exactly that, and worse,
    /// its assembly could not even NAME <see cref="EntityAction"/>, so the widening
    /// forced a new assembly reference on a sibling package for a feature it had not
    /// asked for.
    /// </para>
    /// <para>
    /// That package had already written the rule down, about its own
    /// <c>SetStateAtTick</c>: <i>"Not part of IEntityView, and it cannot be. That
    /// interface is netcode's ... widening it would make every GameObject view in every
    /// consumer implement a method it has no use for."</i> <see cref="IEntityView"/>'s
    /// own remarks say the same thing from the other side — the interface is narrow
    /// precisely so an implementation can be swapped cheaply — and
    /// <see cref="WorldViewBinder"/> records a past decision to reconcile a change
    /// through despawn-then-respawn rather than add a fourth method. Three independent
    /// statements of one rule; this type is what following it looks like.
    /// </para>
    /// <para>
    /// The cost of the split is that an entity's state now arrives in two calls rather
    /// than one. They are adjacent and always in that order, so a view that wants both
    /// can treat them as one update. The benefit is that a view which does not care
    /// about facing needs no change at all, ever — not a stub, not a reference, not a
    /// recompile.
    /// </para>
    /// </remarks>
    public interface IEntityPoseView
    {
        /// <summary>
        /// Latest facing and action for an already-spawned id, called immediately after
        /// the matching <see cref="IEntityView.SetState"/>.
        /// </summary>
        /// <param name="facingBrad">
        /// Facing as biased 16-bit binary radians — the wire's own form. Decode with
        /// <see cref="Cuvara.Netcode.Protocol.FacingCodec"/>.
        /// <para>
        /// <b>Zero means "not sent", not "facing east".</b> An implementation MUST keep
        /// whatever facing it was already showing rather than snapping to a default:
        /// otherwise every entity from a server predating the field points the same way,
        /// which reads as a content bug and gets debugged as one. Holding the last value
        /// is also what makes a character that stops walking keep looking where it was
        /// going.
        /// </para>
        /// </param>
        /// <param name="action">
        /// What the entity is doing. <see cref="EntityAction.Unspecified"/> means
        /// "not sent", never "idle" — idle is 1. Keep whatever was being shown rather
        /// than falling back to an idle pose, or a server predating the field freezes
        /// every entity in the world mid-animation.
        /// </param>
        /// <remarks>
        /// Passed raw rather than decoded so this interface makes no presentation
        /// decision on the implementer's behalf: whether an absent facing should hold or
        /// be derived from movement is the view's call, and it is the only layer with the
        /// context to make it.
        /// </remarks>
        void SetPose(string id, uint facingBrad, EntityAction action);
    }
}
