using System;
using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Windows.Components.Connection;
using NUnit.Framework;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using UnityEngine.TestTools;

namespace MCPForUnityTests.Editor.Services
{
    public class ConnectionUiGenerationTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private sealed class WaitingServer : IServerManagementService
        {
            public int Probes;
            public bool IsLocalHttpServerReachable()
            {
                if (++Probes > 1) throw new InvalidOperationException("retired UI operation probed again");
                return false;
            }
            public bool HasManagedServerLaunchHandle => false;
            public bool ClearUvxCache() => throw new NotSupportedException();
            public bool StartLocalHttpServer(bool quiet = false) => throw new NotSupportedException();
            public string GetLocalHttpServerLaunchLogPath() => null;
            public bool IsManagedServerLaunchProcessAlive() => true;
            public void LogLocalHttpServerLaunchFailure() => throw new NotSupportedException();
            public bool StopLocalHttpServer() => throw new NotSupportedException();
            public bool StopManagedLocalHttpServer() => throw new NotSupportedException();
            public bool IsLocalHttpServerRunning() => false;
            public bool TryGetLocalHttpServerCommand(out string command, out string error)
            { command = null; error = null; return false; }
            public bool IsLocalUrl() => true;
            public bool CanStartLocalServer() => false;
        }

        private sealed class WaitingBridge : IBridgeControlService
        {
            public int Starts;
            public bool IsRunning { get; set; } = true;
            public int CurrentPort => 0;
            public bool IsAutoConnectMode => false;
            public TransportMode? ActiveMode => TransportMode.Http;
            public readonly TaskCompletionSource<BridgeVerificationResult> Verification = new();
            public Task<bool> StartAsync() { Starts++; return Task.FromResult(false); }
            public Task StopAsync() => throw new NotSupportedException();
            public BridgeVerificationResult Verify(int port) => throw new NotSupportedException();
            public Task<BridgeVerificationResult> VerifyAsync() => Verification.Task;
        }

        private static McpConnectionSection CreateSection()
        {
            // Skip the constructor's real polling/registration. Supply only the input
            // used by the operation under test; all service calls use fakes below.
            var section = (McpConnectionSection)FormatterServices.GetUninitializedObject(typeof(McpConnectionSection));
            var protocol = typeof(McpConnectionSection).GetNestedType("TransportProtocol", BindingFlags.NonPublic);
            var selected = (Enum)Enum.Parse(protocol, "HTTPLocal");
            var dropdown = new EnumField(selected);
            typeof(McpConnectionSection).GetField("transportDropdown", Flags).SetValue(section, dropdown);
            return section;
        }

        private static void Invalidate(McpConnectionSection section)
            => typeof(McpConnectionSection).GetMethod("InvalidatePendingOperations", Flags).Invoke(section, null);

        [UnityTest]
        public IEnumerator ManualTakeoverDuringServerWait_PreventsNextPollAndStart()
        {
            var oldServer = MCPServiceLocator.Server;
            var oldBridge = MCPServiceLocator.Bridge;
            var server = new WaitingServer();
            var bridge = new WaitingBridge();
            MCPServiceLocator.Register<IServerManagementService>(server);
            MCPServiceLocator.Register<IBridgeControlService>(bridge);
            var section = CreateSection();
            try
            {
                var task = (Task)typeof(McpConnectionSection).GetMethod("TryAutoStartSessionAsync", Flags)
                    .Invoke(section, new object[] { 0, HttpEndpointUtility.GetLocalBaseUrl() });
                Assert.AreEqual(1, server.Probes);
                Assert.IsFalse(task.IsCompleted);
                Invalidate(section); // same retirement path used by transport changes and manual controls
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!task.IsCompleted && DateTime.UtcNow < deadline) yield return null;
                Assert.IsTrue(task.IsCompletedSuccessfully);
                Assert.AreEqual(1, server.Probes);
                Assert.AreEqual(0, bridge.Starts);
            }
            finally
            {
                Invalidate(section);
                MCPServiceLocator.Register<IServerManagementService>(oldServer);
                MCPServiceLocator.Register<IBridgeControlService>(oldBridge);
            }
        }

        [UnityTest]
        public IEnumerator RetiredVerification_DoesNotPublishHealthForReplacement()
        {
            var oldBridge = MCPServiceLocator.Bridge;
            var bridge = new WaitingBridge();
            MCPServiceLocator.Register<IBridgeControlService>(bridge);
            var section = CreateSection();
            int updates = 0;
            section.SetHealthStatusUpdateCallback((_, __) => updates++);
            try
            {
                var task = section.VerifyBridgeConnectionAsync();
                Assert.IsFalse(task.IsCompleted);
                Invalidate(section);
                bridge.Verification.SetResult(new BridgeVerificationResult { Success = true, PingSucceeded = true });
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!task.IsCompleted && DateTime.UtcNow < deadline) yield return null;
                Assert.IsTrue(task.IsCompletedSuccessfully);
                Assert.AreEqual(0, updates);
            }
            finally
            {
                bridge.Verification.TrySetResult(new BridgeVerificationResult());
                MCPServiceLocator.Register<IBridgeControlService>(oldBridge);
            }
        }
    }
}
