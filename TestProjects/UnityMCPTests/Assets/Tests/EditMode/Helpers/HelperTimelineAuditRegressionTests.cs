using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.TestTools;
using UnityEditor.TestTools;
using System.IO;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using TestNamespace;

namespace MCPForUnityTests.Editor.Helpers
{
    public class HelperTimelineAuditRegressionTests
    {
        [TestCase(-1)]
        [TestCase(0)]
        [TestCase(65536)]
        public void InvalidPortIsRejectedBeforeAnySocketOrPreferenceMutation(int port)
        {
            Assert.IsFalse(PortManager.IsPortAvailable(port));
            Assert.Throws<ArgumentOutOfRangeException>(() => PortManager.SetPreferredPort(port));
        }

        [Test]
        public void Pagination_InvalidSizeIsNormalizedBeforePageOffset()
        {
            var request = PaginationRequest.FromParams(new JObject { ["page_size"] = 0, ["page_number"] = 3 });
            Assert.AreEqual(100, request.Cursor);
            Assert.AreEqual(50, request.PageSize);
        }

        [Test]
        public void Pagination_LargePageCannotWrapBackToEarlierItems()
        {
            var request = PaginationRequest.FromParams(new JObject { ["page_size"] = int.MaxValue, ["page_number"] = int.MaxValue });
            Assert.AreEqual(int.MaxValue, request.Cursor);
            var response = PaginationResponse<int>.Create(new[] { 0, 1, 2 }, request);
            Assert.IsEmpty(response.Items);
            Assert.IsFalse(response.HasMore);
        }

        [Test]
        public void Pagination_HugeSizeAtNonzeroCursorStillReturnsRemainingItems()
        {
            var response = PaginationResponse<int>.Create(new[] { 0, 1, 2 }, new PaginationRequest { Cursor = 1, PageSize = int.MaxValue });
            CollectionAssert.AreEqual(new[] { 1, 2 }, response.Items);
            Assert.IsNull(response.NextCursor);
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void Pagination_DirectInvalidSizeStillMakesProgress(int size)
        {
            var response = PaginationResponse<int>.Create(new[] { 0, 1, 2 }, new PaginationRequest { PageSize = size });
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, response.Items);
            Assert.IsFalse(response.HasMore);
        }

        [Test]
        public void Codex_InvalidExistingTomlCannotBecomeAFreshConfiguration()
        {
            Assert.Throws<FormatException>(() => CodexConfigHelper.UpsertCodexServerBlock("[broken", null));
        }

        [Test]
        public void AtomicWrite_DoesNotUseOrDeleteAnotherWritersScratchFiles()
        {
            string dir = Path.Combine(Path.GetTempPath(), "mcp-helper-audit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "config.json");
                File.WriteAllText(path, "original");
                File.WriteAllText(path + ".backup", "unrelated-old-backup");
                Directory.CreateDirectory(path + ".tmp");
                McpConfigurationHelper.WriteAtomicFile(path, "updated");
                Assert.AreEqual("updated", File.ReadAllText(path));
                Assert.AreEqual("unrelated-old-backup", File.ReadAllText(path + ".backup"));
                Assert.IsTrue(Directory.Exists(path + ".tmp"));
                Assert.AreEqual(2, Directory.GetFiles(dir).Length, "Owned temporary files should be cleaned up.");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void ObjectResolver_DefaultSearchSupportsIdAndAbsoluteHierarchyPath()
        {
            var root = new GameObject("HelperAudit_" + Guid.NewGuid().ToString("N"));
            var child = new GameObject("Child");
            try
            {
                child.transform.SetParent(root.transform);
                Assert.AreEqual(child, ObjectResolver.ResolveGameObject(new JValue(child.GetInstanceID())));
                Assert.AreEqual(child, ObjectResolver.ResolveGameObject(new JValue("/" + root.name + "/Child")));
                child.SetActive(false);
                var found = GameObjectLookup.SearchGameObjects("by_path", "/" + root.name + "/Child", true, 1);
                CollectionAssert.AreEqual(new[] { child.GetInstanceID() }, found);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [UnityTest]
        public IEnumerator Serializer_PlayModeInspectionDoesNotReplaceSharedNativeResources()
        {
            yield return new EnterPlayMode();
            var go = new GameObject("HelperAudit_NativeResources");
            var mesh = new Mesh();
            var material = new Material(Shader.Find("Hidden/InternalErrorShader"));
            bool meshUnchanged;
            bool materialUnchanged;
            try
            {
                var filter = go.AddComponent<MeshFilter>();
                var renderer = go.AddComponent<MeshRenderer>();
                filter.sharedMesh = mesh;
                renderer.sharedMaterial = material;
                GameObjectSerializer.GetComponentData(filter);
                GameObjectSerializer.GetComponentData(renderer);
                meshUnchanged = filter.sharedMesh == mesh;
                materialUnchanged = renderer.sharedMaterial == material;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(mesh);
                UnityEngine.Object.DestroyImmediate(material);
            }
            yield return new ExitPlayMode();
            Assert.IsTrue(meshUnchanged, "Reading component data must not clone its mesh.");
            Assert.IsTrue(materialUnchanged, "Reading component data must not clone its material.");
        }

        [Test]
        public void ComponentArray_LaterInvalidElementDoesNotCommitResizeOrEarlierEdits()
        {
            var go = new GameObject("HelperAudit_Event");
            try
            {
                var component = go.AddComponent<UnityEventTestComponent>();
                var valid = JObject.Parse(@"{ 'm_PersistentCalls': { 'm_Calls': [ { 'm_MethodName': 'Original' } ] } }");
                Assert.IsTrue(ComponentOps.SetProperty(component, "onSimpleEvent", valid, out string error), error);
                var invalid = JObject.Parse(@"{ 'm_PersistentCalls': { 'm_Calls': [ { 'm_MethodName': 'Changed' }, { 'unknownField': true } ] } }");
                Assert.IsFalse(ComponentOps.SetProperty(component, "onSimpleEvent", invalid, out error));
                using var serialized = new SerializedObject(component);
                var calls = serialized.FindProperty("onSimpleEvent.m_PersistentCalls.m_Calls");
                Assert.AreEqual(1, calls.arraySize);
                Assert.AreEqual("Original", calls.GetArrayElementAtIndex(0).FindPropertyRelative("m_MethodName").stringValue);
                Assert.IsTrue(ComponentOps.SetProperty(component, "onSimpleEvent", valid, out error), error, "A failed edit must not poison the next request.");
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
