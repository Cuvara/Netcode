using NUnit.Framework;
using Cuvara.Netcode.Client;

namespace Cuvara.Netcode.Tests.Editor
{
    /// <summary>
    /// The first-connect join loop's retry decision (<c>NetworkClient.IsRetryable</c>), pinned
    /// from the code as it was before the failure classifier. Editor-only: NetworkClient is
    /// UniTask-bound. Expected values come from
    /// <see cref="FailureDecisionBaselineTests.ServerErrors"/>, written from the old code.
    /// </summary>
    [TestFixture]
    public sealed class JoinRetryBaselineTests
    {
        [Test]
        public void JoinLoop_RetriesExactlyTheBaselineErrors()
        {
            foreach (var pair in FailureDecisionBaselineTests.ServerErrors)
            {
                Assert.That(
                    NetworkClient.IsRetryable(new NetworkException("x", pair.Key)),
                    Is.EqualTo(pair.Value.JoinRetries),
                    pair.Key);
            }
        }
    }
}
