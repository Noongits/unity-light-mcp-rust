using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Services.Transport.Transports;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    [TestFixture]
    public class WebSocketTransportClientTests
    {
        private const string CandidateBuilderMethodName = "BuildConnectionCandidateUris";
        private const string WebSocketTransportClientTypeName = "MCPForUnity.Editor.Services.Transport.Transports.WebSocketTransportClient";
        private static readonly MethodInfo BuildConnectionCandidateUrisMethod = ResolveCandidateBuilderMethod();

        [Test]
        public void SocketClosure_ReportsDisconnectedWhileReconnectIsPending()
        {
            var client = new WebSocketTransportClient();
            var lifecycle = new CancellationTokenSource();
            var connection = new CancellationTokenSource();
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            void Set(string name, object value) => typeof(WebSocketTransportClient).GetField(name, flags).SetValue(client, value);
            Set("_lifecycleCts", lifecycle);
            Set("_connectionCts", connection);
            Set("_isConnected", true);
            Set("_state", TransportState.Connected("websocket", sessionId: "old"));
            // Cancel lifecycle as teardown cancels the old connection. This exercises closure
            // and its queued reconnect without opening a socket or relying on a server.
            using var registration = connection.Token.Register(lifecycle.Cancel);
            try
            {
                var task = (Task)typeof(WebSocketTransportClient)
                    .GetMethod("HandleSocketClosureAsync", flags).Invoke(client, new object[] { "Test closure" });
                Assert.IsTrue(task.IsCompletedSuccessfully);
                Assert.IsFalse(client.IsConnected);
                Assert.IsFalse(client.State.IsConnected, "snapshot must agree with the disconnected client");
                Assert.AreEqual("Test closure", client.State.Error);
                Assert.IsNull(client.State.SessionId, "old session cannot remain advertised during reconnect");
                Assert.IsTrue(SpinWait.SpinUntil(
                    () => (int)typeof(WebSocketTransportClient).GetField("_isReconnectingFlag", flags).GetValue(client) == 0,
                    TimeSpan.FromSeconds(5)), "cancelled reconnect should finish without network I/O");
            }
            finally { client.Dispose(); }
        }

        [Test]
        public void QueuedReconnect_AfterForceStop_UsesCapturedCanceledToken()
        {
            var client = new WebSocketTransportClient();
            var lifecycle = new CancellationTokenSource();
            CancellationToken token = lifecycle.Token;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            typeof(WebSocketTransportClient).GetField("_lifecycleCts", flags).SetValue(client, lifecycle);
            client.ForceStop(); // disposes source and clears field before queued callback runs
            Assert.IsTrue(token.IsCancellationRequested);

            // A retired reconnect must not await or clear connection tasks it no longer owns.
            var unrelatedReceive = new TaskCompletionSource<bool>();
            var receiveField = typeof(WebSocketTransportClient).GetField("_receiveTask", flags);
            receiveField.SetValue(client, unrelatedReceive.Task);
            try
            {
                var reconnect = (Task)typeof(WebSocketTransportClient)
                    .GetMethod("ScheduleReconnectAsync", flags).Invoke(client, new object[] { token });
                Assert.IsTrue(reconnect.Wait(TimeSpan.FromSeconds(5)),
                    "cancelled queued reconnect must finish even after the lifecycle field is cleared");
                Assert.IsTrue(reconnect.IsCompletedSuccessfully);
                Assert.AreSame(unrelatedReceive.Task, receiveField.GetValue(client));
                Assert.IsFalse(client.IsConnected);
            }
            finally
            {
                unrelatedReceive.TrySetResult(true);
                client.ForceStop();
                client.Dispose();
            }
        }

        [Test]
        public void BuildConnectionCandidateUris_NullEndpoint_ReturnsEmptyList()
        {
            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(null);

            // Assert
            Assert.IsNotNull(candidates);
            Assert.AreEqual(0, candidates.Count);
        }

        [Test]
        public void BuildConnectionCandidateUris_NonLocalhost_ReturnsOriginalOnly()
        {
            // Arrange
            var endpoint = new Uri("ws://127.0.0.1:8080/hub/plugin");

            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(endpoint);

            // Assert
            Assert.AreEqual(1, candidates.Count);
            Assert.AreEqual(endpoint, candidates[0]);
        }

        [Test]
        public void BuildConnectionCandidateUris_Localhost_AddsIPv4AndIPv6Fallbacks()
        {
            // Arrange
            var endpoint = new Uri("ws://localhost:8080/hub/plugin");

            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(endpoint);

            // Assert
            Assert.AreEqual(3, candidates.Count);
            CollectionAssert.AreEqual(
                new[] { "localhost", "127.0.0.1", "::1" },
                candidates.Select(uri => NormalizeHostForComparison(uri.Host)).ToArray());

            int uniqueCount = candidates
                .Select(uri => uri.AbsoluteUri)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            Assert.AreEqual(candidates.Count, uniqueCount, "Fallback list should not contain duplicate endpoints.");
        }

        [Test]
        public void BuildConnectionCandidateUris_LocalhostFallbacks_PreserveSchemePortPathAndQuery()
        {
            // Arrange
            var endpoint = new Uri("wss://localhost:9443/custom/path?mode=test");

            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(endpoint);

            // Assert
            Assert.AreEqual(3, candidates.Count);
            foreach (Uri candidate in candidates)
            {
                Assert.AreEqual("wss", candidate.Scheme);
                Assert.AreEqual(9443, candidate.Port);
                Assert.AreEqual("/custom/path", candidate.AbsolutePath);
                Assert.AreEqual("?mode=test", candidate.Query);
            }
        }

        private static List<Uri> InvokeBuildConnectionCandidateUris(Uri endpoint)
        {
            if (BuildConnectionCandidateUrisMethod == null)
            {
                Assert.Fail(BuildMissingMethodDiagnostic());
            }
            var result = BuildConnectionCandidateUrisMethod.Invoke(null, new object[] { endpoint });
            Assert.IsNotNull(result);
            Assert.IsInstanceOf<List<Uri>>(result);
            return (List<Uri>)result;
        }

        private static MethodInfo ResolveCandidateBuilderMethod()
        {
            MethodInfo direct = GetCandidateBuilderMethod(typeof(WebSocketTransportClient));
            if (direct != null)
            {
                return direct;
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidateType = assembly.GetType(WebSocketTransportClientTypeName);
                if (candidateType == null)
                {
                    continue;
                }

                MethodInfo method = GetCandidateBuilderMethod(candidateType);
                if (method != null)
                {
                    return method;
                }
            }

            return null;
        }

        private static MethodInfo GetCandidateBuilderMethod(Type type)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            MethodInfo direct = type.GetMethod(
                CandidateBuilderMethodName,
                flags,
                binder: null,
                types: new[] { typeof(Uri) },
                modifiers: null);
            if (direct != null)
            {
                return direct;
            }

            // Fallback for environments where signature binding can differ between loaded copies.
            return type.GetMethods(flags).FirstOrDefault(method =>
            {
                if (!string.Equals(method.Name, CandidateBuilderMethodName, StringComparison.Ordinal))
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(Uri);
            });
        }

        private static string BuildMissingMethodDiagnostic()
        {
            var sb = new StringBuilder();
            sb.Append("Expected private candidate builder method to exist. Searched loaded assemblies for ")
              .Append(WebSocketTransportClientTypeName)
              .Append('.')
              .Append(CandidateBuilderMethodName)
              .Append(". Loaded candidate types:");

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidateType = assembly.GetType(WebSocketTransportClientTypeName);
                if (candidateType == null)
                {
                    continue;
                }

                sb.Append("\n- ")
                  .Append(assembly.FullName)
                  .Append(" @ ")
                  .Append(string.IsNullOrEmpty(assembly.Location) ? "<dynamic>" : assembly.Location);
            }

            return sb.ToString();
        }

        private static string NormalizeHostForComparison(string host)
        {
            if (string.IsNullOrEmpty(host))
            {
                return host;
            }

            return host.Trim('[', ']');
        }
    }
}
