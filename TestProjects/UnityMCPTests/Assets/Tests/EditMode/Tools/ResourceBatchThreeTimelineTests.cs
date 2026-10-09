using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MCPForUnityTests.Editor.Tools
{
    // Unity Test Framework runs these editor fixtures sequentially; its NUnit fork has no NonParallelizable attribute.
    public class ResourceBatchThreeTimelineTests
    {
        private static Type EditorType(string name) => typeof(ManageMaterial).Assembly.GetType(name, true);
        private static object Invoke(Type type, string name, params object[] args) =>
            type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Invoke(null, args);
        private static JObject Response(object result) => JObject.FromObject(result);

        [Test]
        public void Session_ResetBeforeDelayedWrite_PreventsOldSessionRestoration()
        {
            var type = EditorType("MCPForUnity.Editor.Helpers.ProjectIdentityUtility");
            string stored = "initial";
            var write = (Action)Invoke(type, "CreateSessionWrite", (Action)(() => stored = "stale"));
            Invoke(type, "ResetSessionState", (Action)(() => stored = null));
            write();
            Assert.IsNull(stored);
        }

        [Test]
        public void Session_LaterAssignmentWinsEvenWhenCallbacksRunBackwards()
        {
            var type = EditorType("MCPForUnity.Editor.Helpers.ProjectIdentityUtility");
            string stored = null;
            var older = (Action)Invoke(type, "CreateSessionWrite", (Action)(() => stored = "older"));
            var newer = (Action)Invoke(type, "CreateSessionWrite", (Action)(() => stored = "newer"));
            newer();
            older();
            Assert.AreEqual("newer", stored);
        }

        [Test]
        public void Session_ResetFailureStillInvalidatesQueuedWrite()
        {
            var type = EditorType("MCPForUnity.Editor.Helpers.ProjectIdentityUtility");
            bool wrote = false;
            var write = (Action)Invoke(type, "CreateSessionWrite", (Action)(() => wrote = true));
            Assert.Throws<TargetInvocationException>(() => Invoke(type, "ResetSessionState", (Action)(() => throw new InvalidOperationException("mock preferences unavailable"))));
            write();
            Assert.IsFalse(wrote);
        }

        [Test]
        public void Telemetry_OptOutCheckFailureCannotEscapeIntoCaller()
        {
            Assert.DoesNotThrow(() => Invoke(typeof(TelemetryHelper), "RecordEventCore", "test", null,
                (Func<bool>)(() => throw new InvalidOperationException("mock preferences unavailable"))));
        }

        [Test]
        public void FloatParameter_IsIndependentOfEditorCulture()
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                Assert.AreEqual(1.25f, new ToolParams(new JObject { ["weight"] = "1.25" }).GetFloat("weight"));
                Assert.AreEqual(1.25f, new ToolParams(new JObject { ["weight"] = 1.25f }).GetFloat("weight"));
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        [Test]
        public void QualifiedTypeLookup_DoesNotHideShortNameAmbiguity()
        {
            Assert.IsTrue(UnityTypeResolver.TryResolve(typeof(ResourceAuditA.ResourceBatchThreeTwin).FullName, out _, out _));
            Assert.IsFalse(UnityTypeResolver.TryResolve("ResourceBatchThreeTwin", out _, out string error));
            StringAssert.Contains("Ambiguous", error);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void RemoveReference_RemovesExactlyOneSlotAndKeepsNullNeighbor(bool firstIsNull)
        {
            var holder = ScriptableObject.CreateInstance<ResourceBatchThreeHolder>();
            var item = ScriptableObject.CreateInstance<ResourceBatchThreeHolder>();
            try
            {
                holder.references = new Object[] { firstIsNull ? null : item, null, item };
                using var serialized = new SerializedObject(holder);
                Invoke(EditorType("MCPForUnity.Editor.Tools.Graphics.RendererFeatureOps"), "DeleteReferenceElement",
                    serialized.FindProperty("references"), 0);
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.AreEqual(2, holder.references.Length);
                Assert.IsNull(holder.references[0]);
                Assert.AreSame(item, holder.references[1]);
            }
            finally { Object.DestroyImmediate(holder); Object.DestroyImmediate(item); }
        }

        [TestCase("UnknownMode")]
        [TestCase("-1")]
        [TestCase("42")]
        public void InvalidSerializedEnum_DoesNotResetExistingValue(string value)
        {
            var holder = ScriptableObject.CreateInstance<ResourceBatchThreeHolder>();
            try
            {
                holder.mode = ResourceBatchThreeHolder.SampleMode.Second;
                using var serialized = new SerializedObject(holder);
                bool changed = (bool)Invoke(EditorType("MCPForUnity.Editor.Tools.Graphics.GraphicsHelpers"),
                    "SetSerializedValue", serialized.FindProperty("mode"), new JValue(value));
                Assert.IsFalse(changed);
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.AreEqual(ResourceBatchThreeHolder.SampleMode.Second, holder.mode);
            }
            finally { Object.DestroyImmediate(holder); }
        }

        [TestCase("unknown", 0)]
        [TestCase("instance", 4)]
        [TestCase("create_unique", -1)]
        public void InvalidRendererColorRequest_DoesNotAllocateOrReplaceMaterials(string mode, int slot)
        {
            var go = new GameObject("ResourceBatchThree_" + Guid.NewGuid().ToString("N"));
            try
            {
                var renderer = go.AddComponent<MeshRenderer>();
                var original = renderer.sharedMaterials;
                var result = Response(ManageMaterial.HandleCommand(new JObject
                {
                    ["action"] = "set_renderer_color", ["target"] = go.name,
                    ["mode"] = mode, ["slot"] = slot, ["color"] = new JArray(1, 0, 0, 1)
                }));
                Assert.IsFalse(result.Value<bool>("success"));
                CollectionAssert.AreEqual(original, renderer.sharedMaterials);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void MoveAsset_ReturnsSuccessAfterAssetDatabaseReturnsEmptyError()
        {
            string source = "Assets/__ResourceBatchThree_" + Guid.NewGuid().ToString("N") + ".asset";
            string destination = source.Replace(".asset", "_moved.asset");
            var asset = ScriptableObject.CreateInstance<ResourceBatchThreeHolder>();
            try
            {
                AssetDatabase.CreateAsset(asset, source);
                asset = AssetDatabase.LoadAssetAtPath<ResourceBatchThreeHolder>(source);
                var result = Response(ManageAsset.HandleCommand(new JObject
                { ["action"] = "move", ["path"] = source, ["destination"] = destination }));
                Assert.IsTrue(result.Value<bool>("success"), result.ToString());
                Assert.IsNull(AssetDatabase.LoadAssetAtPath<Object>(source));
                Assert.AreEqual(asset, AssetDatabase.LoadAssetAtPath<Object>(destination));
            }
            finally
            {
                AssetDatabase.DeleteAsset(source);
                AssetDatabase.DeleteAsset(destination);
                if (asset != null && !EditorUtility.IsPersistent(asset)) Object.DestroyImmediate(asset);
            }
        }

        [TestCase("../outside")]
        [TestCase("Assets/../outside")]
        public void ProfilePath_RejectsTraversalBeforeNativeAllocation(string path)
        {
            Assert.IsNull(Invoke(EditorType("MCPForUnity.Editor.Tools.Graphics.VolumeOps"), "NormalizeProfilePath", path));
        }

        [Test]
        public void ProfilePath_NormalizesSeparatorsAndAddsExtension()
        {
            Assert.AreEqual("Assets/Settings/Profile.asset", Invoke(
                EditorType("MCPForUnity.Editor.Tools.Graphics.VolumeOps"), "NormalizeProfilePath", "Settings\\Profile"));
        }

        [Test]
        public void CreateProfile_ExistingAssetIsNeverReplaced()
        {
            string path = "Assets/__ResourceBatchThree_" + Guid.NewGuid().ToString("N") + ".asset";
            var existing = ScriptableObject.CreateInstance<ResourceBatchThreeHolder>();
            try
            {
                AssetDatabase.CreateAsset(existing, path);
                string guid = AssetDatabase.AssetPathToGUID(path);
                var result = Response(Invoke(EditorType("MCPForUnity.Editor.Tools.Graphics.VolumeOps"), "CreateProfile", new JObject { ["path"] = path }));
                Assert.IsFalse(result.Value<bool>("success"));
                Assert.AreSame(existing, AssetDatabase.LoadAssetAtPath<Object>(path));
                Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(path));
            }
            finally { AssetDatabase.DeleteAsset(path); }
        }

        [Test]
        public void PersistVolumeEffect_MakesEffectAnOwnedSubAsset()
        {
            string path = "Assets/__ResourceBatchThree_" + Guid.NewGuid().ToString("N") + ".asset";
            var profile = ScriptableObject.CreateInstance<ResourceBatchThreeHolder>();
            var effect = ScriptableObject.CreateInstance<ResourceBatchThreeHolder>();
            effect.name = "OwnedEffect";
            try
            {
                AssetDatabase.CreateAsset(profile, path);
                profile = AssetDatabase.LoadAssetAtPath<ResourceBatchThreeHolder>(path);
                Invoke(EditorType("MCPForUnity.Editor.Tools.Graphics.VolumeOps"), "PersistVolumeComponent", profile, effect);
                Assert.IsTrue(EditorUtility.IsPersistent(effect));
                Assert.AreEqual(path, AssetDatabase.GetAssetPath(effect));
                AssetDatabase.SaveAssetIfDirty(profile);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var savedEffect = System.Linq.Enumerable.FirstOrDefault(AssetDatabase.LoadAllAssetsAtPath(path), item => item.name == "OwnedEffect");
                Assert.IsNotNull(savedEffect, "The effect must survive saving and reimporting its owning asset.");
                Assert.IsTrue(AssetDatabase.IsSubAsset(savedEffect));
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
                if (effect != null && !EditorUtility.IsPersistent(effect)) Object.DestroyImmediate(effect);
            }
        }

        [Test]
        public void Fog_InvalidModeDoesNotChangeEnabledState()
        {
            bool initial = RenderSettings.fog;
            try
            {
                var result = Response(Invoke(EditorType("MCPForUnity.Editor.Tools.Graphics.SkyboxOps"), "SetFog",
                    new JObject { ["enabled"] = !initial, ["mode"] = "invalid_mode" }));
                Assert.IsFalse(result.Value<bool>("success"));
                Assert.AreEqual(initial, RenderSettings.fog);
            }
            finally { RenderSettings.fog = initial; }
        }

        [Test]
        public void Reflection_MissingCubemapDoesNotChangeIntensity()
        {
            float initial = RenderSettings.reflectionIntensity;
            try
            {
                var result = Response(Invoke(EditorType("MCPForUnity.Editor.Tools.Graphics.SkyboxOps"), "SetReflection",
                    new JObject { ["intensity"] = initial == 0 ? 1 : 0, ["path"] = "Assets/__missing_" + Guid.NewGuid().ToString("N") + ".cubemap" }));
                Assert.IsFalse(result.Value<bool>("success"));
                Assert.AreEqual(initial, RenderSettings.reflectionIntensity);
            }
            finally { RenderSettings.reflectionIntensity = initial; }
        }
    }
}

namespace ResourceAuditA { public class ResourceBatchThreeTwin : ScriptableObject { } }
namespace ResourceAuditB { public class ResourceBatchThreeTwin : ScriptableObject { } }
