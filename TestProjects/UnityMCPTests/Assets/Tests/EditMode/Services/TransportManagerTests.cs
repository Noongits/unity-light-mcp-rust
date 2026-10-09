using System.Threading.Tasks;
using NUnit.Framework;
using MCPForUnity.Editor.Services.Transport;

namespace MCPForUnityTests.Editor.Services
{
    /// <summary>
    /// Pins TransportManager.StartAsync's coalescing contract: concurrent starts for the
    /// same mode share one in-flight attempt instead of racing — a second StartAsync would
    /// otherwise tear down the first connection mid-handshake (manual Connect vs the
    /// reload-resume/auto-start loops).
    /// </summary>
    public class TransportManagerTests
    {
        private sealed class PendingTransportClient : IMcpTransportClient
        {
            public readonly TaskCompletionSource<bool> Pending = new TaskCompletionSource<bool>();
            public int StartCalls;

            public bool IsConnected => false;
            public string TransportName => "http";
            public TransportState State { get; } = TransportState.Disconnected("http");

            public Task<bool> StartAsync()
            {
                StartCalls++;
                return Pending.Task;
            }

            public Task StopAsync() => Task.CompletedTask;
            public Task<bool> VerifyAsync() => Task.FromResult(false);
            public Task ReregisterToolsAsync() => Task.CompletedTask;
        }

        private sealed class RestartingTransportClient : IMcpTransportClient
        {
            public readonly TaskCompletionSource<bool> First = new TaskCompletionSource<bool>();
            public int StartCalls, StopCalls;
            public bool IsConnected { get; private set; }
            public string TransportName => "http";
            public TransportState State => IsConnected ? TransportState.Connected("http") : TransportState.Disconnected("http");
            public Task<bool> StartAsync()
            {
                if (++StartCalls == 1) return First.Task;
                IsConnected = true;
                return Task.FromResult(true);
            }
            public Task StopAsync() { StopCalls++; IsConnected = false; return Task.CompletedTask; }
            public Task<bool> VerifyAsync() => Task.FromResult(IsConnected);
            public Task ReregisterToolsAsync() => Task.CompletedTask;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void StopDuringStart_ThenRestart_DoesNotReuseOrCleanUpRetiredAttempt(bool oldSucceeded)
        {
            var client = new RestartingTransportClient();
            var manager = new TransportManager();
            manager.Configure(() => client, () => client);
            var old = manager.StartAsync(TransportMode.Http);
            Assert.IsTrue(manager.StopAsync(TransportMode.Http).IsCompletedSuccessfully);
            var replacement = manager.StartAsync(TransportMode.Http);
            Assert.AreEqual(2, client.StartCalls);
            Assert.AreNotSame(old, replacement);
            Assert.IsTrue(replacement.Result);
            client.First.SetResult(oldSucceeded);
            Assert.IsTrue(old.IsCompletedSuccessfully);
            Assert.IsFalse(old.Result, "retired attempt cannot report the replacement's success");
            Assert.AreEqual(1, client.StopCalls, "late failure must not stop replacement");
            Assert.IsTrue(client.IsConnected);
        }

        [Test]
        public void StartAsync_ConcurrentCallsSameMode_CoalesceIntoOneAttempt()
        {
            var client = new PendingTransportClient();
            var manager = new TransportManager();
            manager.Configure(() => client, () => client);

            Task<bool> first = manager.StartAsync(TransportMode.Http);
            Task<bool> second = manager.StartAsync(TransportMode.Http);

            Assert.AreEqual(1, client.StartCalls, "concurrent starts must share one client attempt");
            Assert.AreSame(first, second, "the in-flight task is returned to concurrent callers");

            client.Pending.SetResult(true); // let the shared attempt finish
        }

        /// <summary>
        /// Stdio fake whose connectivity is flipped by the test without going through
        /// StartAsync/StopAsync — mirrors StdioBridgeHost binding via its editor-idle
        /// retry after a busy-port domain reload, which bypasses TransportManager.
        /// </summary>
        private sealed class ExternallyControlledStdioClient : IMcpTransportClient
        {
            public bool Connected;
            public int Port = 6400;

            public bool IsConnected => Connected;
            public string TransportName => "stdio";
            public TransportState State => Connected
                ? TransportState.Connected("stdio", port: Port)
                : TransportState.Disconnected("stdio", "Bridge not running");

            public Task<bool> StartAsync()
            {
                Connected = true;
                return Task.FromResult(true);
            }

            public Task StopAsync()
            {
                Connected = false;
                return Task.CompletedTask;
            }

            public Task<bool> VerifyAsync() => Task.FromResult(Connected);
            public Task ReregisterToolsAsync() => Task.CompletedTask;
        }

        /// <summary>
        /// The bridge binding via its editor-idle retry (no StartAsync) must surface as
        /// connected through GetState/IsRunning, port included.
        /// </summary>
        [Test]
        public void GetState_Stdio_ReconcilesWhenBridgeStartsOutsideManager()
        {
            var client = new ExternallyControlledStdioClient();
            var manager = new TransportManager();
            manager.Configure(() => client, () => client);

            Assert.IsFalse(manager.IsRunning(TransportMode.Stdio), "sanity: starts disconnected");

            client.Connected = true; // bridge bound via editor-idle retry, no StartAsync involved

            Assert.IsTrue(manager.IsRunning(TransportMode.Stdio),
                "manager must report the live bridge even when it started outside StartAsync");
            TransportState state = manager.GetState(TransportMode.Stdio);
            Assert.IsTrue(state.IsConnected);
            Assert.AreEqual(6400, state.Port, "reconciled state comes from the client, port included");
        }

        /// <summary>
        /// A listener that died without StopAsync (e.g. socket teardown on reload) must stop
        /// being reported as connected.
        /// </summary>
        [Test]
        public void GetState_Stdio_ReconcilesWhenBridgeStopsOutsideManager()
        {
            var client = new ExternallyControlledStdioClient();
            var manager = new TransportManager();
            manager.Configure(() => client, () => client);

            Task<bool> started = manager.StartAsync(TransportMode.Stdio);
            Assert.IsTrue(started.IsCompleted && started.Result, "fake start completes synchronously");
            Assert.IsTrue(manager.IsRunning(TransportMode.Stdio));

            client.Connected = false; // listener died without StopAsync (e.g. socket teardown on reload)

            Assert.IsFalse(manager.IsRunning(TransportMode.Stdio),
                "manager must not report a bridge that is no longer listening");
        }

        /// <summary>
        /// A stop/start cycle between two reads can rebind to a different port while both
        /// snapshots stay "connected" (the 6400↔6401 busy-port fallback); GetState must
        /// surface the new port, not the stale one.
        /// </summary>
        [Test]
        public void GetState_Stdio_ReconcilesWhenBridgePortChangesWhileConnected()
        {
            var client = new ExternallyControlledStdioClient();
            var manager = new TransportManager();
            manager.Configure(() => client, () => client);

            client.Connected = true;
            Assert.AreEqual(6400, manager.GetState(TransportMode.Stdio).Port, "sanity: initial port");

            client.Port = 6401; // bridge restarted onto the fallback port between reads

            TransportState state = manager.GetState(TransportMode.Stdio);
            Assert.IsTrue(state.IsConnected);
            Assert.AreEqual(6401, state.Port, "a rebind while connected must refresh the reported port");
        }

        private sealed class ReconnectingHttpClient : IMcpTransportClient
        {
            public TransportState Current = TransportState.Connected("websocket", sessionId: "first");
            public bool IsConnected => Current.IsConnected;
            public string TransportName => "websocket";
            public TransportState State => Current;
            public Task<bool> StartAsync() => Task.FromResult(true);
            public Task StopAsync()
            {
                Current = TransportState.Disconnected("websocket");
                return Task.CompletedTask;
            }
            public Task<bool> VerifyAsync() => Task.FromResult(IsConnected);
            public Task ReregisterToolsAsync() => Task.CompletedTask;
        }

        [Test]
        public void GetState_Http_TracksDisconnectAndReconnectWithoutVerify()
        {
            var client = new ReconnectingHttpClient();
            var manager = new TransportManager();
            manager.Configure(() => client, () => client);
            Assert.IsTrue(manager.StartAsync(TransportMode.Http).Result);
            Assert.AreEqual("first", manager.GetState(TransportMode.Http).SessionId);

            client.Current = TransportState.Disconnected("websocket", "Server closed connection");
            Assert.IsFalse(manager.IsRunning(TransportMode.Http));
            Assert.AreEqual("Server closed connection", manager.GetState(TransportMode.Http).Error);

            client.Current = TransportState.Connected("websocket", sessionId: "second");
            Assert.IsTrue(manager.IsRunning(TransportMode.Http));
            Assert.AreEqual("second", manager.GetState(TransportMode.Http).SessionId);
            Assert.IsNull(manager.GetState(TransportMode.Http).Error);
        }

        [Test]
        public void GetState_Http_TracksRegistrationAndRetryErrorsWithoutConnectivityChange()
        {
            var client = new ReconnectingHttpClient();
            var manager = new TransportManager();
            manager.Configure(() => client, () => client);
            Assert.IsTrue(manager.StartAsync(TransportMode.Http).Result);

            // Registration can arrive after StartAsync has returned the pending snapshot.
            client.Current = TransportState.Connected("websocket", sessionId: "registered");
            Assert.AreEqual("registered", manager.GetState(TransportMode.Http).SessionId);
            client.Current = TransportState.Disconnected("websocket", "Retrying");
            Assert.IsFalse(manager.IsRunning(TransportMode.Http));
            client.Current = client.Current.WithError("Server unreachable");
            Assert.AreEqual("Server unreachable", manager.GetState(TransportMode.Http).Error);
        }

        [Test]
        public void GetState_Http_AfterStop_ReportsDisconnected()
        {
            var client = new ReconnectingHttpClient();
            var manager = new TransportManager();
            manager.Configure(() => client, () => client);
            Assert.IsTrue(manager.StartAsync(TransportMode.Http).Result);
            Assert.IsTrue(manager.StopAsync(TransportMode.Http).IsCompletedSuccessfully);
            Assert.IsFalse(manager.IsRunning(TransportMode.Http));
            Assert.IsNull(manager.GetState(TransportMode.Http).SessionId);
        }

        [Test]
        public void GetState_Http_BeforeStart_DoesNotCreateClient()
        {
            var manager = new TransportManager();
            int calls = 0;
            manager.Configure(() => { calls++; return new ReconnectingHttpClient(); }, () => new FakeTransportClient());
            Assert.IsFalse(manager.GetState(TransportMode.Http).IsConnected);
            Assert.AreEqual(0, calls);
        }

        [Test]
        public void StartAsync_AfterCompletedStart_StartsFresh()
        {
            var client = new FakeTransportClient();
            var manager = new TransportManager();
            manager.Configure(() => client, () => client);

            Task<bool> first = manager.StartAsync(TransportMode.Http);
            Assert.IsTrue(first.IsCompleted && first.Result, "fake start should complete synchronously");

            Task<bool> second = manager.StartAsync(TransportMode.Http);
            Assert.AreEqual(2, client.StartCalls, "a completed start must not block later restarts");
            Assert.IsTrue(second.IsCompleted && second.Result);
        }
    }
}
