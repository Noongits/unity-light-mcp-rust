using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Xml;
using MCPForUnity.Editor.Tools;
using MCPForUnityTests.Editor.Tools.Fixtures;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    // Unity Test Framework runs these editor fixtures sequentially; its NUnit fork has no NonParallelizable attribute.
    public class ManageUIAuditRegressionTests
    {
        private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Static;
        private string root;
        private bool ownsCaptureState;

        private static object Invoke(string method, params object[] args) =>
            typeof(ManageUI).GetMethod(method, Flags).Invoke(null, args);
        private static FieldInfo Field(string name) => typeof(ManageUI).GetField(name, Flags);

        [SetUp]
        public void SetUp()
        {
            ownsCaptureState = false;
            root = null;
            if (((IDictionary)Field("s_panelRTs").GetValue(null)).Count != 0
                || (bool)Field("s_pendingCaptureStarted").GetValue(null)
                || (bool)Field("s_pendingCaptureDone").GetValue(null)
                || Field("s_pendingCaptureTex").GetValue(null) != null)
                Assert.Ignore("Existing UI render resources or a capture must be preserved.");
            ownsCaptureState = true;
            root = "Assets/Temp/ManageUIAudit_" + Guid.NewGuid().ToString("N");
            EnsureFolder(root);
        }

        [TearDown]
        public void TearDown()
        {
            // These synchronous tests create capture flags only, never native render
            // resources. Do not sweep the global cache or destroy a borrowed texture.
            if (ownsCaptureState && Field("s_pendingCaptureTex").GetValue(null) == null)
                Invoke("ResetPendingCapture");
            ownsCaptureState = false;
            if (root != null) AssetDatabase.DeleteAsset(root);
        }

        [Test]
        public void CreatePanelSettings_PreservesOtherAssetType()
        {
            string path = root + "/Existing.asset";
            var existing = ScriptableObject.CreateInstance<ManageScriptableObjectTestDefinition>();
            AssetDatabase.CreateAsset(existing, path);
            string guid = AssetDatabase.AssetPathToGUID(path);
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create_panel_settings", ["path"] = path
            }));
            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            Assert.AreSame(existing, AssetDatabase.LoadMainAssetAtPath(path));
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(path));
        }

        [Test]
        public void CreatePanelSettings_PreservesUnimportedFile()
        {
            string path = root + "/Unimported.asset";
            const string contents = "This existing file must not be replaced.";
            File.WriteAllText(path, contents);
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create_panel_settings", ["path"] = path
            }));
            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(contents, File.ReadAllText(path));
        }

        [Test]
        public void DefaultPanelSettingsCreation_RejectsCollisionToo()
        {
            string path = root + "/Existing.asset";
            var existing = ScriptableObject.CreateInstance<ManageScriptableObjectTestDefinition>();
            AssetDatabase.CreateAsset(existing, path);
            var error = Assert.Throws<TargetInvocationException>(() => Invoke("CreateDefaultPanelSettings", path));
            Assert.IsInstanceOf<InvalidOperationException>(error.InnerException);
            Assert.AreSame(existing, AssetDatabase.LoadMainAssetAtPath(path));
        }

        [TestCase(PlayModeStateChange.ExitingEditMode)]
        [TestCase(PlayModeStateChange.ExitingPlayMode)]
        public void PlayModeExit_InvalidatesPendingCapture(PlayModeStateChange state)
        {
            int generation = (int)Field("s_captureGeneration").GetValue(null);
            Field("s_pendingCaptureStarted").SetValue(null, true);
            Invoke("OnPlayModeStateChanged", state);
            Assert.IsFalse((bool)Field("s_pendingCaptureStarted").GetValue(null));
            Assert.IsFalse((bool)Field("s_pendingCaptureDone").GetValue(null));
            Assert.AreNotEqual(generation, Field("s_captureGeneration").GetValue(null));
            // Even a late failed callback must not publish a result into the next session.
            Invoke("CompletePlayModeCapture", generation, null);
            Assert.IsFalse((bool)Field("s_pendingCaptureDone").GetValue(null));
        }

        [Test]
        public void CaptureCompletion_NullResultIsTerminal_AndDuplicateIsIgnored()
        {
            int generation = (int)Field("s_captureGeneration").GetValue(null);
            Field("s_pendingCaptureStarted").SetValue(null, true);
            Invoke("CompletePlayModeCapture", generation, null);
            Assert.IsTrue((bool)Field("s_pendingCaptureDone").GetValue(null));
            Assert.IsFalse((bool)Field("s_pendingCaptureStarted").GetValue(null));
            Field("s_pendingCaptureDone").SetValue(null, false);
            Invoke("CompletePlayModeCapture", generation, null);
            Assert.IsFalse((bool)Field("s_pendingCaptureDone").GetValue(null));
        }

        [TestCase("../escape.png")]
        [TestCase("/tmp/escape.png")]
        [TestCase(@"..\escape.png")]
        [TestCase(@"C:\temp\escape.png")]
        public void RenderFileName_CannotEscapeOutputFolder(string name)
        {
            string safe = (string)Invoke("BuildRenderFileName", name);
            Assert.IsFalse(safe.Contains("/"));
            Assert.IsFalse(safe.Contains("\\"));
            Assert.IsFalse(Path.IsPathRooted(safe));
            Assert.That(safe, Does.EndWith(".png"));
        }

        [TestCase("capture", "capture.png")]
        [TestCase(" capture.PNG ", "capture.PNG")]
        public void RenderFileName_PreservesNormalNames(string name, string expected)
        {
            Assert.AreEqual(expected, Invoke("BuildRenderFileName", name));
        }

        [TestCase(0, 1)]
        [TestCase(1, -1)]
        public void Render_RejectsInvalidDimensionsBeforeResolvingTarget(int width, int height)
        {
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "render_ui", ["target"] = "missing", ["width"] = width, ["height"] = height
            }));
            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result.Value<string>("error"), Does.Contain("must be positive"));
        }

        [TestCase("<UXML note='a > b'><Label /></UXML>")]
        [TestCase("<!-- <UXML> editor-extension-mode -->\n<UXML />")]
        [TestCase("<UXML><Label text='editor-extension-mode' /></UXML>")]
        [TestCase("<?xml version='1.0'?><engine:UXML xmlns:engine='UnityEngine.UIElements' />")]
        public void EditorExtensionMode_OnlyChangesActualRootOpeningTag(string contents)
        {
            string result = (string)Invoke("EnsureEditorExtensionMode", contents);
            var document = new XmlDocument();
            document.LoadXml(result);
            Assert.AreEqual("False", document.DocumentElement.GetAttribute("editor-extension-mode"));
            Assert.AreEqual(result, Invoke("EnsureEditorExtensionMode", result));
        }

        [Test]
        public void EditorExtensionMode_PreservesExplicitValue()
        {
            const string contents = "<UXML editor-extension-mode='True' />";
            Assert.AreEqual(contents, Invoke("EnsureEditorExtensionMode", contents));
        }

        [TestCase("<UXML xmlns='UnityEngine.UIElements' note='a > b'>", "</UXML>")]
        [TestCase("<engine:UXML xmlns:engine='UnityEngine.UIElements'>", "</engine:UXML>")]
        public void LinkStylesheet_UsesRootPrefixAndEscapesAttribute(string open, string close)
        {
            string path = root + "/Link.uxml";
            string stylesheet = root + "/A&B.uss";
            File.WriteAllText(stylesheet, "Label { color: red; }");
            AssetDatabase.ImportAsset(stylesheet);
            File.WriteAllText(path, open + "<!-- <Style src=\"" + stylesheet + "\" /> -->" + close);
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "link_stylesheet", ["path"] = path, ["stylesheet"] = stylesheet
            }));
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            var document = new XmlDocument();
            document.LoadXml(File.ReadAllText(path));
            var style = document.DocumentElement.FirstChild;
            while (style != null && style.NodeType != XmlNodeType.Element) style = style.NextSibling;
            Assert.IsNotNull(style);
            Assert.AreEqual("Style", style.LocalName);
            Assert.AreEqual(document.DocumentElement.Prefix, style.Prefix);
            Assert.AreEqual("project://database/" + stylesheet, style.Attributes["src"].Value);
            var again = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "link_stylesheet", ["path"] = path, ["stylesheet"] = stylesheet
            }));
            Assert.IsTrue(again["data"].Value<bool>("alreadyLinked"));
        }
    }
}
