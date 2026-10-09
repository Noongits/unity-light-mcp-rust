using System;
using System.Collections.Generic;
using System.Diagnostics;
using MCPForUnity.Editor.Services.Server;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services.Server
{
    // All OS observations, signals and sleeps are replaced. These tests cannot signal a PID.
    public class ProcessTerminationIdentityTimelineTests
    {
        private sealed class Detector : IProcessDetector
        {
            public bool Owned = true;
            public int CurrentPid = 100;
            public bool LooksLikeMcpServerProcess(int pid) => Owned;
            public bool TryGetProcessCommandLine(int pid, out string args) { args = "mcp-for-unity"; return true; }
            public List<int> GetListeningProcessIdsForPort(int port) => new List<int>();
            public int GetCurrentProcessId() => CurrentPid;
            public bool ProcessExists(int pid) => throw new AssertionException("PID-only liveness must not be used");
            public string NormalizeForMatch(string value) => value;
        }

        private sealed class FakeTerminator : ProcessTerminator
        {
            public readonly Queue<long?> Identities = new Queue<long?>();
            public readonly List<bool> Signals = new List<bool>();
            public bool Windows;
            public bool GracefulResult;
            private long _elapsed;
            public FakeTerminator(Detector detector, params long?[] identities) : base(detector)
            { foreach (var identity in identities) Identities.Enqueue(identity); }
            protected override long? ReadStartTime(int pid) => Identities.Dequeue();
            protected override bool IsWindows => Windows;
            protected override void WaitForExitPoll() { _elapsed = 8000; }
            protected override long ElapsedMilliseconds(Stopwatch timer) => _elapsed;
            protected override bool RunTerminationCommand(int pid, bool force)
            { Signals.Add(force); return force || GracefulResult; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReusedPidAfterGracefulAttempt_IsNeverForced(bool windows)
        {
            var target = new FakeTerminator(new Detector(), 10, 10, 20) { Windows = windows };
            Assert.IsTrue(target.Terminate(200));
            CollectionAssert.AreEqual(new[] { false }, target.Signals);
        }

        [Test]
        public void ReusedPidDuringOwnershipCheck_IsNeverSignaled()
        {
            var target = new FakeTerminator(new Detector(), 10, 20);
            Assert.IsTrue(target.Terminate(200));
            Assert.IsEmpty(target.Signals);
        }

        [Test]
        public void ReusedPidImmediatelyBeforeEscalation_IsNeverForced()
        {
            var target = new FakeTerminator(new Detector(), 10, 10, 10, 20);
            Assert.IsTrue(target.Terminate(200));
            CollectionAssert.AreEqual(new[] { false }, target.Signals);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InspectionFailureAfterGracefulAttempt_FailsClosed(bool windows)
        {
            var target = new FakeTerminator(new Detector(), 10, 10, null) { Windows = windows };
            Assert.IsFalse(target.Terminate(200));
            CollectionAssert.AreEqual(new[] { false }, target.Signals);
        }

        [Test]
        public void SameLifetimeUntilDeadline_EscalatesAndVerifiesExit()
        {
            var target = new FakeTerminator(new Detector(), 10, 10, 10, 10, 0);
            Assert.IsTrue(target.Terminate(200));
            CollectionAssert.AreEqual(new[] { false, true }, target.Signals);
        }

        [Test]
        public void MissingOwnership_NeverSignals()
        {
            var target = new FakeTerminator(new Detector { Owned = false }, 10);
            Assert.IsFalse(target.Terminate(200));
            Assert.IsEmpty(target.Signals);
        }

        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(100)]
        public void UnsafePid_NeverInspectedOrSignaled(int pid)
        {
            var target = new FakeTerminator(new Detector());
            Assert.IsFalse(target.Terminate(pid));
            Assert.IsEmpty(target.Signals);
        }
    }
}
