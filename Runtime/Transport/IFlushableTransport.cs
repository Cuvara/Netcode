using System.Threading;
using Cysharp.Threading.Tasks;

namespace Cuvara.Netcode.Transport
{
    /// <summary>
    /// A transport whose writes can still be in flight after <see cref="ITransport.WriteFrameAsync"/>
    /// has returned, and which can wait for them to be acknowledged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// KCP returns from a write as soon as the frame is in its send queue; delivery is the ARQ's
    /// business, and it needs the socket and its timer to stay alive. Closing straight after the
    /// last write is therefore a race the last frame usually loses. That is how a graceful leave
    /// never told the server anything: the disconnect frame was queued and the socket closed in
    /// the same call, and the server released the entity only on its KCP dead-link timeout
    /// (rpg-factory testlab S08G, both runs).
    /// </para>
    /// <para>
    /// A separate interface, not a member of <see cref="ITransport"/>, so existing transports and
    /// test doubles keep compiling. TCP does not need it: a write completes into the kernel, and
    /// an orderly close sends what is queued before its FIN.
    /// </para>
    /// </remarks>
    public interface IFlushableTransport
    {
        /// <summary>
        /// Waits until everything written has been acknowledged by the peer, bounded by the
        /// transport's own budget.
        /// </summary>
        /// <returns>True when everything was acknowledged; false when the budget ran out or the
        /// link failed first.</returns>
        UniTask<bool> FlushAsync(CancellationToken cancellationToken);
    }
}
