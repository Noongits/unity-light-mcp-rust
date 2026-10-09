using System.Collections;
using System.IO;
using System.Reflection;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
namespace MCPForUnityTests.Editor.Tools
{
    public class PlayModeCaptureLifecycleRegressionTests
    {
        [UnityTest]
        public IEnumerator ExitAndReenterWithoutDomainReload_RetiresPendingCapture()
        {
            bool previousEnabled=EditorSettings.enterPlayModeOptionsEnabled;
            var previousOptions=EditorSettings.enterPlayModeOptions;
            const BindingFlags flags=BindingFlags.Static|BindingFlags.NonPublic;
            var started=typeof(ManageUI).GetField("s_pendingCaptureStarted",flags);
            var done=typeof(ManageUI).GetField("s_pendingCaptureDone",flags);
            var texture=typeof(ManageUI).GetField("s_pendingCaptureTex",flags);
            var generation=typeof(ManageUI).GetField("s_captureGeneration",flags);
            EditorSettings.enterPlayModeOptionsEnabled=true;
            EditorSettings.enterPlayModeOptions=EnterPlayModeOptions.DisableDomainReload;
            try
            {
                yield return new EnterPlayMode(false);
                var result=JObject.FromObject(ManageUI.HandleCommand(new JObject{["action"]="render_ui",["target"]="SyntheticPendingCapture",["width"]=64,["height"]=64,["output_folder"]="Temp/IndependentPlayCaptures"}));
                Assert.IsTrue(result.Value<bool>("success"),result.ToString());
                Assert.IsTrue(result["data"].Value<bool>("pending"));
                int old=(int)generation.GetValue(null);
                yield return new ExitPlayMode();
                Assert.IsFalse((bool)started.GetValue(null));Assert.IsFalse((bool)done.GetValue(null));Assert.IsNull(texture.GetValue(null));
                Assert.AreNotEqual(old,generation.GetValue(null));
                yield return new EnterPlayMode(false);
                Assert.IsFalse((bool)started.GetValue(null));Assert.IsFalse((bool)done.GetValue(null));
                var late=new Texture2D(2,2);
                typeof(ManageUI).GetMethod("CompletePlayModeCapture",flags).Invoke(null,new object[]{old,late});
                Assert.IsTrue(late==null,"A callback from the previous play session must release its texture.");
                Assert.IsFalse((bool)done.GetValue(null));
                yield return new ExitPlayMode();
            }
            finally
            {
                EditorSettings.enterPlayModeOptionsEnabled=previousEnabled;
                EditorSettings.enterPlayModeOptions=previousOptions;
            }
        }
    }
}
