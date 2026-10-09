using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Tools;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    // Unity Test Framework runs these editor fixtures sequentially; its NUnit fork has no NonParallelizable attribute.
    public class TransportCommandDispatcherTests
    {
        private sealed class DeferredContext : SynchronizationContext
        {
            private readonly ConcurrentQueue<Action> callbacks = new();
            public override void Post(SendOrPostCallback callback, object state)
                => callbacks.Enqueue(() => callback(state));
            public void Drain()
            {
                while (callbacks.TryDequeue(out var callback)) callback();
            }
        }

        private static FieldInfo Field(string name) => typeof(TransportCommandDispatcher)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
        private DeferredContext context;
        private object originalContext;
        private object pendingLock;
        private IDictionary pending;

        [SetUp]
        public void SetUp()
        {
            // Initialize the dispatcher on the editor thread before starting test workers.
            TransportCommandDispatcher.ExecuteCommandJsonAsync("ping", CancellationToken.None);
            originalContext = Field("_mainThreadContext").GetValue(null);
            context = new DeferredContext();
            Field("_mainThreadContext").SetValue(null, context);
            pendingLock = Field("PendingLock").GetValue(null);
            pending = (IDictionary)Field("Pending").GetValue(null);
            Assert.AreEqual(0, PendingCount);
        }

        [TearDown]
        public void TearDown()
        {
            try { context?.Drain(); }
            finally { Field("_mainThreadContext").SetValue(null, originalContext); }
        }

        private int PendingCount
        {
            get { lock (pendingLock) return pending.Count; }
        }

        private Task<string> EnqueueOnWorker(CancellationToken token)
        {
            Task<string> result = null;
            Exception error = null;
            var worker = new Thread(() =>
            {
                try { result = TransportCommandDispatcher.ExecuteCommandJsonAsync("ping", token); }
                catch (Exception ex) { error = ex; }
            });
            worker.Start();
            Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(5)), "enqueue worker should not wait for an editor tick");
            Assert.IsNull(error);
            return result;
        }

        [Test]
        public void AlreadyCanceled_DoesNotWaitForEditorPump()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Task<string> task = EnqueueOnWorker(cts.Token);
            Assert.IsTrue(task.IsCanceled);
            Assert.AreEqual(0, PendingCount);
        }

        [Test]
        public void CancellationBeforePublication_DoesNotLoseCallback()
        {
            using var cts = new CancellationTokenSource();
            Task<string> task = null;
            Exception error = null;
            var worker = new Thread(() =>
            {
                try { task = TransportCommandDispatcher.ExecuteCommandJsonAsync("ping", cts.Token); }
                catch (Exception ex) { error = ex; }
            });
            bool reachedPublication;
            lock (pendingLock)
            {
                worker.Start();
                // Registration precedes the PendingLock acquisition. Holding that lock lets
                // cancellation run reentrantly here while the registered entry is still absent.
                reachedPublication = SpinWait.SpinUntil(
                    () => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    TimeSpan.FromSeconds(5));
                cts.Cancel();
            }
            Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(reachedPublication, "worker should reach the publication lock");
            Assert.IsNull(error);
            Assert.IsTrue(task.IsCanceled, "cancellation must complete before any posted pump runs");
            Assert.AreEqual(0, PendingCount);
        }

        [Test]
        public void CancellationAfterPublication_LatePumpCannotExecuteCommand()
        {
            using var cts = new CancellationTokenSource();
            Task<string> task = EnqueueOnWorker(cts.Token);
            Assert.IsFalse(task.IsCompleted);
            Assert.AreEqual(1, PendingCount);
            cts.Cancel();
            Assert.IsTrue(task.IsCanceled);
            Assert.AreEqual(0, PendingCount);
            context.Drain();
            Assert.IsTrue(task.IsCanceled);
        }

        [Test]
        public void CompletionBeforeCancellation_PreservesResultAndCleansEntry()
        {
            using var cts = new CancellationTokenSource();
            Task<string> task = EnqueueOnWorker(cts.Token);
            context.Drain();
            Assert.IsTrue(task.IsCompletedSuccessfully);
            StringAssert.Contains("pong", task.Result);
            cts.Cancel();
            Assert.IsTrue(task.IsCompletedSuccessfully);
            Assert.AreEqual(0, PendingCount);
        }

        [Test]
        public void AsyncCompletion_RemovesEntryWithoutAnotherEditorTick()
        {
            string name = "dispatcher_cleanup_test_" + Guid.NewGuid().ToString("N");
            var handlers = (IDictionary)typeof(CommandRegistry)
                .GetField("_handlers", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            handlers.Add(name, new HandlerInfo(name, null, _ => Task.FromResult<object>("done")));
            try
            {
                Task<string> task = TransportCommandDispatcher.ExecuteCommandJsonAsync(
                    "{\"type\":\"" + name + "\"}", CancellationToken.None);
                Assert.IsTrue(task.IsCompletedSuccessfully);
                // Do not yield a frame: completion cleanup must not mutate delayCall or need it.
                Assert.IsTrue(SpinWait.SpinUntil(() => PendingCount == 0, TimeSpan.FromSeconds(5)),
                    "completed async commands must release managed queue entries without a frame");
            }
            finally { handlers.Remove(name); }
        }
    }
}
