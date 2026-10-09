using System;
using System.IO;
using UnityEditor;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Tools;

namespace MCPForUnity.Tests.EditMode.Tools
{
    [TestFixture]
    public class ManageSceneTemplateTests
    {
        [Test]
        public void Create_UnknownTemplate_ReturnsError()
        {
            string root = "Assets/__SceneTemplateTest_" + Guid.NewGuid().ToString("N");
            try
            {
            var p = new JObject
            {
                ["action"] = "create",
                ["name"] = "TemplateTest",
                ["path"] = root,
                ["template"] = "nonexistent_template"
            };
            var result = ManageScene.HandleCommand(p);
            var r = result as JObject ?? JObject.FromObject(result);
            Assert.IsFalse(r.Value<bool>("success"), "Unknown template should fail");
            StringAssert.Contains("Unknown template", r.Value<string>("error"));
            Assert.IsFalse(File.Exists(root + "/TemplateTest.unity"));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
                if (File.Exists(root + ".meta")) File.Delete(root + ".meta");
                AssetDatabase.Refresh();
            }
        }
    }
}
