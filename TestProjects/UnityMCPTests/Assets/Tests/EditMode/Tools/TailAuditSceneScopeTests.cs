using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnityTests.Editor.Tools
{
    public class TailAuditSceneScopeTests
    {
        [Test]
        public void Dispose_RestoresBorrowedSceneAndPreservesItsObjects()
        {
            using (var outer = new TailAuditSceneScope())
            {
                var borrowedScene = SceneManager.GetActiveScene();
                var borrowedObject = new GameObject("PhysTest_BorrowedObject");
                Scene ownedScene;
                using (var inner = new TailAuditSceneScope())
                {
                    ownedScene = SceneManager.GetActiveScene();
                    new GameObject("OwnedObject");
                }
                Assert.AreEqual(borrowedScene, SceneManager.GetActiveScene());
                Assert.IsTrue(borrowedObject != null);
                Assert.IsFalse(ownedScene.isLoaded);
            }
        }

        [Test]
        public void Dispose_AfterAssertionFailure_StillPreservesBorrowedScene()
        {
            using (var outer = new TailAuditSceneScope())
            {
                var borrowedScene = SceneManager.GetActiveScene();
                Assert.Throws<AssertionException>(() =>
                {
                    using (var inner = new TailAuditSceneScope())
                    {
                        new GameObject("OwnedBeforeFailure");
                        Assert.Fail("Simulated failure before normal cleanup");
                    }
                });
                Assert.AreEqual(borrowedScene, SceneManager.GetActiveScene());
                Assert.IsTrue(borrowedScene.isLoaded);
            }
        }

        [Test]
        public void Dispose_Twice_DoesNotCloseBorrowedScene()
        {
            using (var outer = new TailAuditSceneScope())
            {
                var borrowedScene = SceneManager.GetActiveScene();
                var inner = new TailAuditSceneScope();
                inner.Dispose();
                inner.Dispose();
                Assert.IsTrue(borrowedScene.isLoaded);
                Assert.AreEqual(borrowedScene, SceneManager.GetActiveScene());
            }
        }

        [Test]
        public void PhysicsGuard_WithBorrowedBody_RejectsSimulation()
        {
            using (var outer = new TailAuditSceneScope())
            {
                var borrowedObject = new GameObject("BorrowedBody");
                borrowedObject.AddComponent<Rigidbody>();
                using (var inner = new TailAuditSceneScope())
                    Assert.Throws<IgnoreException>(() => inner.RequireExclusivePhysicsScene());
                Assert.IsTrue(borrowedObject != null);
            }
        }
    }
}
