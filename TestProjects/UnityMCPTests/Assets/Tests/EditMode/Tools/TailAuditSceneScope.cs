using System;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace MCPForUnityTests.Editor.Tools
{
    // Own only the additive scene created by this fixture; never close a borrowed stage/scene.
    internal sealed class TailAuditSceneScope : IDisposable
    {
        private readonly Scene previousScene;
        private readonly Scene ownedScene;

        internal TailAuditSceneScope()
        {
            if (StageUtility.GetCurrentStageHandle() != StageUtility.GetMainStageHandle())
                Assert.Ignore("Run these tests from the main stage; an existing stage must remain untouched.");
            previousScene = SceneManager.GetActiveScene();
            ownedScene = MCPForUnityTests.Editor.Tools.TestSceneFactory.Create();
            SceneManager.SetActiveScene(ownedScene);
        }

        internal void RequireExclusivePhysicsScene()
        {
            foreach (var body in UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Rigidbody>())
                if (body.gameObject.scene.IsValid() && body.gameObject.scene.isLoaded && body.gameObject.scene != ownedScene)
                    Assert.Ignore("Default physics simulation would move a Rigidbody in a borrowed scene.");
            foreach (var body in UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.ArticulationBody>())
                if (body.gameObject.scene.IsValid() && body.gameObject.scene.isLoaded && body.gameObject.scene != ownedScene)
                    Assert.Ignore("Default physics simulation would move an ArticulationBody in a borrowed scene.");
        }

        public void Dispose()
        {
            if (previousScene.IsValid() && previousScene.isLoaded)
                SceneManager.SetActiveScene(previousScene);
            if (ownedScene.IsValid() && ownedScene.isLoaded)
                EditorSceneManager.CloseScene(ownedScene, true);
        }
    }
}
