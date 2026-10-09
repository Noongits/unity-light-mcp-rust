using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Tools;
using MCPForUnity.Editor.Tools.Blender;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MCPForUnityTests.Editor.Tools
{
    [TestFixture]
    public class NativeTextureLifetimeTests
    {
        private string directory;
        private string assetPath;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "MCPTextureLifetime_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            assetPath = "Assets/__MCPTextureLifetime_" + Guid.NewGuid().ToString("N") + ".png";
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(assetPath);
            Directory.Delete(directory, true);
        }

        private static HashSet<int> Snapshot()
            => new HashSet<int>(UnityEngine.Resources.FindObjectsOfTypeAll<Texture2D>()
                .Where(t => !EditorUtility.IsPersistent(t)).Select(t => t.GetInstanceID()));

        private static void AssertNoNewTextures(HashSet<int> before)
        {
            var added = Snapshot();
            added.ExceptWith(before);
            Assert.That(added, Is.Empty, "Operation leaked a native, non-asset Texture2D.");
        }

        private string WritePng(string name, int size = 4)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            try
            {
                texture.Apply();
                string path = Path.Combine(directory, name);
                File.WriteAllBytes(path, texture.EncodeToPNG());
                return path;
            }
            finally { Object.DestroyImmediate(texture); }
        }

        [TestCase("fillColor")]
        [TestCase("palette")]
        [TestCase("pixels")]
        public void CreateTexture_InvalidColorAfterAllocation_ReleasesTexture(string field)
        {
            var badColor = new JArray("not-a-number", 0, 0, 255);
            var args = new JObject { ["action"] = "create", ["path"] = assetPath, ["width"] = 1, ["height"] = 1 };
            if (field == "palette")
            {
                args["pattern"] = "checkerboard";
                args[field] = new JArray { badColor };
            }
            else if (field == "pixels") args[field] = new JArray { badColor };
            else args[field] = badColor;
            var before = Snapshot();
            var response = JObject.FromObject(ManageTexture.HandleCommand(args));
            Assert.That(response.Value<bool>("success"), Is.False);
            AssertNoNewTextures(before);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CreateTexture_Success_ReleasesWorkingTextureAndKeepsImportedAsset(bool fromImage)
        {
            string imagePath = fromImage ? WritePng("input.png") : null;
            var before = Snapshot();
            var args = new JObject { ["action"] = "create", ["path"] = assetPath, ["width"] = 4, ["height"] = 4 };
            if (fromImage) args["imagePath"] = imagePath;
            var response = JObject.FromObject(ManageTexture.HandleCommand(args));
            Assert.That(response.Value<bool>("success"), Is.True);
            Assert.That(AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath), Is.Not.Null);
            AssertNoNewTextures(before);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ModifyTexture_SuccessOrParseFailure_ReleasesCopyAndPreservesAsset(bool invalidColor)
        {
            File.Copy(WritePng("source.png"), assetPath);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            var before = Snapshot();
            var response = JObject.FromObject(ManageTexture.HandleCommand(new JObject
            {
                ["action"] = "modify", ["path"] = assetPath,
                ["setPixels"] = new JObject
                {
                    ["color"] = invalidColor ? new JArray("invalid", 0, 0, 255) : new JArray(255, 0, 0, 255)
                }
            }));
            Assert.That(response.Value<bool>("success"), Is.EqualTo(!invalidColor));
            Assert.That(asset != null, Is.True, "The imported asset is not owned by the operation.");
            AssertNoNewTextures(before);
        }

        [TestCase("missing_left")]
        [TestCase("missing_right")]
        [TestCase("write_failure")]
        [TestCase("success")]
        public void BlenderComposite_AllExitPaths_ReleaseOwnedTextures(string scenario)
        {
            string left = WritePng("left.png", 8);
            string right = WritePng("right.png", 4);
            string output = scenario == "write_failure" ? directory : Path.Combine(directory, "out.png");
            if (scenario == "missing_left") left += ".missing";
            if (scenario == "missing_right") right += ".missing";
            var before = Snapshot();
            if (scenario == "success")
            {
                var size = BlenderBridgeTool.CompositeSideBySide(left, right, output);
                Assert.That(size.Width, Is.EqualTo(8));
                Assert.That(size.Height, Is.EqualTo(4));
                Assert.That(File.Exists(output), Is.True);
            }
            else Assert.Catch(() => BlenderBridgeTool.CompositeSideBySide(left, right, output));
            AssertNoNewTextures(before);
        }

        [Test]
        public void BlenderScale_UnreadableSource_ReleasesDestinationAndPreservesSource()
        {
            var source = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            try
            {
                source.Apply(false, true);
                var before = Snapshot();
                var method = typeof(BlenderBridgeTool).GetMethod("ScaleToHeight", BindingFlags.NonPublic | BindingFlags.Static);
                var exception = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] { source, 4 }));
                Assert.That(exception.InnerException, Is.TypeOf<UnityException>());
                Assert.That(source != null, Is.True);
                AssertNoNewTextures(before);
            }
            finally { Object.DestroyImmediate(source); }
        }

        [TestCase("success")]
        [TestCase("invalid_first")]
        [TestCase("unreadable_second")]
        public void ContactSheet_ConsumesTilesEvenWhenSetupOrPixelReadFails(string scenario)
        {
            var before = Snapshot();
            var first = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var second = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            try
            {
                first.Apply();
                second.Apply(false, scenario == "unreadable_second");
                if (scenario == "invalid_first") Object.DestroyImmediate(first);
                var tiles = new List<Texture2D> { first, second };
                if (scenario == "success")
                {
                    var result = ScreenshotUtility.ComposeContactSheet(tiles, null);
                    Assert.That(result.base64, Is.Not.Empty);
                }
                else Assert.Catch(() => ScreenshotUtility.ComposeContactSheet(tiles, null));
                Assert.That(first == null && second == null, Is.True);
                AssertNoNewTextures(before);
            }
            finally
            {
                if (first != null) Object.DestroyImmediate(first);
                if (second != null) Object.DestroyImmediate(second);
            }
        }
    }
}
