using System;
using System.Reflection;
using MCPForUnity.Editor.Tools.Cameras;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MCPForUnityTests.Editor.Tools
{
    public enum OwnershipPipelineStage { Body, Aim }
    public abstract class OwnershipPipelineBase : MonoBehaviour
    {
        public abstract OwnershipPipelineStage Stage { get; }
    }
    public class OwnershipBodyA : OwnershipPipelineBase
    {
        public int Setting = 42;
        public override OwnershipPipelineStage Stage => OwnershipPipelineStage.Body;
    }
    public class OwnershipBodyB : OwnershipPipelineBase
    {
        public override OwnershipPipelineStage Stage => OwnershipPipelineStage.Body;
    }
    public class OwnershipAim : OwnershipPipelineBase
    {
        public override OwnershipPipelineStage Stage => OwnershipPipelineStage.Aim;
    }
    public class OwnershipThrowingStage : OwnershipPipelineBase
    {
        public override OwnershipPipelineStage Stage => throw new InvalidOperationException("invalid stage");
    }

    public class CameraPipelineOwnershipTests
    {
        private GameObject go;
        private OwnershipBodyA original;
        [SetUp]
        public void SetUp()
        {
            go = new GameObject("Pipeline ownership test");
            original = go.AddComponent<OwnershipBodyA>();
        }
        [TearDown]
        public void TearDown()
        {
            if (go != null) UnityEngine.Object.DestroyImmediate(go);
        }
        private Component Swap(Type type) => CameraConfigure.SwapPipelineComponent(
            go, "Body", type, typeof(OwnershipPipelineBase), original);

        [Test]
        public void UnrelatedComponent_IsRejectedWithoutRemovingBody()
        {
            Assert.IsNull(Swap(typeof(Transform)));
            Assert.AreSame(original, go.GetComponent<OwnershipBodyA>());
            Assert.AreEqual(42, original.Setting);
        }
        [Test]
        public void WrongStage_IsRolledBackWithoutRemovingBody()
        {
            Assert.IsNull(Swap(typeof(OwnershipAim)));
            Assert.AreSame(original, go.GetComponent<OwnershipBodyA>());
            Assert.IsNull(go.GetComponent<OwnershipAim>());
        }
        [Test]
        public void ThrowingStage_IsRolledBackWithoutRemovingBody()
        {
            Assert.Throws<TargetInvocationException>(() => Swap(typeof(OwnershipThrowingStage)));
            Assert.AreSame(original, go.GetComponent<OwnershipBodyA>());
            Assert.IsNull(go.GetComponent<OwnershipThrowingStage>());
        }
        [Test]
        public void AbstractReplacement_IsRejectedBeforeAddition()
        {
            Assert.IsNull(Swap(typeof(OwnershipPipelineBase)));
            Assert.AreSame(original, go.GetComponent<OwnershipBodyA>());
        }
        [Test]
        public void SameType_ReusesOriginalWithoutRemovingIt()
        {
            Assert.AreSame(original, Swap(typeof(OwnershipBodyA)));
            Assert.AreEqual(1, go.GetComponents<OwnershipPipelineBase>().Length);
        }
        [Test]
        public void SuccessfulReplacement_UndoRestoresOriginalSettings()
        {
            Assert.IsInstanceOf<OwnershipBodyB>(Swap(typeof(OwnershipBodyB)));
            Assert.IsNull(go.GetComponent<OwnershipBodyA>());
            Undo.PerformUndo();
            Assert.IsNull(go.GetComponent<OwnershipBodyB>());
            Assert.AreEqual(42, go.GetComponent<OwnershipBodyA>().Setting);
        }
        [Test]
        public void EnsureBrain_UndoRemovesAddedComponent()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var has = typeof(CameraHelpers).GetField("_hasCinemachine", flags);
            var cameraType = typeof(CameraHelpers).GetField("_cmCameraType", flags);
            var brainType = typeof(CameraHelpers).GetField("_cmBrainType", flags);
            var savedHas = has.GetValue(null);
            var savedCamera = cameraType.GetValue(null);
            var savedBrain = brainType.GetValue(null);
            try
            {
                has.SetValue(null, true);
                cameraType.SetValue(null, typeof(OverrideOwnershipCamera));
                brainType.SetValue(null, typeof(OverrideOwnershipBrain));
                go.AddComponent<Camera>();
                Undo.IncrementCurrentGroup();
                var result = JObject.FromObject(CameraCreate.EnsureBrain(new JObject
                {
                    ["properties"] = new JObject { ["camera"] = go.GetInstanceID().ToString() }
                }));
                Assert.IsTrue(result.Value<bool>("success"));
                Assert.IsNotNull(go.GetComponent<OverrideOwnershipBrain>());
                Undo.PerformUndo();
                Assert.IsNull(go.GetComponent<OverrideOwnershipBrain>());
            }
            finally
            {
                has.SetValue(null, savedHas);
                cameraType.SetValue(null, savedCamera);
                brainType.SetValue(null, savedBrain);
            }
        }
    }
}
