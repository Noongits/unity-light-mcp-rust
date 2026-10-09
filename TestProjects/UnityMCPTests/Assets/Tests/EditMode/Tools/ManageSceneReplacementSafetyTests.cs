using System;
using System.IO;
using MCPForUnity.Editor.Tools;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace MCPForUnityTests.Editor.Tools
{
    public class ManageSceneReplacementSafetyTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void ReplacementGuard_SeesDirtySceneRegardlessOfWhichSceneIsActive(bool dirtySceneActive)
        {
            WithTemporaryScenes((first, second) =>
            {
                EditorSceneManager.MarkSceneDirty(first);
                SceneManager.SetActiveScene(dirtySceneActive ? first : second);
                Assert.IsTrue(ManageScene.HasUnsavedLoadedScenes(),
                    "Single-mode replacement must protect dirty inactive additive scenes too.");
            });
        }

        [Test]
        public void ReplacementGuard_AcceptsCleanScenesAndRechecksAfterDirtySceneCloses()
        {
            if (ManageScene.HasUnsavedLoadedScenes())
                Assert.Ignore("Existing user scenes are dirty; do not change them to run this test.");

            WithTemporaryScenes((first, second) =>
            {
                Assert.IsFalse(ManageScene.HasUnsavedLoadedScenes());
                EditorSceneManager.MarkSceneDirty(first);
                Assert.IsTrue(ManageScene.HasUnsavedLoadedScenes());
                SceneManager.SetActiveScene(second);
                Assert.IsTrue(EditorSceneManager.CloseScene(first, true));
                Assert.IsFalse(ManageScene.HasUnsavedLoadedScenes(),
                    "The guard must observe current loaded scenes rather than cache a stale dirty flag.");
            });
        }

        [Test]
        public void SceneLookup_ExplicitMissingPathDoesNotFallBackToMatchingName()
        {
            var active = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(active.name))
                Assert.Ignore("The active scene needs a name for this lookup-only regression.");

            Assert.IsNull(ManageScene.FindLoadedScene(active.name,
                "Assets/__missing_scene_" + Guid.NewGuid().ToString("N") + ".unity"));
            Assert.AreEqual(active, ManageScene.FindLoadedScene(active.name, null));
        }

        [Test]
        public void BuildSettingsIndexes_IgnoreDisabledEntries()
        {
            var settings = new[]
            {
                new EditorBuildSettingsScene("Assets/DisabledFirst.unity", false),
                new EditorBuildSettingsScene("Assets/EnabledFirst.unity", true),
                new EditorBuildSettingsScene("Assets/DisabledMiddle.unity", false),
                new EditorBuildSettingsScene("Assets/EnabledSecond.unity", true),
            };
            var response = JObject.FromObject(ManageScene.GetBuildSettingsScenes(settings));
            Assert.IsTrue(response.Value<bool>("success"));
            var scenes = (JArray)response["data"];
            CollectionAssert.AreEqual(new[] { -1, 0, -1, 1 }, new[]
            {
                (int)scenes[0]["buildIndex"], (int)scenes[1]["buildIndex"],
                (int)scenes[2]["buildIndex"], (int)scenes[3]["buildIndex"],
            });
        }

        [TestCase("../../../escape.png")]
        [TestCase("/tmp/rooted.png")]
        [TestCase(@"C:\captures\rooted.png")]
        [TestCase(@"..\..\escape")]
        [TestCase("nested/name.jpg")]
        public void PositionedScreenshotFilename_CannotEscapeItsOutputDirectory(string requested)
        {
            string name = ManageScene.BuildPositionedScreenshotFileName(requested);
            Assert.IsFalse(Path.IsPathRooted(name));
            StringAssert.DoesNotContain("/", name);
            StringAssert.DoesNotContain("\\", name);
            StringAssert.EndsWith(".png", name);
            string folder = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mcp-capture-test"));
            Assert.AreEqual(folder, Path.GetDirectoryName(Path.GetFullPath(Path.Combine(folder, name))));
        }

        [TestCase("shot.png", "shot.png")]
        [TestCase("shot", "shot.png")]
        [TestCase("shot.jpg", "shot.jpg.png")]
        public void PositionedScreenshotFilename_PreservesPngNaming(string requested, string expected)
        {
            Assert.AreEqual(expected, ManageScene.BuildPositionedScreenshotFileName(requested));
        }

        private static void WithTemporaryScenes(Action<Scene, Scene> test)
        {
            Scene original = SceneManager.GetActiveScene();
            Scene first = default;
            Scene second = default;
            try
            {
                first = MCPForUnityTests.Editor.Tools.TestSceneFactory.Create();
                second = MCPForUnityTests.Editor.Tools.TestSceneFactory.Create();
                test(first, second);
            }
            finally
            {
                if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
                if (second.IsValid() && second.isLoaded) EditorSceneManager.CloseScene(second, true);
                if (first.IsValid() && first.isLoaded) EditorSceneManager.CloseScene(first, true);
            }
        }
    }
}
