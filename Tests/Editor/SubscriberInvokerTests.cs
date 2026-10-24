using System;
using Cuvara.Netcode.Diagnostics;
using NUnit.Framework;

namespace Cuvara.Netcode.Tests.Editor
{
    [TestFixture]
    public sealed class SubscriberInvokerTests
    {
        private sealed class TestLog : INetLog
        {
            public string Warning;
            public void Info(string message) { }
            public void Warn(string message) => Warning = message;
            public void Error(string message, Exception exception = null) { }
        }

        [Test]
        public void InvokeSafely_LogsExceptionTypeAndStack_ThenInvokesNextSubscriber()
        {
            var log = new TestLog();
            var laterSubscriberCalled = false;
            Action<int> handlers = _ => throw new InvalidOperationException("subscriber failed");
            handlers += _ => laterSubscriberCalled = true;

            SubscriberInvoker.InvokeSafely(handlers, 1, "SnapshotReceived", log);

            Assert.That(laterSubscriberCalled, Is.True);
            Assert.That(log.Warning, Does.Contain(nameof(InvalidOperationException)));
            Assert.That(log.Warning, Does.Contain("at "), "the logged exception should include its stack");
        }

        [Test]
        public void InvokeSafely_DoesNotSwallowOperationCanceledException()
        {
            Action handlers = () => throw new OperationCanceledException();
            Assert.Throws<OperationCanceledException>(() =>
                SubscriberInvoker.InvokeSafely(handlers, "event", new TestLog()));
        }
    }
}
