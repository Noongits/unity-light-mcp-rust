using System;
using System.IO;
using MCPForUnity.Editor.Tools.Physics;
using MCPForUnity.Editor.Tools.Prefabs;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor.PackageManager;

namespace MCPForUnityTests.Editor.Tools
{
    // Pure request validation and source contracts only: no scene/asset/provider mutation.
    public class GameplayBatchTwoTimelineRegressionTests
    {
        [Test]
        public void LaterMalformedVectorRejectsWholeRequestRegardlessOfFieldOrder()
        {
            Func<string, Type> schema = key => key == "gravity" ? typeof(Vector3) : typeof(float);
            var first = new JObject { ["mass"] = 2, ["gravity"] = new JArray(1, 2) };
            var reverse = new JObject { ["gravity"] = new JArray(1, 2), ["mass"] = 2 };
            StringAssert.Contains("gravity", PhysicsSettingsOps.ValidateValues(first, schema));
            StringAssert.Contains("gravity", PhysicsSettingsOps.ValidateValues(reverse, schema));
            Assert.IsNull(PhysicsSettingsOps.ValidateValues(new JObject { ["gravity"] = new JArray(1, 2, 3) }, schema));
        }

        [TestCase("999"), TestCase("UnknownValue")]
        public void UndefinedEnumsCannotFollowAnAlreadyValidProperty(string value)
        {
            var request = new JObject { ["mass"] = 2, ["mode"] = value };
            StringAssert.Contains("mode", PhysicsSettingsOps.ValidateValues(request,
                key => key == "mode" ? typeof(ForceMode) : typeof(float)));
        }

        [Test]
        public void RepeatedNormalizedKeysAreRejectedBeforeApplying()
        {
            var request = new JObject { ["use_gravity"] = true, ["useGravity"] = false };
            StringAssert.Contains("Duplicate", PhysicsSettingsOps.ValidateValues(request, key => typeof(bool)));
        }

        [TestCase(float.NaN), TestCase(float.PositiveInfinity), TestCase(float.NegativeInfinity)]
        public void NonFiniteNumbersAreRejected(float value)
        {
            Assert.IsNotNull(PhysicsSettingsOps.ValidateValues(new JObject { ["mass"] = value }, key => typeof(float)));
            Assert.IsNotNull(PhysicsSettingsOps.ValidateValues(new JObject { ["force"] = new JArray(0, value, 0) }, key => typeof(Vector3)));
        }

        [Test]
        public void NullVectorElementIsNotSilentlyZero()
        {
            Assert.IsNotNull(PhysicsSettingsOps.ValidateValues(new JObject { ["force"] = new JArray(0, JValue.CreateNull(), 0) }, key => typeof(Vector3)));
        }

        [Test]
        public void FlagCombinationsRemainValidButUnknownBitsDoNot()
        {
            Assert.IsNull(PhysicsSettingsOps.ValidateValues(new JObject { ["constraints"] = "FreezePositionX, FreezeRotationY" }, key => typeof(RigidbodyConstraints)));
            Assert.IsNotNull(PhysicsSettingsOps.ValidateValues(new JObject { ["constraints"] = 1073741824 }, key => typeof(RigidbodyConstraints)));
        }

        [Test]
        public void TextBudgetIncludesOversizedHeaderAndTruncationNotice()
        {
            var output = new InspectPrefab.TextOut(500);
            output.Line(new string('x', 1000));
            string text = output.Finish(new string('h', 800), new string('n', 800));
            Assert.LessOrEqual(text.Length, 500);
            StringAssert.EndsWith("... truncated", text);
        }

        private static string Source(string relative)
        {
            var package = PackageInfo.FindForAssembly(typeof(ManagePhysics).Assembly);
            Assert.IsNotNull(package, "Source-contract tests require the local package checkout.");
            return File.ReadAllText(Path.Combine(package.resolvedPath, "Editor/Tools", relative));
        }

        private static void Before(string source, string earlier, string later)
        {
            int a = source.IndexOf(earlier, StringComparison.Ordinal);
            int b = source.IndexOf(later, StringComparison.Ordinal);
            Assert.GreaterOrEqual(a, 0, earlier);
            Assert.GreaterOrEqual(b, 0, later);
            Assert.Less(a, b, earlier + " must precede " + later);
        }

        [Test]
        public void MutationEntryPointsPreflightBeforeNativeWrites()
        {
            Before(Source("Physics/PhysicsRigidbodyOps.cs"), "ValidateValues(properties", "Undo.RecordObject(rb,");
            Before(Source("Physics/PhysicsMaterialOps.cs"), "string createError", "EnsureFolderExists(folder");
            Before(Source("Physics/PhysicsForceOps.cs"), "ValidateValues(values", "rb2d.AddForce");
            Before(Source("Physics/JointOps.cs"), "ValidateProperties(jointComponentType", "Undo.AddComponent");
            Before(Source("Physics/JointOps.cs"), "foreach (string section", "Undo.RecordObject(joint");
        }

        [Test]
        public void FailedMaterialCreationCleansOnlyUnadoptedNativeObjects()
        {
            string source = Source("Physics/PhysicsMaterialOps.cs");
            StringAssert.Contains("if (!EditorUtility.IsPersistent(mat))", source);
            StringAssert.Contains("if (mat != null && !EditorUtility.IsPersistent(mat)) UnityEngine.Object.DestroyImmediate(mat);", source);
            Assert.AreEqual(2, source.Split(new[] { "Ownership transfers to the AssetDatabase" }, StringSplitOptions.None).Length - 1);
        }

        [Test]
        public void SimulationRestoresModesAndChecksFailedSteps()
        {
            string source = Source("Physics/PhysicsSimulationOps.cs");
            StringAssert.Contains("if (!Physics2D.Simulate(stepSize))", source);
            StringAssert.Contains("Physics2D.simulationMode = previousMode;", source);
            StringAssert.Contains("!UnityPhysicsCompat.TrySetPhysicsSimulationMode(UnityPhysicsCompat.SimulationMode.Script)", source);
            Before(source, "stepSize <= 0", "Physics2D.SyncTransforms()");
        }

        [Test]
        public void RepeatedInspectionDisposesNativeSerializationHandlesAndAvoidsUserCallbacks()
        {
            string node = Source("Prefabs/InspectPrefab.Node.cs");
            StringAssert.Contains("using var so = new SerializedObject(c);", node);
            StringAssert.Contains("using var so = new SerializedObject(c);", Source("Prefabs/InspectPrefab.Refs.cs"));
            Before(node, "typeof(MonoBehaviour).IsAssignableFrom(type)", "EditorSceneManager.NewPreviewScene()");
            StringAssert.Contains("return p.propertyType == SerializedPropertyType.Float", node);
        }

        [Test]
        public void FilteredTreeKeepsDistinctPathsAndDllFilteringPrecedesPropagation()
        {
            string tree = Source("Prefabs/InspectPrefab.Tree.cs");
            Before(tree, "if (_filtering)", "bySig.TryGetValue(k.Sig");
            string usages = Source("Prefabs/InspectPrefab.Usages.cs");
            Before(usages, "asset.GetComponentsInChildren(type, true).Length == 0", "usedBy[file] = \"\";");
            StringAssert.Contains("candidates.Skip(rootSegment.index - 1).Take(1)", Source("Prefabs/InspectPrefab.cs"));
        }
    }
}
