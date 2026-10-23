namespace Cuvara.Netcode.Protocol.Messages
{
    /// <summary>
    /// client -> game server (7), once per client input tick.
    /// </summary>
    /// <remarks>
    /// This type only carries the fields; it applies no rules. Direction
    /// normalisation, speed, range and cooldown are server-authoritative and live
    /// in <c>Shared.GameLogic</c> — a client-side copy would silently diverge.
    /// </remarks>
    public sealed class InputMessage : IWireMessage
    {
        /// <summary>
        /// The client's own monotonically increasing input sequence number, not a
        /// server tick. The server drops any input whose tick is not strictly
        /// greater than the last it accepted, and echoes the newest accepted value
        /// back as <c>ack_tick</c>.
        /// </summary>
        public long Tick { get; set; }

        /// <summary>Movement direction on X, not a displacement.</summary>
        public float MoveX { get; set; }

        /// <summary>Movement direction on Y, not a displacement.</summary>
        public float MoveY { get; set; }

        /// <summary>Optional entity id to attack this tick. Empty when not attacking.</summary>
        public string AttackTargetId { get; set; } = string.Empty;

        /// <summary>
        /// Content id of the ability to use this tick, or 0 for none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Zero means "no ability".</b> Ability ids are allocated from 1 by the content
        /// validator for exactly this reason: proto3 elides a zero, so id 0 and "sent no
        /// ability" would be identical bytes.
        /// </para>
        /// <para>
        /// <b>This is a request, and it is not predicted.</b> Prediction covers movement
        /// only, because movement is a pure function of input the client already has while
        /// an ability outcome depends on cooldowns, content and other entities' state. The
        /// only report of the outcome is the snapshot: a
        /// <see cref="GameEventType.AbilityCast"/> event if it resolved, nothing if it did
        /// not. Do not move a cooldown bar or play a cast animation off this field — play
        /// them off the event.
        /// </para>
        /// </remarks>
        public uint AbilityId { get; set; }

        /// <summary>
        /// Target entity for an entity-targeted ability, empty otherwise. A server-side
        /// entity ID, in the same space as <see cref="AttackTargetId"/> — never a
        /// <see cref="EntitySnapshot.Handle"/>, which the server allocates for its own
        /// outbound snapshots and would not recognise coming back.
        /// </summary>
        public string AbilityTargetId { get; set; } = string.Empty;

        /// <summary>
        /// Aim POINT in world coordinates for a ground-targeted ability, unlike
        /// <see cref="MoveX"/>/<see cref="MoveY"/> which are a direction. Only read by the
        /// server when <see cref="AbilityId"/> is non-zero; the world origin is a
        /// legitimate aim point, so (0,0) does not mean "not aimed".
        /// </summary>
        public float AimX { get; set; }

        /// <inheritdoc cref="AimX"/>
        public float AimY { get; set; }

        // --- Protocol version 3 (ADR-28, ADR-29). A version 2 server skips these fields
        // (proto3 unknown fields; JSON unknown keys), so sending them to one is harmless and
        // simply has no effect. ---

        /// <summary>
        /// Height of the aim point (wire field 9). With <see cref="AimX"/>/<see cref="AimY"/>
        /// this is the full 3D point a skillshot is fired AT; the server derives the direction
        /// from the caster's own authoritative position, never from a client origin.
        /// </summary>
        public float AimZ { get; set; }

        /// <summary>
        /// The server tick the client was RENDERING remote entities at when it produced this
        /// input -- its interpolation time, not its prediction tick (wire field 10). Lag
        /// compensation rewinds hit targets to this instant, clamped to 200 ms (ADR-29).
        /// <b>Zero means "not sent"</b>: no rewind.
        /// </summary>
        /// <remarks>
        /// Fill it with <see cref="SetRenderTime"/> from the interpolation clock
        /// (<c>WorldViewBinder.RenderTick</c>), which splits the fractional tick into this and
        /// <see cref="RenderAlpha"/>.
        /// </remarks>
        public ulong RenderTick { get; set; }

        /// <summary>
        /// Fraction [0, 1) of the way from <see cref="RenderTick"/> to the next tick that the
        /// client was rendering at (wire field 13). Rewind interpolates hitboxes to it.
        /// </summary>
        public float RenderAlpha { get; set; }

        /// <summary>
        /// Jump request (wire field 11). Level-triggered for the tick it is sent on; the motor
        /// ignores it unless the character is grounded, so a held button does not fly.
        /// </summary>
        public bool Jump { get; set; }

        /// <summary>
        /// Client-chosen sequence number for a projectile this input fires (wire field 12), so
        /// the predicted projectile can be matched to the server's entity
        /// (<see cref="EntitySnapshot.SpawnSeq"/>). Zero when the input fires nothing.
        /// </summary>
        public uint SpawnSeq { get; set; }

        /// <summary>
        /// Splits a fractional interpolation tick into <see cref="RenderTick"/> and
        /// <see cref="RenderAlpha"/>. A value below 1 or non-finite leaves both at zero --
        /// "not sent", so the server applies no rewind rather than a wrong one.
        /// </summary>
        /// <param name="renderTick">
        /// The moment being rendered, in server ticks -- <c>WorldViewBinder.RenderTick</c> /
        /// <c>InterpolationClock.RenderTick</c>.
        /// </param>
        /// <returns>This message, for chaining.</returns>
        public InputMessage SetRenderTime(double renderTick)
        {
            if (double.IsNaN(renderTick) || double.IsInfinity(renderTick) || renderTick < 1.0)
            {
                RenderTick = 0UL;
                RenderAlpha = 0f;
                return this;
            }

            double whole = System.Math.Floor(renderTick);
            float alpha = (float)(renderTick - whole);

            // Float rounding can turn 0.99999999 into 1.0f, which the wire defines as out of
            // range ([0, 1)); fold it into the next tick instead.
            if (alpha >= 1f)
            {
                whole += 1.0;
                alpha = 0f;
            }

            RenderTick = (ulong)whole;
            RenderAlpha = alpha < 0f ? 0f : alpha;
            return this;
        }
    }
}
