using System;
using System.Diagnostics;
using System.Threading.Tasks;
using MCPForUnity.Editor.Security;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.AssetGen
{
    public class KeyStoreProcessTimelineTests
    {
        private sealed class FakeChild : KeyStoreProcessRunner.IChild
        {
            public Task<string> Output = Task.FromResult("synthetic value");
            public Task<string> Error = Task.FromResult("");
            public Task Writing = Task.CompletedTask;
            public bool Exits = true;
            public bool OutputStarted, ErrorStarted, Killed, Disposed;
            public int Code;
            public Task<string> ReadOutputAsync() { OutputStarted = true; return Output; }
            public Task<string> ReadErrorAsync() { ErrorStarted = true; return Error; }
            public Task WriteInputAsync(string input) => Writing;
            public bool WaitForExit(int timeoutMs)
            {
                Assert.IsTrue(OutputStarted && ErrorStarted, "Both drains must start before waiting");
                Assert.GreaterOrEqual(timeoutMs, 0);
                return Exits;
            }
            public int ExitCode => Code;
            public void Kill() { Killed = true; }
            public void Dispose() { Disposed = true; }
        }

        [Test]
        public void NormalExit_DrainsBothPipesAndDisposesWithoutKill()
        {
            var child = new FakeChild();
            var result = KeyStoreProcessRunner.Run(new ProcessStartInfo(), start: _ => child);
            Assert.AreEqual(0, result.code);
            Assert.AreEqual("synthetic value", result.stdout);
            Assert.IsTrue(child.Disposed);
            Assert.IsFalse(child.Killed);
        }

        [Test]
        public void HungChild_IsKilledAndDisposedWithoutReadingExitCode()
        {
            var child = new FakeChild { Exits = false };
            var result = KeyStoreProcessRunner.Run(new ProcessStartInfo(), timeoutMs: 1, start: _ => child);
            Assert.AreEqual(-1, result.code);
            Assert.IsNull(result.stdout);
            Assert.IsTrue(child.Killed && child.Disposed);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ExitedChild_WithUnfinishedPipeOrInput_RemainsBounded(bool blockedPipe)
        {
            var child = new FakeChild();
            var pending = new TaskCompletionSource<string>();
            if (blockedPipe) child.Error = pending.Task;
            else child.Writing = pending.Task;
            var result = KeyStoreProcessRunner.Run(new ProcessStartInfo(), "synthetic input", 1, _ => child);
            Assert.AreEqual(-1, result.code);
            Assert.IsTrue(child.Killed && child.Disposed);
            pending.SetResult("");
        }

        [Test]
        public void FailedPipe_CleansChildAndDoesNotExposeExceptionOrOutput()
        {
            var child = new FakeChild { Error = Task.FromException<string>(new Exception("synthetic secret")) };
            var result = KeyStoreProcessRunner.Run(new ProcessStartInfo(), start: _ => child);
            Assert.AreEqual(-1, result.code);
            Assert.IsNull(result.stdout);
            Assert.That(result.stderr, Does.Not.Contain("synthetic secret"));
            Assert.IsTrue(child.Killed && child.Disposed);
        }
    }
}
