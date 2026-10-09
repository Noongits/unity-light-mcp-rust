using System;
using System.Collections.Generic;
using System.IO;
using MCPForUnity.Editor.Setup;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    public class SkillSyncSnapshotSafetyTests
    {
        [Test]
        public void CaseAliasedRemoteFiles_FailBeforePlanCanMutateDisk()
        {
            var remote = new Dictionary<string, string> { ["A.md"] = "a", ["a.md"] = "b" };
            Assert.Throws<InvalidOperationException>(() => SkillSyncService.BuildPlan(
                remote, new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase));
        }

        [TestCase(".unity-mcp-skill-sync")]
        [TestCase("../outside")]
        public void UnsafeRemoteFiles_FailBeforePlanCanMutateDisk(string path)
        {
            var remote = new Dictionary<string, string> { [path] = "a" };
            Assert.Throws<InvalidOperationException>(() => SkillSyncService.BuildPlan(
                remote, new Dictionary<string, string>(), StringComparer.Ordinal));
        }

        [Test]
        public void RemoteFileAndChildCollision_FailsBeforePlanCanMutateDisk()
        {
            var remote = new Dictionary<string, string> { ["folder"] = "a", ["folder/file"] = "b" };
            Assert.Throws<InvalidOperationException>(() => SkillSyncService.BuildPlan(
                remote, new Dictionary<string, string>(), StringComparer.Ordinal));
        }

        [TestCase("folder", "folder/child")]
        [TestCase("folder/child", "folder")]
        public void FileDirectoryMigration_FailsBeforeAnyDiskWrite(string localPath, string remotePath)
        {
            var remote = new Dictionary<string, string> { [remotePath] = "a" };
            var local = new Dictionary<string, string> { [localPath] = "never-read" };
            Assert.Throws<InvalidOperationException>(() => SkillSyncService.BuildPlan(remote, local, StringComparer.Ordinal));
        }

        [Test]
        public void DistinctCaseSensitiveRemotePaths_RemainDistinct()
        {
            var remote = new Dictionary<string, string> { ["A.md"] = "a", ["a.md"] = "b" };
            var plan = SkillSyncService.BuildPlan(remote, new Dictionary<string, string>(), StringComparer.Ordinal);
            Assert.AreEqual(2, plan.Added.Count);
        }

        [Test]
        public void LinkedAncestor_IsRejectedWithoutTouchingFilesystem()
        {
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "skill-fake-root"));
            string target = Path.Combine(root, "scripts", "tool.txt");
            Assert.Throws<InvalidOperationException>(() => SkillSyncService.EnsureNoReparsePoints(
                target, path => path == root ? FileAttributes.ReparsePoint : FileAttributes.Directory));
        }

        [Test]
        public void UninspectableAncestor_FailsClosed()
        {
            Assert.Throws<UnauthorizedAccessException>(() => SkillSyncService.EnsureNoReparsePoints(
                Path.GetTempPath(), path => throw new UnauthorizedAccessException()));
        }

        [Test]
        public void LegacyRootSharingOnlyTopLevelNames_IsNotAdopted()
        {
            Assert.IsFalse(SkillSyncService.CanAdoptLegacyManagedRoot(
                new[] { "SKILL.md", "scripts/my-personal-tool.py" },
                new[] { "SKILL.md", "scripts/remote-tool.py" }, StringComparer.Ordinal));
        }

        [Test]
        public void LegacyRootWithOnlyRemotePaths_CanBeAdopted()
        {
            Assert.IsTrue(SkillSyncService.CanAdoptLegacyManagedRoot(
                new[] { "SKILL.md" }, new[] { "SKILL.md", "scripts/tool.py" }, StringComparer.Ordinal));
        }

        [Test]
        public void TraversalOutsideRoot_IsRejected()
        {
            Assert.Throws<InvalidOperationException>(() => SkillSyncService.ResolvePathUnderRoot(
                Path.GetTempPath(), "../outside", StringComparison.Ordinal));
        }
    }
}
