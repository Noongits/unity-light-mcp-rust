using System;
using System.Collections.Generic;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools.Prefabs;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MCPForUnityTests.Editor.Tools
{
    // Unity Test Framework runs these editor fixtures sequentially; its NUnit fork has no NonParallelizable attribute.
    public class ManagePrefabsOwnershipRegressionTests
    {
        private OwnedSceneTestScope sceneScope;
        private string folder;
        private readonly List<Material> runtimeMaterials = new();

        [SetUp]
        public void SetUp()
        {
            sceneScope = new OwnedSceneTestScope();
            folder = "Assets/McpPrefabOwnership_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            AssetDatabase.CreateFolder(folder, "Materials");
        }

        [TearDown]
        public void TearDown()
        {
            sceneScope?.Dispose();
            sceneScope = null;
            foreach (Material material in runtimeMaterials)
                if (material != null) Object.DestroyImmediate(material);
            runtimeMaterials.Clear();
            if (!string.IsNullOrEmpty(folder) && AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
        }

        private Material Material(Color color)
        {
            Shader shader = Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            if (shader == null) Assert.Ignore("The fixture requires a shader with a _Color property.");
            var material = new Material(shader);
            material.SetColor("_Color", color);
            runtimeMaterials.Add(material);
            return material;
        }

        private static Renderer Renderer(GameObject root, string name, params Material[] materials)
        {
            var child = new GameObject(name);
            child.transform.SetParent(root.transform);
            var renderer = child.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
            return renderer;
        }

        private void Persist(GameObject root)
        {
            typeof(ManagePrefabs).GetMethod("PersistRuntimeMaterials", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { root, folder + "/Root.prefab" });
        }

        [Test]
        public void Persistence_DuplicateNames_DoNotOverwriteExistingOrEachOther()
        {
            var borrowed = Material(Color.green);
            runtimeMaterials.Remove(borrowed);
            string borrowedPath = folder + "/Materials/Same_mat.mat";
            AssetDatabase.CreateAsset(borrowed, borrowedPath);
            var root = new GameObject("Root");
            var first = Renderer(root, "Same", Material(Color.red));
            var second = Renderer(root, "Same", Material(Color.blue));

            Persist(root);

            Assert.AreEqual(Color.green, borrowed.GetColor("_Color"));
            Assert.AreNotSame(borrowed, first.sharedMaterial);
            Assert.AreNotSame(first.sharedMaterial, second.sharedMaterial);
            Assert.AreEqual(Color.red, first.sharedMaterial.GetColor("_Color"));
            Assert.AreEqual(Color.blue, second.sharedMaterial.GetColor("_Color"));
            Assert.AreEqual(borrowedPath, AssetDatabase.GetAssetPath(borrowed));
        }

        [Test]
        public void Persistence_PreservesUntouchedBlocks_AndDoesNotSaveUnrelatedDirtyAssets()
        {
            var persistent = Material(Color.green);
            runtimeMaterials.Remove(persistent);
            AssetDatabase.CreateAsset(persistent, folder + "/Borrowed.mat");
            persistent = AssetDatabase.LoadAssetAtPath<Material>(folder + "/Borrowed.mat");
            persistent.SetColor("_Color", Color.yellow);
            EditorUtility.SetDirty(persistent);
            var root = new GameObject("Root");
            var renderer = Renderer(root, "TwoSlots", Material(Color.red), persistent);
            var block = new MaterialPropertyBlock();
            block.SetFloat("_Unrelated", 7f);
            renderer.SetPropertyBlock(block, 1);
            var convertedBlock = new MaterialPropertyBlock();
            convertedBlock.SetColor("_Color", Color.blue);
            convertedBlock.SetFloat("_Unrelated", 9f);
            renderer.SetPropertyBlock(convertedBlock, 0);

            Persist(root);

            renderer.GetPropertyBlock(block, 1);
            Assert.AreEqual(7f, block.GetFloat("_Unrelated"));
            renderer.GetPropertyBlock(block, 0);
            Assert.AreEqual(9f, block.GetFloat("_Unrelated"));
            Assert.AreEqual(Color.blue, renderer.sharedMaterials[0].GetColor("_Color"));
            Assert.AreSame(persistent, renderer.sharedMaterials[1]);
            Assert.IsTrue(EditorUtility.IsDirty(persistent), "Only newly persisted materials should be saved.");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ModifyContents_DetachChild_IsRejectedWithoutLosingOwnership(bool jsonNull)
        {
            var root = new GameObject("Root");
            var child = new GameObject("Child");
            child.transform.SetParent(root.transform);
            var parameters = new JObject { ["parent"] = jsonNull ? JValue.CreateNull() : new JValue("") };
            var method = typeof(ManagePrefabs).GetMethod("ApplyModificationsToPrefabObject", BindingFlags.NonPublic | BindingFlags.Static);
            var result = ((bool modified, ErrorResponse error))method.Invoke(null, new object[] { child, parameters, root, folder + "/Root.prefab" });
            Assert.IsNotNull(result.error);
            Assert.AreSame(root.transform, child.transform.parent);
            Assert.AreEqual(1, root.transform.childCount);
        }

        [Test]
        public void ModifyContents_ReparentWithinRoot_RemainsAllowed()
        {
            var root = new GameObject("Root");
            var parent = new GameObject("Parent");
            parent.transform.SetParent(root.transform);
            var child = new GameObject("Child");
            child.transform.SetParent(root.transform);
            var method = typeof(ManagePrefabs).GetMethod("ApplyModificationsToPrefabObject", BindingFlags.NonPublic | BindingFlags.Static);
            var result = ((bool modified, ErrorResponse error))method.Invoke(null, new object[] { child, new JObject { ["parent"] = "Parent" }, root, folder + "/Root.prefab" });
            Assert.IsNull(result.error);
            Assert.IsTrue(result.modified);
            Assert.AreSame(parent.transform, child.transform.parent);
        }
    }
}
