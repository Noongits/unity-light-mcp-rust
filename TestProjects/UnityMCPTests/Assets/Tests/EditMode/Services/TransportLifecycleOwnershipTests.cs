using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport.Transports;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace MCPForUnityTests.Editor.Services
{
    public class TransportLifecycleOwnershipTests
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.NonPublic;

        private static void Set(WebSocketTransportClient client, string field, object value)
            => typeof(WebSocketTransportClient).GetField(field, InstanceFlags).SetValue(client, value);
        private static object Get(WebSocketTransportClient client, string field)
            => typeof(WebSocketTransportClient).GetField(field, InstanceFlags).GetValue(client);

        [Test]
        public void StopLoops_AwaitsOnlyRetiredTasks_AndDoesNotClearReplacement()
        {
            using var client = new WebSocketTransportClient();
            var oldReceive = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var newReceive = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var newConnection = new CancellationTokenSource();
            Set(client, "_receiveTask", oldReceive.Task);
            Set(client, "_connectionCts", new CancellationTokenSource());
            var stop = (Task)typeof(WebSocketTransportClient).GetMethod("StopConnectionLoopsAsync", InstanceFlags)
                .Invoke(client, new object[] { true });
            Assert.IsFalse(stop.IsCompleted);
            Set(client, "_receiveTask", newReceive.Task);
            Set(client, "_connectionCts", newConnection);
            try
            {
                oldReceive.SetResult(true);
                Assert.IsTrue(stop.Wait(TimeSpan.FromSeconds(5)));
                Assert.AreSame(newReceive.Task, Get(client, "_receiveTask"));
                Assert.AreSame(newConnection, Get(client, "_connectionCts"));
                Assert.IsFalse(newConnection.IsCancellationRequested);
            }
            finally { newReceive.TrySetResult(true); }
        }

        [Test]
        public void StopThenForceStop_CompletesWithoutReadingDisposedLifecycleField()
        {
            using var client = new WebSocketTransportClient();
            var receive = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Set(client, "_lifecycleCts", new CancellationTokenSource());
            Set(client, "_connectionCts", new CancellationTokenSource());
            Set(client, "_receiveTask", receive.Task);
            var stop = client.StopAsync();
            Assert.IsFalse(stop.IsCompleted);
            client.ForceStop();
            receive.SetResult(true);
            Assert.IsTrue(stop.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(stop.IsCompletedSuccessfully);
            Assert.IsFalse(client.IsConnected);
        }

        [UnityTest]
        public IEnumerator QueuedStart_ThenForceStop_DoesNotResurrectLifecycle()
        {
            using var client = new WebSocketTransportClient();
            var gate = (SemaphoreSlim)Get(client, "_lifecycleGate");
            Assert.IsTrue(gate.Wait(0));
            var start = client.StartAsync();
            Assert.IsFalse(start.IsCompleted);
            client.ForceStop();
            gate.Release();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!start.IsCompleted && DateTime.UtcNow < deadline) yield return null;
            Assert.IsTrue(start.IsCompletedSuccessfully);
            Assert.IsFalse(start.Result);
            Assert.IsNull(Get(client, "_lifecycleCts"));
        }

        [Test]
        public void CanceledConnectionClosure_DoesNotRetireCurrentConnection()
        {
            using var client = new WebSocketTransportClient();
            using var retired = new CancellationTokenSource();
            retired.Cancel();
            using var current = new CancellationTokenSource();
            Set(client, "_lifecycleCts", new CancellationTokenSource());
            Set(client, "_connectionCts", current);
            var task = (Task)typeof(WebSocketTransportClient).GetMethod("HandleConnectionClosureAsync", InstanceFlags)
                .Invoke(client, new object[] { "late old closure", retired.Token });
            Assert.IsTrue(task.IsCompletedSuccessfully);
            Assert.AreSame(current, Get(client, "_connectionCts"));
            Assert.IsFalse(current.IsCancellationRequested);
        }

        [Test]
        public void RetiredListenerCallback_UsesCapturedCanceledTokenWithoutCurrentFields()
        {
            using var retired = new CancellationTokenSource();
            retired.Cancel();
            var capturedListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            var loop = typeof(StdioBridgeHost).GetMethod("ListenerLoopAsync", StaticFlags);
            Assert.NotNull(loop);
            // No listener is bound. A retired worker must finish before dereferencing
            // either the captured listener or this editor's current CTS/listener fields.
            var task = (Task)loop.Invoke(null, new object[] { capturedListener, retired.Token });
            Assert.IsTrue(task.IsCompletedSuccessfully);
        }

        [Test]
        public void FrameWriteTimeout_RemainsArmedWhileWriteIsPending()
        {
            var write = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken captured = default;
            var task = StdioBridgeHost.WriteWithTimeoutAsync(token =>
            {
                captured = token;
                return write.Task;
            }, 30);
            try
            {
                Assert.IsFalse(task.IsCompleted);
                Assert.IsTrue(SpinWait.SpinUntil(() => captured.IsCancellationRequested, TimeSpan.FromSeconds(5)),
                    "disposing the timeout source when the write returns an incomplete Task disables its timer");
            }
            finally { write.TrySetResult(true); }
            Assert.IsTrue(task.Wait(TimeSpan.FromSeconds(5)));
        }

        [Test]
        public void StopBeforeStdioBind_CancelsBothDeferredStartCallbacks()
        {
            if (StdioBridgeHost.IsRunning) Assert.Ignore("Requires an idle bridge to avoid interrupting a real session.");
            var type = typeof(StdioBridgeHost);
            if ((bool)type.GetField("initScheduled", StaticFlags).GetValue(null)
                || (bool)type.GetField("ensureUpdateHooked", StaticFlags).GetValue(null))
                Assert.Ignore("An existing bridge retry must be preserved.");
            type.GetMethod("ScheduleInitRetry", StaticFlags).Invoke(null, null);
            StdioBridgeHost.Stop();
            Assert.IsFalse((bool)type.GetField("initScheduled", StaticFlags).GetValue(null));
            Assert.IsFalse((bool)type.GetField("ensureUpdateHooked", StaticFlags).GetValue(null));
            // Simulate callbacks already copied into Unity's dispatch list before Stop.
            type.GetMethod("InitializeAfterCompilation", StaticFlags).Invoke(null, null);
            type.GetMethod("EnsureStartedOnEditorIdle", StaticFlags).Invoke(null, null);
            Assert.IsFalse(StdioBridgeHost.IsRunning);
        }

        [Test]
        public void AcceptedClient_CanceledBeforeHandlerStarts_IsDisposedWithoutNetworkIo()
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var client = new System.Net.Sockets.TcpClient();
            var task = StdioBridgeHost.HandleAcceptedClientAsync(client, canceled.Token);
            Assert.IsTrue(task.IsCompletedSuccessfully);
            Assert.IsNull(client.Client);
        }

        [Test]
        public void CanceledHttpAutoStart_DoesNotProbeOrReconnect()
        {
            bool previous = SessionState.GetBool(HttpAutoStartHandler.ConnectPendingKey, false);
            try
            {
                SessionState.SetBool(HttpAutoStartHandler.ConnectPendingKey, true);
                HttpAutoStartHandler.CancelPendingReconnect();
                var type = typeof(HttpAutoStartHandler);
                foreach (var method in new[] { "WaitForServerAndConnectAsync", "ConnectBridgeAsync" })
                {
                    var task = (Task)type.GetMethod(method, StaticFlags).Invoke(null, null);
                    Assert.IsTrue(task.IsCompletedSuccessfully, method + " must stop before touching network/services");
                }
            }
            finally { SessionState.SetBool(HttpAutoStartHandler.ConnectPendingKey, previous); }
        }

        // Replaced source-order assertions with delayed runtime regression tests in
        // AutoStartGenerationRegressionTests. Source order cannot establish ownership.
    }
}
