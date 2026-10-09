using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AssetStoreTools.Validator.Services.Validation
{
    internal class SceneUtilityService : ISceneUtilityService
    {
        private ScanScope _scan;

        public System.IDisposable BeginScan()
        {
            if (_scan != null) throw new System.InvalidOperationException("A scene scan is already active.");
            return _scan = new ScanScope(this);
        }

        // Keep original scenes loaded in memory. Restoring paths would lose unsaved objects,
        // dirty edits, additive scene order and original scene handles.
        private sealed class ScanScope : System.IDisposable
        {
            private readonly SceneUtilityService _owner;
            private readonly Scene _originalActive;
            private readonly System.Collections.Generic.List<Scene> _opened = new System.Collections.Generic.List<Scene>();
            private bool _disposed;
            internal ScanScope(SceneUtilityService owner)
            {
                _owner = owner;
                _originalActive = SceneManager.GetActiveScene();
            }
            internal Scene Open(string path)
            {
                if (string.IsNullOrEmpty(path) || AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                    throw new System.ArgumentException("Cannot scan a missing scene: " + path);
                var scene = SceneManager.GetSceneByPath(path);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                    _opened.Add(scene);
                }
                SceneManager.SetActiveScene(scene);
                return scene;
            }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                try
                {
                    if (_originalActive.IsValid() && _originalActive.isLoaded)
                        SceneManager.SetActiveScene(_originalActive);
                    for (int i = _opened.Count - 1; i >= 0; --i)
                        if (_opened[i].IsValid() && _opened[i].isLoaded)
                            EditorSceneManager.CloseScene(_opened[i], true);
                }
                finally { _owner._scan = null; }
            }
        }

        public string CurrentScenePath => SceneManager.GetActiveScene().path;

        public Scene OpenScene(string scenePath)
        {
            if (_scan != null) return _scan.Open(scenePath);
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                throw new System.OperationCanceledException("Scene change was cancelled.");
            if (string.IsNullOrEmpty(scenePath) || AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) == null)
                return EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects);
            else
                return EditorSceneManager.OpenScene(scenePath);
        }

        public GameObject[] GetRootGameObjects()
        {
            return SceneManager.GetActiveScene().GetRootGameObjects();
        }
    }
}