using System;
using System.Collections.Generic;
using System.IO;
using MCPForUnity.Editor.Services;
using NUnit.Framework;
using UnityEditor.PackageManager;

namespace MCPForUnityTests.Editor.Services
{
    public class PackageRecoveryOrderingTests
    {
        [Test]
        public void ReplacementFailure_LeavesCompletedBackupPublished()
        {
            var events = new List<string>();
            string recovery = null;
            Assert.Throws<IOException>(() => PackageDeploymentService.BackupThenReplace("source", "target",
                target => { events.Add("backup"); Assert.AreEqual("target", target); return "new-backup"; },
                (backup, target, source) =>
                {
                    events.Add("publish");
                    recovery = backup;
                    Assert.AreEqual("target", target);
                    Assert.AreEqual("source", source);
                },
                (source, target) =>
                {
                    events.Add("replace");
                    Assert.AreEqual("new-backup", recovery);
                    throw new IOException("copy failed after deletion");
                }));
            CollectionAssert.AreEqual(new[] { "backup", "publish", "replace" }, events);
            Assert.AreEqual("new-backup", recovery);
        }

        [Test]
        public void BackupFailure_DoesNotPublishOrReplace()
        {
            Assert.Throws<IOException>(() => PackageDeploymentService.BackupThenReplace("source", "target",
                _ => throw new IOException("backup failed"),
                (_, __, ___) => Assert.Fail("incomplete backup must not be published"),
                (_, __) => Assert.Fail("target must remain intact")));
        }

        [Test]
        public void PublicationFailure_DoesNotReplace()
        {
            Assert.Throws<IOException>(() => PackageDeploymentService.BackupThenReplace("source", "target",
                _ => "backup", (_, __, ___) => throw new IOException("publication failed"),
                (_, __) => Assert.Fail("target must remain intact")));
        }

        [Test]
        public void BackupsInSameSecond_HaveDistinctNames()
        {
            var timestamp = new DateTime(2026, 10, 9, 0, 0, 0);
            Assert.AreNotEqual(PackageDeploymentService.CreateBackupFolderName(timestamp),
                PackageDeploymentService.CreateBackupFolderName(timestamp));
        }

        [Test]
        public void DeploymentPaths_RejectNestedRootsButAllowSiblings()
        {
            string root = Path.Combine(Path.GetTempPath(), "deployment-path-test");
            Assert.IsTrue(PackageDeploymentService.PathsOverlap(root, root));
            Assert.IsTrue(PackageDeploymentService.PathsOverlap(root, Path.Combine(root, "Editor", "nested")));
            Assert.IsTrue(PackageDeploymentService.PathsOverlap(Path.Combine(root, "Editor", "nested"), root));
            Assert.IsFalse(PackageDeploymentService.PathsOverlap(root, root + "-other"));
        }

        [TestCase("add", "com.example.test@2.0.0", "1.0.0", PackageSource.Registry, false)]
        [TestCase("add", "com.example.test@2.0.0", "2.0.0", PackageSource.Registry, true)]
        [TestCase("add", "com.example.test", "1.0.0", PackageSource.Registry, true)]
        [TestCase("embed", "com.example.test", "1.0.0", PackageSource.Registry, false)]
        [TestCase("embed", "com.example.test", "1.0.0", PackageSource.Embedded, true)]
        public void Recovery_RequiresRequestedVersionAndEmbeddedSource(string operation, string identifier,
            string version, PackageSource source, bool expected)
        {
            Assert.AreEqual(expected, PackageJobManager.MatchesRecoveryTarget(operation, identifier, version, source));
        }

        [TestCase("git@github.com:team/package.git")]
        [TestCase("ssh://git@example.com/team/package.git#revision")]
        [TestCase("file:/tmp/package@local")]
        public void SourceIdentifier_DoesNotLoseUserInfoOrPathAtSign(string identifier)
            => Assert.AreEqual(identifier, PackageJobManager.ExtractPackageName(identifier));
    }
}
