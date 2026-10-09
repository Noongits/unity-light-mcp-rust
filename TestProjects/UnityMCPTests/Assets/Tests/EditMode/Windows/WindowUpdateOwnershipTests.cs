using MCPForUnity.Editor.Windows;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Windows
{
    public class WindowUpdateOwnershipTests
    {
        [Test]
        public void CurrentWindowAndPackage_AcceptCompletion()
        {
            Assert.IsTrue(MCPForUnityEditorWindow.IsUpdateCheckCurrent(4, 4, "1.0", "1.0"));
        }

        [Test]
        public void DisableThenRebuild_RejectsOldCompletion()
        {
            Assert.IsFalse(MCPForUnityEditorWindow.IsUpdateCheckCurrent(4, 5, "1.0", "1.0"));
        }

        [Test]
        public void PackageDeployedDuringFetch_RejectsOldComparison()
        {
            Assert.IsFalse(MCPForUnityEditorWindow.IsUpdateCheckCurrent(4, 4, "1.0", "2.0"));
        }

        [Test]
        public void NewRequestCompletesBeforeOld_OnlyNewOwnerCanPublish()
        {
            Assert.IsTrue(MCPForUnityEditorWindow.IsUpdateCheckCurrent(5, 5, "2.0", "2.0"));
            Assert.IsFalse(MCPForUnityEditorWindow.IsUpdateCheckCurrent(4, 5, "1.0", "2.0"));
        }
    }
}
