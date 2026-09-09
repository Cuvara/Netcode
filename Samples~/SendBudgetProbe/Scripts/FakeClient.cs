using System.Collections.Generic;
using Pb = RpgMmo.Wire.V1;

namespace Cuvara.Netcode.Samples.SendBudgetProbe
{
    /// <summary>
    /// A client, to the extent the wire contract defines one: it resolves entity handles
    /// against bindings it was actually sent, refuses to guess, and reconstructs the AOI
    /// set with the same keyframe/delta rule <c>SnapshotMerger</c> applies.
    /// </summary>
    /// <remarks>
    /// The counter that matters here is <see cref="HandleErrors"/>. wire.proto is explicit
    /// that a receiver seeing a handle it has no binding for MUST NOT guess — it has lost
    /// state the sender assumed it had, and its only correct move is <c>MsgResync</c>. If
    /// a downlink budget ever shed an entity while recording it as sent, this is where it
    /// would show: a handle-only mention of an entity whose introduction never arrived.
    /// The scene puts the number on screen so it can be watched staying at zero under
    /// pressure, which is a far stronger statement than an assertion nobody runs.
    /// </remarks>
    public sealed class FakeClient
    {
        private readonly Dictionary<uint, string> _bindings = new Dictionary<uint, string>();
        private readonly Dictionary<string, Pb.EntitySnapshot> _entities =
            new Dictionary<string, Pb.EntitySnapshot>();

        /// <summary>Handles that arrived with no binding. Must stay at zero.</summary>
        public int HandleErrors { get; private set; }

        /// <summary>Entities the client currently believes are visible.</summary>
        public int Count => _entities.Count;

        /// <summary>Keyframes applied.</summary>
        public int Keyframes { get; private set; }

        public void Reset()
        {
            _bindings.Clear();
            _entities.Clear();
            HandleErrors = 0;
            Keyframes = 0;
        }

        public void Receive(Pb.SnapshotMessage msg)
        {
            if (msg.Full)
            {
                // A keyframe resets the handle space on both sides, and replaces the AOI
                // set outright: anything absent must be dropped. An entity the server's
                // budget deferred out of a keyframe therefore disappears here and is
                // re-introduced by a following delta — a visible pop, never wrong state.
                _bindings.Clear();
                _entities.Clear();
                Keyframes++;
            }

            for (int i = 0; i < msg.Entities.Count; i++)
            {
                Pb.EntitySnapshot e = msg.Entities[i];
                string id;

                if (e.Handle != 0)
                {
                    if (e.Id.Length > 0)
                    {
                        _bindings[e.Handle] = e.Id;
                        id = e.Id;
                    }
                    else if (!_bindings.TryGetValue(e.Handle, out id))
                    {
                        HandleErrors++;
                        continue;
                    }
                }
                else
                {
                    id = e.Id;
                }

                _entities[id] = e;
            }

            for (int i = 0; i < msg.Removed.Count; i++)
            {
                _entities.Remove(msg.Removed[i]);
            }
        }

        /// <summary>
        /// Entities the server considers visible that this client does not have right, i.e.
        /// the staleness the budget is trading for a bounded frame.
        /// </summary>
        public int CountStale(CrowdEntity[] crowd, int visibleCount)
        {
            int stale = 0;
            for (int i = 0; i < visibleCount; i++)
            {
                string id = SendBudgetModel.IdFor(crowd[i].Key);
                if (!_entities.TryGetValue(id, out Pb.EntitySnapshot have)
                    || !have.X.Equals(crowd[i].X)
                    || !have.Y.Equals(crowd[i].Y)
                    || have.Hp != crowd[i].Hp)
                {
                    stale++;
                }
            }
            return stale;
        }
    }
}
