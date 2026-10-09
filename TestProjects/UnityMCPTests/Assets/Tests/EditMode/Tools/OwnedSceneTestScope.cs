using System;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnityTests.Editor.Tools
{
    // Keep fixture objects out of borrowed scenes, and never let global name/tag/layer
    // searches silently operate on an object that belongs to the editor user.
    internal sealed class OwnedSceneTestScope : IDisposable
    {
        private readonly Scene previous;
        private readonly UnityEngine.Object[] selection;
        private readonly Scene owned;

        public OwnedSceneTestScope()
        {
            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
                Assert.Ignore("Close the existing prefab stage before running scene-mutating fixtures.");
            previous = SceneManager.GetActiveScene();
            selection = Selection.objects;
            owned = MCPForUnityTests.Editor.Tools.TestSceneFactory.Create();
            SceneManager.SetActiveScene(owned);
        }

        public void Validate(JObject parameters)
        {
            string method = parameters.Value<string>("searchMethod") ?? "by_name";
            JToken target = parameters["target"] ?? parameters["name"];
            Check(target, method);
            Check(parameters["parent"], "by_name");
            if (parameters["action"]?.ToString() == "create") Check(parameters["name"], "by_name");
        }

        private void Check(JToken token, string method)
        {
            if (token == null || token.Type == JTokenType.Null || string.IsNullOrEmpty(token.ToString())) return;
            foreach (int id in GameObjectLookup.SearchGameObjects(method, token.ToString(), true))
            {
                var go = GameObjectLookup.FindById(id);
                if (go != null && go.scene != owned)
                    Assert.Ignore("A borrowed scene contains a matching fixture target; refusing to modify it.");
            }
        }

        public void Dispose()
        {
            if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
            if (owned.IsValid() && owned.isLoaded) EditorSceneManager.CloseScene(owned, true);
            Selection.objects = selection.Where(item => item != null).ToArray();
        }
    }
}
