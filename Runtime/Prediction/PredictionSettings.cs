using Shared.GameLogic.Components;

namespace Cuvara.Netcode.Prediction
{
    /// <summary>
    /// The three facts a client must share with the server before it is allowed to
    /// predict: tick rate, movement speed, and map bounds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not defaulted, deliberately.</b> Every field here has a plausible default —
    /// <c>GameConstants.DefaultTickRate</c>, the map's default size, the server's
    /// starting player speed — and taking any of them silently is the failure this type
    /// exists to prevent. Prediction that runs against the wrong speed does not fail
    /// loudly; it produces a position that is wrong by a little on every tick, gets
    /// corrected by every snapshot, and reads to a player as rubber-banding rather than
    /// as a misconfiguration. A caller that cannot state these three numbers should not
    /// be predicting, so <see cref="LocalMovePredictor"/> refuses rather than guesses.
    /// </para>
    /// <para>
    /// <b>Speed is the fragile one, and it is the only one the server can correct.</b>
    /// Tick rate and bounds are per-map constants the caller has to get right by knowing
    /// them. Speed is a per-entity server stat (<c>Locomotion.Speed</c>), so a value
    /// stated here is only ever the client's belief about it — and a wrong belief does
    /// not fail loudly. It produces a position wrong by a little on every tick, corrected
    /// by every snapshot, which reads as rubber-banding rather than as a
    /// misconfiguration. See <see cref="LocalMovePredictor"/> for what that looks like on
    /// screen.
    /// </para>
    /// <para>
    /// <b>Since the wire carries speed, <see cref="Speed"/> is the fallback rather than
    /// the answer.</b> Snapshots include a per-entity speed
    /// (<c>wire.proto</c> field 9), and
    /// <see cref="LocalMovePredictor.SetServerSpeed"/> adopts it — the binder calls that
    /// on every snapshot for the local entity, so a buff, mount or slow is picked up
    /// rather than silently desynced. Do not conclude from this paragraph that the speed
    /// must be maintained by hand: it must be <i>stated</i>, because the predictor
    /// refuses to run without one, but the server's value supersedes it as soon as one
    /// arrives.
    /// </para>
    /// <para>
    /// This value therefore matters in exactly two situations, and it must still be
    /// right in both: before the first snapshot, and against a server predating field 9.
    /// A zero on the wire means "not sent" rather than "immobile" — proto3 elides a zero
    /// float — so <see cref="LocalMovePredictor.SetServerSpeed"/> ignores non-positive
    /// values and this value stands. <see cref="LocalMovePredictor.EffectiveSpeed"/>
    /// reports which one is live; it diverging from this is the normal, healthy case.
    /// </para>
    /// </remarks>
    public readonly struct PredictionSettings
    {
        /// <summary>
        /// Simulation tick rate in Hz. Must equal the server's: it is the sole source of
        /// the <c>dt</c> both sides integrate with, and a mismatch scales every predicted
        /// step by the ratio.
        /// </summary>
        public readonly int TickRate;

        /// <summary>
        /// Fallback movement speed in world units per second — what replay integrates
        /// with until a snapshot supplies the server's own value for this entity.
        /// </summary>
        /// <remarks>
        /// Should match the server's spawn default (<c>ServerDefaults.DefaultPlayerSpeed</c>).
        /// It governs in exactly two situations, and has to be right in both: before the
        /// first snapshot, and against a server predating <c>wire.proto</c> field 9. Once
        /// a positive speed arrives it is superseded — see
        /// <see cref="LocalMovePredictor.SetServerSpeed"/> and
        /// <see cref="LocalMovePredictor.EffectiveSpeed"/>.
        /// </remarks>
        public readonly float Speed;

        /// <summary>Play area the server clamps positions into.</summary>
        public readonly MapBounds Bounds;

        public PredictionSettings(int tickRate, float speed, MapBounds bounds)
        {
            TickRate = tickRate;
            Speed = speed;
            Bounds = bounds;
        }

        /// <summary>
        /// Whether these settings can drive a prediction. False for a non-positive or
        /// non-finite tick rate or speed — the two values a caller is most likely to
        /// leave at zero by forgetting to set them.
        /// </summary>
        public bool IsUsable =>
            TickRate > 0 &&
            Speed > 0f &&
            !float.IsNaN(Speed) &&
            !float.IsInfinity(Speed);
    }
}
