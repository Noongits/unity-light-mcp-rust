using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Resources.Scene;
using MCPForUnity.Editor.Resources.Tests;
using MCPForUnity.Editor.Security;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.AssetGen;
using MCPForUnity.Editor.Services.AssetGen.Http;
using MCPForUnity.Editor.Services.AssetGen.Providers;
using MCPForUnity.Editor.Tools.GameObjects;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;
using MCPForUnityTests.Editor.Tools;

namespace MCPForUnityTests.Editor.AssetGen
{
    // These tests use fake transports only. Run in Unity EditMode; syntax checks are not execution.
    // Unity Test Framework runs these editor fixtures sequentially; its NUnit fork has no NonParallelizable attribute.
    public class GameplayBatchOneTimelineRegressionTests
    {
        private OwnedSceneTestScope sceneScope;
        [SetUp]
        public void SetUp() => sceneScope = new OwnedSceneTestScope();
        [TearDown]
        public void TearDown() { sceneScope?.Dispose(); sceneScope = null; }

        private static HttpResult Json(string json) => new HttpResult { Status = 200, IsSuccess = true, Text = json };

        [TestCase("glb"), TestCase("fbx"), TestCase("zip")]
        public void ModelResults_UseModelAllowlist(string extension)
        {
            Assert.IsTrue(AssetGenJobManager.IsAllowedResultExtension("model", extension));
            Assert.IsFalse(AssetGenJobManager.IsAllowedResultExtension("unknown", extension));
            Assert.IsFalse(AssetGenJobManager.IsAllowedResultExtension("model", "cs"));
        }

        [TestCase(-1), TestCase(int.MinValue)]
        public void RecentJobs_NegativeLimit_ReturnsEmpty(int max)
            => Assert.IsEmpty(AssetGenJobManager.RecentJobs(max));

        [Test]
        public void PreCanceledTransport_DoesNotCreateRequest()
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var task = new UnityWebRequestTransport().SendAsync(new HttpRequestSpec { Url = "not a URL" }, canceled.Token);
            Assert.IsTrue(task.IsCanceled);
        }

        private sealed class WaitingTransport : IHttpTransport
        {
            public CancellationToken Token;
            public readonly TaskCompletionSource<HttpResult> Completion = new TaskCompletionSource<HttpResult>();
            public Task<HttpResult> SendAsync(HttpRequestSpec spec, CancellationToken token)
            {
                Token = token;
                return Completion.Task;
            }
        }
        private sealed class MemoryKeys : ISecureKeyStore
        {
            public bool TryGet(string providerId, out string apiKey) { apiKey = "fake-only"; return true; }
            public bool Has(string providerId) => true;
            public void Set(string providerId, string apiKey) { }
            public void Delete(string providerId) { }
        }

        [Test]
        public void Timeout_CancelsPendingSubmit_AndLateCompletionCannotResumeJob()
        {
            using var fixture = new AssetGenFixtureScope(protectJobs: true);
            AssetGenJobManager.ResetForTests();
            AssetGenModelCatalog.ResetForTests(true);
            SecureKeyStore.OverrideForTests(new MemoryKeys());
            var transport = new WaitingTransport();
            try
            {
                AssetGenJobManager.TransportOverrideForTests = transport;
                AssetGenJobManager.SkipModelVerificationForTests = true;
                var job = AssetGenJobManager.StartModelGeneration(new ModelGenRequest { Provider = "tripo", Prompt = "test" });
                AssetGenJobManager.TryAdvanceForTests(job.JobId);
                Assert.IsFalse(transport.Token.IsCancellationRequested);
                AssetGenJobManager.TimeoutSeconds = -1;
                Assert.IsTrue(AssetGenJobManager.TryAdvanceForTests(job.JobId));
                Assert.AreEqual(AssetGenJobState.Failed, job.State);
                Assert.IsTrue(transport.Token.IsCancellationRequested);
                transport.Completion.SetResult(Json("{\"code\":0,\"data\":{\"task_id\":\"late\"}}"));
                Assert.IsTrue(AssetGenJobManager.TryAdvanceForTests(job.JobId));
                Assert.AreEqual(AssetGenJobState.Failed, job.State);
            }
            finally { AssetGenJobManager.ResetForTests(); }
        }

        [Test]
        public async Task Meshy_CanceledPreviewCompletion_DoesNotSubmitRefinement()
        {
            var adapter = new MeshyAdapter();
            using var cancel = new CancellationTokenSource();
            var fake = new FakeHttpTransport { Handler = spec =>
            {
                if (spec.Method == "POST") return Json("{\"result\":\"preview\"}");
                cancel.Cancel();
                return Json("{\"status\":\"SUCCEEDED\"}");
            }};
            await adapter.SubmitAsync(new ModelGenRequest { Prompt = "test", Texture = true }, "fake", fake, cancel.Token);
            Assert.CatchAsync<OperationCanceledException>(async () => await adapter.PollAsync("preview", "fake", fake, cancel.Token));
            Assert.AreEqual(2, fake.RecordedRequests.Count);
        }

        [Test]
        public async Task Meshy_FormatFallback_CarriesActualExtension()
        {
            var adapter = new MeshyAdapter();
            var fake = new FakeHttpTransport { Handler = spec => spec.Method == "POST"
                ? Json("{\"result\":\"preview\"}")
                : Json("{\"status\":\"SUCCEEDED\",\"model_urls\":{\"glb\":\"https://example.invalid/model.glb\"}}") };
            await adapter.SubmitAsync(new ModelGenRequest { Format = "fbx", Texture = false }, "fake", fake, CancellationToken.None);
            var result = await adapter.PollAsync("preview", "fake", fake, CancellationToken.None);
            Assert.AreEqual("glb", result.ResultExt);
        }

        [Test]
        public async Task FalAudio_MissingResponseUrl_UsesQueueAppRootAndEscapedId()
        {
            var fake = new FakeHttpTransport { Handler = _ => Json("{\"request_id\":\"a/b\"}") };
            var url = await new FalAudioAdapter().SubmitAsync(new AudioGenRequest { Model = "fal-ai/stable-audio-25/text-to-audio" }, "fake", fake, CancellationToken.None);
            Assert.AreEqual("https://queue.fal.run/fal-ai/stable-audio-25/requests/a%2Fb", url);
        }

        [Test]
        public void ComponentPagination_NegativeCursor_StartsAtZero()
        {
            var go = new GameObject("PaginationTimeline");
            try
            {
                go.AddComponent<BoxCollider>();
                var response = JObject.FromObject(GameObjectComponentsResource.HandleCommand(new JObject
                { ["instanceID"] = go.GetInstanceID(), ["cursor"] = -20, ["pageSize"] = 1, ["includeProperties"] = false }));
                Assert.AreEqual(0, (int)response["data"]["cursor"]);
                Assert.AreEqual(1, (int)response["data"]["nextCursor"]);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void NestedValueType_SetThenSetAgain_PreservesBothAxes()
        {
            var go = new GameObject("StructTimeline");
            try
            {
                var result = GameObjectComponentHelpers.SetComponentPropertiesInternal(go, "Transform", new JObject
                { ["localPosition.x"] = 4f, ["localPosition.y"] = 8f }, go.transform);
                Assert.IsNull(result);
                Assert.AreEqual(new Vector3(4, 8, 0), go.transform.localPosition);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        private sealed class StructOwner { public Vector3[] Points = { new Vector3(1, 2, 3) }; }
        [Test]
        public void NestedValueType_ArrayElement_WritesBackToOwningArray()
        {
            var owner = new StructOwner();
            var method = typeof(GameObjectComponentHelpers).GetMethod("SetNestedProperty", BindingFlags.NonPublic | BindingFlags.Static);
            object[] args = { owner, "Points[0].x", new JValue(9f), null, null };
            Assert.IsTrue((bool)method.Invoke(null, args), args[4]?.ToString());
            Assert.AreEqual(new Vector3(9, 2, 3), owner.Points[0]);
        }

        private sealed class EmptyTests : ITestRunnerService
        {
            public Task<IReadOnlyList<Dictionary<string, string>>> GetTestsAsync(TestMode? mode)
                => Task.FromResult<IReadOnlyList<Dictionary<string, string>>>(new List<Dictionary<string, string>>());
            public Task<TestRunResult> RunTestsAsync(TestMode mode, TestFilterOptions filterOptions = null)
                => throw new NotSupportedException();
        }
        [Test]
        public async Task TestsResource_NullParameters_ReturnsDefaultPageAfterAwait()
        {
            var field = typeof(MCPServiceLocator).GetField("_testRunnerService", BindingFlags.NonPublic | BindingFlags.Static);
            object previous = field.GetValue(null);
            try
            {
                MCPServiceLocator.Register<ITestRunnerService>(new EmptyTests());
                Assert.IsInstanceOf<SuccessResponse>(await GetTests.HandleCommand(null));
            }
            finally { field.SetValue(null, previous); }
        }

        [Test]
        public void DuplicateRoot_StaysInSourceAdditiveScene()
        {
            Scene original = SceneManager.GetActiveScene();
            Scene sourceScene = MCPForUnityTests.Editor.Tools.TestSceneFactory.Create();
            GameObject source = new GameObject("DuplicateSceneTimeline");
            SceneManager.MoveGameObjectToScene(source, sourceScene);
            SceneManager.SetActiveScene(original);
            try
            {
                var response = GameObjectDuplicate.Handle(new JObject(), new JValue(source.GetInstanceID()), "by_id");
                Assert.IsInstanceOf<SuccessResponse>(response);
                Assert.AreEqual(sourceScene, Selection.activeGameObject.scene);
            }
            finally { EditorSceneManager.CloseScene(sourceScene, true); }
        }

        [Test]
        public void ModifyParent_UndoRestoresPreviousParent()
        {
            var parent = new GameObject("UndoParentTimeline");
            var child = new GameObject("UndoChildTimeline");
            child.transform.SetParent(parent.transform);
            Undo.IncrementCurrentGroup();
            try
            {
                var response = GameObjectModify.Handle(new JObject { ["parent"] = JValue.CreateNull() }, new JValue(child.GetInstanceID()), "by_id");
                Assert.IsInstanceOf<SuccessResponse>(response);
                Assert.IsNull(child.transform.parent);
                Undo.FlushUndoRecordObjects();
                Undo.PerformUndo();
                Assert.AreEqual(parent.transform, child.transform.parent);
            }
            finally { if (child != null) UnityEngine.Object.DestroyImmediate(child); UnityEngine.Object.DestroyImmediate(parent); }
        }

        [Test]
        public void CreatePrefab_RejectsOutsideAssetsBeforeCreatingObject()
        {
            var response = GameObjectCreate.Handle(new JObject
            { ["name"] = "RejectedPrefabTimeline", ["saveAsPrefab"] = true, ["prefabPath"] = "../Outside/rejected.prefab" });
            Assert.IsInstanceOf<ErrorResponse>(response);
            Assert.IsNull(GameObject.Find("RejectedPrefabTimeline"));
        }

        [Test]
        public void ModelBounds_ChildScaleAndOffset_AreIncluded()
        {
            var root = new GameObject("BoundsTimeline");
            var child = new GameObject("Child");
            var mesh = new Mesh { bounds = new Bounds(Vector3.zero, new Vector3(2, 1, 1)) };
            child.transform.SetParent(root.transform, false);
            child.transform.localScale = new Vector3(3, 1, 1);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            try
            {
                Assert.AreEqual(6f, MCPForUnity.Editor.Services.AssetGen.Import.ModelImportPipeline.ComputeMaxDimension(root), 0.001f);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void EmptyZipAllowlist_ExtractsNoFiles()
        {
            string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcp_empty_allowlist_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            try
            {
                string zip = System.IO.Path.Combine(directory, "input.zip");
                using (var stream = System.IO.File.Create(zip))
                using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
                using (var writer = new System.IO.StreamWriter(archive.CreateEntry("payload.cs").Open())) writer.Write("inert test data");
                string output = System.IO.Path.Combine(directory, "out");
                MCPForUnity.Editor.Services.AssetGen.Import.SafeZipExtractor.ExtractTo(zip, output, new HashSet<string>());
                Assert.IsEmpty(System.IO.Directory.GetFiles(output, "*", System.IO.SearchOption.AllDirectories));
            }
            finally { System.IO.Directory.Delete(directory, true); }
        }

        [Test]
        public void SavePrefab_ReturnsAndSelectsSceneInstance()
        {
            string path = "Assets/__TimelinePrefab_" + Guid.NewGuid().ToString("N") + ".prefab";
            GameObject instance = null;
            try
            {
                var result = GameObjectCreate.Handle(new JObject
                { ["name"] = "PrefabInstanceTimeline", ["saveAsPrefab"] = true, ["prefabPath"] = path });
                Assert.IsInstanceOf<SuccessResponse>(result);
                instance = Selection.activeGameObject;
                Assert.IsNotNull(instance);
                Assert.IsFalse(EditorUtility.IsPersistent(instance));
                Assert.IsTrue(instance.scene.IsValid());
                Assert.IsTrue(PrefabUtility.IsPartOfPrefabInstance(instance));
            }
            finally
            {
                if (instance != null && !EditorUtility.IsPersistent(instance)) UnityEngine.Object.DestroyImmediate(instance);
                AssetDatabase.DeleteAsset(path);
            }
        }
    }
}
