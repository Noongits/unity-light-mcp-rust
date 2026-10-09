using System;
using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine.TestTools;
using NUnit.Framework;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using UnityEditor;

namespace MCPForUnityTests.Editor.Services
{
    /// <summary>
    /// Tests for the auto-start tick decisions (#1229): the session latch must only be
    /// written when the deferred work actually dispatches, so a domain reload that wipes
    /// the pending tick can no longer consume the once-per-session auto-start.
    /// TickCore is a pure decision — it never writes the latch and never spawns servers.
    /// The TryBeginReconnect tests only exercise its deliberate-drop paths, which never
    /// dispatch the async connect.
    /// </summary>
    public class HttpAutoStartHandlerTests : TransportPreferenceTestBase
    {
        private FakeTransportClient _fakeClient;
        private TransportManager _savedManager;
        private bool _savedLatch;
        private bool _savedConnectPending;
        private bool _savedResumeFlag;
        private bool _savedAutoStart;
        private bool _savedUseHttpTransport;

        [SetUp]
        public void SetUp()
        {
            _savedLatch = SessionState.GetBool(HttpAutoStartHandler.SessionInitKey, false);
            _savedConnectPending = SessionState.GetBool(HttpAutoStartHandler.ConnectPendingKey, false);
            _savedResumeFlag = SessionState.GetBool(HttpBridgeReloadHandler.ResumeSessionKey, false);
            _savedAutoStart = EditorPrefs.GetBool(EditorPrefKeys.AutoStartOnLoad, false);
            _savedUseHttpTransport = EditorPrefs.GetBool(EditorPrefKeys.UseHttpTransport, true);
            _savedManager = MCPServiceLocator.TransportManager;

            SessionState.EraseBool(HttpAutoStartHandler.SessionInitKey);
            SessionState.EraseBool(HttpAutoStartHandler.ConnectPendingKey);
            SessionState.EraseBool(HttpBridgeReloadHandler.ResumeSessionKey);
            EditorPrefs.SetBool(EditorPrefKeys.AutoStartOnLoad, false);
            EditorPrefs.SetBool(EditorPrefKeys.UseHttpTransport, true);
            EditorConfigurationCache.Instance.Refresh();

            _fakeClient = new FakeTransportClient();
            var manager = new TransportManager();
            manager.Configure(() => _fakeClient, () => _fakeClient);
            MCPServiceLocator.Register(manager);
        }

        [TearDown]
        public void TearDown()
        {
            // Stored-false and absent are indistinguishable: every read uses GetBool(key, false).
            SessionState.SetBool(HttpAutoStartHandler.SessionInitKey, _savedLatch);
            SessionState.SetBool(HttpAutoStartHandler.ConnectPendingKey, _savedConnectPending);
            SessionState.SetBool(HttpBridgeReloadHandler.ResumeSessionKey, _savedResumeFlag);
            EditorPrefs.SetBool(EditorPrefKeys.AutoStartOnLoad, _savedAutoStart);
            EditorPrefs.SetBool(EditorPrefKeys.UseHttpTransport, _savedUseHttpTransport);
            EditorConfigurationCache.Instance.Refresh();
            MCPServiceLocator.Register(_savedManager);
        }

        private static bool LatchSet =>
            SessionState.GetBool(HttpAutoStartHandler.SessionInitKey, false);

        private static bool ConnectPendingSet =>
            SessionState.GetBool(HttpAutoStartHandler.ConnectPendingKey, false);

        private sealed class WaitingServer : IServerManagementService
        {
            public int Probes;
            public bool IsLocalHttpServerReachable()
            {
                if (++Probes > 1) throw new InvalidOperationException("Canceled poll probed again");
                return false;
            }
            public bool HasManagedServerLaunchHandle => false;
            public bool ClearUvxCache() => throw new NotSupportedException();
            public bool StartLocalHttpServer(bool quiet = false) => throw new NotSupportedException();
            public string GetLocalHttpServerLaunchLogPath() => null;
            public bool IsManagedServerLaunchProcessAlive() => false;
            public void LogLocalHttpServerLaunchFailure() => throw new NotSupportedException();
            public bool StopLocalHttpServer() => throw new NotSupportedException();
            public bool StopManagedLocalHttpServer() => throw new NotSupportedException();
            public bool IsLocalHttpServerRunning() => false;
            public bool TryGetLocalHttpServerCommand(out string command, out string error)
            { command = null; error = null; return false; }
            public bool IsLocalUrl() => true;
            public bool CanStartLocalServer() => false;
        }

        [UnityTest]
        public IEnumerator CancelDuringServerWait_PreventsNextPollAndConnect()
        {
            var savedServer = MCPServiceLocator.Server;
            var waiting = new WaitingServer();
            MCPServiceLocator.Register<IServerManagementService>(waiting);
            Task task = null;
            try
            {
                EditorPrefs.SetBool(EditorPrefKeys.AutoStartOnLoad, true);
                SessionState.SetBool(HttpAutoStartHandler.ConnectPendingKey, true);
                task = (Task)typeof(HttpAutoStartHandler).GetMethod("WaitForServerAndConnectAsync",
                    BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                Assert.AreEqual(1, waiting.Probes);
                Assert.IsFalse(task.IsCompleted, "first unsuccessful probe should suspend the poll");
                HttpAutoStartHandler.CancelPendingReconnect();
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!task.IsCompleted && DateTime.UtcNow < deadline) yield return null;
                Assert.IsTrue(task.IsCompletedSuccessfully, "canceled auto-start must exit without probing again");
                Assert.AreEqual(1, waiting.Probes);
                Assert.AreEqual(0, _fakeClient.StartCalls);
            }
            finally
            {
                HttpAutoStartHandler.CancelPendingReconnect();
                MCPServiceLocator.Register<IServerManagementService>(savedServer);
            }
        }

        [Test]
        public void TickCore_EditorBusy_Defers()
        {
            EditorPrefs.SetBool(EditorPrefKeys.AutoStartOnLoad, true);

            Assert.AreEqual(
                HttpAutoStartHandler.TickDecision.DeferBusy,
                HttpAutoStartHandler.TickCore(editorBusy: true));
            Assert.IsFalse(LatchSet);
        }

        [Test]
        public void TickCore_ResumePending_YieldsToReloadHandler()
        {
            EditorPrefs.SetBool(EditorPrefKeys.AutoStartOnLoad, true);
            SessionState.SetBool(HttpBridgeReloadHandler.ResumeSessionKey, true);

            Assert.AreEqual(
                HttpAutoStartHandler.TickDecision.DeferToResume,
                HttpAutoStartHandler.TickCore(editorBusy: false));
            Assert.IsFalse(LatchSet);
        }

        [Test]
        public void TickCore_ResumePendingButNothingToDo_SkipsWithoutWaiting()
        {
            // Auto-start disabled and not latched: a pending resume must not keep the
            // tick alive when the only possible outcome is Skip.
            SessionState.SetBool(HttpBridgeReloadHandler.ResumeSessionKey, true);

            Assert.AreEqual(
                HttpAutoStartHandler.TickDecision.Skip,
                HttpAutoStartHandler.TickCore(editorBusy: false));
        }

        [Test]
        public void TickCore_AutoStartDisabled_SkipsWithoutLatch()
        {
            Assert.AreEqual(
                HttpAutoStartHandler.TickDecision.Skip,
                HttpAutoStartHandler.TickCore(editorBusy: false));
            Assert.IsFalse(LatchSet, "no latch when disabled — the pref is re-read on the next domain load");
        }

        [Test]
        public void TickCore_AutoStartEnabled_ShouldStartWithoutWritingLatch()
        {
            EditorPrefs.SetBool(EditorPrefKeys.AutoStartOnLoad, true);

            Assert.AreEqual(
                HttpAutoStartHandler.TickDecision.ShouldStart,
                HttpAutoStartHandler.TickCore(editorBusy: false));
            Assert.IsFalse(LatchSet, "the caller latches only after the start work actually dispatches");
        }

        [Test]
        public void TickCore_Latched_Skips()
        {
            EditorPrefs.SetBool(EditorPrefKeys.AutoStartOnLoad, true);
            SessionState.SetBool(HttpAutoStartHandler.SessionInitKey, true);

            Assert.AreEqual(
                HttpAutoStartHandler.TickDecision.Skip,
                HttpAutoStartHandler.TickCore(editorBusy: false));
        }

        [Test]
        public void TickCore_LatchedWithConnectPending_Reconnects()
        {
            SessionState.SetBool(HttpAutoStartHandler.SessionInitKey, true);
            SessionState.SetBool(HttpAutoStartHandler.ConnectPendingKey, true);

            Assert.AreEqual(
                HttpAutoStartHandler.TickDecision.ShouldReconnect,
                HttpAutoStartHandler.TickCore(editorBusy: false),
                "a reload that killed the in-flight connect should finish connect-only, never re-spawn");
        }

        [Test]
        public void TickCore_ResumePendingBeatsReconnect()
        {
            SessionState.SetBool(HttpAutoStartHandler.SessionInitKey, true);
            SessionState.SetBool(HttpAutoStartHandler.ConnectPendingKey, true);
            SessionState.SetBool(HttpBridgeReloadHandler.ResumeSessionKey, true);

            Assert.AreEqual(
                HttpAutoStartHandler.TickDecision.DeferToResume,
                HttpAutoStartHandler.TickCore(editorBusy: false),
                "the reload handler owns bridge revival while a resume is pending");
        }

        [Test]
        public void TryBeginReconnect_AutoStartDisabled_DropsPendingReconnect()
        {
            SessionState.SetBool(HttpAutoStartHandler.ConnectPendingKey, true);

            Assert.IsTrue(HttpAutoStartHandler.TryBeginReconnect());
            Assert.IsFalse(ConnectPendingSet, "a deliberate drop must consume the pending marker");
        }

        [Test]
        public void TryBeginReconnect_StdioSelected_DropsPendingReconnect()
        {
            EditorPrefs.SetBool(EditorPrefKeys.AutoStartOnLoad, true);
            EditorPrefs.SetBool(EditorPrefKeys.UseHttpTransport, false);
            EditorConfigurationCache.Instance.Refresh();
            SessionState.SetBool(HttpAutoStartHandler.ConnectPendingKey, true);

            Assert.IsTrue(HttpAutoStartHandler.TryBeginReconnect());
            Assert.IsFalse(ConnectPendingSet);
        }

        [Test]
        public void TryBeginReconnect_BridgeAlreadyRunning_DropsPendingReconnect()
        {
            EditorPrefs.SetBool(EditorPrefKeys.AutoStartOnLoad, true);
            var start = MCPServiceLocator.TransportManager.StartAsync(TransportMode.Http);
            Assert.IsTrue(start.IsCompleted && start.Result, "fake bridge should start synchronously");
            SessionState.SetBool(HttpAutoStartHandler.ConnectPendingKey, true);

            Assert.IsTrue(HttpAutoStartHandler.TryBeginReconnect());
            Assert.IsFalse(ConnectPendingSet);
            Assert.AreEqual(1, _fakeClient.StartCalls, "an already-running bridge must not be restarted");
        }
    }
}
