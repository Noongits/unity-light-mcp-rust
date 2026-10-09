using System;
using System.Collections;
using System.Reflection;
using MCPForUnity.Editor.Tools.Graphics;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
namespace MCPForUnityTests.Editor.Tools
{
    public class RendererFeatureOwnershipRegressionTests
    {
        [Test]
        public void AddAndRemoveFeature_UndoRestoresOwnedSubAsset_AndPreservesUnrelatedDirtyMaterial()
        {
            var rendererType=Type.GetType("UnityEngine.Rendering.Universal.UniversalRendererData, Unity.RenderPipelines.Universal.Runtime");
            var pipelineType=Type.GetType("UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset, Unity.RenderPipelines.Universal.Runtime");
            if(rendererType==null||pipelineType==null)Assert.Ignore("URP required.");
            string folder="Assets/__FeatureOwnership_"+Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets",folder.Substring(7));
            var previous=GraphicsSettings.defaultRenderPipeline;
            var quality=QualitySettings.renderPipeline;
            var pipeline=(RenderPipelineAsset)ScriptableObject.CreateInstance(pipelineType);
            var data=ScriptableObject.CreateInstance(rendererType);
            AssetDatabase.CreateAsset(data,folder+"/Renderer.asset");
            data=AssetDatabase.LoadAssetAtPath<ScriptableObject>(folder+"/Renderer.asset");
            using(var so=new SerializedObject(pipeline))
            {
                var list=so.FindProperty("m_RendererDataList");list.arraySize=1;list.GetArrayElementAtIndex(0).objectReferenceValue=data;so.ApplyModifiedPropertiesWithoutUndo();
            }
            var material=new Material(Shader.Find("Unlit/Color"));
            AssetDatabase.CreateAsset(material,folder+"/Borrowed.mat");
            material=AssetDatabase.LoadAssetAtPath<Material>(folder+"/Borrowed.mat");
            material.color=Color.magenta;EditorUtility.SetDirty(material);
            try
            {
                GraphicsSettings.defaultRenderPipeline=pipeline;QualitySettings.renderPipeline=null;
                var added=JObject.FromObject(ManageGraphics.HandleCommand(new JObject{["action"]="feature_add",["type"]="RenderObjects",["name"]="OwnedFeature"}));
                Assert.IsTrue(added.Value<bool>("success"),added.ToString());
                Assert.IsTrue(EditorUtility.IsDirty(material),"Saving an owned renderer must not persist a borrowed dirty material.");
                var prop=rendererType.GetProperty("rendererFeatures");
                var features=(IList)prop.GetValue(data);
                Assert.AreEqual(1,features.Count);Assert.IsTrue(AssetDatabase.IsSubAsset((UnityEngine.Object)features[0]));
                Undo.IncrementCurrentGroup();
                var removed=JObject.FromObject(ManageGraphics.HandleCommand(new JObject{["action"]="feature_remove",["name"]="OwnedFeature"}));
                Assert.IsTrue(removed.Value<bool>("success"),removed.ToString());
                Undo.FlushUndoRecordObjects();Undo.PerformUndo();
                features=(IList)prop.GetValue(data);
                Assert.AreEqual(1,features.Count);
                Assert.IsTrue((UnityEngine.Object)features[0]!=null,"Undo must restore the referenced native feature, not a missing reference.");
                Assert.AreEqual(folder+"/Renderer.asset",AssetDatabase.GetAssetPath((UnityEngine.Object)features[0]));
                AssetDatabase.SaveAssetIfDirty(data);
                AssetDatabase.ImportAsset(folder+"/Renderer.asset",ImportAssetOptions.ForceSynchronousImport);
                data=AssetDatabase.LoadAssetAtPath<ScriptableObject>(folder+"/Renderer.asset");
                features=(IList)prop.GetValue(data);
                Assert.AreEqual(1,features.Count);
                Assert.IsTrue((UnityEngine.Object)features[0]!=null,"Restored feature must survive a disk save and reimport.");
                Assert.IsTrue(AssetDatabase.IsSubAsset((UnityEngine.Object)features[0]));
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline=previous;QualitySettings.renderPipeline=quality;
                AssetDatabase.DeleteAsset(folder);UnityEngine.Object.DestroyImmediate(pipeline);
            }
        }
    }
}
