using Cuvara.Netcode.Client;
using NUnit.Framework;

namespace Cuvara.Netcode.Tests.Editor
{
    public sealed class ConnectionStateEventTests
    {
        [Test]
        public void Event_ToString_ShowsTransition()
        {
            var evt = new ConnectionStateChangedEvent(
                NetworkClientState.Disconnected,
                NetworkClientState.Authenticating);
            Assert.AreEqual("Disconnected → Authenticating", evt.ToString());
        }

        [Test]
        public void Event_WithReason_ShowsReason()
        {
            var evt = new ConnectionStateChangedEvent(
                NetworkClientState.InWorld,
                NetworkClientState.Ended,
                "server_shutdown");
            Assert.AreEqual("InWorld → Ended (server_shutdown)", evt.ToString());
        }

        [Test]
        public void Event_NullReason_BecomesEmpty()
        {
            var evt = new ConnectionStateChangedEvent(
                NetworkClientState.Disconnected,
                NetworkClientState.Authenticating,
                null);
            Assert.AreEqual("", evt.Reason);
        }

        [Test]
        public void Event_PreviousAndCurrent_AreCorrect()
        {
            var evt = new ConnectionStateChangedEvent(
                NetworkClientState.Authenticating,
                NetworkClientState.Assigning);
            Assert.AreEqual(NetworkClientState.Authenticating, evt.Previous);
            Assert.AreEqual(NetworkClientState.Assigning, evt.Current);
        }

        [Test]
        public void AllStates_ExistInEnum()
        {
            // Verify all expected states are defined
            Assert.AreEqual(0, (int)NetworkClientState.Disconnected);
            Assert.AreEqual(1, (int)NetworkClientState.Authenticating);
            Assert.AreEqual(2, (int)NetworkClientState.Assigning);
            Assert.AreEqual(3, (int)NetworkClientState.Joining);
            Assert.AreEqual(4, (int)NetworkClientState.InWorld);
            Assert.AreEqual(5, (int)NetworkClientState.Transferring);
            Assert.AreEqual(6, (int)NetworkClientState.Ended);
        }
    }
}
