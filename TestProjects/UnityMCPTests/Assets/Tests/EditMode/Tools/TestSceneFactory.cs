using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
namespace MCPForUnityTests.Editor.Tools
{
    // Opening a saved empty fixture additively preserves untitled/dirty borrowed scenes.
    // NewScene(Additive) refuses that editor state, including nested test scopes.
    internal static class TestSceneFactory
    {
        private static readonly Dictionary<int,string> Owned = new Dictionary<int,string>();
        static TestSceneFactory() { EditorSceneManager.sceneClosed += OnClosed; }
        internal static Scene Create()
        {
            const string directory = "Assets/Temp";
            Directory.CreateDirectory(directory);
            string path = directory + "/__OwnedScene_" + Guid.NewGuid().ToString("N") + ".unity";
            try
            {
                File.WriteAllText(path, "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!1660057539 &9223372036854775807\nSceneRoots:\n  m_ObjectHideFlags: 0\n  m_Roots: []\n");
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                Owned.Add(scene.handle,path);
                return scene;
            }
            catch { AssetDatabase.DeleteAsset(path); throw; }
        }
        private static void OnClosed(Scene scene)
        {
            if (!Owned.TryGetValue(scene.handle,out string path)) return;
            Owned.Remove(scene.handle);
            AssetDatabase.DeleteAsset(path);
        }
    }
}
