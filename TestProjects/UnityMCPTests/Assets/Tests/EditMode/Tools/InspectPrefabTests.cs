using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TestNamespace;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using MCPForUnity.Editor.Tools.Prefabs;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    /// <summary>
    /// inspect_prefab against small fixtures built once per run:
    ///   Seat.prefab     Seat [InspectPrefabFixture]
    ///   Car.prefab      Car [InspectPrefabFixture value=42, target=Body, onClick: 2 listeners, onValue: 1 dynamic]
    ///                     Body [InspectPrefabFixture] / Seat (nested Seat.prefab)
    ///                     Wheels / Wheel_FL, Wheel_FR, Wheel_RL, Wheel_RR [BoxCollider]
    ///                     Parts (inactive) / Part0..Part29, Part{i} with i empty children (474 objs total)
    ///   Car_Variant     variant of Car: value=7, Body moved to (0, 1, 0) and gets a Rigidbody
    ///   Holder.prefab   Holder / Seat (nested Seat.prefab only)
    ///   Broken.prefab   Broken [InspectPrefabFixture mat=deleted material] / Ghost [missing script], Smoke [ParticleSystem]
    /// </summary>
    // Unity Test Framework runs these editor fixtures sequentially; its NUnit fork has no NonParallelizable attribute.
    public class InspectPrefabTests
    {
        private static readonly string Dir = "Assets/InspectPrefabTests_" + System.Guid.NewGuid().ToString("N");
        private static readonly string SeatPath = Dir + "/Seat.prefab";
        private static readonly string CarPath = Dir + "/Car.prefab";
        private static readonly string VariantPath = Dir + "/Car_Variant.prefab";
        private static readonly string HolderPath = Dir + "/Holder.prefab";
        private static readonly string BrokenPath = Dir + "/Broken.prefab";
        private static readonly string MaterialPath = Dir + "/Doomed.mat";

        private OwnedSceneTestScope sceneScope;

        [OneTimeSetUp]
        public void BuildFixtures()
        {
            sceneScope = new OwnedSceneTestScope();
            EnsureFolder(Dir);

            var seat = new GameObject("Seat");
            seat.AddComponent<InspectPrefabFixture>();
            PrefabUtility.SaveAsPrefabAsset(seat, SeatPath);
            Object.DestroyImmediate(seat);
            var seatAsset = AssetDatabase.LoadAssetAtPath<GameObject>(SeatPath);

            var car = new GameObject("Car");
            var carFixture = car.AddComponent<InspectPrefabFixture>();
            carFixture.value = 42;
            var body = new GameObject("Body");
            body.transform.SetParent(car.transform);
            var bodyFixture = body.AddComponent<InspectPrefabFixture>();
            carFixture.target = body;
            UnityEventTools.AddPersistentListener(carFixture.onClick, carFixture.Ping);
            UnityEventTools.AddIntPersistentListener(carFixture.onClick, bodyFixture.SetValue, 3);
            UnityEventTools.AddPersistentListener(carFixture.onValue, bodyFixture.SetValue);
            var nestedSeat = (GameObject)PrefabUtility.InstantiatePrefab(seatAsset);
            nestedSeat.transform.SetParent(body.transform);
            var wheels = new GameObject("Wheels");
            wheels.transform.SetParent(car.transform);
            foreach (string name in new[] { "Wheel_FL", "Wheel_FR", "Wheel_RL", "Wheel_RR" })
            {
                var wheel = new GameObject(name);
                wheel.transform.SetParent(wheels.transform);
                wheel.AddComponent<BoxCollider>();
            }
            var parts = new GameObject("Parts");
            parts.transform.SetParent(car.transform);
            for (int i = 0; i < 30; i++)
            {
                var part = new GameObject("Part" + i);
                part.transform.SetParent(parts.transform);
                for (int j = 0; j < i; j++) new GameObject("Leaf" + j).transform.SetParent(part.transform);
            }
            parts.SetActive(false);
            PrefabUtility.SaveAsPrefabAsset(car, CarPath);
            Object.DestroyImmediate(car);

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(CarPath));
            var instanceFixture = instance.GetComponent<InspectPrefabFixture>();
            instanceFixture.value = 7;
            PrefabUtility.RecordPrefabInstancePropertyModifications(instanceFixture);
            Transform variantBody = instance.transform.Find("Body");
            variantBody.localPosition = new Vector3(0f, 1f, 0f);
            PrefabUtility.RecordPrefabInstancePropertyModifications(variantBody);
            variantBody.gameObject.AddComponent<Rigidbody>();
            PrefabUtility.SaveAsPrefabAsset(instance, VariantPath);
            Object.DestroyImmediate(instance);

            var holder = new GameObject("Holder");
            ((GameObject)PrefabUtility.InstantiatePrefab(seatAsset)).transform.SetParent(holder.transform);
            PrefabUtility.SaveAsPrefabAsset(holder, HolderPath);
            Object.DestroyImmediate(holder);

            var material = new Material(Shader.Find("Unlit/Color") ?? Shader.Find("Standard"));
            AssetDatabase.CreateAsset(material, MaterialPath);
            var broken = new GameObject("Broken");
            broken.AddComponent<InspectPrefabFixture>().mat = material;
            var ghost = new GameObject("Ghost");
            ghost.transform.SetParent(broken.transform);
            ghost.AddComponent<CustomComponent>();
            var smoke = new GameObject("Smoke");
            smoke.transform.SetParent(broken.transform);
            smoke.AddComponent<ParticleSystem>(); // non-mesh renderer: problems must not touch a MeshFilter
            PrefabUtility.SaveAsPrefabAsset(broken, BrokenPath);
            Object.DestroyImmediate(broken);
            AssetDatabase.DeleteAsset(MaterialPath);

            // A missing script cannot be made through the API: point Ghost's script at a GUID that does not exist.
            string customGuid = AssetDatabase.FindAssets("CustomComponent t:MonoScript")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => Path.GetFileNameWithoutExtension(p) == "CustomComponent")
                .Select(AssetDatabase.AssetPathToGUID)
                .First();
            string yaml = File.ReadAllText(BrokenPath);
            Assert.That(yaml, Does.Contain(customGuid), "Broken.prefab must be serialized as text for this fixture");
            File.WriteAllText(BrokenPath, yaml.Replace(customGuid, "0123456789abcdef0123456789abcdef"));
            AssetDatabase.ImportAsset(BrokenPath, ImportAssetOptions.ForceUpdate);
        }

        [OneTimeTearDown]
        public void DeleteFixtures()
        {
            sceneScope?.Dispose();
            sceneScope = null;
            if (AssetDatabase.IsValidFolder(Dir)) AssetDatabase.DeleteAsset(Dir);
        }

        private static string Run(JObject args)
        {
            JObject result = ToJObject(InspectPrefab.HandleCommand(args));
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            return result["data"].Value<string>("text");
        }

        private static string RunError(JObject args)
        {
            JObject result = ToJObject(InspectPrefab.HandleCommand(args));
            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            return result.Value<string>("error");
        }

        [Test]
        public void Tree_DefaultDepth_ShowsStructureCollapsesRepeatsAndMarksPrefabs()
        {
            string text = Run(new JObject { ["mode"] = "tree", ["prefab_path"] = CarPath });

            StringAssert.StartsWith("Car.prefab  474 objs  depth<=2", text);
            StringAssert.Contains("Car [InspectPrefabFixture]", text);
            StringAssert.Contains("    Seat @Seat.prefab [InspectPrefabFixture]", text);
            StringAssert.Contains("    Wheel_FL..RR x4 [BoxCollider]", text);
            StringAssert.Contains("  Parts (-)\n    Part0\n    Part1 ...1 below", text);
            StringAssert.Contains("    Part29 ...29 below", text);
            StringAssert.DoesNotContain("Leaf", text);
            StringAssert.DoesNotContain("Transform", text);
            Assert.Less(text.Length, 6000);
        }

        [Test]
        public void Tree_CollapsedNode_StillNamesNestedPrefabsBelow()
        {
            string text = Run(new JObject { ["mode"] = "tree", ["prefab_path"] = CarPath, ["depth"] = 1 });
            StringAssert.Contains("  Body [InspectPrefabFixture] ...1 below [@Seat.prefab]", text);
            StringAssert.Contains("  Parts (-) ...465 below", text);
        }

        [Test]
        public void Tree_Filter_ShowsMatchesWithTheirAncestors()
        {
            string text = Run(new JObject { ["mode"] = "tree", ["prefab_path"] = CarPath, ["filter"] = "BoxCollider" });
            StringAssert.Contains("filter=BoxCollider (4 match)", text);
            StringAssert.Contains("  Wheels", text);
            foreach (string wheel in new[] { "Wheel_FL", "Wheel_FR", "Wheel_RL", "Wheel_RR" })
                StringAssert.Contains(wheel + " [BoxCollider]", text);
            StringAssert.DoesNotContain("Parts", text);
        }

        [Test]
        public void Tree_OverBudget_EndsWithTruncationHint()
        {
            string text = Run(new JObject { ["mode"] = "tree", ["prefab_path"] = CarPath, ["depth"] = 9, ["max_chars"] = 500 });
            StringAssert.Contains("... truncated:", text);
            StringAssert.Contains("Use root=", text);
            Assert.LessOrEqual(text.Length, 500);
        }

        [Test]
        public void Node_ShowsOnlyChangedFieldsWithResolvedRefsAndListeners()
        {
            string text = Run(new JObject
            {
                ["mode"] = "node",
                ["prefab_path"] = CarPath,
                ["path"] = "",
                ["component"] = "InspectPrefabFixture",
            });

            StringAssert.StartsWith("Car.prefab:Car", text);
            StringAssert.Contains("    value: 42", text);
            StringAssert.Contains("    target -> Body", text);
            StringAssert.Contains("    onClick: 2 listeners", text);
            StringAssert.Contains("-> Car (InspectPrefabFixture).Ping()", text);
            StringAssert.Contains("-> Body (InspectPrefabFixture).SetValue(int 3)", text);
            // User MonoBehaviour defaults are deliberately not instantiated: Reset/
            // validation callbacks can run arbitrary user code during an inspection.
            StringAssert.Contains("no default to compare; all fields", text);
            StringAssert.Contains("speed: 1", text);
            StringAssert.Contains("label: \"default\"", text);
            StringAssert.DoesNotContain("instanceID", text);
        }

        [Test]
        public void Node_AllFields_IncludesDefaults()
        {
            string text = Run(new JObject
            {
                ["mode"] = "node",
                ["prefab_path"] = CarPath,
                ["component"] = "InspectPrefabFixture",
                ["all_fields"] = true,
            });
            StringAssert.Contains("    speed: 1", text);
            StringAssert.Contains("    label: \"default\"", text);
            StringAssert.Contains("    other -> null", text);
        }

        [Test]
        public void Node_BuiltInComponentAtDefaults_IsReportedAsAllDefault()
        {
            string text = Run(new JObject
            {
                ["mode"] = "node",
                ["prefab_path"] = CarPath,
                ["path"] = "Wheels/Wheel_FL",
                ["component"] = "BoxCollider",
            });
            StringAssert.Contains("  BoxCollider (all default)", text);
            StringAssert.DoesNotContain("no default", text);
        }

        [Test]
        public void Node_BadPath_SuggestsNearbyPaths()
        {
            string error = RunError(new JObject { ["mode"] = "node", ["prefab_path"] = CarPath, ["path"] = "Wheels/Wheel_F" });
            StringAssert.Contains("No object 'Wheels/Wheel_F'", error);
            StringAssert.Contains("Wheels/Wheel_FL", error);
        }

        [Test]
        public void BadPrefabPath_SuggestsSimilarPrefabs()
        {
            string error = RunError(new JObject { ["mode"] = "tree", ["prefab_path"] = Dir + "/Carr.prefab" });
            StringAssert.Contains("No prefab at", error);
            StringAssert.Contains(CarPath, error);
        }

        [Test]
        public void UnknownMode_ListsValidModes()
        {
            StringAssert.Contains("Valid modes", RunError(new JObject { ["mode"] = "all", ["prefab_path"] = CarPath }));
        }

        [Test]
        public void Refs_ListsEveryPersistentListenerAndReference()
        {
            string text = Run(new JObject { ["mode"] = "refs", ["prefab_path"] = CarPath });
            StringAssert.Contains("(2 events, 3 listeners, 0 MISSING)", text);
            StringAssert.Contains("  InspectPrefabFixture.target -> Body", text);
            StringAssert.Contains("  InspectPrefabFixture.onClick: 2 listeners", text);
            StringAssert.Contains("-> Car (InspectPrefabFixture).Ping()", text);
            StringAssert.Contains("-> Body (InspectPrefabFixture).SetValue(int 3)", text);
            StringAssert.Contains("  InspectPrefabFixture.onValue: 1 listener", text);
            StringAssert.Contains("-> Body (InspectPrefabFixture).SetValue(dynamic int)", text);
        }

        [Test]
        public void Overrides_Variant_ShowsBaseChangedPropsAndAddedComponents()
        {
            string text = Run(new JObject { ["mode"] = "overrides", ["prefab_path"] = VariantPath });
            StringAssert.Contains("variant of @Car.prefab", text);
            StringAssert.Contains("value: 42 -> 7", text);
            StringAssert.Contains("localPosition: (0, 0, 0) -> (0, 1, 0)", text);
            StringAssert.DoesNotContain("localPosition.y", text);
            StringAssert.Contains("+ Body (Rigidbody)", text);
        }

        [Test]
        public void Overrides_PlainPrefab_ReportsNestedInstances()
        {
            string text = Run(new JObject { ["mode"] = "overrides", ["prefab_path"] = CarPath });
            StringAssert.Contains("(1 nested prefab instances)", text);
            StringAssert.Contains("@Seat.prefab", text);
        }

        [Test]
        public void Usages_Script_FindsDirectAndNestedOnlyUsers()
        {
            string text = Run(new JObject { ["mode"] = "usages", ["script"] = "InspectPrefabFixture" });
            StringAssert.Contains(CarPath + ": Car, Body", text);
            StringAssert.Contains(SeatPath + ": Seat", text);
            StringAssert.Contains(HolderPath + ": via @Seat.prefab", text);
            StringAssert.Contains(VariantPath, text);
        }

        [Test]
        public void Usages_Asset_FindsPrefabsThatReferenceIt()
        {
            string text = Run(new JObject { ["mode"] = "usages", ["asset"] = SeatPath });
            StringAssert.Contains(CarPath + ": Body/Seat", text);
            StringAssert.Contains(HolderPath + ": Seat", text);
            StringAssert.Contains(VariantPath + ": via @Car.prefab", text);
        }

        [Test]
        public void Problems_FindsMissingScriptAndMissingReference()
        {
            string text = Run(new JObject { ["mode"] = "problems", ["prefab_path"] = BrokenPath });
            StringAssert.Contains("missing script: Ghost", text);
            StringAssert.Contains("MISSING ref: Broken (InspectPrefabFixture).mat", text);
        }

        [Test]
        public void Problems_Folder_ListsOnlyPrefabsWithProblems()
        {
            string text = Run(new JObject { ["mode"] = "problems", ["prefab_path"] = Dir });
            StringAssert.Contains("5 prefabs scanned, 1 with problems", text);
            StringAssert.Contains(BrokenPath, text);
        }

        [Test]
        public void EveryMode_LeavesAssetsUntouched()
        {
            string[] files = Directory.GetFiles(Dir);
            var before = files.ToDictionary(f => f, File.ReadAllBytes);

            Run(new JObject { ["mode"] = "tree", ["prefab_path"] = CarPath, ["depth"] = 9 });
            Run(new JObject { ["mode"] = "node", ["prefab_path"] = CarPath, ["path"] = "Body" });
            Run(new JObject { ["mode"] = "refs", ["prefab_path"] = CarPath });
            Run(new JObject { ["mode"] = "overrides", ["prefab_path"] = VariantPath });
            Run(new JObject { ["mode"] = "usages", ["script"] = "InspectPrefabFixture" });
            Run(new JObject { ["mode"] = "problems", ["prefab_path"] = Dir });

            foreach (var entry in before)
                CollectionAssert.AreEqual(entry.Value, File.ReadAllBytes(entry.Key), entry.Key + " changed");
            Assert.IsFalse(EditorUtility.IsDirty(AssetDatabase.LoadAssetAtPath<GameObject>(CarPath)));
            Assert.IsFalse(EditorUtility.IsDirty(AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath)));
        }
    }
}
