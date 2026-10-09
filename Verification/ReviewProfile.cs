using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
public static class ReviewProfile
{
    const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
    static readonly string Output = Environment.GetEnvironmentVariable("REVIEW_PROFILE_OUTPUT");
    static JObject Snapshot()
    {
        var rt=UnityEngine.Resources.FindObjectsOfTypeAll<RenderTexture>();
        var tex=UnityEngine.Resources.FindObjectsOfTypeAll<Texture2D>();
        return new JObject { ["renderTextures"] = rt.Length, ["texture2Ds"] = tex.Length, ["renderTextureNativeBytes"] = rt.Sum(t=>Profiler.GetRuntimeMemorySizeLong(t)), ["texture2DNativeBytes"] = tex.Sum(t=>Profiler.GetRuntimeMemorySizeLong(t)), ["gfxDriverBytes"] = Profiler.GetAllocatedMemoryForGraphicsDriver(), ["panelAssets"] = AssetDatabase.FindAssets("t:PanelSettings").Length, ["renderTextureAssets"] = AssetDatabase.FindAssets("t:RenderTexture").Length };
    }
    static JObject Render(string path,string folder,int width=64)
    {
        return JObject.FromObject(ManageUI.HandleCommand(new JObject { ["action"]="render_ui",["path"]=path,["width"]=width,["height"]=64,["output_folder"]=folder }));
    }
    private static IEnumerator workload;
    public static void Run()
    {
        workload = RunFrames();
        EditorApplication.update += Tick;
    }
    private static void Tick()
    {
        try { if (workload.MoveNext()) return; EditorApplication.update -= Tick; EditorApplication.Exit(0); }
        catch (Exception e) { Debug.LogException(e); EditorApplication.update -= Tick; EditorApplication.Exit(1); }
    }
    private static IEnumerator RunFrames()
    {
        var report=new JObject { ["unity"]=Application.unityVersion,["graphicsDevice"]=SystemInfo.graphicsDeviceType.ToString(),["gpu"]=SystemInfo.graphicsDeviceName,["samples"]=new JArray() };
        string root="Assets/__IndependentProfile";
        if (!Directory.Exists(root)) Directory.CreateDirectory(root);
        string path=root+"/Probe.uxml";
        File.WriteAllText(path,"<ui:UXML xmlns:ui=\"UnityEngine.UIElements\"><ui:VisualElement style=\"width: 64px; height: 64px; background-color: rgb(240, 20, 30);\" /></ui:UXML>");
        AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport);
        string folder=Path.Combine(Path.GetDirectoryName(Application.dataPath), "Captures", Path.GetFileNameWithoutExtension(Output)+"-images");
        Directory.CreateDirectory(folder);
        // Probe live internal API availability, without invoking or changing any global panel.
        report["panelAPI"]=new JArray(typeof(UIDocument).Assembly.GetType("UnityEngine.UIElements.UIElementsRuntimeUtility").GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static).Where(m=>m.Name.Contains("Repaint")||m.Name.Contains("Render")||m.Name.Contains("Update")).Select(m=>m.ToString()));
        foreach(bool existing in new[]{false,true})
        {
            typeof(ManageUI).GetMethod("CleanupRenderTextures",Flags).Invoke(null,null);
            var sample=new JObject { ["existingSettings"]=existing };
            PanelSettings panel=null; RenderTexture user=null;
            if(existing)
            {
                panel=ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(panel,root+"/Panel.asset");
                panel=AssetDatabase.LoadAssetAtPath<PanelSettings>(root+"/Panel.asset");
                user=new RenderTexture(13,17,0);user.name="UserOwnedProfileTarget";user.Create();panel.targetTexture=user;
            }
            sample["beforeCold"]=Snapshot();
            for(int warm=0;warm<10;warm++){Render(path,folder);yield return null;}
            for(int settle=0;settle<20;settle++)yield return null;
            sample["before"]=Snapshot();int success=0,content=0,red=0;
            for(int i=0;i<100;i++)
            {
                var result=Render(path,folder);
                if(result.Value<bool>("success"))success++;
                if(result["data"]?.Value<bool>("hasContent")==true)content++;
                string image=result["data"]?.Value<string>("fullPath");
                if(image!=null && i%25==0)
                {
                    var read=new Texture2D(2,2);try{read.LoadImage(File.ReadAllBytes(image));if(read.GetPixels32().Count(c=>c.r>150&&c.g<80&&c.b<100)>100)red++;}finally{UnityEngine.Object.DestroyImmediate(read);}
                }
                if(i==0)sample["firstResponse"]=result;
                yield return null;
                if(i==49)sample["after50"]=Snapshot();
            }
            for(int settle=0;settle<20;settle++)yield return null;
            sample["after100"]=Snapshot();sample["successes"]=success;sample["contentResponses"]=content;sample["redPixelSamples"]=red;
            sample["resizeResponse"]=Render(path,folder,96);
            string blocked=folder+"/blocked";if(!File.Exists(blocked))File.WriteAllText(blocked,"file");sample["failureResponse"]=Render(path,blocked);
            typeof(ManageUI).GetMethod("CleanupRenderTextures",Flags).Invoke(null,null);
            for(int settle=0;settle<20;settle++)yield return null;
            sample["afterCleanup"]=Snapshot();sample["userTargetSurvives"]=!existing||(panel.targetTexture==user&&user.IsCreated());
            if(panel!=null)AssetDatabase.DeleteAsset(root+"/Panel.asset");if(user!=null){user.Release();UnityEngine.Object.DestroyImmediate(user);}
            ((JArray)report["samples"]).Add(sample);
        }
        AssetDatabase.DeleteAsset(root);
        File.WriteAllText(Output,report.ToString());
    }
}
