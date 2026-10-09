using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using MCPForUnity.Editor.Tools.Animation;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    public class AnimationIntegrationRegressionTests
    {
        string root;
        GameObject target;
        Texture2D unrelated;

        [SetUp]
        public void SetUp()
        {
            root = "Assets/Temp/AnimationIntegration_" + Guid.NewGuid().ToString("N");
            EnsureFolder(root);
            target = new GameObject("AnimationIntegrationTarget");
            unrelated = new Texture2D(2, 2);
            AssetDatabase.CreateAsset(unrelated, root + "/Unrelated.asset");
            unrelated = AssetDatabase.LoadAssetAtPath<Texture2D>(root + "/Unrelated.asset");
            unrelated.SetPixel(0, 0, Color.red);
            EditorUtility.SetDirty(unrelated);
        }

        [TearDown]
        public void TearDown()
        {
            if (target != null) UnityEngine.Object.DestroyImmediate(target);
            AssetDatabase.DeleteAsset(root);
            CleanupEmptyParentFolders(root);
        }

        JObject Call(string action, JObject properties = null)
        {
            var result = ToJObject(ManageAnimation.HandleCommand(new JObject
            {
                ["action"] = action, ["target"] = target.GetInstanceID().ToString(),
                ["search_method"] = "by_id", ["clip_path"] = root + "/Move.anim",
                ["controller_path"] = root + "/Actor.controller",
                ["properties"] = properties
            }));
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsTrue(EditorUtility.IsDirty(unrelated), action + " saved an unrelated dirty asset");
            return result;
        }

        [Test]
        public void AuthoringWorkflow_PersistsClipControllerAndBlendTree_WithoutSavingUnrelatedAssets()
        {
            Call("clip_create", new JObject { ["length"] = 1, ["loop"] = true });
            Call("clip_set_curve", new JObject
            {
                ["property_path"] = "m_LocalPosition.x", ["type"] = "Transform",
                ["keys"] = new JArray(new JArray(0, 0), new JArray(1, 2))
            });
            Call("clip_add_event", new JObject { ["time"] = 0.5, ["function_name"] = "OnStep" });
            Call("controller_create");
            Call("controller_add_parameter", new JObject { ["parameter_name"] = "Speed", ["parameter_type"] = "float" });
            Call("controller_add_state", new JObject { ["state_name"] = "Move", ["is_default"] = true });
            Call("controller_add_state", new JObject { ["state_name"] = "Idle" });
            Call("controller_add_transition", new JObject { ["from_state"] = "Idle", ["to_state"] = "Move" });
            Call("controller_add_layer", new JObject { ["layer_name"] = "Upper", ["weight"] = 0.5 });
            Call("controller_create_blend_tree_1d", new JObject { ["state_name"] = "Locomotion", ["blend_parameter"] = "Speed" });
            Call("controller_add_blend_tree_child", new JObject { ["state_name"] = "Locomotion", ["threshold"] = 1 });
            Call("controller_assign");
            Call("animator_set_parameter", new JObject { ["parameter_name"] = "Speed", ["parameter_type"] = "float", ["value"] = 2 });

            AssetDatabase.ImportAsset(root + "/Move.anim", ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(root + "/Actor.controller", ImportAssetOptions.ForceSynchronousImport);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(root + "/Move.anim");
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(root + "/Actor.controller");
            Assert.AreEqual(controller, AssetDatabase.LoadMainAssetAtPath(root + "/Actor.controller"));
            Assert.IsFalse(clip.legacy);
            Assert.Greater(AnimationUtility.GetCurveBindings(clip).Length, 0);
            Assert.AreEqual("OnStep", AnimationUtility.GetAnimationEvents(clip).Single().functionName);
            Assert.AreEqual(2, controller.layers.Length);
            Assert.AreEqual(2f, controller.parameters.Single(p => p.name == "Speed").defaultFloat);
            var state = controller.layers[0].stateMachine.states.Single(s => s.state.name == "Locomotion").state;
            var tree = (BlendTree)state.motion;
            // Hidden Animator objects can report IsSubAsset=false despite native persistence.
            // Verify the actual file membership and local identity after save/reimport.
            Assert.IsTrue(EditorUtility.IsPersistent(tree));
            Assert.AreEqual(root + "/Actor.controller", AssetDatabase.GetAssetPath(tree));
            CollectionAssert.Contains(AssetDatabase.LoadAllAssetsAtPath(root + "/Actor.controller"), tree);
            Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(tree, out string treeGuid, out long treeId));
            Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(controller, out string ownerGuid, out long ownerId));
            Assert.AreEqual(ownerGuid, treeGuid);
            Assert.AreNotEqual(ownerId, treeId);
            Assert.AreEqual(clip, tree.children.Single().motion);
            Assert.IsTrue(EditorUtility.IsDirty(unrelated));
        }

        [Test]
        public void MalformedPropertiesJson_ReturnsToolError()
        {
            var result = ToJObject(ManageAnimation.HandleCommand(new JObject
            {
                ["action"] = "clip_create", ["properties"] = "{ broken"
            }));
            Assert.IsFalse(result.Value<bool>("success"));
            StringAssert.Contains("Invalid animation properties JSON", result.Value<string>("error") ?? result.Value<string>("message"));
        }

        [TestCase("clip_create")]
        [TestCase("clip_create_preset")]
        [TestCase("controller_create")]
        public void Create_RejectsOccupiedPathOfAnotherAssetType(string action)
        {
            string path = root + (action == "controller_create" ? "/Actor.controller" : "/Move.anim");
            var existing = new TextAsset("preserve me");
            AssetDatabase.CreateAsset(existing, path);
            var result = ToJObject(ManageAnimation.HandleCommand(new JObject
            {
                ["action"] = action, ["clip_path"] = path, ["controller_path"] = path,
                ["properties"] = new JObject { ["preset"] = "bounce" }
            }));
            Assert.IsFalse(result.Value<bool>("success"));
            Assert.AreEqual("preserve me", AssetDatabase.LoadAssetAtPath<TextAsset>(path).text);
        }
    }
}
