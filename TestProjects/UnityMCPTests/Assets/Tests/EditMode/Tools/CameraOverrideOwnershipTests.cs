using System;
using System.Collections.Generic;
using System.Reflection;
using MCPForUnity.Editor.Tools.Cameras;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace MCPForUnityTests.Editor.Tools
{
    public class OverrideOwnershipBrain : MonoBehaviour
    {
        public readonly List<int> Released = new List<int>();
        public bool FailRelease;
        private int nextId;
        public int SetCameraOverride(int id, int priority, Component a, Component b, float weight, float delta)
            => ++nextId;
        public void ReleaseCameraOverride(int id)
        {
            if (FailRelease) throw new InvalidOperationException("release failed");
            Released.Add(id);
        }
    }

    public class OverrideOwnershipCamera : MonoBehaviour
    {
        public int Priority { get; set; } = 17;
    }

    [Serializable]
    public struct OverrideOwnershipPriority
    {
        public bool Enabled;
        public int m_Value;
    }

    public class OverrideOwnershipStructCamera : MonoBehaviour
    {
        public OverrideOwnershipPriority Priority;
    }

    // Unity Test Framework runs these editor fixtures sequentially; its NUnit fork has no NonParallelizable attribute.
    public class CameraOverrideOwnershipTests
    {
        private readonly List<GameObject> objects = new List<GameObject>();
        private OwnedSceneTestScope sceneScope;
        private bool ownsOverride;

        [SetUp]
        public void SetUp()
        {
            ownsOverride = false;
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            if ((int)typeof(CameraControl).GetField("_overrideId", flags).GetValue(null) != -1
                || typeof(CameraControl).GetField("_overrideBrain", flags).GetValue(null) != null
                || typeof(CameraControl).GetField("_restorePriority", flags).GetValue(null) != null)
                Assert.Ignore("An existing camera override must be preserved.");
            sceneScope = new OwnedSceneTestScope();
            ownsOverride = true;
        }
        private T Create<T>() where T : Component
        {
            var go = new GameObject(typeof(T).Name);
            objects.Add(go);
            return go.AddComponent<T>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in objects)
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            objects.Clear();
            if (ownsOverride) CameraControl.ReleaseOverride(new JObject());
            ownsOverride = false;
            sceneScope?.Dispose();
            sceneScope = null;
        }

        [Test]
        public void Release_UsesRecordedOwner_WithoutSearchingScene()
        {
            var owner = Create<OverrideOwnershipBrain>();
            var unrelated = Create<OverrideOwnershipBrain>();
            CameraControl.ForceCamera(owner, Create<OverrideOwnershipCamera>());
            CameraControl.ReleaseOverride(new JObject());
            CollectionAssert.AreEqual(new[] { 1 }, owner.Released);
            Assert.IsEmpty(unrelated.Released);
        }

        [Test]
        public void ForceAnotherBrain_ReleasesPreviousOwnerFirst()
        {
            var a = Create<OverrideOwnershipBrain>();
            var b = Create<OverrideOwnershipBrain>();
            var camera = Create<OverrideOwnershipCamera>();
            CameraControl.ForceCamera(a, camera);
            CameraControl.ForceCamera(b, camera);
            CollectionAssert.AreEqual(new[] { 1 }, a.Released);
            Assert.IsEmpty(b.Released);
            CameraControl.ReleaseOverride(new JObject());
            CollectionAssert.AreEqual(new[] { 1 }, b.Released);
        }

        [Test]
        public void RepeatedForceAndRelease_DoesNotAccumulateOverrides()
        {
            var brain = Create<OverrideOwnershipBrain>();
            var camera = Create<OverrideOwnershipCamera>();
            CameraControl.ForceCamera(brain, camera);
            CameraControl.ForceCamera(brain, camera);
            CameraControl.ReleaseOverride(new JObject());
            CameraControl.ReleaseOverride(new JObject());
            CollectionAssert.AreEqual(new[] { 1, 2 }, brain.Released);
        }

        [Test]
        public void DestroyedOwner_ReleaseClearsStateWithoutTouchingAnotherBrain()
        {
            var owner = Create<OverrideOwnershipBrain>();
            var unrelated = Create<OverrideOwnershipBrain>();
            CameraControl.ForceCamera(owner, Create<OverrideOwnershipCamera>());
            UnityEngine.Object.DestroyImmediate(owner.gameObject);
            Assert.DoesNotThrow(() => CameraControl.ReleaseOverride(new JObject()));
            Assert.IsEmpty(unrelated.Released);
        }

        [Test]
        public void FailedRelease_RetainsOwnerForRetry()
        {
            var brain = Create<OverrideOwnershipBrain>();
            CameraControl.ForceCamera(brain, Create<OverrideOwnershipCamera>());
            brain.FailRelease = true;
            Assert.Throws<TargetInvocationException>(() => CameraControl.ReleaseOverride(new JObject()));
            brain.FailRelease = false;
            CameraControl.ReleaseOverride(new JObject());
            CollectionAssert.AreEqual(new[] { 1 }, brain.Released);
        }

        [Test]
        public void PriorityFallback_ReleaseRestoresOriginalPriority()
        {
            var camera = Create<OverrideOwnershipCamera>();
            CameraControl.ForceCamera(camera.transform, camera);
            Assert.AreEqual(999, camera.Priority);
            CameraControl.ForceCamera(camera.transform, camera);
            CameraControl.ReleaseOverride(new JObject());
            Assert.AreEqual(17, camera.Priority);
        }

        [Test]
        public void PriorityFallback_RestoresDisabledStructAndValue()
        {
            var camera = Create<OverrideOwnershipStructCamera>();
            camera.Priority = new OverrideOwnershipPriority { Enabled = false, m_Value = 23 };
            CameraControl.ForceCamera(camera.transform, camera);
            Assert.IsTrue(camera.Priority.Enabled);
            Assert.AreEqual(999, camera.Priority.m_Value);
            CameraControl.ReleaseOverride(new JObject());
            Assert.IsFalse(camera.Priority.Enabled);
            Assert.AreEqual(23, camera.Priority.m_Value);
        }

        [Test]
        public void UnsupportedPriority_ReturnsErrorInsteadOfClaimingSuccess()
        {
            var camera = Create<Camera>();
            var result = JObject.FromObject(CameraControl.ForceCamera(camera.transform, camera));
            Assert.IsFalse(result.Value<bool>("success"));
        }
    }
}
