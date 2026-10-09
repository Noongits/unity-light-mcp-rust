using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using MCPForUnity.Editor.Tools;
using MCPForUnity.Runtime.Helpers;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    public class ManageUITests
    {
        private string TempRoot;
        private string panelSettingsPath;

        [SetUp]
        public void SetUp()
        {
            TempRoot = "Assets/Temp/ManageUITests_" + Guid.NewGuid().ToString("N");
            EnsureFolder(TempRoot);
            panelSettingsPath = TempRoot + "/OwnedPanel.asset";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<PanelSettings>(), panelSettingsPath);
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(TempRoot))
            {
                AssetDatabase.DeleteAsset(TempRoot);
            }

        }

        // ---- Action validation ----

        [Test]
        public void HandleCommand_MissingAction_ReturnsError()
        {
            var result = ToJObject(ManageUI.HandleCommand(new JObject()));
            Assert.IsFalse(result.Value<bool>("success"));
        }

        [Test]
        public void HandleCommand_UnknownAction_ReturnsError()
        {
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "explode"
            }));
            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result["error"].ToString(), Does.Contain("Unknown action"));
        }

        [Test]
        public void Ping_ReturnsPong()
        {
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "ping"
            }));
            Assert.IsTrue(result.Value<bool>("success"));
            Assert.AreEqual("pong", result.Value<string>("message"));
        }

        // ---- Create file ----

        [Test]
        public void Create_Uxml_CreatesFile()
        {
            string path = $"{TempRoot}/Test_{Guid.NewGuid():N}.uxml";
            string content = "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\"><ui:Label text=\"Hi\" /></ui:UXML>";

            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["path"] = path,
                ["contents"] = content,
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());

            // Verify file was created on disk
            string fullPath = Path.Combine(Application.dataPath,
                path.Substring("Assets/".Length)).Replace('/', Path.DirectorySeparatorChar);
            Assert.IsTrue(File.Exists(fullPath), $"File should exist at {fullPath}");

            // EnsureEditorExtensionMode may inject editor-extension-mode attribute
            string actual = File.ReadAllText(fullPath);
            Assert.That(actual, Does.Contain("ui:UXML"));
            Assert.That(actual, Does.Contain("ui:Label"));
        }

        [Test]
        public void Create_Uss_CreatesFile()
        {
            string path = $"{TempRoot}/Test_{Guid.NewGuid():N}.uss";
            string content = ".root { background-color: red; }";

            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["path"] = path,
                ["contents"] = content,
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
        }

        [Test]
        public void Create_InvalidExtension_ReturnsError()
        {
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["path"] = $"{TempRoot}/Test.txt",
                ["contents"] = "hello",
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result["error"].ToString(), Does.Contain(".uxml or .uss"));
        }

        [Test]
        public void Create_MissingContents_ReturnsError()
        {
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["path"] = $"{TempRoot}/Test.uxml",
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result["error"].ToString(), Does.Contain("contents"));
        }

        [Test]
        public void Create_AlreadyExists_ReturnsError()
        {
            string path = $"{TempRoot}/Exists_{Guid.NewGuid():N}.uxml";
            string content = "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" />";

            // Create first time
            ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["path"] = path,
                ["contents"] = content,
            });

            // Try to create again
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["path"] = path,
                ["contents"] = content,
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result["error"].ToString(), Does.Contain("already exists"));
        }

        [Test]
        public void Create_WithBase64EncodedContents_Decodes()
        {
            string path = $"{TempRoot}/Encoded_{Guid.NewGuid():N}.uxml";
            string content = "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" />";
            string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));

            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["path"] = path,
                ["encodedContents"] = encoded,
                ["contentsEncoded"] = true,
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());

            string fullPath = Path.Combine(Application.dataPath,
                path.Substring("Assets/".Length)).Replace('/', Path.DirectorySeparatorChar);
            string actual = File.ReadAllText(fullPath);
            // EnsureEditorExtensionMode may inject editor-extension-mode attribute
            Assert.That(actual, Does.Contain("ui:UXML"));
            Assert.That(actual, Does.Contain("UnityEngine.UIElements"));
        }

        // ---- Read file ----

        [Test]
        public void Read_ExistingFile_ReturnsContents()
        {
            string path = $"{TempRoot}/ReadTest_{Guid.NewGuid():N}.uxml";
            string content = "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" />";

            ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["path"] = path,
                ["contents"] = content,
            });

            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "read",
                ["path"] = path,
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            var data = result["data"] as JObject;
            Assert.IsNotNull(data);
            // EnsureEditorExtensionMode may inject editor-extension-mode attribute
            Assert.That(data.Value<string>("contents"), Does.Contain("ui:UXML"));
        }

        [Test]
        public void Read_NonExistentFile_ReturnsError()
        {
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "read",
                ["path"] = $"{TempRoot}/DoesNotExist.uxml",
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result["error"].ToString(), Does.Contain("not found"));
        }

        // ---- Update file ----

        [Test]
        public void Update_ExistingFile_OverwritesContents()
        {
            string path = $"{TempRoot}/UpdateTest_{Guid.NewGuid():N}.uss";
            string original = ".root { color: red; }";
            string updated = ".root { color: blue; font-size: 20px; }";

            ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["path"] = path,
                ["contents"] = original,
            });

            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "update",
                ["path"] = path,
                ["contents"] = updated,
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());

            // Verify content was updated
            var readResult = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "read",
                ["path"] = path,
            }));
            Assert.AreEqual(updated, readResult["data"].Value<string>("contents"));
        }

        [Test]
        public void Update_NonExistentFile_ReturnsError()
        {
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "update",
                ["path"] = $"{TempRoot}/Missing.uxml",
                ["contents"] = "<ui:UXML />",
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result["error"].ToString(), Does.Contain("not found"));
        }

        // ---- Create PanelSettings ----

        [Test]
        public void CreatePanelSettings_CreatesAsset()
        {
            string path = $"{TempRoot}/TestPanel_{Guid.NewGuid():N}.asset";

            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create_panel_settings",
                ["path"] = path,
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());

            var ps = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            Assert.IsNotNull(ps, "PanelSettings should exist at the path");
        }

        [Test]
        public void CreatePanelSettings_AlreadyExists_ReturnsError()
        {
            string path = $"{TempRoot}/ExistingPanel_{Guid.NewGuid():N}.asset";

            ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create_panel_settings",
                ["path"] = path,
            });

            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create_panel_settings",
                ["path"] = path,
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result["error"].ToString(), Does.Contain("already exists"));
        }

        // ---- Attach UIDocument ----

        [Test]
        public void AttachUIDocument_AddsComponent()
        {
            // Create a UXML file first
            string uxmlPath = $"{TempRoot}/Attach_{Guid.NewGuid():N}.uxml";
            ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["path"] = uxmlPath,
                ["contents"] = "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\"><ui:Label text=\"Test\" /></ui:UXML>",
            });
            AssetDatabase.Refresh();

            // Create a test GameObject
            var go = new GameObject("UITestObject_Attach_" + Guid.NewGuid().ToString("N"));
            try
            {
                var result = ToJObject(ManageUI.HandleCommand(new JObject
                {
                    ["action"] = "attach_ui_document",
                    ["target"] = go.name,
                    ["source_asset"] = uxmlPath,
                    ["panel_settings"] = panelSettingsPath,
                }));

                Assert.IsTrue(result.Value<bool>("success"), result.ToString());

                var uiDoc = go.GetComponent<UIDocument>();
                Assert.IsNotNull(uiDoc, "UIDocument component should be attached");
                Assert.IsNotNull(uiDoc.visualTreeAsset, "VisualTreeAsset should be assigned");
                Assert.IsNotNull(uiDoc.panelSettings, "PanelSettings should be assigned (auto-created)");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void AttachUIDocument_MissingTarget_ReturnsError()
        {
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "attach_ui_document",
                ["source_asset"] = "Assets/UI/Test.uxml",
            }));

            Assert.IsFalse(result.Value<bool>("success"));
        }

        [Test]
        public void AttachUIDocument_MissingSourceAsset_ReturnsError()
        {
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "attach_ui_document",
                ["target"] = "SomeObject",
            }));

            Assert.IsFalse(result.Value<bool>("success"));
        }

        // ---- Get Visual Tree ----

        [Test]
        public void GetVisualTree_MissingTarget_ReturnsError()
        {
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "get_visual_tree",
            }));

            Assert.IsFalse(result.Value<bool>("success"));
        }

        [Test]
        public void GetVisualTree_NoUIDocument_ReturnsError()
        {
            var go = new GameObject("UITestObject_NoDoc_" + Guid.NewGuid().ToString("N"));
            try
            {
                var result = ToJObject(ManageUI.HandleCommand(new JObject
                {
                    ["action"] = "get_visual_tree",
                    ["target"] = go.name,
                }));

                Assert.IsFalse(result.Value<bool>("success"));
                Assert.That(result["error"].ToString(), Does.Contain("UIDocument"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // ---- Delete file ----

        [Test]
        public void Delete_ExistingFile_DeletesFile()
        {
            string path = $"{TempRoot}/Delete_{Guid.NewGuid():N}.uss";
            ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["path"] = path,
                ["contents"] = ".root { color: red; }",
            });

            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "delete",
                ["path"] = path,
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());

            string fullPath = Path.Combine(Application.dataPath,
                path.Substring("Assets/".Length)).Replace('/', Path.DirectorySeparatorChar);
            Assert.IsFalse(File.Exists(fullPath), "File should be deleted");
        }

        [Test]
        public void Delete_NonExistentFile_ReturnsError()
        {
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "delete",
                ["path"] = $"{TempRoot}/Missing.uxml",
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result["error"].ToString(), Does.Contain("not found"));
        }

        [Test]
        public void Delete_InvalidExtension_ReturnsError()
        {
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "delete",
                ["path"] = $"{TempRoot}/File.txt",
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result["error"].ToString(), Does.Contain(".uxml or .uss"));
        }

        // ---- List UI assets ----

        [Test]
        public void List_ReturnsUIAssets()
        {
            string uxmlPath = $"{TempRoot}/ListTest_{Guid.NewGuid():N}.uxml";
            string ussPath = $"{TempRoot}/ListTest_{Guid.NewGuid():N}.uss";

            ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["path"] = uxmlPath,
                ["contents"] = "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" />",
            });
            ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["path"] = ussPath,
                ["contents"] = ".root { }",
            });

            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "list",
                ["path"] = TempRoot,
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            var data = result["data"] as JObject;
            Assert.IsNotNull(data);
            int total = data.Value<int>("total");
            Assert.GreaterOrEqual(total, 2, "Should find at least 2 UI assets");
        }

        [Test]
        public void List_WithFilterType_FiltersResults()
        {
            string uxmlPath = $"{TempRoot}/FilterTest_{Guid.NewGuid():N}.uxml";
            ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["path"] = uxmlPath,
                ["contents"] = "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" />",
            });

            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "list",
                ["path"] = TempRoot,
                ["filterType"] = "uxml",
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            var assets = result["data"]["assets"] as JArray;
            Assert.IsNotNull(assets);
            Assert.Greater(assets.Count, 0, "Filtering must retain the matching test UXML.");
            foreach (var asset in assets)
            {
                Assert.AreEqual("uxml", asset.Value<string>("type"));
            }
        }

        // ---- Detach UIDocument ----

        [Test]
        public void DetachUIDocument_RemovesComponent()
        {
            string uxmlPath = $"{TempRoot}/Detach_{Guid.NewGuid():N}.uxml";
            ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["path"] = uxmlPath,
                ["contents"] = "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\"><ui:Label text=\"Test\" /></ui:UXML>",
            });
            AssetDatabase.Refresh();

            var go = new GameObject("UITestObject_Detach_" + Guid.NewGuid().ToString("N"));
            try
            {
                ManageUI.HandleCommand(new JObject
                {
                    ["action"] = "attach_ui_document",
                    ["target"] = go.name,
                    ["source_asset"] = uxmlPath,
                    ["panel_settings"] = panelSettingsPath,
                });
                Assert.IsNotNull(go.GetComponent<UIDocument>(), "UIDocument should be attached");

                var result = ToJObject(ManageUI.HandleCommand(new JObject
                {
                    ["action"] = "detach_ui_document",
                    ["target"] = go.name,
                }));

                Assert.IsTrue(result.Value<bool>("success"), result.ToString());
                Assert.IsNull(go.GetComponent<UIDocument>(), "UIDocument should be removed");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void DetachUIDocument_NoUIDocument_ReturnsError()
        {
            var go = new GameObject("UITestObject_DetachNoDoc_" + Guid.NewGuid().ToString("N"));
            try
            {
                var result = ToJObject(ManageUI.HandleCommand(new JObject
                {
                    ["action"] = "detach_ui_document",
                    ["target"] = go.name,
                }));

                Assert.IsFalse(result.Value<bool>("success"));
                Assert.That(result["error"].ToString(), Does.Contain("UIDocument"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void DetachUIDocument_MissingTarget_ReturnsError()
        {
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "detach_ui_document",
            }));

            Assert.IsFalse(result.Value<bool>("success"));
        }

        // ---- Modify visual element ----

        [Test]
        public void ModifyVisualElement_MissingTarget_ReturnsError()
        {
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "modify_visual_element",
                ["elementName"] = "test",
            }));

            Assert.IsFalse(result.Value<bool>("success"));
        }

        [Test]
        public void ModifyVisualElement_MissingElementName_ReturnsError()
        {
            var go = new GameObject("UITestObject_ModifyNoName_" + Guid.NewGuid().ToString("N"));
            try
            {
                var result = ToJObject(ManageUI.HandleCommand(new JObject
                {
                    ["action"] = "modify_visual_element",
                    ["target"] = go.name,
                }));

                Assert.IsFalse(result.Value<bool>("success"));
                Assert.That(result["error"].ToString(), Does.Contain("element_name"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ModifyVisualElement_NoUIDocument_ReturnsError()
        {
            var go = new GameObject("UITestObject_ModifyNoDoc_" + Guid.NewGuid().ToString("N"));
            try
            {
                var result = ToJObject(ManageUI.HandleCommand(new JObject
                {
                    ["action"] = "modify_visual_element",
                    ["target"] = go.name,
                    ["elementName"] = "test",
                }));

                Assert.IsFalse(result.Value<bool>("success"));
                Assert.That(result["error"].ToString(), Does.Contain("UIDocument"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // ---- UXML validation ----

        [Test]
        public void Create_MalformedXml_ReturnsError_FileNotWritten()
        {
            string path = $"{TempRoot}/Malformed_{Guid.NewGuid():N}.uxml";
            string badContent = "<ui:UXML><ui:Label text=\"unclosed\">";

            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["path"] = path,
                ["contents"] = badContent,
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result["error"].ToString(), Does.Contain("Malformed XML"));

            // Verify file was NOT written
            string fullPath = Path.Combine(Application.dataPath,
                path.Substring("Assets/".Length)).Replace('/', Path.DirectorySeparatorChar);
            Assert.IsFalse(File.Exists(fullPath), "Malformed UXML should not be written to disk");
        }

        [Test]
        public void Create_MissingNamespace_WritesWithWarning()
        {
            string path = $"{TempRoot}/NoNs_{Guid.NewGuid():N}.uxml";
            string content = "<ui:UXML><ui:Label text=\"hi\" /></ui:UXML>";

            // Unity's UXML importer logs an error for undeclared 'ui' prefix
            bool previousIgnoreFailingMessages = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            try
            {
                var result = ToJObject(ManageUI.HandleCommand(new JObject
                {
                    ["action"] = "create",
                    ["path"] = path,
                    ["contents"] = content,
                }));

                Assert.IsTrue(result.Value<bool>("success"), result.ToString());
                var data = result["data"] as JObject;
                Assert.IsNotNull(data);
                var warnings = data["validationWarnings"] as JArray;
                Assert.IsNotNull(warnings, "Should have validationWarnings");
                Assert.That(warnings.ToString(), Does.Contain("Missing namespace"));
            }
            finally
            {
                LogAssert.ignoreFailingMessages = previousIgnoreFailingMessages;
            }
        }

        [Test]
        public void Create_ValidUxml_NoWarnings()
        {
            string path = $"{TempRoot}/Valid_{Guid.NewGuid():N}.uxml";
            string content = "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\"><ui:Label text=\"ok\" /></ui:UXML>";

            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["path"] = path,
                ["contents"] = content,
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            var data = result["data"] as JObject;
            Assert.IsNull(data?["validationWarnings"],
                "Fully valid UXML should not have validationWarnings");
        }

        [Test]
        public void Create_WrongRootElement_WritesWithWarning()
        {
            string path = $"{TempRoot}/WrongRoot_{Guid.NewGuid():N}.uxml";
            string content = "<div xmlns:ui=\"UnityEngine.UIElements\"><ui:Label text=\"hi\" /></div>";

            // Unity's UXML importer logs an error about expected root element
            bool previousIgnoreFailingMessages = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            try
            {
                var result = ToJObject(ManageUI.HandleCommand(new JObject
                {
                    ["action"] = "create",
                    ["path"] = path,
                    ["contents"] = content,
                }));

                Assert.IsTrue(result.Value<bool>("success"), result.ToString());
                var data = result["data"] as JObject;
                var warnings = data["validationWarnings"] as JArray;
                Assert.IsNotNull(warnings, "Should have validationWarnings");
                Assert.That(warnings.ToString(), Does.Contain("Root element"));
            }
            finally
            {
                LogAssert.ignoreFailingMessages = previousIgnoreFailingMessages;
            }
        }

        [Test]
        public void Create_EmptyContent_ReturnsError()
        {
            string path = $"{TempRoot}/Empty_{Guid.NewGuid():N}.uxml";

            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["path"] = path,
                ["contents"] = "   ",
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result["error"].ToString(), Does.Contain("empty"));
        }

        [Test]
        public void Update_MalformedXml_ReturnsError_FileNotChanged()
        {
            string path = $"{TempRoot}/UpdateMalformed_{Guid.NewGuid():N}.uxml";
            string original = "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" />";

            ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["path"] = path,
                ["contents"] = original,
            });

            string badContent = "<ui:UXML><broken>";
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "update",
                ["path"] = path,
                ["contents"] = badContent,
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result["error"].ToString(), Does.Contain("Malformed XML"));

            // Verify original content was preserved (EnsureEditorExtensionMode may have injected attribute)
            string fullPath = Path.Combine(Application.dataPath,
                path.Substring("Assets/".Length)).Replace('/', Path.DirectorySeparatorChar);
            string actual = File.ReadAllText(fullPath);
            Assert.That(actual, Does.Contain("ui:UXML"), "Original file content should be preserved");
            Assert.That(actual, Does.Not.Contain("<broken>"), "Malformed content should not be written");
        }

        [Test]
        public void Create_Uss_SkipsUxmlValidation()
        {
            string path = $"{TempRoot}/NoValidation_{Guid.NewGuid():N}.uss";
            // USS is CSS-like, not XML — validation should be skipped
            string content = "This is not valid XML <broken>";

            // Unity 6000.4+'s USS importer logs an error on this content; the test only
            // verifies that ManageUI itself doesn't pre-validate .uss as UXML, not the
            // downstream importer's behavior.
            bool previousIgnoreFailingMessages = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            try
            {
                var result = ToJObject(ManageUI.HandleCommand(new JObject
                {
                    ["action"] = "create",
                    ["path"] = path,
                    ["contents"] = content,
                }));

                Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            }
            finally
            {
                LogAssert.ignoreFailingMessages = previousIgnoreFailingMessages;
            }
        }

        // ---- Path traversal validation ----

        [Test]
        public void Create_TraversalPath_ReturnsError()
        {
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["path"] = "Assets/../etc/evil.uxml",
                ["contents"] = "<ui:UXML />",
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result["error"].ToString(), Does.Contain("traversal"));
        }

        [Test]
        public void Create_DotDotInMiddle_ReturnsError()
        {
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "create",
                ["path"] = "Assets/UI/../../secret.uxml",
                ["contents"] = "<ui:UXML />",
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result["error"].ToString(), Does.Contain("traversal"));
        }

        [Test]
        public void Read_TraversalPath_ReturnsError()
        {
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "read",
                ["path"] = "Assets/../secret.uxml",
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result["error"].ToString(), Does.Contain("traversal"));
        }

        [Test]
        public void Update_TraversalPath_ReturnsError()
        {
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "update",
                ["path"] = "Assets/../../etc/passwd.uxml",
                ["contents"] = "overwrite",
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result["error"].ToString(), Does.Contain("traversal"));
        }

        [Test]
        public void Delete_TraversalPath_ReturnsError()
        {
            var result = ToJObject(ManageUI.HandleCommand(new JObject
            {
                ["action"] = "delete",
                ["path"] = "Assets/../outside.uxml",
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result["error"].ToString(), Does.Contain("traversal"));
        }
    }

    // Resource ownership regression tests. These require an editor with a graphics device.
    public class ManageUIRenderLifetimeTests
    {
        private string root;
        private string output;
        private GameObject go;
        private PanelSettings panel;
        private RenderTexture userTarget;
        private RenderTexture previousActive;
        private bool ownsCaptureState;

        private static System.Collections.IDictionary Cache =>
            (System.Collections.IDictionary)typeof(ManageUI).GetField("s_panelRTs",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);

        private static void InvokeLifecycle(string name) => typeof(ManageUI).GetMethod(name,
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, null);

        [SetUp]
        public void SetUp()
        {
            ownsCaptureState = false;
            previousActive = RenderTexture.active;
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                Assert.Ignore("Render lifetime tests need a graphics device; do not run with -nographics.");
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            if (Cache.Count != 0 || (bool)typeof(ManageUI).GetField("s_pendingCaptureStarted", flags).GetValue(null)
                || typeof(ManageUI).GetField("s_pendingCaptureTex", flags).GetValue(null) != null)
                Assert.Ignore("A user UI capture is active; lifecycle tests must not consume it.");
            ownsCaptureState = true;
            root = $"Assets/Temp/ManageUIRender_{Guid.NewGuid():N}";
            EnsureFolder(root);
            output = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Temp", $"ManageUIRender_{Guid.NewGuid():N}");
            Directory.CreateDirectory(output);
            panel = ScriptableObject.CreateInstance<PanelSettings>();
            AssetDatabase.CreateAsset(panel, root + "/Panel.asset");
            panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(root + "/Panel.asset");
            userTarget = new RenderTexture(13, 17, 0);
            AssetDatabase.CreateAsset(userTarget, root + "/User.renderTexture");
            userTarget = AssetDatabase.LoadAssetAtPath<RenderTexture>(root + "/User.renderTexture");
            userTarget.Create();
            panel.targetTexture = userTarget;
            go = new GameObject("ManageUIRender_" + Guid.NewGuid().ToString("N"));
            go.SetActive(false);
            go.AddComponent<UIDocument>().panelSettings = panel;
            go.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            if (!ownsCaptureState) return;
            InvokeLifecycle("CleanupRenderTextures");
            RenderTexture.active = previousActive;
            if (go != null) UnityEngine.Object.DestroyImmediate(go);
            if (userTarget != null) userTarget.Release();
            if (root != null) AssetDatabase.DeleteAsset(root);
            if (output != null && Directory.Exists(output)) Directory.Delete(output, true);
        }

        private JObject Render(bool pathOnly = false, int width = 37, string folder = null)
        {
            var args = new JObject
            {
                ["action"] = "render_ui", ["width"] = width, ["height"] = 29,
                ["output_folder"] = folder ?? output, ["file_name"] = "capture.png",
            };
            if (pathOnly) args["path"] = root + "/Test.uxml";
            else args["target"] = go.name;
            return ToJObject(ManageUI.HandleCommand(args));
        }

        private void CreateUxml()
        {
            File.WriteAllText(root + "/Test.uxml",
                "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\"><ui:Label text=\"Lifetime test\" /></ui:UXML>");
            AssetDatabase.ImportAsset(root + "/Test.uxml", ImportAssetOptions.ForceSynchronousImport);
        }

        private static int CaptureTextures() => System.Linq.Enumerable.Count(
            UnityEngine.Resources.FindObjectsOfTypeAll<RenderTexture>(), t => t.name.StartsWith("MCP_UI_Render_"));

        private static int ReadbackTextures() => System.Linq.Enumerable.Count(
            UnityEngine.Resources.FindObjectsOfTypeAll<Texture2D>(), t => t.width == 37 && t.height == 29);

        [TestCase(false)]
        [TestCase(true)]
        public void PathRender_WithExistingSettings_ReusesCaptureAndReleasesReadbacks(bool failWrite)
        {
            CreateUxml();
            int textures = CaptureTextures();
            int readbacks = ReadbackTextures();
            var borrowed = AssetDatabase.LoadAssetAtPath<PanelSettings>(AssetDatabase.GUIDToAssetPath(
                AssetDatabase.FindAssets("t:PanelSettings", new[] { "Assets" })[0]));
            if (borrowed != panel)
                Assert.Ignore("Path rendering selected a user PanelSettings asset; only fixture-owned settings may be mutated.");
            var originalTarget = borrowed.targetTexture;
            string blocked = Path.Combine(output, "blocked");
            File.WriteAllText(blocked, "This is a file, so Directory.CreateDirectory must fail.");
            for (int i = 0; i < 5; ++i)
            {
                var result = Render(true, folder: failWrite ? blocked : output);
                Assert.AreEqual(!failWrite, result.Value<bool>("success"), result.ToString());
                Assert.AreEqual(0, Cache.Count);
                Assert.AreEqual(textures, CaptureTextures());
                Assert.AreEqual(readbacks, ReadbackTextures());
                Assert.AreEqual(originalTarget, borrowed.targetTexture);
                Assert.IsFalse(System.Linq.Enumerable.Any(UnityEngine.Resources.FindObjectsOfTypeAll<GameObject>(),
                    item => item.name == "__MCP_UI_Render_Temp__"));
                Assert.IsFalse(System.Linq.Enumerable.Any(UnityEngine.Resources.FindObjectsOfTypeAll<PanelSettings>(),
                    item => item.name == "__MCP_UI_Render_Panel__"));
            }
            InvokeLifecycle("CleanupRenderTextures");
            Assert.AreSame(originalTarget, borrowed.targetTexture);
            Assert.AreEqual(textures, CaptureTextures());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PathRender_WithoutAnyPanelSettings_DoesNotCreatePersistentAssets(bool failWrite)
        {
            CreateUxml();
            UnityEngine.Object.DestroyImmediate(go);
            AssetDatabase.DeleteAsset(root + "/Panel.asset");
            if (AssetDatabase.FindAssets("t:PanelSettings").Length != 0)
                Assert.Ignore("This branch requires a project without other PanelSettings assets.");
            int textures = CaptureTextures();
            int readbacks = ReadbackTextures();
            string[] assets = AssetDatabase.FindAssets("t:RenderTexture");
            string blocked = Path.Combine(output, "blocked");
            File.WriteAllText(blocked, "file");
            for (int i = 0; i < 3; ++i)
            {
                var result = Render(true, folder: failWrite ? blocked : output);
                Assert.AreEqual(!failWrite, result.Value<bool>("success"), result.ToString());
                Assert.AreEqual(readbacks, ReadbackTextures());
                Assert.AreEqual(0, Cache.Count);
                Assert.AreEqual(textures, CaptureTextures());
                Assert.AreEqual(0, AssetDatabase.FindAssets("t:PanelSettings").Length);
                CollectionAssert.AreEquivalent(assets, AssetDatabase.FindAssets("t:RenderTexture"));
            }
        }

        [Test]
        public void TemporaryCapture_DisposeAfterRenderException_CreatesNoAssetsAndReleasesTexture()
        {
            // Deterministically exercise temporary ownership even in projects with existing settings.
            var temporary = ScriptableObject.CreateInstance<PanelSettings>();
            var captureType = typeof(ManageUI).GetNestedType("PanelCapture",
                System.Reflection.BindingFlags.NonPublic);
            var assets = AssetDatabase.FindAssets("t:RenderTexture");
            RenderTexture owned = null;
            IDisposable capture = null;
            try
            {
                capture = (IDisposable)Activator.CreateInstance(captureType, temporary, 37, 29, false);
                owned = (RenderTexture)captureType.GetField("Texture").GetValue(capture);
                captureType.GetMethod("Attach").Invoke(capture, null);
                Assert.AreSame(owned, temporary.targetTexture);
                Assert.IsEmpty(AssetDatabase.GetAssetPath(owned));
                Assert.Throws<InvalidOperationException>(() =>
                {
                    try { throw new InvalidOperationException("Simulated render failure after allocation."); }
                    finally { capture.Dispose(); }
                });
                capture.Dispose();
                Assert.IsTrue(owned == null);
                Assert.IsNull(temporary.targetTexture);
                Assert.AreEqual(0, Cache.Count);
                CollectionAssert.AreEquivalent(assets, AssetDatabase.FindAssets("t:RenderTexture"));
            }
            finally
            {
                capture?.Dispose();
                UnityEngine.Object.DestroyImmediate(temporary);
            }
        }

        [Test]
        public void TargetRender_SecondReadReusesTextureAndRestoresUserTarget()
        {
            Assert.IsTrue(Render().Value<bool>("success"));
            var owned = panel.targetTexture;
            Assert.AreNotSame(userTarget, owned);
            Assert.AreEqual(1, Cache.Count);
            var active = RenderTexture.active;
            try
            {
                RenderTexture.active = owned;
                GL.Clear(true, true, Color.red);
            }
            finally { RenderTexture.active = active; }
            var result = Render();
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsTrue(result["data"].Value<bool>("hasContent"), "The second read must capture the existing RT.");
            Assert.AreEqual(userTarget, panel.targetTexture);
            Assert.IsTrue(owned != null, "The reusable RT remains alive after a successful read.");
            Assert.IsTrue(Render().Value<bool>("success"));
            Assert.AreSame(owned, panel.targetTexture);
            Assert.AreEqual(1, Cache.Count);
        }

        [Test]
        public void TargetRender_ResizeReleasesOldTexture_AndCleanupIsIdempotent()
        {
            Assert.IsTrue(Render().Value<bool>("success"));
            var old = panel.targetTexture;
            string oldPath = AssetDatabase.GetAssetPath(old);
            Assert.IsTrue(Render(width: 43).Value<bool>("success"));
            var replacement = panel.targetTexture;
            Assert.IsTrue(old == null);
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<RenderTexture>(oldPath));
            Assert.AreEqual(43, replacement.width);
            Assert.AreEqual(1, Cache.Count);
            InvokeLifecycle("CleanupRenderTextures");
            InvokeLifecycle("CleanupRenderTextures");
            Assert.IsTrue(replacement == null);
            Assert.AreEqual(0, Cache.Count);
            Assert.AreEqual(userTarget, panel.targetTexture);
            Assert.IsTrue(userTarget.IsCreated());
            Assert.AreSame(userTarget, AssetDatabase.LoadAssetAtPath<RenderTexture>(root + "/User.renderTexture"));
        }

        [Test]
        public void TargetRender_WriteFailureRestoresActiveAndTarget_WithoutReadbackLeak()
        {
            string blocked = Path.Combine(output, "blocked");
            File.WriteAllText(blocked, "file");
            RenderTexture.active = userTarget;
            int before = ReadbackTextures();
            for (int i = 0; i < 3; ++i)
            {
                var result = Render(folder: blocked);
                Assert.IsFalse(result.Value<bool>("success"));
                Assert.AreSame(userTarget, RenderTexture.active);
                Assert.AreEqual(userTarget, panel.targetTexture);
                Assert.AreEqual(before, ReadbackTextures());
                Assert.AreEqual(1, Cache.Count, "Failure must reuse one owned capture rather than allocate per call.");
            }
            Assert.IsTrue(Render().Value<bool>("success"), "A failed write must not poison the next capture.");
        }

        [Test]
        public void TargetRender_DeletedPanelPrunesItsOwnedTexture()
        {
            Assert.IsTrue(Render().Value<bool>("success"));
            var owned = panel.targetTexture;
            string path = AssetDatabase.GetAssetPath(owned);
            UnityEngine.Object.DestroyImmediate(go);
            AssetDatabase.DeleteAsset(root + "/Panel.asset");
            InvokeLifecycle("PruneRenderTextures");
            Assert.AreEqual(0, Cache.Count);
            Assert.IsTrue(owned == null);
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<RenderTexture>(path));
            Assert.IsTrue(userTarget != null && userTarget.IsCreated());
        }

        [Test]
        public void TargetRender_DeletedOwnedTextureCanBeRecreated()
        {
            Assert.IsTrue(Render().Value<bool>("success"));
            UnityEngine.Object.DestroyImmediate(panel.targetTexture);
            InvokeLifecycle("PruneRenderTextures");
            Assert.AreEqual(0, Cache.Count);
            Assert.AreEqual(userTarget, panel.targetTexture);
            Assert.IsTrue(Render().Value<bool>("success"));
            Assert.AreEqual(1, Cache.Count);
        }

        [Test]
        public void Cleanup_ReleasesUnconsumedPlayModeCaptureAndInvalidatesPendingCallbacks()
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var type = typeof(ManageUI);
            var texture = new Texture2D(37, 29);
            type.GetField("s_pendingCaptureTex", flags).SetValue(null, texture);
            type.GetField("s_pendingCaptureDone", flags).SetValue(null, true);
            type.GetField("s_pendingCaptureStarted", flags).SetValue(null, true);
            int generation = (int)type.GetField("s_captureGeneration", flags).GetValue(null);
            InvokeLifecycle("CleanupRenderTextures");
            Assert.IsTrue(texture == null);
            Assert.IsNull(type.GetField("s_pendingCaptureTex", flags).GetValue(null));
            Assert.IsFalse((bool)type.GetField("s_pendingCaptureDone", flags).GetValue(null));
            Assert.IsFalse((bool)type.GetField("s_pendingCaptureStarted", flags).GetValue(null));
            Assert.AreNotEqual(generation, type.GetField("s_captureGeneration", flags).GetValue(null));
        }

        [Test]
        public void Cleanup_DoesNotOverwriteAnExternalTargetChange()
        {
            Assert.IsTrue(Render().Value<bool>("success"));
            panel.targetTexture = null;
            InvokeLifecycle("CleanupRenderTextures");
            Assert.IsNull(panel.targetTexture);
            Assert.IsTrue(userTarget != null && userTarget.IsCreated());
        }

        [Test]
        public void TargetRender_DoesNotOverwriteSameNamedUserAsset()
        {
            EnsureFolder("Assets/UI");
            string path = $"Assets/UI/RT_MCP_UI_Render_{panel.GetInstanceIDCompat()}.renderTexture";
            if (AssetDatabase.LoadMainAssetAtPath(path) != null)
                Assert.Ignore("An asset already occupies the collision-test path.");
            var unrelated = new RenderTexture(7, 11, 0);
            AssetDatabase.CreateAsset(unrelated, path);
            try
            {
                Assert.IsTrue(Render().Value<bool>("success"));
                Assert.AreNotEqual(path, AssetDatabase.GetAssetPath(panel.targetTexture));
                InvokeLifecycle("CleanupRenderTextures");
                Assert.AreEqual(unrelated, AssetDatabase.LoadAssetAtPath<RenderTexture>(path));
            }
            finally { AssetDatabase.DeleteAsset(path); }
        }
    }
}
