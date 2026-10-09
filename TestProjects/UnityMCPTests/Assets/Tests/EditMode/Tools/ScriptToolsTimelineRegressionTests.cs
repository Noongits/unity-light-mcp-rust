using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using MCPForUnity.Editor.Tools.Build;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace MCPForUnity.Tests.EditMode.Tools
{
    [TestFixture]
    public class ScriptToolsTimelineRegressionTests
    {
        private readonly List<string> buildIds = new();
        private readonly List<string> batchIds = new();
        private BuildJob previousLastCompleted;

        [SetUp]
        public void SetUp()
        {
            previousLastCompleted = BuildJobStore.LastCompletedJob;
        }

        [TearDown]
        public void TearDown()
        {
            var builds = (IDictionary)typeof(BuildJobStore).GetField("_buildJobs", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var batches = (IDictionary)typeof(BuildJobStore).GetField("_batchJobs", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            foreach (string id in buildIds) builds.Remove(id);
            foreach (string id in batchIds)
            {
                // If a test failed before its update callback, it must not schedule another build.
                var batch = (BatchJob)batches[id];
                if (batch != null)
                {
                    batch.State = BuildJobState.Cancelled;
                    foreach (var child in batch.Children) child.State = BuildJobState.Cancelled;
                }
                batches.Remove(id);
            }
            typeof(BuildJobStore).GetField("_lastCompletedJob", BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, previousLastCompleted);
            buildIds.Clear();
            batchIds.Clear();
        }

        private BuildJob NewBuild(BuildJobState state)
        {
            var job = new BuildJob(BuildJobStore.CreateJobId(), BuildTarget.StandaloneWindows64, "unused.exe") { State = state };
            buildIds.Add(job.JobId);
            BuildJobStore.AddBuildJob(job);
            return job;
        }

        private BatchJob NewBatch(BuildJobState state)
        {
            var batch = new BatchJob(BuildJobStore.CreateBatchId()) { State = state };
            batchIds.Add(batch.JobId);
            BuildJobStore.AddBatchJob(batch);
            return batch;
        }

        [TestCase("public class Inline { public int Value(){return 1;} public int Other(){return 7;} }", "Value")]
        [TestCase("public class Inline { public int First(){return 1;} public int Value(){return 2;} }", "Value")]
        [TestCase("public class Inline { public int First(){ int Value(){return 9;} return Value(); } public int Value(){return 2;} }", "Value")]
        public void InlineMethodSpan_LeavesClassAndOtherMethodsIntact(string source, string methodName)
        {
            var method=typeof(ManageScript).GetMethod("TryComputeMethodSpan", BindingFlags.NonPublic|BindingFlags.Static);
            object[] args={source,0,source.Length,methodName,null,null,null,0,0,null};
            Assert.IsTrue((bool)method.Invoke(null,args), (string)args[9]);
            int start=(int)args[7], length=(int)args[8];
            string target=source.Substring(start,length).Trim();
            StringAssert.StartsWith("public int Value()",target);
            Assert.IsFalse(target.Contains("Other()"));
            Assert.IsFalse(target.Contains("First()"));
            string changed=source.Remove(start,length).Insert(start," public int Value(){return 3;}");
            StringAssert.StartsWith("public class Inline {",changed);
            Assert.AreEqual(source.Contains("Other()"),changed.Contains("Other()"));
            Assert.AreEqual(source.Contains("First()"),changed.Contains("First()"));
        }
        [Test]
        public void BatchSuccessDetection_HandlesAnonymousFailuresAndScalarResults()
        {
            var method = typeof(BatchExecute).GetMethod("DetermineCallSucceeded", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsFalse((bool)method.Invoke(null, new object[] { new { success = false } }));
            Assert.IsTrue((bool)method.Invoke(null, new object[] { new { success = true } }));
            Assert.IsTrue((bool)method.Invoke(null, new object[] { new JValue("result") }));
            Assert.IsTrue((bool)method.Invoke(null, new object[] { new JArray(1, 2) }));
        }

        [TestCase("List<T>", "List`1")]
        [TestCase("Dictionary<string,List<int>>", "Dictionary`2")]
        [TestCase("Dictionary<List<int>,Dictionary<string,List<float>>>", "Dictionary`2")]
        [TestCase("List<Dictionary<string,int>>", "List`1")]
        public void ReflectionGenericNormalization_CountsOutermostArguments(string input, string expected)
        {
            var method = typeof(UnityReflect).GetMethod("NormalizeGenericName", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.AreEqual(expected, method.Invoke(null, new object[] { input }));
        }

        [TestCase(BuildJobState.Pending)]
        [TestCase(BuildJobState.Building)]
        public void BatchStatus_ContinuesPollingUntilTerminal(BuildJobState state)
        {
            var batch = NewBatch(state);
            batch.Children.Add(NewBuild(BuildJobState.Pending));
            var parameters = new JObject { ["action"] = "status", ["job_id"] = batch.JobId };
            Assert.IsInstanceOf<PendingResponse>(ManageBuild.HandleCommand(parameters));
            batch.Children[0].State = BuildJobState.Succeeded;
            // Child completion does not imply the batch has finished scheduling.
            Assert.IsInstanceOf<PendingResponse>(ManageBuild.HandleCommand(parameters));
            batch.State = BuildJobState.Succeeded;
            Assert.IsInstanceOf<SuccessResponse>(ManageBuild.HandleCommand(parameters));
        }

        [TestCase(BuildJobState.Failed)]
        [TestCase(BuildJobState.Cancelled)]
        public void BatchStatus_TerminalStateDoesNotPoll(BuildJobState state)
        {
            var batch = NewBatch(state);
            Assert.IsInstanceOf<SuccessResponse>(ManageBuild.HandleCommand(new JObject
            {
                ["action"] = "status", ["job_id"] = batch.JobId
            }));
        }

        [TestCase(BuildJobState.Pending)]
        [TestCase(BuildJobState.Building)]
        public void StatusWithoutId_PrefersActiveBuildOverLastCompleted(BuildJobState state)
        {
            if (BuildJobStore.GetActiveBuildJob() != null || BuildJobStore.GetActiveBatchJob() != null)
                Assert.Ignore("An unrelated build is active.");
            var old = NewBuild(BuildJobState.Succeeded);
            BuildJobStore.SetLastCompleted(old);
            var active = NewBuild(state);
            var result = ManageBuild.HandleCommand(new JObject { ["action"] = "status" });
            Assert.IsInstanceOf<PendingResponse>(result);
            Assert.AreEqual(active.JobId, JObject.FromObject(((PendingResponse)result).Data)["job_id"].Value<string>());
        }

        [UnityTest]
        public IEnumerator BatchSetupException_FailsChildAndContinuesToNextChild()
        {
            var batch = NewBatch(BuildJobState.Building);
            var failed = NewBuild(BuildJobState.Pending);
            var next = NewBuild(BuildJobState.Pending);
            batch.Children.Add(failed);
            batch.Children.Add(next);
            int calls = 0;
            BuildRunner.ScheduleNextBatchBuild(batch, index =>
            {
                calls++;
                if (index == 0) throw new InvalidOperationException("setup failed before scheduling");
                next.State = BuildJobState.Succeeded;
                return next;
            });
            Assert.AreEqual(BuildJobState.Failed, failed.State);
            Assert.IsNotNull(failed.CompletedAt);
            StringAssert.Contains("setup failed", failed.ErrorMessage);
            double deadline = EditorApplication.timeSinceStartup + 5;
            while (batch.State == BuildJobState.Building && EditorApplication.timeSinceStartup < deadline)
                yield return null;
            Assert.AreEqual(2, calls);
            Assert.AreEqual(BuildJobState.Failed, batch.State);
        }

        [Test]
        public void StatusWithoutId_BetweenChildrenStillPollsTheActiveBatch()
        {
            if (BuildJobStore.GetActiveBatchJob() != null) Assert.Ignore("An unrelated batch is active.");
            var batch = NewBatch(BuildJobState.Building);
            var finished = NewBuild(BuildJobState.Succeeded);
            batch.Children.Add(finished);
            BuildJobStore.SetLastCompleted(finished);
            var result = ManageBuild.HandleCommand(new JObject { ["action"] = "status" });
            Assert.IsInstanceOf<PendingResponse>(result);
            Assert.AreEqual(batch.JobId, JObject.FromObject(((PendingResponse)result).Data)["job_id"].Value<string>());
        }

        [Test]
        public void CancelledBatch_SkipsUnscheduledChildren()
        {
            var batch = NewBatch(BuildJobState.Cancelled);
            batch.Children.Add(NewBuild(BuildJobState.Pending));
            BuildRunner.ScheduleNextBatchBuild(batch, index => throw new Exception("Must not schedule after cancellation"));
            Assert.AreEqual(BuildJobState.Skipped, batch.Children[0].State);
        }

        [Test]
        public void Settings_ExplicitEmptyStringWrites_WhileMissingValueReads()
        {
            string original = PlayerSettings.productName;
            try
            {
                PlayerSettings.productName = "TimelineSettingsProbe";
                var read = ManageBuild.HandleCommand(new JObject { ["action"] = "settings", ["property"] = "product_name" });
                Assert.IsInstanceOf<SuccessResponse>(read);
                Assert.AreEqual("TimelineSettingsProbe", PlayerSettings.productName);
                var write = ManageBuild.HandleCommand(new JObject { ["action"] = "settings", ["property"] = "product_name", ["value"] = "" });
                Assert.IsInstanceOf<SuccessResponse>(write);
                Assert.AreEqual("", PlayerSettings.productName);
            }
            finally { PlayerSettings.productName = original; }
        }

        [Test]
        public void ComponentProperty_MissingValueRejectsBeforeAnyPropertiesAreChanged()
        {
            var go = new GameObject("TimelineComponentProbe");
            try
            {
                var audio = go.AddComponent<AudioSource>();
                audio.volume = 0.75f;
                var result = ManageComponents.HandleCommand(new JObject
                {
                    ["action"] = "set_property", ["target"] = go.GetInstanceID(), ["componentType"] = "AudioSource",
                    ["property"] = "volume", ["properties"] = new JObject { ["volume"] = 0.25f }
                });
                Assert.IsInstanceOf<ErrorResponse>(result);
                Assert.AreEqual(0.75f, audio.volume);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void MarkOwningSceneDirty_DoesNotChooseAnUnrelatedOpenPrefabStage()
        {
            if (PrefabStageUtility.GetCurrentPrefabStage() != null) Assert.Ignore("A prefab stage is already open.");
            string root = "Assets/TimelineSceneProbe_" + Guid.NewGuid().ToString("N");
            string prefabPath = root + ".prefab";
            string scenePath = root + ".unity";
            Scene originalScene = SceneManager.GetActiveScene();
            Scene scene = default;
            GameObject source = null;
            try
            {
                source = new GameObject("PrefabProbe");
                PrefabUtility.SaveAsPrefabAsset(source, prefabPath);
                Object.DestroyImmediate(source);
                source = null;
                scene = MCPForUnityTests.Editor.Tools.TestSceneFactory.Create();
                var target = new GameObject("SceneProbe");
                SceneManager.MoveGameObjectToScene(target, scene);
                Assert.IsTrue(EditorSceneManager.SaveScene(scene, scenePath));
                Assert.IsFalse(scene.isDirty);
                Assert.IsNotNull(PrefabStageUtility.OpenPrefab(prefabPath));
                typeof(ManageComponents).GetMethod("MarkOwningSceneDirty", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { target });
                Assert.IsTrue(scene.isDirty, "The target's scene must be dirtied even when another prefab stage is open.");
            }
            finally
            {
                StageUtility.GoToMainStage();
                if (source != null) Object.DestroyImmediate(source);
                if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
                if (originalScene.IsValid() && originalScene.isLoaded) SceneManager.SetActiveScene(originalScene);
                AssetDatabase.DeleteAsset(prefabPath);
                AssetDatabase.DeleteAsset(scenePath);
            }
        }

        [TestCase("Exception")]
        [TestCase("LogError")]
        [TestCase("LogWarning")]
        [TestCase("Assertion")]
        public void ConsoleSeverity_UsesModeInsteadOfMessageWords(string word)
        {
            string unique = "TimelineSeverity_" + Guid.NewGuid().ToString("N") + " " + word;
            Debug.Log(unique);
            var result = (SuccessResponse)ReadConsole.HandleCommand(new JObject
            {
                ["action"] = "get", ["types"] = new JArray("log"), ["filterText"] = unique, ["format"] = "detailed"
            });
            var entries = JArray.FromObject(result.Data);
            Assert.AreEqual(1, entries.Count, "A normal Debug.Log must remain visible in the log filter.");
            Assert.AreEqual("Log", entries[0]["type"].Value<string>());
        }

        [Test]
        public void LayerOperations_HandleDuplicateAndMissingEarlyReturns()
        {
            string name = "TimelineLayer_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            bool added = false;
            try
            {
                var result = ManageEditor.HandleCommand(new JObject { ["action"] = "add_layer", ["layerName"] = name });
                if (result is ErrorResponse error && error.Error.Contains("No empty User Layer")) Assert.Ignore("No free user layer.");
                Assert.IsInstanceOf<SuccessResponse>(result);
                added = true;
                Assert.IsInstanceOf<ErrorResponse>(ManageEditor.HandleCommand(new JObject { ["action"] = "add_layer", ["layerName"] = name }));
                Assert.IsInstanceOf<SuccessResponse>(ManageEditor.HandleCommand(new JObject { ["action"] = "remove_layer", ["layerName"] = name }));
                added = false;
                Assert.IsInstanceOf<ErrorResponse>(ManageEditor.HandleCommand(new JObject { ["action"] = "remove_layer", ["layerName"] = name }));
            }
            finally
            {
                if (added) ManageEditor.HandleCommand(new JObject { ["action"] = "remove_layer", ["layerName"] = name });
            }
        }
    }
}
