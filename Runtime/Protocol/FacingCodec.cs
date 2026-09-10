namespace Cuvara.Netcode.Protocol
{
    /// <summary>
    /// Decodes the wire's biased 16-bit binary-radian facing value. Mirrors
    /// <c>shared/messages/facing.go</c> (Go) and <c>GameServer/Net/FacingCodec.cs</c>
    /// (C# server).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the encoding is biased.</b> The obvious wire type is <c>float facing</c> in
    /// radians, and it is wrong: proto3 elides a zero float and 0.0 radians is a
    /// perfectly ordinary facing — due east. A server meaning "east" and a server
    /// predating the field would send IDENTICAL BYTES, and no receiver rule could
    /// separate them. <c>speed</c> has exactly that ambiguity and documents its way
    /// around it because a zero speed is genuinely meaningful; facing has no such
    /// excuse, so the ambiguity is removed by construction — every representable angle
    /// maps to a NON-ZERO wire value, and zero is reserved permanently.
    /// </para>
    /// <para>
    /// <b>The client only ever decodes.</b> There is deliberately no encode half here.
    /// Encoding a direction needs <c>Atan2</c>, which is implementation-defined across
    /// NativeAOT x64 and IL2CPP ARM64 — the reason ADR-10 forbids it in
    /// <c>Shared.GameLogic</c>. Since the client never produces a facing, no
    /// <c>Atan2</c> ever has to agree across runtimes.
    /// </para>
    /// </remarks>
    public static class FacingCodec
    {
        /// <summary>
        /// Representable directions in a full turn. One step is 360/65536 = 0.0055
        /// degrees, far below anything a player can perceive.
        /// </summary>
        public const int BradSteps = 65536;

        /// <summary>
        /// The wire value meaning "the sender has no facing to report". Reserved
        /// permanently; a real facing is always >= 1.
        /// </summary>
        public const uint NotSent = 0;

        private const float TwoPi = 6.28318530717958647692f;

        /// <summary>
        /// Decode a wire facing value into radians counter-clockwise from +X.
        /// Returns false when the sender supplied none.
        /// </summary>
        /// <remarks>
        /// A caller MUST honour a false return rather than using the zeroed
        /// <paramref name="radians"/>: keep the entity's last known facing, or derive one
        /// from its movement. Snapping to east instead means every entity from a server
        /// that predates the field points the same way, which reads as a content bug and
        /// gets debugged as one. An out-of-range value is refused for the same reason — a
        /// wrong facing is much harder to notice than an absent one.
        /// </remarks>
        public static bool TryToRadians(uint brad, out float radians)
        {
            if (brad == NotSent || brad > BradSteps)
            {
                radians = 0f;
                return false;
            }

            radians = (float)((brad - 1) / (double)BradSteps * TwoPi);
            return true;
        }

        /// <summary>
        /// Decode into DEGREES clockwise from +Z, which is what a Unity Y-axis rotation
        /// wants for a server-space (x, y) plane mapped onto Unity's (x, z).
        /// </summary>
        /// <remarks>
        /// The server's angle is counter-clockwise from +X in a right-handed 2D plane;
        /// Unity's Y rotation is clockwise from +Z when viewed from above. The
        /// conversion is therefore <c>90 - degrees</c>, not a bare scale — getting this
        /// wrong produces a mirrored or 90-degree-rotated world that still animates
        /// smoothly, which is the kind of bug that survives a casual look.
        /// </remarks>
        public static bool TryToUnityYaw(uint brad, out float degrees)
        {
            if (!TryToRadians(brad, out float radians))
            {
                degrees = 0f;
                return false;
            }

            degrees = 90f - radians * (180f / 3.14159265358979323846f);
            return true;
        }
    }
}
