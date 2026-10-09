using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace MCPForUnityTests.Editor.Tools
{
    public class ValidatorSceneRestorationRegressionTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void Scan_DisposalPreservesDirtyUntitledAndAdditiveScenes(bool throwDuringScan)
        {
            var type=Type.GetType("AssetStoreTools.Validator.Services.Validation.SceneUtilityService, asset-store-tools-editor");
            if(type==null)Assert.Ignore("Bundled Asset Store Tools is required for validator scene tests.");
            using(var borrowed=new TailAuditSceneScope())
            using(var untitledScope=new TailAuditSceneScope())
            {
                var untitled=SceneManager.GetActiveScene();
                var user=new GameObject("UnsavedUserObject");
                // A fixture scene has a saved base, but keep the object dirty in memory.
                EditorSceneManager.MarkSceneDirty(untitled);
                var original=EditorSceneManager.GetSceneManagerSetup();
                var active=SceneManager.GetActiveScene();
                var scanAsset=TestSceneFactory.Create();
                string path=scanAsset.path;
                EditorSceneManager.CloseScene(scanAsset,true);
                // Own a separate saved target; TestSceneFactory deletes its source on close.
                string target="Assets/Temp/__ValidatorScan_"+Guid.NewGuid().ToString("N")+".unity";
                System.IO.File.WriteAllText(target,"%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!1660057539 &9223372036854775807\nSceneRoots:\n  m_ObjectHideFlags: 0\n  m_Roots: []\n");
                AssetDatabase.ImportAsset(target);
                SceneManager.SetActiveScene(active);
                var service=Activator.CreateInstance(type,true);
                Scene scanned=default;
                try
                {
                    Action run=()=>
                    {
                        using(var scope=(IDisposable)type.GetMethod("BeginScan").Invoke(service,null))
                        {
                            scanned=(Scene)type.GetMethod("OpenScene").Invoke(service,new object[]{target});
                            Assert.IsTrue(user!=null && untitled.isLoaded);
                            if(throwDuringScan)throw new OperationCanceledException("Synthetic scanner cancellation");
                        }
                    };
                    if(throwDuringScan)Assert.Throws<OperationCanceledException>(()=>run());else run();
                    Assert.AreEqual(active,SceneManager.GetActiveScene());
                    Assert.IsTrue(user!=null);
                    Assert.IsTrue(untitled.isDirty);
                    Assert.IsFalse(scanned.isLoaded);
                    var after=EditorSceneManager.GetSceneManagerSetup();
                    Assert.AreEqual(original.Length,after.Length);
                    for(int i=0;i<original.Length;i++){Assert.AreEqual(original[i].path,after[i].path);Assert.AreEqual(original[i].isActive,after[i].isActive);Assert.AreEqual(original[i].isLoaded,after[i].isLoaded);}
                }
                finally{AssetDatabase.DeleteAsset(target);}
            }
        }
    }
}
