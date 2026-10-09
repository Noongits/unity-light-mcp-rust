using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Services.Transport.Transports;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    public class WebSocketReconnectPublicationTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Set(WebSocketTransportClient client, string name, object value)
            => typeof(WebSocketTransportClient).GetField(name, Flags).SetValue(client, value);
        private static object Get(WebSocketTransportClient client, string name)
            => typeof(WebSocketTransportClient).GetField(name, Flags).GetValue(client);

        [Test]
        public void CandidateClosesDuringReconnect_RetiresCandidateWithoutSchedulingDuplicate()
        {
            using var client = new WebSocketTransportClient();
            using var lifecycle = new CancellationTokenSource();
            using var connection = new CancellationTokenSource();
            var token = connection.Token;
            Set(client, "_lifecycleCts", lifecycle);
            Set(client, "_connectionCts", connection);
            Set(client, "_isReconnectingFlag", 1);
            Set(client, "_isConnected", true);
            Set(client, "_state", TransportState.Connected("websocket", sessionId: "retired"));
            var close = (Task)typeof(WebSocketTransportClient).GetMethod("HandleConnectionClosureAsync", Flags)
                .Invoke(client, new object[] { "candidate closed during registration", token });
            Assert.IsTrue(close.IsCompletedSuccessfully);
            Assert.IsTrue(token.IsCancellationRequested, "the active attempt must observe this candidate's failure");
            Assert.IsNull(Get(client, "_connectionCts"));
            Assert.IsFalse(client.IsConnected);
            Assert.AreEqual(1, Get(client, "_isReconnectingFlag"), "the existing attempt retains retry ownership");
            bool published = (bool)typeof(WebSocketTransportClient).GetMethod("TryPublishConnected", Flags)
                .Invoke(client, new object[] { lifecycle.Token, true });
            Assert.IsFalse(published, "a closed candidate cannot overwrite Disconnected with Connected");
        }

        [Test]
        public void QueuedStopOvertakenByNewGeneration_DoesNotClearReplacement()
        {
            using var client = new WebSocketTransportClient();
            var gate = (SemaphoreSlim)Get(client, "_lifecycleGate");
            Assert.IsTrue(gate.Wait(0));
            var stop = client.StopAsync();
            Assert.IsFalse(stop.IsCompleted);
            using var replacement = new CancellationTokenSource();
            Set(client, "_lifecycleGeneration", (int)Get(client, "_lifecycleGeneration") + 1);
            Set(client, "_lifecycleCts", replacement);
            gate.Release();
            Assert.IsTrue(stop.Wait(TimeSpan.FromSeconds(5)));
            Assert.AreSame(replacement, Get(client, "_lifecycleCts"));
            Assert.IsFalse(replacement.IsCancellationRequested);
        }
    }
}
