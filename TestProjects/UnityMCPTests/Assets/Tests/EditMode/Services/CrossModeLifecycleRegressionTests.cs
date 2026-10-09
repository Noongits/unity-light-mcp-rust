using System;
using System.Collections;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Transport;
using NUnit.Framework;
using MCPForUnity.Editor.Services;
using UnityEditor;
using UnityEngine.TestTools;
namespace MCPForUnityTests.Editor.Services
{
    public class CrossModeLifecycleRegressionTests
    {
        private sealed class Client : IMcpTransportClient
        {
            public bool Connected;
            public int Stops;
            public TaskCompletionSource<bool> StopGate;
            public bool IsConnected => Connected;
            public string TransportName => "fake";
            public TransportState State => Connected ? TransportState.Connected("fake") : TransportState.Disconnected("fake");
            public Task<bool> StartAsync() { Connected = true; return Task.FromResult(true); }
            public async Task StopAsync() { Stops++; Connected = false; if (StopGate != null) await StopGate.Task; }
            public Task<bool> VerifyAsync() => Task.FromResult(Connected);
            public Task ReregisterToolsAsync() => Task.CompletedTask;
        }
        [UnityTest]
        public IEnumerator BridgeStop_DuringOtherModeRetirement_MustCancelPendingStart()
        {
            var previousManager = MCPServiceLocator.TransportManager;
            var config = EditorConfigurationCache.Instance;
            bool wasPinned = SessionState.GetBool(EditorConfigurationCache.SessionKeyForceStdio, false);
            var http = new Client { StopGate = new TaskCompletionSource<bool>() };
            var stdio = new Client();
            var manager = new TransportManager();
            manager.Configure(() => http, () => stdio);
            try
            {
                config.PinStdioForSession();
                MCPServiceLocator.Register(manager);
                Assert.IsTrue(manager.StartAsync(TransportMode.Http).Result);
                var bridge = new BridgeControlService();
                var oldStart = bridge.StartAsync();
                Assert.IsFalse(oldStart.IsCompleted);
                var stop = bridge.StopAsync();
                while (!stop.IsCompleted) yield return null;
                http.StopGate.SetResult(true);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!oldStart.IsCompleted && DateTime.UtcNow < deadline) yield return null;
                Assert.IsTrue(oldStart.IsCompleted);
                Assert.IsFalse(oldStart.Result, "stop retires the preflight start request");
                Assert.IsFalse(stdio.Connected, "retired preflight cannot start stdio after Stop");
            }
            finally
            {
                http.StopGate.TrySetResult(true);
                MCPServiceLocator.Register(previousManager);
                if (!wasPinned) config.UnpinStdioForSession();
            }
        }
        [UnityTest]
        public IEnumerator AllModeStop_WaitingForHttp_MustNotStopNewerStdioStart()
        {
            var http = new Client { StopGate = new TaskCompletionSource<bool>() };
            var stdio = new Client();
            var manager = new TransportManager();
            manager.Configure(() => http, () => stdio);
            Assert.IsTrue(manager.StartAsync(TransportMode.Http).Result);
            Assert.IsTrue(manager.StartAsync(TransportMode.Stdio).Result);
            var stop = manager.StopAsync();
            Assert.IsFalse(stop.IsCompleted);
            Assert.IsTrue(manager.StartAsync(TransportMode.Stdio).Result);
            int stopsBeforeHttpCompletes = stdio.Stops;
            http.StopGate.SetResult(true);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!stop.IsCompleted && DateTime.UtcNow < deadline) yield return null;
            Assert.IsTrue(stop.IsCompletedSuccessfully);
            Assert.AreEqual(stopsBeforeHttpCompletes, stdio.Stops, "retired stop must not issue a new stdio stop after its await");
            Assert.IsTrue(manager.IsRunning(TransportMode.Stdio), "new stdio session survives completion of old all-mode stop");
        }
    }
}
