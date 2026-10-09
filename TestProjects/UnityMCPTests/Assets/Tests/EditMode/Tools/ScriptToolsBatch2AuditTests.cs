using System;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Tests.EditMode.Tools
{
    public class Batch2PatchFixture : ScriptableObject
    {
        public int number = 7;
        public int[] values = Array.Empty<int>();
    }

    [TestFixture]
    public class ScriptToolsBatch2AuditTests
    {
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

        [TestCase("\"a;b\"")]
        [TestCase("@\"a;b\"")]
        [TestCase("/* ; */ \"value\"")]
        [TestCase("Factory(() => { return \"a;b\"; })")]
        public void ExpressionSpan_EndsAtOuterSemicolon(string expression)
        {
            string method = "    public string M() => " + expression + ";";
            string source = "class C\n{\n" + method + "\n    public void Next() {}\n}";
            var args = new object[] { source, 0, source.Length, "M", null, null, null, 0, 0, null };
            Assert.IsTrue((bool)typeof(ManageScript).GetMethod("TryComputeMethodSpan", PrivateStatic).Invoke(null, args), args[9]?.ToString());
            Assert.AreEqual(method, source.Substring((int)args[7], (int)args[8]));
        }

        [TestCase("Assets/../Outside")]
        [TestCase("../Outside")]
        [TestCase("Assets\\..\\Outside")]
        [TestCase("Assets/./Folder")]
        public void FolderNormalization_RejectsTraversalBeforeAssetOperations(string path)
        {
            var args = new object[] { path, null, null };
            Assert.IsFalse((bool)typeof(ManageScriptableObject).GetMethod("TryNormalizeFolderPath", PrivateStatic).Invoke(null, args));
        }

        [Test]
        public void UnknownPatchOperation_DoesNotBecomeSet()
        {
            var target = ScriptableObject.CreateInstance<Batch2PatchFixture>();
            try
            {
                using var serialized = new SerializedObject(target);
                serialized.Update();
                var args = new object[] { serialized, "number", "delete", new JObject { ["value"] = 42 }, false };
                var result = JObject.FromObject(typeof(ManageScriptableObject).GetMethod("ApplyPatch", PrivateStatic).Invoke(null, args));
                Assert.IsFalse(result.Value<bool>("ok"));
                Assert.IsFalse((bool)args[4]);
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.AreEqual(7, target.number);
            }
            finally { UnityEngine.Object.DestroyImmediate(target); }
        }

        [Test]
        public void AutoGrowth_FailedValueStillReportsAppliedResize()
        {
            var target = ScriptableObject.CreateInstance<Batch2PatchFixture>();
            try
            {
                using var serialized = new SerializedObject(target);
                serialized.Update();
                var args = new object[] { serialized, "values.Array.data[2]", new JObject { ["value"] = "not an integer" }, false };
                var result = JObject.FromObject(typeof(ManageScriptableObject).GetMethod("ApplySet", PrivateStatic).Invoke(null, args));
                Assert.IsFalse(result.Value<bool>("ok"));
                Assert.IsTrue((bool)args[3], "The already-applied resize must be marked dirty and saved by the caller.");
                Assert.AreEqual(3, target.values.Length);
            }
            finally { UnityEngine.Object.DestroyImmediate(target); }
        }

        [Test]
        public void AutoGrowth_MaximumIntegerIndexDoesNotOverflowSize()
        {
            var target = ScriptableObject.CreateInstance<Batch2PatchFixture>();
            try
            {
                using var serialized = new SerializedObject(target);
                var args = new object[] { serialized, "values.Array.data[2147483647]", false };
                Assert.IsFalse((bool)typeof(ManageScriptableObject).GetMethod("EnsureArrayCapacity", PrivateStatic).Invoke(null, args));
                Assert.AreEqual(0, target.values.Length);
            }
            finally { UnityEngine.Object.DestroyImmediate(target); }
        }

        [Test]
        public void OptionalRuntimeTool_IsDiscoveredAsProjectCustomTool()
        {
            var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("MCPForUnity.Examples.Editor.ManageRuntimeCompilation")).FirstOrDefault(t=>t!=null);
            if(type==null)Assert.Ignore("Optional CustomTools runtime tool is not installed.");
            var metadata=MCPForUnity.Editor.Services.MCPServiceLocator.ToolDiscovery.GetToolMetadata("runtime_compilation");
            Assert.IsNotNull(metadata);
            Assert.IsFalse(metadata.IsBuiltIn,"A project example must be exported through custom-tool discovery.");
        }

        [Test]
        public void OptionalRuntimeCompiler_NullSourceIsRejectedWithoutThrowing()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("RoslynRuntimeCompiler")).FirstOrDefault(t => t != null);
            if (type == null) Assert.Ignore("Optional CustomTools Roslyn compiler is not installed in this project.");
            var host = new GameObject("Batch2OwnedCompiler");
            try
            {
                var compiler = host.AddComponent(type);
                var method = type.GetMethod("CompileAndExecute", new[] { typeof(string), typeof(string), typeof(GameObject), typeof(string).MakeByRefType() });
                var args = new object[] { null, "Generated", host, null };
                Assert.IsFalse((bool)method.Invoke(compiler, args));
                StringAssert.Contains("empty", (string)args[3]);
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }
    }
}
