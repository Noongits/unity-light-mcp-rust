using System;
using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Services;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;
namespace MCPForUnityTests.Editor.Services
{
    public class AutoStartGenerationRegressionTests : TransportPreferenceTestBase
    {
        [Test]
        public void RetiredCompletion_CannotEraseReusedPendingMarker()
        {
            bool pending = SessionState.GetBool(HttpAutoStartHandler.ConnectPendingKey,false);
            int previous = SessionState.GetInt(HttpAutoStartHandler.ConnectGenerationKey,0);
            try
            {
                int old = HttpAutoStartHandler.BeginPendingConnect();
                HttpAutoStartHandler.CancelPendingReconnect();
                int current = HttpAutoStartHandler.BeginPendingConnect();
                HttpAutoStartHandler.CompletePendingConnect(old);
                Assert.IsFalse(HttpAutoStartHandler.OwnsPendingConnect(old));
                Assert.IsTrue(HttpAutoStartHandler.OwnsPendingConnect(current));
                HttpAutoStartHandler.CompletePendingConnect(current);
                Assert.IsFalse(SessionState.GetBool(HttpAutoStartHandler.ConnectPendingKey,false));
            }
            finally
            {
                SessionState.SetBool(HttpAutoStartHandler.ConnectPendingKey,pending);
                SessionState.SetInt(HttpAutoStartHandler.ConnectGenerationKey,previous);
            }
        }
        [UnityTest]
        public IEnumerator RetiredPoll_DoesNotProbeWhenAnotherAttemptReusesMarker()
        {
            var previousServer = MCPServiceLocator.Server;
            var fakeType = typeof(ConnectionUiGenerationTests).GetNestedType("WaitingServer",BindingFlags.NonPublic);
            var server = (IServerManagementService)Activator.CreateInstance(fakeType,true);
            bool pending = SessionState.GetBool(HttpAutoStartHandler.ConnectPendingKey,false);
            int previousGeneration = SessionState.GetInt(HttpAutoStartHandler.ConnectGenerationKey,0);
            bool hadAuto = EditorPrefs.HasKey(EditorPrefKeys.AutoStartOnLoad);
            bool auto = EditorPrefs.GetBool(EditorPrefKeys.AutoStartOnLoad,false);
            try
            {
                EditorPrefs.SetBool(EditorPrefKeys.AutoStartOnLoad,true);
                EditorConfigurationCache.Instance.SetUseHttpTransport(true);
                MCPServiceLocator.Register<IServerManagementService>(server);
                int old = HttpAutoStartHandler.BeginPendingConnect();
                var method = typeof(HttpAutoStartHandler).GetMethod("WaitForServerAndConnectCoreAsync",BindingFlags.Static|BindingFlags.NonPublic);
                var poll = (Task)method.Invoke(null,new object[]{old});
                Assert.IsFalse(poll.IsCompleted);
                HttpAutoStartHandler.CancelPendingReconnect();
                int current = HttpAutoStartHandler.BeginPendingConnect();
                var deadline=DateTime.UtcNow.AddSeconds(5);
                while(!poll.IsCompleted && DateTime.UtcNow<deadline)yield return null;
                Assert.IsTrue(poll.IsCompletedSuccessfully,poll.Exception?.ToString());
                Assert.AreEqual(1,fakeType.GetField("Probes").GetValue(server));
                Assert.IsTrue(HttpAutoStartHandler.OwnsPendingConnect(current));
                HttpAutoStartHandler.CompletePendingConnect(old);
                Assert.IsTrue(HttpAutoStartHandler.OwnsPendingConnect(current));
            }
            finally
            {
                HttpAutoStartHandler.CancelPendingReconnect();
                MCPServiceLocator.Register<IServerManagementService>(previousServer);
                if(hadAuto)EditorPrefs.SetBool(EditorPrefKeys.AutoStartOnLoad,auto);else EditorPrefs.DeleteKey(EditorPrefKeys.AutoStartOnLoad);
                SessionState.SetBool(HttpAutoStartHandler.ConnectPendingKey,pending);
                SessionState.SetInt(HttpAutoStartHandler.ConnectGenerationKey,previousGeneration);
            }
        }
    }
}
