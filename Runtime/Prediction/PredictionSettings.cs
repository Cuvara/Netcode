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
    /// <b>Speed is the fragile one.</b> Tick rate and bounds are per-map constants the
    /// caller can reasonably know, but speed is a per-entity server stat
    /// (<c>Locomotion.Speed</c>) that no message on the wire carries today. The client
    /// can only assume the server's spawn default. Anything that changes a player's speed
    /// at runtime — a buff, a mount, a slow — desyncs prediction until the next snapshot
    /// corrects it, and neither side will report an error. See
    /// <see cref="LocalMovePredictor"/> for what that looks like on screen.
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
        /// Local player's movement speed in world units per second, matching the
        /// server's <c>Locomotion.Speed</c> for this entity.
        /// </summary>
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
