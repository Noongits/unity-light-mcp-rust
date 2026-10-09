using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using UnityEditor;
using NUnit.Framework;
using MCPForUnity.Editor.Services;

namespace MCPForUnityTests.Editor.Services
{
    /// <summary>
    /// Tests for TestJobManager's per-job InitTimeoutMs feature.
    /// Uses reflection to manipulate internal state since StartJob triggers a real test run.
    /// </summary>
    public class TestJobManagerInitTimeoutTests
    {
        private FieldInfo _jobsField;
        private FieldInfo _currentJobIdField;
        private MethodInfo _getJobMethod;
        private MethodInfo _persistMethod;
        private MethodInfo _restoreMethod;
        private Type _testJobType;

        private string _originalJobId;
        private Dictionary<string, TestJob> _originalJobs;
        private Dictionary<FieldInfo, object> _originalStatus;
        private object _originalLastPersist;
        private string _originalSessionJobs;
        private string _originalSessionCurrent;
        private const string JobsKey = "MCPForUnity.TestJobsV1";
        private const string CurrentKey = "MCPForUnity.CurrentTestJobIdV1";

        [SetUp]
        public void SetUp()
        {
            var asm = typeof(MCPServiceLocator).Assembly;
            var managerType = asm.GetType("MCPForUnity.Editor.Services.TestJobManager");
            Assert.NotNull(managerType, "Could not find TestJobManager");

            _testJobType = asm.GetType("MCPForUnity.Editor.Services.TestJob");
            Assert.NotNull(_testJobType, "Could not find TestJob");

            _jobsField = managerType.GetField("Jobs", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(_jobsField, "Could not find Jobs field");

            _currentJobIdField = managerType.GetField("_currentJobId", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(_currentJobIdField, "Could not find _currentJobId field");

            _getJobMethod = managerType.GetMethod("GetJob", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(_getJobMethod, "Could not find GetJob method");

            _persistMethod = managerType.GetMethod("PersistToSessionState", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(_persistMethod, "Could not find PersistToSessionState method");

            _restoreMethod = managerType.GetMethod("TryRestoreFromSessionState", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(_restoreMethod, "Could not find TryRestoreFromSessionState method");

            // Snapshot original state
            _originalJobId = _currentJobIdField.GetValue(null) as string;
            _originalJobs = new Dictionary<string, TestJob>((Dictionary<string, TestJob>)_jobsField.GetValue(null));
            _originalStatus = typeof(TestRunStatus).GetFields(BindingFlags.NonPublic | BindingFlags.Static)
                .Where(f => !f.IsInitOnly).ToDictionary(f => f, f => f.GetValue(null));
            _originalLastPersist = managerType.GetField("_lastPersistUnixMs", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            _originalSessionJobs = SessionState.GetString(JobsKey, string.Empty);
            _originalSessionCurrent = SessionState.GetString(CurrentKey, string.Empty);
            ((Dictionary<string, TestJob>)_jobsField.GetValue(null)).Clear();
            _currentJobIdField.SetValue(null, null);
        }

        [TearDown]
        public void TearDown()
        {
            if (_originalJobs == null) return;
            // Restore object identities/results as well as the persisted bytes. Re-persisting the
            // live dictionary would truncate history and lose the outer runner's Result payloads.
            var jobs = (Dictionary<string, TestJob>)_jobsField.GetValue(null);
            jobs.Clear();
            foreach (var entry in _originalJobs) jobs.Add(entry.Key, entry.Value);
            _currentJobIdField.SetValue(null, _originalJobId);
            _currentJobIdField.DeclaringType.GetField("_lastPersistUnixMs", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, _originalLastPersist);
            foreach (var entry in _originalStatus) entry.Key.SetValue(null, entry.Value);
            SessionState.SetString(JobsKey, _originalSessionJobs);
            SessionState.SetString(CurrentKey, _originalSessionCurrent);
        }

        [Test]
        public void NestedFixture_RestoresOriginalJobIdentityAndPersistedState()
        {
            var jobs = (Dictionary<string, TestJob>)_jobsField.GetValue(null);
            var sentinel = new TestJob { JobId = "outer-job", Status = TestJobStatus.Succeeded };
            jobs[sentinel.JobId] = sentinel;
            SessionState.SetString(JobsKey, "outer snapshot");
            var nested = new TestJobManagerInitTimeoutTests();
            nested.SetUp();
            try
            {
                Assert.IsEmpty((Dictionary<string, TestJob>)_jobsField.GetValue(null));
                SessionState.SetString(JobsKey, "synthetic snapshot");
            }
            finally { nested.TearDown(); }
            Assert.AreSame(sentinel, jobs[sentinel.JobId]);
            Assert.AreEqual("outer snapshot", SessionState.GetString(JobsKey, ""));
        }

        [Test]
        public void GetJob_WithCustomInitTimeout_UsesPerJobTimeout()
        {
            // Arrange: insert a job with a custom init timeout and a start time far enough in the
            // past to exceed the default 15s but within the custom 120s.
            var jobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var job = Activator.CreateInstance(_testJobType);
            _testJobType.GetProperty("JobId").SetValue(job, "test-init-timeout-job");
            _testJobType.GetProperty("Status").SetValue(job, TestJobStatus.Running);
            _testJobType.GetProperty("Mode").SetValue(job, "PlayMode");
            _testJobType.GetProperty("StartedUnixMs").SetValue(job, now - 30_000); // 30s ago
            _testJobType.GetProperty("LastUpdateUnixMs").SetValue(job, now - 30_000);
            _testJobType.GetProperty("TotalTests").SetValue(job, null); // Not initialized yet
            _testJobType.GetProperty("InitTimeoutMs").SetValue(job, 120_000L); // 120s custom timeout
            _testJobType.GetProperty("FailuresSoFar").SetValue(job, new List<TestJobFailure>());

            jobs["test-init-timeout-job"] = job;
            _currentJobIdField.SetValue(null, "test-init-timeout-job");

            // Act: GetJob should NOT auto-fail because 30s < 120s custom timeout
            var result = _getJobMethod.Invoke(null, new object[] { "test-init-timeout-job" });

            // Assert: job should still be running
            var status = (TestJobStatus)_testJobType.GetProperty("Status").GetValue(result);
            Assert.AreEqual(TestJobStatus.Running, status,
                "Job with 120s custom timeout should not auto-fail after 30s");
        }

        [Test]
        public void GetJob_WithDefaultTimeout_AutoFailsAfter15Seconds()
        {
            // Arrange: insert a job with InitTimeoutMs=0 (use default) and start time 20s ago
            var jobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var job = Activator.CreateInstance(_testJobType);
            _testJobType.GetProperty("JobId").SetValue(job, "test-init-timeout-default");
            _testJobType.GetProperty("Status").SetValue(job, TestJobStatus.Running);
            _testJobType.GetProperty("Mode").SetValue(job, "EditMode");
            _testJobType.GetProperty("StartedUnixMs").SetValue(job, now - 20_000); // 20s ago
            _testJobType.GetProperty("LastUpdateUnixMs").SetValue(job, now - 20_000);
            _testJobType.GetProperty("TotalTests").SetValue(job, null);
            _testJobType.GetProperty("InitTimeoutMs").SetValue(job, 0L); // Use default
            _testJobType.GetProperty("FailuresSoFar").SetValue(job, new List<TestJobFailure>());

            jobs["test-init-timeout-default"] = job;
            _currentJobIdField.SetValue(null, "test-init-timeout-default");

            // Act: GetJob should auto-fail because 20s > 15s default
            var result = _getJobMethod.Invoke(null, new object[] { "test-init-timeout-default" });

            // Assert: job should be failed
            var status = (TestJobStatus)_testJobType.GetProperty("Status").GetValue(result);
            Assert.AreEqual(TestJobStatus.Failed, status,
                "Job with default timeout should auto-fail after 20s");
        }

        [Test]
        public void InitTimeoutMs_SurvivesPersistAndRestore()
        {
            // Arrange: insert a job with custom InitTimeoutMs
            var jobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var job = Activator.CreateInstance(_testJobType);
            _testJobType.GetProperty("JobId").SetValue(job, "test-init-timeout-persist");
            _testJobType.GetProperty("Status").SetValue(job, TestJobStatus.Running);
            _testJobType.GetProperty("Mode").SetValue(job, "PlayMode");
            _testJobType.GetProperty("StartedUnixMs").SetValue(job, now);
            _testJobType.GetProperty("LastUpdateUnixMs").SetValue(job, now);
            _testJobType.GetProperty("TotalTests").SetValue(job, null);
            _testJobType.GetProperty("InitTimeoutMs").SetValue(job, 90_000L);
            _testJobType.GetProperty("FailuresSoFar").SetValue(job, new List<TestJobFailure>());

            jobs["test-init-timeout-persist"] = job;
            _currentJobIdField.SetValue(null, "test-init-timeout-persist");

            // Act: persist then restore (simulates domain reload)
            _persistMethod.Invoke(null, new object[] { true });
            // Clear in-memory state
            jobs.Remove("test-init-timeout-persist");
            _currentJobIdField.SetValue(null, null);
            // Restore from SessionState
            _restoreMethod.Invoke(null, null);

            // Assert: restored job should have the same InitTimeoutMs
            var restoredJobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            Assert.IsTrue(restoredJobs.Contains("test-init-timeout-persist"),
                "Job should be restored from SessionState");

            var restoredJob = restoredJobs["test-init-timeout-persist"];
            var restoredTimeout = (long)_testJobType.GetProperty("InitTimeoutMs").GetValue(restoredJob);
            Assert.AreEqual(90_000L, restoredTimeout,
                "InitTimeoutMs should survive persist/restore cycle");
        }
    }
}
