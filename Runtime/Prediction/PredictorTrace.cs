using System;
using System.Globalization;
using System.Text;
using Shared.GameLogic.Systems;
using Shared.GameLogic.World;

namespace Cuvara.Netcode.Prediction
{
    /// <summary>One traced call; see <see cref="PredictorTraceEvent"/> for which fields each one uses.</summary>
    public enum PredictorCall : byte
    {
        /// <summary><c>UseServerProtocol(A)</c>.</summary>
        UseServerProtocol = 1,
        /// <summary><c>UseServerTickRate(A)</c>.</summary>
        UseServerTickRate = 2,
        /// <summary><c>SetMapGeometry</c>; <c>Flag</c> = a geometry was given. The replayer applies its own.</summary>
        SetMapGeometry = 3,
        /// <summary><c>SetMotorParams</c>: X Gravity, Y JumpSpeed, Z StepHeight, W MaxSlopeRise, U CapsuleRadius, V CapsuleHeight.</summary>
        SetMotorParams = 4,
        /// <summary><c>RecordInput(A tick, X moveX, Y moveY, Flag jump)</c>.</summary>
        RecordInput = 5,
        /// <summary>A reconcile, as the predictor received it: X/Y/Z position, W vertical velocity, Flag grounded,
        /// A ackTick, B snapshot tick, C ackAppliedTick.</summary>
        Reconcile = 6,
        /// <summary><c>Advance(X deltaTime)</c>.</summary>
        Advance = 7,
        /// <summary><c>SetServerSpeed(X)</c>.</summary>
        SetServerSpeed = 8,
        /// <summary><c>SeedBaseTick(A)</c>.</summary>
        SeedBaseTick = 9,
        /// <summary><c>SteerToServerTick(A serverTick, B targetLeadTicks)</c>.</summary>
        SteerToServerTick = 10,
        /// <summary>The steering marked the snapshot at A as part of a backlog (B = lead).</summary>
        SkipReconcileForBunchedSnapshot = 11,
        /// <summary><c>SetClockRateScale(X)</c>.</summary>
        SetClockRateScale = 12,
        /// <summary><c>LocalMovePredictor.Reset()</c>.</summary>
        Reset = 13,
        /// <summary><c>SetHoldTicks(A)</c>.</summary>
        SetHoldTicks = 14,

        /// <summary>Output, after a reconcile: X LastCorrection, Y/Z simulated position, W snaps,
        /// A BaseTick, B TickError, C StallResyncs.</summary>
        ReconcileResult = 32,

        /// <summary><c>PredictionClockSteering.OnSnapshot</c> begins: A worldTick, B ackTick, T now seconds.
        /// Predictor calls up to <see cref="SteeringOnSnapshotEnd"/> are its effects.</summary>
        SteeringOnSnapshotBegin = 64,
        /// <summary><c>PredictionClockSteering.OnSnapshot</c> ended.</summary>
        SteeringOnSnapshotEnd = 65,
        /// <summary><c>PredictionClockSteering.NoteInputSent(A inputTick, T now)</c>.</summary>
        SteeringNoteInputSent = 66,
        /// <summary><c>PredictionClockSteering.SampleTickRate(A worldTick, T now)</c>.</summary>
        SteeringSampleTickRate = 67,
        /// <summary><c>PredictionClockSteering.RoundTripMs = A</c>.</summary>
        SteeringRoundTripMs = 68,
        /// <summary><c>PredictionClockSteering.Reset()</c>.</summary>
        SteeringReset = 69,
    }

    /// <summary>
    /// Receives each traced call. Called synchronously, on the caller's thread, before the call
    /// does anything (results: after). The event is passed by reference and must not be kept:
    /// copy or format it.
    /// </summary>
    public delegate void PredictorTraceSink(in PredictorTraceEvent e);

    /// <summary>
    /// One call into <see cref="LocalMovePredictor"/> or <see cref="PredictionClockSteering"/>, with
    /// its arguments: the record a headless replay needs to reproduce a live session exactly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> Three successive stall fixes passed the synthetic harness and not the live client,
    /// because the harness modelled the client's frame order and timing instead of recording them
    /// (rpg-factory testlab S13, Netcode 461be3a, 44db5bb, d5ffb75). The predictor is deterministic
    /// given its calls, and the server's half (snapshots, acknowledgements) does not depend on the
    /// client's prediction, so a recorded call sequence replays a live session exactly, and a fix
    /// can then be judged against what the client really did.
    /// </para>
    /// <para>
    /// A plain struct of fixed fields, so producing one allocates nothing; the meaning of each field
    /// per call is on <see cref="PredictorCall"/>. <see cref="AppendJson"/> and
    /// <see cref="TryParseJson"/> are one stable NDJSON form for files; a consumer may use its own.
    /// </para>
    /// </remarks>
    public readonly struct PredictorTraceEvent
    {
        public readonly PredictorCall Call;
        public readonly long A;
        public readonly long B;
        public readonly long C;
        public readonly float X;
        public readonly float Y;
        public readonly float Z;
        public readonly float W;
        public readonly float U;
        public readonly float V;
        public readonly bool Flag;

        /// <summary>Seconds, on the caller's clock, for the steering calls that take a time.</summary>
        public readonly double T;

        public PredictorTraceEvent(
            PredictorCall call, long a = 0, long b = 0, long c = 0,
            float x = 0f, float y = 0f, float z = 0f, float w = 0f, float u = 0f, float v = 0f,
            bool flag = false, double t = 0.0)
        {
            Call = call;
            A = a;
            B = b;
            C = c;
            X = x;
            Y = y;
            Z = z;
            W = w;
            U = u;
            V = v;
            Flag = flag;
            T = t;
        }

        /// <summary>
        /// Appends this event as one JSON object (no newline): <c>{"c":6,"a":..,"b":..,"c2":..,"x":..,
        /// "y":..,"z":..,"w":..,"u":..,"v":..,"f":0,"t":..}</c>. Floats round-trip exactly ("R").
        /// </summary>
        public void AppendJson(StringBuilder sb)
        {
            var inv = CultureInfo.InvariantCulture;
            sb.Append("{\"c\":").Append(((int)Call).ToString(inv))
              .Append(",\"a\":").Append(A.ToString(inv))
              .Append(",\"b\":").Append(B.ToString(inv))
              .Append(",\"c2\":").Append(C.ToString(inv))
              .Append(",\"x\":").Append(X.ToString("R", inv))
              .Append(",\"y\":").Append(Y.ToString("R", inv))
              .Append(",\"z\":").Append(Z.ToString("R", inv))
              .Append(",\"w\":").Append(W.ToString("R", inv))
              .Append(",\"u\":").Append(U.ToString("R", inv))
              .Append(",\"v\":").Append(V.ToString("R", inv))
              .Append(",\"f\":").Append(Flag ? '1' : '0')
              .Append(",\"t\":").Append(T.ToString("R", inv))
              .Append('}');
        }

        /// <summary>Parses one line written by <see cref="AppendJson"/>. Other keys are ignored.</summary>
        public static bool TryParseJson(string line, out PredictorTraceEvent e)
        {
            e = default;
            if (string.IsNullOrEmpty(line) || line[0] != '{') return false;

            long call = 0, a = 0, b = 0, c = 0;
            float x = 0, y = 0, z = 0, w = 0, u = 0, v = 0;
            bool flag = false, haveCall = false;
            double t = 0;
            var inv = CultureInfo.InvariantCulture;
            int i = 1;
            while (i < line.Length)
            {
                int q1 = line.IndexOf('"', i);
                if (q1 < 0) break;
                int q2 = line.IndexOf('"', q1 + 1);
                if (q2 < 0) return false;
                string key = line.Substring(q1 + 1, q2 - q1 - 1);
                int colon = line.IndexOf(':', q2);
                if (colon < 0) return false;
                int end = colon + 1;
                while (end < line.Length && line[end] != ',' && line[end] != '}') end++;
                string val = line.Substring(colon + 1, end - colon - 1).Trim();
                switch (key)
                {
                    case "c": haveCall = long.TryParse(val, NumberStyles.Integer, inv, out call); break;
                    case "a": long.TryParse(val, NumberStyles.Integer, inv, out a); break;
                    case "b": long.TryParse(val, NumberStyles.Integer, inv, out b); break;
                    case "c2": long.TryParse(val, NumberStyles.Integer, inv, out c); break;
                    case "x": float.TryParse(val, NumberStyles.Float, inv, out x); break;
                    case "y": float.TryParse(val, NumberStyles.Float, inv, out y); break;
                    case "z": float.TryParse(val, NumberStyles.Float, inv, out z); break;
                    case "w": float.TryParse(val, NumberStyles.Float, inv, out w); break;
                    case "u": float.TryParse(val, NumberStyles.Float, inv, out u); break;
                    case "v": float.TryParse(val, NumberStyles.Float, inv, out v); break;
                    case "f": flag = val == "1" || val == "true"; break;
                    case "t": double.TryParse(val, NumberStyles.Float, inv, out t); break;
                }

                i = end + 1;
            }

            if (!haveCall) return false;
            e = new PredictorTraceEvent((PredictorCall)call, a, b, c, x, y, z, w, u, v, flag, t);
            return true;
        }
    }

    /// <summary>
    /// Re-executes a recorded call sequence against a fresh predictor (and steering).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two modes.</b> <see cref="ReplaySteering"/> false (the default) replays the predictor's own
    /// calls, the steering's effects included, and ignores the steering's inputs: an exact replay of
    /// the predictor, the mode that validates a predictor change. True replays the steering's inputs
    /// (<c>OnSnapshot</c>, <c>NoteInputSent</c>, <c>SampleTickRate</c>, the round trip) through
    /// <see cref="Steering"/> and skips the predictor calls recorded inside a steering
    /// <c>OnSnapshot</c>, so a steering change is judged by what it now decides.
    /// </para>
    /// <para>
    /// <b>Map geometry is not in the trace</b>: set <see cref="Geometry"/> to the map the session ran
    /// on, and it is applied where the trace records the call.
    /// </para>
    /// </remarks>
    public sealed class PredictorTraceReplayer
    {
        private bool _insideSteering;

        public PredictorTraceReplayer(LocalMovePredictor predictor, PredictionClockSteering steering = null)
        {
            Predictor = predictor ?? throw new ArgumentNullException(nameof(predictor));
            Steering = steering ?? new PredictionClockSteering(predictor);
        }

        public LocalMovePredictor Predictor { get; }

        public PredictionClockSteering Steering { get; }

        /// <summary>Replay the steering's inputs rather than its recorded effects. See the remarks.</summary>
        public bool ReplaySteering { get; set; }

        /// <summary>Applied at a recorded <see cref="PredictorCall.SetMapGeometry"/>; null means flat.</summary>
        public MapGeometry Geometry { get; set; }

        /// <summary>Applies one recorded event. Output events (results) are ignored.</summary>
        public void Apply(in PredictorTraceEvent e)
        {
            switch (e.Call)
            {
                case PredictorCall.SteeringOnSnapshotBegin:
                    _insideSteering = true;
                    if (ReplaySteering) Steering.OnSnapshot(Predictor, e.A, e.B, e.T);
                    return;
                case PredictorCall.SteeringOnSnapshotEnd:
                    _insideSteering = false;
                    return;
                case PredictorCall.SteeringNoteInputSent:
                    if (ReplaySteering) Steering.NoteInputSent(e.A, e.T);
                    return;
                case PredictorCall.SteeringSampleTickRate:
                    if (ReplaySteering) Steering.SampleTickRate(e.A, e.T);
                    return;
                case PredictorCall.SteeringRoundTripMs:
                    if (ReplaySteering) Steering.RoundTripMs = e.A;
                    return;
                case PredictorCall.SteeringReset:
                    if (ReplaySteering) Steering.Reset();
                    return;
                case PredictorCall.ReconcileResult:
                    return;
            }

            if (ReplaySteering && _insideSteering)
            {
                // The steering being replayed makes these calls itself.
                return;
            }

            switch (e.Call)
            {
                case PredictorCall.UseServerProtocol: Predictor.UseServerProtocol((uint)e.A); break;
                case PredictorCall.UseServerTickRate: Predictor.UseServerTickRate((uint)e.A); break;
                case PredictorCall.SetMapGeometry: Predictor.SetMapGeometry(e.Flag ? Geometry : null); break;
                case PredictorCall.SetMotorParams:
                    Predictor.SetMotorParams(new MotorParams
                    {
                        Gravity = e.X,
                        JumpSpeed = e.Y,
                        StepHeight = e.Z,
                        MaxSlopeRise = e.W,
                        CapsuleRadius = e.U,
                        CapsuleHeight = e.V,
                    });
                    break;
                case PredictorCall.RecordInput: Predictor.RecordInput(e.A, e.X, e.Y, e.Flag); break;
                case PredictorCall.Reconcile: Predictor.ReplayReconcile(in e); break;
                case PredictorCall.Advance: Predictor.Advance(e.X); break;
                case PredictorCall.SetServerSpeed: Predictor.SetServerSpeed(e.X); break;
                case PredictorCall.SeedBaseTick: Predictor.SeedBaseTick(e.A); break;
                case PredictorCall.SteerToServerTick: Predictor.SteerToServerTick(e.A, (int)e.B); break;
                case PredictorCall.SkipReconcileForBunchedSnapshot:
                    Predictor.SkipReconcileForBunchedSnapshot(e.A, (int)e.B);
                    break;
                case PredictorCall.SetClockRateScale: Predictor.SetClockRateScale(e.X); break;
                case PredictorCall.Reset: Predictor.Reset(); break;
                case PredictorCall.SetHoldTicks: Predictor.SetHoldTicks((int)e.A); break;
            }
        }
    }
}
