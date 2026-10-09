using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
public static class ReviewValidator
{
    public static void Run()
    {
        var report=new JArray();
        try
        {
            var untitled=SceneManager.GetActiveScene();
            if(untitled.path!="")throw new InvalidOperationException("Requires a fresh untitled editor scene; refuses to replace another scene.");
            var user=new GameObject("IndependentUnsavedUserObject");
            EditorSceneManager.MarkSceneDirty(untitled);
            string folder="Assets/__IndependentValidator";Directory.CreateDirectory(folder);
            string path=folder+"/Probe.unity";
            File.WriteAllText(path,"%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!1660057539 &9223372036854775807\nSceneRoots:\n  m_ObjectHideFlags: 0\n  m_Roots: []\n");AssetDatabase.ImportAsset(path);
            string borrowedPath=folder+"/Borrowed.unity";File.Copy(path,borrowedPath);AssetDatabase.ImportAsset(borrowedPath);
            var borrowed=EditorSceneManager.OpenScene(borrowedPath,OpenSceneMode.Additive);
            SceneManager.SetActiveScene(untitled);
            var type=Type.GetType("AssetStoreTools.Validator.Services.Validation.SceneUtilityService, asset-store-tools-editor",true);
            foreach(bool cancel in new[]{false,true})
            {
                var original=EditorSceneManager.GetSceneManagerSetup();
                var service=Activator.CreateInstance(type,true);Scene scanned=default;
                try
                {
                    using((IDisposable)type.GetMethod("BeginScan").Invoke(service,null))
                    {
                        scanned=(Scene)type.GetMethod("OpenScene").Invoke(service,new object[]{path});
                        if(cancel)throw new OperationCanceledException("Synthetic cancellation");
                    }
                }
                catch(OperationCanceledException){if(!cancel)throw;}
                var after=EditorSceneManager.GetSceneManagerSetup();
                bool same=original.Length==after.Length;
                for(int i=0;same&&i<original.Length;i++)same=original[i].path==after[i].path&&original[i].isActive==after[i].isActive&&original[i].isLoaded==after[i].isLoaded;
                bool success=user!=null&&untitled.isLoaded&&untitled.isDirty&&untitled.path==""&&borrowed.isLoaded&&!scanned.isLoaded&&SceneManager.GetActiveScene()==untitled&&same;
                report.Add(new JObject{["cancel"]=cancel,["untitledPath"]=untitled.path,["dirtyObjectSurvives"]=user!=null,["borrowedSceneSurvives"]=borrowed.isLoaded,["originalSetupPreserved"]=same,["scanSceneClosed"]=!scanned.isLoaded,["success"]=success});
                if(!success)throw new InvalidOperationException("Scene scan did not preserve the live untitled scene");
            }
            File.WriteAllText(Environment.GetEnvironmentVariable("REVIEW_VALIDATOR_OUTPUT"),report.ToString());
            EditorApplication.Exit(0);
        }
        catch(Exception ex){Debug.LogException(ex);File.WriteAllText(Environment.GetEnvironmentVariable("REVIEW_VALIDATOR_OUTPUT"),new JObject{["failure"]=ex.ToString(),["samples"]=report}.ToString());EditorApplication.Exit(1);}
    }
}
