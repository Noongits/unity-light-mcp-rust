using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MCPForUnity.Editor.Tools
{
    [McpForUnityTool("manage_ui", AutoRegister = false, Group = "ui")]
    public static class ManageUI
    {
        private static readonly HashSet<string> ValidExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".uxml", ".uss"
        };

        // UTF-8 without BOM — UI Builder in Unity 6 can fail to open UXML files with a BOM.
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        static ManageUI()
        {
            EditorApplication.quitting += CleanupRenderTextures;
            AssemblyReloadEvents.beforeAssemblyReload += CleanupRenderTextures;
            EditorApplication.update += PruneRenderTextures;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void ResetPendingCapture()
        {
            // A queued callback may arrive after cleanup; invalidate its ownership first.
            ++s_captureGeneration;
            if (s_pendingCaptureTex != null) UnityEngine.Object.DestroyImmediate(s_pendingCaptureTex);
            s_pendingCaptureTex = null;
            s_pendingCaptureDone = false;
            s_pendingCaptureStarted = false;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // With domain reload disabled, static capture state survives play-mode transitions.
            // A capturer can be destroyed before its end-of-frame callback ever runs.
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode)
                CleanupRenderTextures();
        }

        private static void CleanupRenderTextures()
        {
            ResetPendingCapture();
            foreach (var capture in s_panelRTs.Values)
                capture.Dispose();
            s_panelRTs.Clear();
        }

        private static void PruneRenderTextures()
        {
            if (s_panelRTs.Count == 0) return;
            // A destroyed PanelSettings must not keep its GPU allocation alive until reload.
            List<int> staleKeys = null;
            foreach (var kvp in s_panelRTs)
            {
                if (kvp.Value.Panel != null && kvp.Value.Texture != null) continue;
                if (staleKeys == null) staleKeys = new List<int>();
                staleKeys.Add(kvp.Key);
            }
            if (staleKeys == null) return;
            foreach (var key in staleKeys)
            {
                s_panelRTs[key].Dispose();
                s_panelRTs.Remove(key);
            }
        }

        public static object HandleCommand(JObject @params)
        {
            string action = @params["action"]?.ToString()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(action))
            {
                return new ErrorResponse("Action is required");
            }

            try
            {
                switch (action)
                {
                    case "ping":
                        return new SuccessResponse("pong", new { tool = "manage_ui" });

                    case "create":
                        return CreateFile(@params);

                    case "read":
                        return ReadFile(@params);

                    case "update":
                        return UpdateFile(@params);

                    case "attach_ui_document":
                        return AttachUIDocument(@params);

                    case "create_panel_settings":
                        return CreatePanelSettings(@params);

                    case "update_panel_settings":
                        return UpdatePanelSettings(@params);

                    case "get_visual_tree":
                        return GetVisualTree(@params);

                    case "render_ui":
                        return RenderUI(@params);

                    case "link_stylesheet":
                        return LinkStylesheet(@params);

                    case "delete":
                        return DeleteFile(@params);

                    case "list":
                        return ListUIAssets(@params);

                    case "detach_ui_document":
                        return DetachUIDocument(@params);

                    case "modify_visual_element":
                        return ModifyVisualElement(@params);

                    default:
                        return new ErrorResponse($"Unknown action: {action}");
                }
            }
            catch (Exception ex)
            {
                return new ErrorResponse(ex.Message, new { stackTrace = ex.StackTrace });
            }
        }

        private static string ValidatePath(string path, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path))
            {
                error = "'path' parameter is required.";
                return null;
            }

            path = AssetPathUtility.SanitizeAssetPath(path);
            if (path == null)
            {
                error = "Invalid path: contains traversal sequences.";
                return null;
            }

            string ext = Path.GetExtension(path);
            if (!ValidExtensions.Contains(ext))
            {
                error = $"Invalid file extension '{ext}'. Must be .uxml or .uss.";
                return null;
            }

            return path;
        }

        private static object CreateFile(JObject @params)
        {
            var p = new ToolParams(@params);
            string path = ValidatePath(p.Get("path"), out string pathError);
            if (pathError != null) return new ErrorResponse(pathError);

            string contents;
            try
            {
                contents = GetDecodedContents(p);
            }
            catch (ArgumentException ex)
            {
                return new ErrorResponse(ex.Message);
            }

            if (contents == null)
            {
                return new ErrorResponse("'contents' parameter is required for create.");
            }

            string fullPath = Path.Combine(Application.dataPath,
                path.Substring("Assets/".Length));
            fullPath = fullPath.Replace('/', Path.DirectorySeparatorChar);

            if (File.Exists(fullPath))
            {
                return new ErrorResponse($"File already exists at {path}. Use 'update' action to overwrite.");
            }

            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            bool isUxml = path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase);
            var validationWarnings = new List<string>();

            if (isUxml)
            {
                string xmlError = ValidateUxmlContent(contents, validationWarnings);
                if (xmlError != null)
                {
                    return new ErrorResponse($"UXML validation failed — file was NOT written. {xmlError}");
                }
                contents = EnsureEditorExtensionMode(contents);
            }

            File.WriteAllText(fullPath, contents, Utf8NoBom);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            if (isUxml)
            {
                ValidateUxmlPostImport(path, validationWarnings);
            }

            string ext = Path.GetExtension(path).TrimStart('.');
            if (validationWarnings.Count > 0)
            {
                return new SuccessResponse(
                    $"Created {ext} file at {path} with {validationWarnings.Count} warning(s)",
                    new { path, validationWarnings });
            }

            return new SuccessResponse($"Created {ext} file at {path}",
                new { path });
        }

        private static object ReadFile(JObject @params)
        {
            var p = new ToolParams(@params);
            string path = ValidatePath(p.Get("path"), out string pathError);
            if (pathError != null) return new ErrorResponse(pathError);

            string fullPath = Path.Combine(Application.dataPath,
                path.Substring("Assets/".Length));
            fullPath = fullPath.Replace('/', Path.DirectorySeparatorChar);

            if (!File.Exists(fullPath))
            {
                return new ErrorResponse($"File not found: {path}");
            }

            string contents = File.ReadAllText(fullPath, Encoding.UTF8);
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(contents));

            return new SuccessResponse($"Read {Path.GetExtension(path).TrimStart('.')} file at {path}",
                new
                {
                    path,
                    contents,
                    encodedContents = encoded,
                    contentsEncoded = true,
                    lengthBytes = Encoding.UTF8.GetByteCount(contents)
                });
        }

        private static object UpdateFile(JObject @params)
        {
            var p = new ToolParams(@params);
            string path = ValidatePath(p.Get("path"), out string pathError);
            if (pathError != null) return new ErrorResponse(pathError);

            string contents;
            try
            {
                contents = GetDecodedContents(p);
            }
            catch (ArgumentException ex)
            {
                return new ErrorResponse(ex.Message);
            }

            if (contents == null)
            {
                return new ErrorResponse("'contents' parameter is required for update.");
            }

            string fullPath = Path.Combine(Application.dataPath,
                path.Substring("Assets/".Length));
            fullPath = fullPath.Replace('/', Path.DirectorySeparatorChar);

            if (!File.Exists(fullPath))
            {
                return new ErrorResponse($"File not found: {path}. Use 'create' action for new files.");
            }

            bool isUxml = path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase);
            var validationWarnings = new List<string>();

            if (isUxml)
            {
                string xmlError = ValidateUxmlContent(contents, validationWarnings);
                if (xmlError != null)
                {
                    return new ErrorResponse($"UXML validation failed — file was NOT updated. {xmlError}");
                }
                contents = EnsureEditorExtensionMode(contents);
            }

            File.WriteAllText(fullPath, contents, Utf8NoBom);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            if (isUxml)
            {
                ValidateUxmlPostImport(path, validationWarnings);
            }

            string ext = Path.GetExtension(path).TrimStart('.');
            if (validationWarnings.Count > 0)
            {
                return new SuccessResponse(
                    $"Updated {ext} file at {path} with {validationWarnings.Count} warning(s)",
                    new { path, validationWarnings });
            }

            return new SuccessResponse($"Updated {ext} file at {path}",
                new { path });
        }

        private static object AttachUIDocument(JObject @params)
        {
            var p = new ToolParams(@params);

            var targetResult = p.GetRequired("target");
            var targetError = targetResult.GetOrError(out string target);
            if (targetError != null) return targetError;

            var sourceResult = p.GetRequired("source_asset");
            var sourceError = sourceResult.GetOrError(out string sourceAssetPath);
            if (sourceError != null) return sourceError;

            sourceAssetPath = AssetPathUtility.SanitizeAssetPath(sourceAssetPath);
            if (sourceAssetPath == null)
            {
                return new ErrorResponse("Invalid source_asset path.");
            }

            // Find the GameObject
            var goInstruction = new JObject { ["find"] = target };
            GameObject go = ObjectResolver.Resolve(goInstruction, typeof(GameObject)) as GameObject;
            if (go == null)
            {
                return new ErrorResponse($"Could not find target GameObject: {target}");
            }

            // Load the VisualTreeAsset
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(sourceAssetPath);
            if (vta == null)
            {
                return new ErrorResponse($"Could not load VisualTreeAsset at: {sourceAssetPath}");
            }

            // Load or create PanelSettings
            string panelSettingsPath = p.Get("panel_settings") ?? p.Get("panelSettings");
            PanelSettings panelSettings = null;

            if (!string.IsNullOrEmpty(panelSettingsPath))
            {
                panelSettingsPath = AssetPathUtility.SanitizeAssetPath(panelSettingsPath);
                if (panelSettingsPath != null)
                {
                    panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelSettingsPath);
                }
                if (panelSettings == null)
                {
                    return new ErrorResponse($"Could not load PanelSettings at: {panelSettingsPath}");
                }
            }
            else
            {
                // Find existing or create default PanelSettings
                string[] guids = AssetDatabase.FindAssets("t:PanelSettings");
                if (guids.Length > 0)
                {
                    string existingPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                    panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(existingPath);
                }

                if (panelSettings == null)
                {
                    panelSettings = CreateDefaultPanelSettings("Assets/UI/DefaultPanelSettings.asset");
                    if (panelSettings == null)
                    {
                        return new ErrorResponse("Failed to create default PanelSettings.");
                    }
                }
            }

            Undo.RecordObject(go, "Attach UIDocument");

            // Add or get UIDocument component
            var uiDoc = go.GetComponent<UIDocument>();
            if (uiDoc == null)
            {
                uiDoc = Undo.AddComponent<UIDocument>(go);
            }

            uiDoc.visualTreeAsset = vta;
            uiDoc.panelSettings = panelSettings;

            int sortOrder = p.GetInt("sort_order") ?? 0;
            uiDoc.sortingOrder = sortOrder;

            EditorUtility.SetDirty(go);

            return new SuccessResponse($"Attached UIDocument to {go.name}",
                new
                {
                    gameObject = go.name,
                    sourceAsset = sourceAssetPath,
                    panelSettings = AssetDatabase.GetAssetPath(panelSettings),
                    sortOrder
                });
        }

        private static object CreatePanelSettings(JObject @params)
        {
            var p = new ToolParams(@params);

            var pathResult = p.GetRequired("path");
            var pathError = pathResult.GetOrError(out string path);
            if (pathError != null) return pathError;

            path = AssetPathUtility.SanitizeAssetPath(path);
            if (path == null)
            {
                return new ErrorResponse("Invalid path: contains traversal sequences.");
            }

            if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                path += ".asset";
            }

            if (PanelSettingsPathExists(path))
            {
                return new ErrorResponse($"An asset or folder already exists at {path}");
            }

            var ps = CreateDefaultPanelSettings(path);
            if (ps == null)
            {
                return new ErrorResponse("Failed to create PanelSettings asset.");
            }

            // Apply any settings passed as a flat dict
            JToken settingsToken = p.GetRaw("settings");
            var changes = new List<string>();
            if (settingsToken is JObject settingsObj)
            {
                ApplyPanelSettingsProperties(ps, settingsObj, changes);
            }
            else
            {
                // Legacy: support top-level scale_mode / reference_resolution
                string scaleMode = p.Get("scale_mode");
                if (!string.IsNullOrEmpty(scaleMode))
                {
                    if (Enum.TryParse<PanelScaleMode>(scaleMode, true, out var mode))
                    {
                        ps.scaleMode = mode;
                        changes.Add("scaleMode");
                    }
                }

                JToken refResToken = p.GetRaw("reference_resolution");
                if (refResToken is JObject refRes)
                {
                    int w = refRes["width"]?.ToObject<int>() ?? 1920;
                    int h = refRes["height"]?.ToObject<int>() ?? 1080;
                    ps.referenceResolution = new Vector2Int(w, h);
                    changes.Add("referenceResolution");
                }
            }

            EditorUtility.SetDirty(ps);
            AssetDatabase.SaveAssets();

            return new SuccessResponse($"Created PanelSettings at {path}",
                new { path, applied = changes });
        }

        private static object UpdatePanelSettings(JObject @params)
        {
            var p = new ToolParams(@params);

            var pathResult = p.GetRequired("path");
            var pathError = pathResult.GetOrError(out string path);
            if (pathError != null) return pathError;

            path = AssetPathUtility.SanitizeAssetPath(path);
            if (path == null)
                return new ErrorResponse("Invalid path: contains traversal sequences.");

            if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                path += ".asset";

            var ps = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (ps == null)
                return new ErrorResponse($"No PanelSettings found at {path}");

            JToken settingsToken = p.GetRaw("settings");
            if (settingsToken is not JObject settingsObj || settingsObj.Count == 0)
                return new ErrorResponse("'settings' dict is required with at least one property to update.");

            var changes = new List<string>();
            ApplyPanelSettingsProperties(ps, settingsObj, changes);

            if (changes.Count == 0)
                return new ErrorResponse("No recognised properties were applied. Check the key names.");

            EditorUtility.SetDirty(ps);
            AssetDatabase.SaveAssets();

            return new SuccessResponse($"Updated PanelSettings at {path}",
                new { path, applied = changes });
        }

        private static PanelSettings CreateDefaultPanelSettings(string path)
        {
            // Also protect the implicit default-settings path used by attach_ui_document.
            if (PanelSettingsPathExists(path))
                throw new InvalidOperationException($"An asset or folder already exists at {path}");

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                EnsureFolderExists(dir);
            }

            var ps = ScriptableObject.CreateInstance<PanelSettings>();
            AssetDatabase.CreateAsset(ps, path);
            AssetDatabase.SaveAssets();
            return ps;
        }

        private static bool PanelSettingsPathExists(string path)
        {
            string fullPath = Path.Combine(Application.dataPath, path.Substring("Assets/".Length));
            return AssetDatabase.LoadMainAssetAtPath(path) != null ||
                   File.Exists(fullPath) || Directory.Exists(fullPath);
        }

        /// <summary>
        /// Generic, data-driven applicator for PanelSettings properties.
        /// Accepts a flat JObject where each key maps to a PanelSettings property.
        /// Recognised keys (case-insensitive matching via snake_case/camelCase):
        ///   scaleMode, referenceResolution, screenMatchMode, match,
        ///   referenceDpi, fallbackDpi, sortingOrder, targetDisplay,
        ///   clearColor, colorClearValue, clearDepthStencil,
        ///   themeStyleSheet, dynamicAtlasSettings.
        /// </summary>
        private static void ApplyPanelSettingsProperties(PanelSettings ps, JObject settings, List<string> changes)
        {
            foreach (var prop in settings)
            {
                string key = NormalizeKey(prop.Key);
                JToken val = prop.Value;

                switch (key)
                {
                    // ── Enum properties ─────────────────────────────────────
                    case "scalemode":
                        if (TryParseEnum<PanelScaleMode>(val, out var sm)) { ps.scaleMode = sm; changes.Add("scaleMode"); }
                        break;

                    case "screenmatchmode":
                        if (TryParseEnum<PanelScreenMatchMode>(val, out var smm)) { ps.screenMatchMode = smm; changes.Add("screenMatchMode"); }
                        break;

                    // ── Numeric properties ──────────────────────────────────
                    case "match":
                        if (TryFloat(val, out float matchVal)) { ps.match = Mathf.Clamp01(matchVal); changes.Add("match"); }
                        break;

                    case "referencedpi":
                        if (TryFloat(val, out float refDpi)) { ps.referenceDpi = refDpi; changes.Add("referenceDpi"); }
                        break;

                    case "fallbackdpi":
                        if (TryFloat(val, out float fbDpi)) { ps.fallbackDpi = fbDpi; changes.Add("fallbackDpi"); }
                        break;

                    case "sortingorder":
                        if (TryInt(val, out int so)) { ps.sortingOrder = so; changes.Add("sortingOrder"); }
                        break;

                    case "targetdisplay":
                        if (TryInt(val, out int td)) { ps.targetDisplay = td; changes.Add("targetDisplay"); }
                        break;

                    // ── Bool properties ──────────────────────────────────────
                    case "clearcolor":
                        ps.clearColor = ParamCoercion.CoerceBool(val, false);
                        changes.Add("clearColor");
                        break;

                    case "cleardepthstencil":
                        ps.clearDepthStencil = ParamCoercion.CoerceBool(val, false);
                        changes.Add("clearDepthStencil");
                        break;

                    // ── Composite properties ────────────────────────────────
                    case "referenceresolution":
                        if (val is JObject resObj)
                        {
                            int w = resObj["width"]?.ToObject<int>() ?? ps.referenceResolution.x;
                            int h = resObj["height"]?.ToObject<int>() ?? ps.referenceResolution.y;
                            ps.referenceResolution = new Vector2Int(w, h);
                            changes.Add("referenceResolution");
                        }
                        break;

                    case "colorclearvalue":
                        if (TryParseColor(val, out Color clr)) { ps.colorClearValue = clr; changes.Add("colorClearValue"); }
                        break;

                    case "dynamicatlassettings":
                        if (val is JObject daObj) { ApplyDynamicAtlasSettings(ps, daObj, changes); }
                        break;

                    // ── Asset reference properties ──────────────────────────
                    case "themestylesheet":
                    {
                        string tsPath = val?.ToString();
                        if (!string.IsNullOrEmpty(tsPath))
                        {
                            var ts = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(tsPath);
                            if (ts != null) { ps.themeStyleSheet = ts; changes.Add("themeStyleSheet"); }
                        }
                        break;
                    }

                    // unknown keys are silently ignored
                }
            }
        }

        private static void ApplyDynamicAtlasSettings(PanelSettings ps, JObject da, List<string> changes)
        {
            var daCopy = ps.dynamicAtlasSettings;

            if (da["minAtlasSize"] != null && TryInt(da["minAtlasSize"], out int minSize))
                daCopy.minAtlasSize = minSize;
            if (da["maxAtlasSize"] != null && TryInt(da["maxAtlasSize"], out int maxSize))
                daCopy.maxAtlasSize = maxSize;
            if (da["maxSubTextureSize"] != null && TryInt(da["maxSubTextureSize"], out int maxSub))
                daCopy.maxSubTextureSize = maxSub;
            if (da["activeFilters"] != null && TryParseEnum<DynamicAtlasFilters>(da["activeFilters"], out var af))
                daCopy.activeFilters = af;

            ps.dynamicAtlasSettings = daCopy;
            changes.Add("dynamicAtlasSettings");
        }

        // ── Tiny helpers to keep the switch compact ─────────────────────────

        private static string NormalizeKey(string key)
        {
            // Strip underscores and lowercase so "scale_mode", "scaleMode", "ScaleMode"
            // all match the same case label.
            return key.Replace("_", "").ToLowerInvariant();
        }

        private static bool TryParseEnum<T>(JToken token, out T result) where T : struct, Enum
        {
            result = default;
            string s = token?.ToString();
            return !string.IsNullOrEmpty(s) && Enum.TryParse(s, true, out result);
        }

        private static bool TryFloat(JToken token, out float result)
        {
            result = 0f;
            if (token == null) return false;
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                result = token.ToObject<float>();
                return true;
            }
            return float.TryParse(token.ToString(), out result);
        }

        private static bool TryInt(JToken token, out int result)
        {
            result = 0;
            if (token == null) return false;
            if (token.Type == JTokenType.Integer)
            {
                result = token.ToObject<int>();
                return true;
            }
            return int.TryParse(token.ToString(), out result);
        }

        private static bool TryParseColor(JToken token, out Color color)
        {
            color = Color.clear;
            if (token == null) return false;

            // Accept "#RRGGBB", "#RRGGBBAA", or {r,g,b,a} object
            if (token.Type == JTokenType.String)
            {
                return ColorUtility.TryParseHtmlString(token.ToString(), out color);
            }

            if (token is JObject cObj)
            {
                color = new Color(
                    cObj["r"]?.ToObject<float>() ?? 0f,
                    cObj["g"]?.ToObject<float>() ?? 0f,
                    cObj["b"]?.ToObject<float>() ?? 0f,
                    cObj["a"]?.ToObject<float>() ?? 1f
                );
                return true;
            }

            return false;
        }

        private static void EnsureFolderExists(string assetFolderPath)
        {
            if (AssetDatabase.IsValidFolder(assetFolderPath))
                return;

            string[] parts = assetFolderPath.Replace('\\', '/').Split('/');
            string current = parts[0]; // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }
                current = next;
            }
        }

        private static object GetVisualTree(JObject @params)
        {
            var p = new ToolParams(@params);

            var targetResult = p.GetRequired("target");
            var targetError = targetResult.GetOrError(out string target);
            if (targetError != null) return targetError;

            int maxDepth = p.GetInt("max_depth") ?? 10;

            var goInstruction = new JObject { ["find"] = target };
            GameObject go = ObjectResolver.Resolve(goInstruction, typeof(GameObject)) as GameObject;
            if (go == null)
            {
                return new ErrorResponse($"Could not find target GameObject: {target}");
            }

            var uiDoc = go.GetComponent<UIDocument>();
            if (uiDoc == null)
            {
                return new ErrorResponse($"GameObject {go.name} has no UIDocument component.");
            }

            var root = uiDoc.rootVisualElement;
            if (root == null)
            {
                return new SuccessResponse($"UIDocument on {go.name} has no visual tree (not yet built).",
                    new
                    {
                        gameObject = go.name,
                        sourceAsset = uiDoc.visualTreeAsset != null
                            ? AssetDatabase.GetAssetPath(uiDoc.visualTreeAsset)
                            : null,
                        tree = (object)null
                    });
            }

            var tree = SerializeVisualElement(root, 0, maxDepth);

            return new SuccessResponse($"Visual tree for UIDocument on {go.name}",
                new
                {
                    gameObject = go.name,
                    sourceAsset = uiDoc.visualTreeAsset != null
                        ? AssetDatabase.GetAssetPath(uiDoc.visualTreeAsset)
                        : null,
                    tree
                });
        }

        private static object SerializeVisualElement(VisualElement element, int depth, int maxDepth)
        {
            var result = new Dictionary<string, object>
            {
                ["type"] = element.GetType().Name,
                ["name"] = element.name ?? "",
                ["classes"] = new List<string>(element.GetClasses()),
            };

            // Include basic computed style info
            var style = new Dictionary<string, object>();
            var resolved = element.resolvedStyle;

            if (resolved.width > 0) style["width"] = resolved.width;
            if (resolved.height > 0) style["height"] = resolved.height;
            if (resolved.color != Color.clear)
                style["color"] = ColorToHex(resolved.color);
            if (resolved.backgroundColor != Color.clear)
                style["backgroundColor"] = ColorToHex(resolved.backgroundColor);
            if (resolved.fontSize > 0) style["fontSize"] = resolved.fontSize;

            if (style.Count > 0)
                result["resolvedStyle"] = style;

            // Include text content for labels/buttons
            if (element is TextElement textEl && !string.IsNullOrEmpty(textEl.text))
            {
                result["text"] = textEl.text;
            }

            // Serialize children
            if (depth < maxDepth && element.childCount > 0)
            {
                var children = new List<object>();
                foreach (var child in element.Children())
                {
                    children.Add(SerializeVisualElement(child, depth + 1, maxDepth));
                }
                result["children"] = children;
            }
            else if (element.childCount > 0)
            {
                result["childCount"] = element.childCount;
                result["truncated"] = true;
            }

            return result;
        }

        // ---- Render UI ----

        // Persistent RenderTextures keyed by PanelSettings instance ID so the panel
        // renders into them automatically every frame.
        private static readonly Dictionary<int, PanelCapture> s_panelRTs = new();

        // Own only textures created here. The panel and its original target belong to the user.
        private sealed class PanelCapture : IDisposable
        {
            public readonly PanelSettings Panel;
            public RenderTexture Texture;
            private RenderTexture originalTarget;
            private bool disposed;

            public PanelCapture(PanelSettings panel, int width, int height, bool persistent)
            {
                Panel = panel;
                originalTarget = panel.targetTexture;
                try
                {
                    Texture = new RenderTexture(width, height, 32, RenderTextureFormat.ARGB32);
                    Texture.name = $"MCP_UI_Render_{panel.GetInstanceIDCompat()}";
                    if (!Texture.Create())
                        throw new InvalidOperationException("Could not create the UI render texture.");
                    // The cache owns this native object. A temporary capture must not
                    // create/import a project asset, which can replace its native wrapper
                    // and release its GPU allocation during the same request.
                    Texture.hideFlags = HideFlags.HideAndDontSave;
                    var previous = RenderTexture.active;
                    try
                    {
                        RenderTexture.active = Texture;
                        GL.Clear(true, true, Color.clear);
                    }
                    finally { RenderTexture.active = previous; }
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void Attach()
            {
                if (Panel.targetTexture != Texture)
                    originalTarget = Panel.targetTexture;
                Panel.targetTexture = Texture;
            }

            public void Restore()
            {
                // Respect a user changing the target while a capture was pending.
                if (Panel != null && Panel.targetTexture == Texture)
                    Panel.targetTexture = originalTarget;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                Restore();
                if (Texture == null) return;
                if (RenderTexture.active == Texture) RenderTexture.active = null;
                string path = AssetDatabase.GetAssetPath(Texture);
                Texture.Release();
                if (!string.IsNullOrEmpty(path)) AssetDatabase.DeleteAsset(path);
                else UnityEngine.Object.DestroyImmediate(Texture);
                Texture = null;
            }
        }

        // Play-mode coroutine capture state.  Only one capture is in-flight at a
        // time; concurrent render_ui calls while a capture is pending are rejected
        // with an explicit error.
        private static Texture2D s_pendingCaptureTex;
        private static bool s_pendingCaptureDone;
        private static bool s_pendingCaptureStarted;
        private static int s_captureGeneration;

        private static object RenderUI(JObject @params)
        {
            var p = new ToolParams(@params);

            string target = p.Get("target");
            string uxmlPath = p.Get("path");
            int width = p.GetInt("width") ?? 1920;
            int height = p.GetInt("height") ?? 1080;
            bool includeImage = p.GetBool("include_image") || p.GetBool("includeImage");
            int maxResolution = p.GetInt("max_resolution") ?? p.GetInt("maxResolution") ?? 640;
            string fileName = p.Get("file_name") ?? p.Get("fileName");
            string outputFolderOverride = p.Get("output_folder") ?? p.Get("outputFolder");

            if (width <= 0 || height <= 0)
                return new ErrorResponse("'width' and 'height' must be positive.");

            string resolvedName = BuildRenderFileName(fileName);

            if (string.IsNullOrEmpty(target) && string.IsNullOrEmpty(uxmlPath))
            {
                return new ErrorResponse("Either 'target' (GameObject with UIDocument) or 'path' (UXML asset path) is required.");
            }

            string resolvedFolderSpec = ScreenshotPreferences.Resolve(outputFolderOverride);
            string resolvedFolderAbs;
            try
            {
                resolvedFolderAbs = ScreenshotUtility.ResolveFolderAbsolute(resolvedFolderSpec);
            }
            catch (InvalidOperationException ex)
            {
                return new ErrorResponse(ex.Message);
            }

            // ── Play-mode capture via ScreenCapture coroutine ──────────────────────
            // PanelSettings.targetTexture is read in the same frame it is assigned,
            // so the RT is always blank in a synchronous tool call.  In play mode we
            // dispatch a WaitForEndOfFrame coroutine that uses ScreenCapture, which
            // captures the fully-composited game view (including UI Toolkit overlays).
            // First call: queues the capture and returns "pending".
            // Second call: result is ready – save PNG and return data.
            if (Application.isPlaying)
            {
                // Build the output paths (used by both the pending and ready branches)
                Directory.CreateDirectory(resolvedFolderAbs);
                string playFullPath = Path.Combine(resolvedFolderAbs, resolvedName).Replace('\\', '/');
                playFullPath = EnsureUniqueFilePath(playFullPath);
                string playProjectRelPath = ScreenshotUtility.ToProjectRelativePath(playFullPath);

                // ── Case 1: capture is ready ──────────────────────────────────────
                if (s_pendingCaptureDone)
                {
                    var captureTex = s_pendingCaptureTex;
                    s_pendingCaptureDone = false;
                    s_pendingCaptureTex = null;

                    if (captureTex == null)
                        return new ErrorResponse("Play-mode screenshot capture failed. Call render_ui again to retry.");

                    int captureW = captureTex.width;
                    int captureH = captureTex.height;
                    byte[] capturePng;
                    try { capturePng = captureTex.EncodeToPNG(); }
                    finally { UnityEngine.Object.DestroyImmediate(captureTex); }

                    File.WriteAllBytes(playFullPath, capturePng);
                    if (ScreenshotUtility.IsUnderAssets(playProjectRelPath))
                        AssetDatabase.ImportAsset(playProjectRelPath, ImportAssetOptions.ForceSynchronousImport);

                    var playData = new Dictionary<string, object>
                    {
                        { "path", playProjectRelPath },
                        { "fullPath", playFullPath },
                        { "width", captureW },
                        { "height", captureH },
                        { "hasContent", true },
                    };

                    if (!string.IsNullOrEmpty(target)) playData["gameObject"] = target;
                    if (!string.IsNullOrEmpty(uxmlPath)) playData["sourceAsset"] = uxmlPath;

                    if (includeImage)
                    {
                        int targetMax = maxResolution > 0 ? maxResolution : 640;
                        Texture2D downscaled = null;
                        Texture2D fullTex = null;
                        try
                        {
                            fullTex = new Texture2D(captureW, captureH, TextureFormat.RGBA32, false);
                            fullTex.LoadImage(capturePng);
                            if (captureW > targetMax || captureH > targetMax)
                            {
                                downscaled = ScreenshotUtility.DownscaleTexture(fullTex, targetMax);
                                playData["imageBase64"] = Convert.ToBase64String(downscaled.EncodeToPNG());
                                playData["imageWidth"] = downscaled.width;
                                playData["imageHeight"] = downscaled.height;
                            }
                            else
                            {
                                playData["imageBase64"] = Convert.ToBase64String(capturePng);
                                playData["imageWidth"] = captureW;
                                playData["imageHeight"] = captureH;
                            }
                        }
                        finally
                        {
                            if (fullTex != null) UnityEngine.Object.DestroyImmediate(fullTex);
                            if (downscaled != null) UnityEngine.Object.DestroyImmediate(downscaled);
                        }
                    }

                    return new SuccessResponse($"UI render saved to '{playProjectRelPath}'.", playData);
                }

                // ── Case 2: start a new capture ───────────────────────────────────
                // Only one capture in flight at a time.  If one is already pending,
                // reject rather than silently overwriting the state.
                if (s_pendingCaptureStarted)
                {
                    return new ErrorResponse(
                        "Cannot capture: another capture is already in progress.",
                        new { retry_after_ms = 100, reason = "capture_in_progress" });
                }

                s_pendingCaptureDone = false;
                s_pendingCaptureTex = null;
                s_pendingCaptureStarted = true;
                int captureGeneration = ++s_captureGeneration;
                try
                {
                    ScreenshotCapturer.Begin(1, capturedTexture => CompletePlayModeCapture(captureGeneration, capturedTexture));
                }
                catch
                {
                    ResetPendingCapture();
                    throw;
                }

                return new SuccessResponse(
                    "Play-mode screenshot capture queued (WaitForEndOfFrame). Call render_ui again to retrieve the rendered image.",
                    new Dictionary<string, object>
                    {
                        { "pending", true },
                        { "gameObject", (object)target ?? uxmlPath },
                        { "note", "A screen capture was scheduled for the end of this frame. Call render_ui once more to get the result." }
                    });
            }
            // ── End play-mode branch ────────────────────────────────────────────────

            // Resolve UIDocument
            UIDocument uiDoc = null;
            GameObject tempGo = null;
            PanelSettings tempPs = null;
            PanelCapture capture = null;
            Texture2D tex = null;
            bool keepAttached = false;

            try
            {
                if (!string.IsNullOrEmpty(target))
                {
                    var goInstruction = new JObject { ["find"] = target };
                    GameObject go = ObjectResolver.Resolve(goInstruction, typeof(GameObject)) as GameObject;
                    if (go == null)
                        return new ErrorResponse($"Could not find target GameObject: {target}");

                    uiDoc = go.GetComponent<UIDocument>();
                    if (uiDoc == null)
                        return new ErrorResponse($"GameObject '{go.name}' has no UIDocument component.");
                }
                else
                {
                    uxmlPath = AssetPathUtility.SanitizeAssetPath(uxmlPath);
                    if (uxmlPath == null)
                        return new ErrorResponse("Invalid UXML path.");

                    var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
                    if (vta == null)
                        return new ErrorResponse($"Could not load VisualTreeAsset at: {uxmlPath}");

                    tempGo = new GameObject("__MCP_UI_Render_Temp__");
                    tempGo.hideFlags = HideFlags.HideAndDontSave;
                    tempGo.SetActive(false);
                    uiDoc = tempGo.AddComponent<UIDocument>();

                    string[] guids = AssetDatabase.FindAssets("t:PanelSettings", new[] { "Assets" });
                    PanelSettings ps = null;
                    if (guids.Length > 0)
                        ps = AssetDatabase.LoadAssetAtPath<PanelSettings>(AssetDatabase.GUIDToAssetPath(guids[0]));
                    // Render paths on a request-owned panel. Reusing a borrowed panel
                    // also renders unrelated documents attached to it, and redirects the
                    // user's output while this temporary document is alive.
                    tempPs = ps != null ? UnityEngine.Object.Instantiate(ps)
                        : ScriptableObject.CreateInstance<PanelSettings>();
                    tempPs.name = "__MCP_UI_Render_Panel__";
                    tempPs.hideFlags = HideFlags.HideAndDontSave;
                    uiDoc.panelSettings = tempPs;
                    uiDoc.visualTreeAsset = vta;
                    tempGo.SetActive(true);
                }

                if (uiDoc.panelSettings == null)
                    return new ErrorResponse("UIDocument has no PanelSettings assigned.");

                var panelSettings = uiDoc.panelSettings;
                int psId = panelSettings.GetInstanceIDCompat();

                PruneRenderTextures();
                bool rtJustAssigned;
                if (tempPs != null)
                {
                    capture = new PanelCapture(panelSettings, width, height, false);
                }
                else
                {
                    if (s_panelRTs.TryGetValue(psId, out capture) &&
                        (capture.Texture.width != width || capture.Texture.height != height))
                    {
                        capture.Dispose();
                        s_panelRTs.Remove(psId);
                        capture = null;
                    }
                    if (capture == null)
                    {
                        capture = new PanelCapture(panelSettings, width, height, true);
                        s_panelRTs[psId] = capture;
                    }
                }

                var rt = capture.Texture;
                rtJustAssigned = panelSettings.targetTexture != rt;
                capture.Attach();
                if (rtJustAssigned)
                {
                    uiDoc.rootVisualElement?.MarkDirtyRepaint();
                    UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                    Canvas.ForceUpdateCanvases();
                }

                RepaintCapturePanel(uiDoc);

                // Restore active even if allocation, ReadPixels or Apply throws.
                RenderTexture prevActive = RenderTexture.active;
                try
                {
                    RenderTexture.active = rt;
                    tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                    tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    tex.Apply();
                }
                finally
                {
                    RenderTexture.active = prevActive;
                }

                // Check if any content was rendered
                bool hasContent = false;
                var pixels = tex.GetPixels32();
                for (int i = 0; i < pixels.Length; i += Mathf.Max(1, pixels.Length / 100))
                {
                    if (pixels[i].a > 0) { hasContent = true; break; }
                }

                // Save to Screenshots folder
                Directory.CreateDirectory(resolvedFolderAbs);
                string fullPath = Path.Combine(resolvedFolderAbs, resolvedName).Replace('\\', '/');
                fullPath = EnsureUniqueFilePath(fullPath);

                byte[] png = tex.EncodeToPNG();
                File.WriteAllBytes(fullPath, png);

                string projectRelPath = ScreenshotUtility.ToProjectRelativePath(fullPath);
                if (ScreenshotUtility.IsUnderAssets(projectRelPath))
                    AssetDatabase.ImportAsset(projectRelPath, ImportAssetOptions.ForceSynchronousImport);

                var data = new Dictionary<string, object>
                {
                    { "path", projectRelPath },
                    { "fullPath", fullPath },
                    { "width", width },
                    { "height", height },
                    { "hasContent", hasContent },
                };

                if (tempPs == null && rtJustAssigned)
                    data["note"] = "RenderTexture was just assigned to PanelSettings. Call render_ui again to capture the rendered UI.";

                if (!string.IsNullOrEmpty(target))
                    data["gameObject"] = target;
                if (!string.IsNullOrEmpty(uxmlPath))
                    data["sourceAsset"] = uxmlPath;

                if (includeImage)
                {
                    int targetMax = maxResolution > 0 ? maxResolution : 640;
                    Texture2D downscaled = null;
                    try
                    {
                        if (width > targetMax || height > targetMax)
                        {
                            downscaled = ScreenshotUtility.DownscaleTexture(tex, targetMax);
                            data["imageBase64"] = Convert.ToBase64String(downscaled.EncodeToPNG());
                            data["imageWidth"] = downscaled.width;
                            data["imageHeight"] = downscaled.height;
                        }
                        else
                        {
                            data["imageBase64"] = Convert.ToBase64String(png);
                            data["imageWidth"] = width;
                            data["imageHeight"] = height;
                        }
                    }
                    finally
                    {
                        if (downscaled != null) UnityEngine.Object.DestroyImmediate(downscaled);
                    }
                }

                string msg = hasContent
                    ? $"UI rendered to '{projectRelPath}'."
                    : rtJustAssigned && tempPs == null
                        ? $"RenderTexture assigned to PanelSettings. Call render_ui again to capture the rendered content."
                        : $"UI render saved to '{projectRelPath}' (no visible content detected).";

                // A persistent document needs one editor frame to populate a newly attached RT.
                // Errors never leave the user's panel redirected.
                keepAttached = tempPs == null && rtJustAssigned;
                return new SuccessResponse(msg, data);
            }
            finally
            {
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                if (!keepAttached) capture?.Restore();
                if (tempGo != null) UnityEngine.Object.DestroyImmediate(tempGo);
                if (tempPs != null)
                {
                    capture?.Dispose();
                    UnityEngine.Object.DestroyImmediate(tempPs);
                }
            }
        }

        // UI Toolkit performs its normal repaint on a later frame. A request-scoped
        // UXML document cannot survive until then, so repaint only its own offscreen
        // runtime panel before readback. Version-check reflected engine entry points;
        // never return a stale/blank frame merely because the API is unavailable.
        private static void RepaintCapturePanel(UIDocument document)
        {
            object panel = document.rootVisualElement?.panel;
            if (panel == null) throw new InvalidOperationException("UI document has no runtime panel to render.");
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic;
            var utility = typeof(UIDocument).Assembly.GetType("UnityEngine.UIElements.UIElementsRuntimeUtility");
            var repaint = utility?.GetMethods(flags | System.Reflection.BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "RepaintPanel" && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType.IsInstanceOfType(panel));
            if (repaint == null)
                throw new NotSupportedException("This Unity version does not expose offscreen UI panel repaint.");
            var previousTarget = RenderTexture.active;
            var previousCamera = Camera.current;
            try
            {
                panel.GetType().GetMethod("Update", flags | System.Reflection.BindingFlags.Instance,
                    null, Type.EmptyTypes, null)?.Invoke(panel, null);
                repaint.Invoke(null, new[] { panel });
                // Unity 6 separates repaint preparation from rendering. Earlier editors
                // perform both in RepaintPanel and do not have this second entry point.
                var render = utility.GetMethods(flags | System.Reflection.BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "RenderPanel" && m.GetParameters().Length == 2
                        && m.GetParameters()[0].ParameterType.IsInstanceOfType(panel)
                        && m.GetParameters()[1].ParameterType == typeof(bool));
                render?.Invoke(null, new object[] { panel, true });
            }
            finally
            {
                Camera.SetupCurrent(previousCamera);
                RenderTexture.active = previousTarget;
            }
        }

        // ---- Link Stylesheet ----

        private static void CompletePlayModeCapture(int generation, Texture2D texture)
        {
            if (generation != s_captureGeneration || !s_pendingCaptureStarted)
            {
                if (texture != null && texture != s_pendingCaptureTex)
                    UnityEngine.Object.DestroyImmediate(texture);
                return;
            }
            s_pendingCaptureTex = texture;
            s_pendingCaptureDone = true;
            s_pendingCaptureStarted = false;
        }

        private static object LinkStylesheet(JObject @params)
        {
            var p = new ToolParams(@params);

            string uxmlPathRaw = p.Get("path");
            string uxmlPath = ValidatePath(uxmlPathRaw, out string pathError);
            if (pathError != null) return new ErrorResponse(pathError);

            // Validate the UXML path is actually a .uxml
            if (!uxmlPath.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase))
                return new ErrorResponse("'path' must point to a .uxml file.");

            string stylesheetPath = p.Get("stylesheet");
            if (string.IsNullOrEmpty(stylesheetPath))
                return new ErrorResponse("'stylesheet' parameter is required.");

            stylesheetPath = AssetPathUtility.SanitizeAssetPath(stylesheetPath);
            if (stylesheetPath == null)
                return new ErrorResponse("Invalid stylesheet path: contains traversal sequences.");

            if (!stylesheetPath.EndsWith(".uss", StringComparison.OrdinalIgnoreCase))
                return new ErrorResponse("'stylesheet' must point to a .uss file.");

            // Read the UXML file
            string fullPath = Path.Combine(Application.dataPath,
                uxmlPath.Substring("Assets/".Length)).Replace('/', Path.DirectorySeparatorChar);

            if (!File.Exists(fullPath))
                return new ErrorResponse($"UXML file not found: {uxmlPath}");

            string content = File.ReadAllText(fullPath, Encoding.UTF8);

            // Parse attributes so comments and quote style cannot masquerade as an existing link.
            bool alreadyLinked = false;
            using (var reader = CreateUxmlReader(content))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Style") continue;
                    string src = reader.GetAttribute("src");
                    if (src == stylesheetPath || src == "project://database/" + stylesheetPath)
                        alreadyLinked = true;
                }
            }
            if (alreadyLinked)
            {
                return new SuccessResponse($"Stylesheet already linked in '{uxmlPath}'.",
                    new { path = uxmlPath, stylesheet = stylesheetPath, alreadyLinked = true });
            }

            // Find the insertion point (after the opening <ui:UXML ...> or <UXML ...> tag)
            int insertIdx = FindUxmlBodyStart(content);
            if (insertIdx < 0)
                return new ErrorResponse("Could not find insertion point. Ensure UXML has a root <ui:UXML> or <UXML> element.");

            FindUxmlRootTag(content, out _, out _, out string rootName);
            int colon = rootName.IndexOf(':');
            string styleName = colon >= 0 ? rootName.Substring(0, colon + 1) + "Style" : "Style";
            string escapedPath = System.Security.SecurityElement.Escape(stylesheetPath);
            string styleTag = $"\n    <{styleName} src=\"project://database/{escapedPath}\" />";
            content = content.Insert(insertIdx, styleTag);

            File.WriteAllText(fullPath, content, Utf8NoBom);
            AssetDatabase.ImportAsset(uxmlPath, ImportAssetOptions.ForceUpdate);

            return new SuccessResponse($"Linked stylesheet '{stylesheetPath}' to '{uxmlPath}'.",
                new { path = uxmlPath, stylesheet = stylesheetPath });
        }

        // ---- Delete ----

        private static object DeleteFile(JObject @params)
        {
            var p = new ToolParams(@params);
            string path = ValidatePath(p.Get("path"), out string pathError);
            if (pathError != null) return new ErrorResponse(pathError);

            string fullPath = Path.Combine(Application.dataPath,
                path.Substring("Assets/".Length));
            fullPath = fullPath.Replace('/', Path.DirectorySeparatorChar);

            if (!File.Exists(fullPath))
            {
                return new ErrorResponse($"File not found: {path}");
            }

            try
            {
                bool success = AssetDatabase.DeleteAsset(path);
                if (!success)
                {
                    return new ErrorResponse($"Failed to delete file through AssetDatabase: '{path}'");
                }

                // Fallback: if file still exists after AssetDatabase.DeleteAsset
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }

                return new SuccessResponse($"Deleted {Path.GetExtension(path).TrimStart('.')} file at {path}",
                    new { path });
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Failed to delete '{path}': {e.Message}");
            }
        }

        // ---- List UI Assets ----

        private static object ListUIAssets(JObject @params)
        {
            var p = new ToolParams(@params);
            string scope = p.Get("path") ?? "Assets";
            string filterType = p.Get("filter_type") ?? p.Get("filterType");
            int pageSize = p.GetInt("page_size") ?? p.GetInt("pageSize") ?? 50;
            int pageNumber = p.GetInt("page_number") ?? p.GetInt("pageNumber") ?? 1;

            scope = AssetPathUtility.SanitizeAssetPath(scope);
            if (scope == null)
            {
                return new ErrorResponse("Invalid path: contains traversal sequences.");
            }

            string[] folderScope = AssetDatabase.IsValidFolder(scope)
                ? new[] { scope }
                : null;

            // Find UXML and USS assets based on filter
            var allAssets = new List<object>();

            bool includeUxml = string.IsNullOrEmpty(filterType) ||
                               filterType.Equals("uxml", StringComparison.OrdinalIgnoreCase) ||
                               filterType.Equals("VisualTreeAsset", StringComparison.OrdinalIgnoreCase);
            bool includeUss = string.IsNullOrEmpty(filterType) ||
                              filterType.Equals("uss", StringComparison.OrdinalIgnoreCase) ||
                              filterType.Equals("StyleSheet", StringComparison.OrdinalIgnoreCase);
            bool includePanelSettings = string.IsNullOrEmpty(filterType) ||
                                        filterType.Equals("PanelSettings", StringComparison.OrdinalIgnoreCase);

            if (includeUxml)
            {
                string[] guids = AssetDatabase.FindAssets("t:VisualTreeAsset", folderScope);
                foreach (string guid in guids)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        allAssets.Add(new Dictionary<string, object>
                        {
                            ["path"] = assetPath,
                            ["type"] = "uxml",
                            ["name"] = Path.GetFileName(assetPath),
                        });
                    }
                }
            }

            if (includeUss)
            {
                string[] guids = AssetDatabase.FindAssets("t:StyleSheet", folderScope);
                foreach (string guid in guids)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        allAssets.Add(new Dictionary<string, object>
                        {
                            ["path"] = assetPath,
                            ["type"] = "uss",
                            ["name"] = Path.GetFileName(assetPath),
                        });
                    }
                }
            }

            if (includePanelSettings)
            {
                string[] guids = AssetDatabase.FindAssets("t:PanelSettings", folderScope);
                foreach (string guid in guids)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        allAssets.Add(new Dictionary<string, object>
                        {
                            ["path"] = assetPath,
                            ["type"] = "PanelSettings",
                            ["name"] = Path.GetFileName(assetPath),
                        });
                    }
                }
            }

            int total = allAssets.Count;
            int startIndex = (pageNumber - 1) * pageSize;
            var paged = allAssets.Skip(startIndex).Take(pageSize).ToList();

            return new SuccessResponse(
                $"Found {total} UI asset(s). Returning page {pageNumber} ({paged.Count} items).",
                new
                {
                    total,
                    pageSize,
                    pageNumber,
                    assets = paged,
                });
        }

        // ---- Detach UIDocument ----

        private static object DetachUIDocument(JObject @params)
        {
            var p = new ToolParams(@params);

            var targetResult = p.GetRequired("target");
            var targetError = targetResult.GetOrError(out string target);
            if (targetError != null) return targetError;

            var goInstruction = new JObject { ["find"] = target };
            GameObject go = ObjectResolver.Resolve(goInstruction, typeof(GameObject)) as GameObject;
            if (go == null)
            {
                return new ErrorResponse($"Could not find target GameObject: {target}");
            }

            var uiDoc = go.GetComponent<UIDocument>();
            if (uiDoc == null)
            {
                return new ErrorResponse($"GameObject '{go.name}' has no UIDocument component.");
            }

            string sourceAsset = uiDoc.visualTreeAsset != null
                ? AssetDatabase.GetAssetPath(uiDoc.visualTreeAsset)
                : null;

            Undo.DestroyObjectImmediate(uiDoc);
            EditorUtility.SetDirty(go);

            return new SuccessResponse($"Removed UIDocument from {go.name}",
                new
                {
                    gameObject = go.name,
                    removedSourceAsset = sourceAsset,
                });
        }

        // ---- Modify Visual Element ----

        private static object ModifyVisualElement(JObject @params)
        {
            var p = new ToolParams(@params);

            var targetResult = p.GetRequired("target");
            var targetError = targetResult.GetOrError(out string target);
            if (targetError != null) return targetError;

            string elementName = p.Get("element_name") ?? p.Get("elementName");
            if (string.IsNullOrEmpty(elementName))
            {
                return new ErrorResponse("'element_name' parameter is required.");
            }

            var goInstruction = new JObject { ["find"] = target };
            GameObject go = ObjectResolver.Resolve(goInstruction, typeof(GameObject)) as GameObject;
            if (go == null)
            {
                return new ErrorResponse($"Could not find target GameObject: {target}");
            }

            var uiDoc = go.GetComponent<UIDocument>();
            if (uiDoc == null)
            {
                return new ErrorResponse($"GameObject '{go.name}' has no UIDocument component.");
            }

            var root = uiDoc.rootVisualElement;
            if (root == null)
            {
                return new ErrorResponse($"UIDocument on {go.name} has no visual tree (not yet built).");
            }

            // Find the target element by name
            var element = root.Q(elementName);
            if (element == null)
            {
                return new ErrorResponse($"Visual element with name '{elementName}' not found in the visual tree.");
            }

            var modifications = new List<string>();

            // Set text content (Label, Button, etc.)
            string text = p.Get("text");
            if (text != null && element is TextElement textEl)
            {
                textEl.text = text;
                modifications.Add($"text='{text}'");
            }
            else if (text != null)
            {
                return new ErrorResponse($"Element '{elementName}' ({element.GetType().Name}) does not support text content.");
            }

            // Add CSS classes
            JToken addClassesToken = p.GetRaw("add_classes") ?? p.GetRaw("addClasses");
            if (addClassesToken is JArray addArr)
            {
                foreach (var cls in addArr)
                {
                    string className = cls.ToString();
                    if (!element.ClassListContains(className))
                    {
                        element.AddToClassList(className);
                        modifications.Add($"+class '{className}'");
                    }
                }
            }

            // Remove CSS classes
            JToken removeClassesToken = p.GetRaw("remove_classes") ?? p.GetRaw("removeClasses");
            if (removeClassesToken is JArray removeArr)
            {
                foreach (var cls in removeArr)
                {
                    string className = cls.ToString();
                    if (element.ClassListContains(className))
                    {
                        element.RemoveFromClassList(className);
                        modifications.Add($"-class '{className}'");
                    }
                }
            }

            // Toggle CSS classes
            JToken toggleClassesToken = p.GetRaw("toggle_classes") ?? p.GetRaw("toggleClasses");
            if (toggleClassesToken is JArray toggleArr)
            {
                foreach (var cls in toggleArr)
                {
                    string className = cls.ToString();
                    element.ToggleInClassList(className);
                    modifications.Add($"~class '{className}'");
                }
            }

            // Set inline styles
            JToken styleToken = p.GetRaw("style") ?? p.GetRaw("inline_style") ?? p.GetRaw("inlineStyle");
            if (styleToken is JObject styleObj)
            {
                ApplyInlineStyles(element, styleObj, modifications);
            }

            // Set enabled/disabled
            bool? enabled = p.GetNullableBool("enabled");
            if (enabled.HasValue)
            {
                element.SetEnabled(enabled.Value);
                modifications.Add($"enabled={enabled.Value}");
            }

            // Set visibility
            string visibility = p.Get("visible");
            if (visibility != null)
            {
                bool isVisible = visibility.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                 visibility == "1";
                element.style.display = isVisible ? DisplayStyle.Flex : DisplayStyle.None;
                modifications.Add($"visible={isVisible}");
            }

            // Set tooltip
            string tooltip = p.Get("tooltip");
            if (tooltip != null)
            {
                element.tooltip = tooltip;
                modifications.Add($"tooltip='{tooltip}'");
            }

            // Filter out [skipped] entries so they don't count as real modifications
            var applied = modifications.Where(m => !m.StartsWith("[skipped]")).ToList();
            var skipped = modifications.Where(m => m.StartsWith("[skipped]")).ToList();

            if (applied.Count == 0)
            {
                string msg = skipped.Count > 0
                    ? $"No modifications applied. Skipped unsupported styles: {string.Join(", ", skipped)}"
                    : "No modifications specified. Provide at least one of: text, add_classes, remove_classes, toggle_classes, style, enabled, visible, tooltip.";
                return new ErrorResponse(msg);
            }

            var responseData = new Dictionary<string, object>
            {
                { "gameObject", go.name },
                { "elementName", elementName },
                { "elementType", element.GetType().Name },
                { "modifications", applied },
                { "currentClasses", new List<string>(element.GetClasses()) },
            };
            if (skipped.Count > 0)
                responseData["skipped"] = skipped;

            return new SuccessResponse(
                $"Modified element '{elementName}' on {go.name}: {string.Join(", ", applied)}",
                responseData);
        }

        private static void ApplyInlineStyles(VisualElement element, JObject styleObj, List<string> modifications)
        {
            foreach (var prop in styleObj)
            {
                string key = prop.Key;
                JToken val = prop.Value;

                switch (key.ToLowerInvariant())
                {
                    case "backgroundcolor":
                    case "background-color":
                        if (ColorUtility.TryParseHtmlString(val.ToString(), out Color bgColor))
                        {
                            element.style.backgroundColor = bgColor;
                            modifications.Add($"backgroundColor={val}");
                        }
                        break;

                    case "color":
                        if (ColorUtility.TryParseHtmlString(val.ToString(), out Color fgColor))
                        {
                            element.style.color = fgColor;
                            modifications.Add($"color={val}");
                        }
                        break;

                    case "fontsize":
                    case "font-size":
                        element.style.fontSize = val.ToObject<float>();
                        modifications.Add($"fontSize={val}");
                        break;

                    case "width":
                        element.style.width = val.ToObject<float>();
                        modifications.Add($"width={val}");
                        break;

                    case "height":
                        element.style.height = val.ToObject<float>();
                        modifications.Add($"height={val}");
                        break;

                    case "opacity":
                        element.style.opacity = val.ToObject<float>();
                        modifications.Add($"opacity={val}");
                        break;

                    case "display":
                        if (Enum.TryParse<DisplayStyle>(val.ToString(), true, out var display))
                        {
                            element.style.display = display;
                            modifications.Add($"display={val}");
                        }
                        break;

                    case "visibility":
                        if (Enum.TryParse<Visibility>(val.ToString(), true, out var vis))
                        {
                            element.style.visibility = vis;
                            modifications.Add($"visibility={val}");
                        }
                        break;

                    case "flexgrow":
                    case "flex-grow":
                        element.style.flexGrow = val.ToObject<float>();
                        modifications.Add($"flexGrow={val}");
                        break;

                    case "flexshrink":
                    case "flex-shrink":
                        element.style.flexShrink = val.ToObject<float>();
                        modifications.Add($"flexShrink={val}");
                        break;

                    case "marginleft":
                    case "margin-left":
                        element.style.marginLeft = val.ToObject<float>();
                        modifications.Add($"marginLeft={val}");
                        break;

                    case "marginright":
                    case "margin-right":
                        element.style.marginRight = val.ToObject<float>();
                        modifications.Add($"marginRight={val}");
                        break;

                    case "margintop":
                    case "margin-top":
                        element.style.marginTop = val.ToObject<float>();
                        modifications.Add($"marginTop={val}");
                        break;

                    case "marginbottom":
                    case "margin-bottom":
                        element.style.marginBottom = val.ToObject<float>();
                        modifications.Add($"marginBottom={val}");
                        break;

                    case "paddingleft":
                    case "padding-left":
                        element.style.paddingLeft = val.ToObject<float>();
                        modifications.Add($"paddingLeft={val}");
                        break;

                    case "paddingright":
                    case "padding-right":
                        element.style.paddingRight = val.ToObject<float>();
                        modifications.Add($"paddingRight={val}");
                        break;

                    case "paddingtop":
                    case "padding-top":
                        element.style.paddingTop = val.ToObject<float>();
                        modifications.Add($"paddingTop={val}");
                        break;

                    case "paddingbottom":
                    case "padding-bottom":
                        element.style.paddingBottom = val.ToObject<float>();
                        modifications.Add($"paddingBottom={val}");
                        break;

                    case "borderradius":
                    case "border-radius":
                        float radius = val.ToObject<float>();
                        element.style.borderTopLeftRadius = radius;
                        element.style.borderTopRightRadius = radius;
                        element.style.borderBottomLeftRadius = radius;
                        element.style.borderBottomRightRadius = radius;
                        modifications.Add($"borderRadius={val}");
                        break;

                    default:
                        modifications.Add($"[skipped] {key} (unsupported inline style)");
                        break;
                }
            }
        }

        private static bool? GetNullableBool(this ToolParams p, string key)
        {
            var raw = p.GetRaw(key);
            if (raw == null) return null;
            if (raw.Type == JTokenType.Boolean) return raw.ToObject<bool>();
            string s = raw.ToString();
            if (bool.TryParse(s, out bool result)) return result;
            return null;
        }

        /// <summary>
        /// Finds the index right after the closing '>' of the root UXML element opening tag.
        /// Returns -1 if not found or if the root tag is self-closing.
        /// </summary>
        private static int FindUxmlBodyStart(string content)
        {
            return FindUxmlRootTag(content, out _, out int end, out _) && content[end - 1] != '/'
                ? end + 1 : -1;
        }

        private static bool FindUxmlRootTag(string content, out int start, out int end, out string name)
        {
            start = end = -1;
            name = null;
            int cursor = 0;
            while ((start = content.IndexOf('<', cursor)) >= 0)
            {
                if (content.IndexOf("<!--", start, StringComparison.Ordinal) == start)
                {
                    int close = content.IndexOf("-->", start + 4, StringComparison.Ordinal);
                    if (close < 0) return false;
                    cursor = close + 3;
                    continue;
                }
                if (content.IndexOf("<?", start, StringComparison.Ordinal) == start)
                {
                    int close = content.IndexOf("?>", start + 2, StringComparison.Ordinal);
                    if (close < 0) return false;
                    cursor = close + 2;
                    continue;
                }

                int nameEnd = start + 1;
                while (nameEnd < content.Length && !char.IsWhiteSpace(content[nameEnd]) &&
                       content[nameEnd] != '/' && content[nameEnd] != '>') ++nameEnd;
                name = content.Substring(start + 1, nameEnd - start - 1);
                if (name.Substring(name.LastIndexOf(':') + 1) != "UXML") return false;

                char quote = '\0';
                for (int i = nameEnd; i < content.Length; ++i)
                {
                    char ch = content[i];
                    if (quote != '\0') { if (ch == quote) quote = '\0'; }
                    else if (ch == '\'' || ch == '"') quote = ch;
                    else if (ch == '>') { end = i; return true; }
                }
                return false;
            }
            return false;
        }

        // ---- Helpers ----

        private static string BuildRenderFileName(string fileName)
        {
            string name = string.IsNullOrWhiteSpace(fileName)
                ? $"ui-render-{DateTime.Now:yyyyMMdd-HHmmss}.png"
                : fileName.Trim();
            var invalid = Path.GetInvalidFileNameChars();
            name = new string(name.Select(ch => invalid.Contains(ch) || ch == '/' || ch == '\\' ? '_' : ch).ToArray());
            return name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? name : name + ".png";
        }

        private static string EnsureUniqueFilePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            int counter = 1;
            string candidate;
            do
            {
                candidate = Path.Combine(dir, $"{baseName}-{counter}{ext}").Replace('\\', '/');
                counter++;
            } while (File.Exists(candidate));
            return candidate;
        }

        private static string ColorToHex(Color c)
        {
            return $"#{ColorUtility.ToHtmlStringRGBA(c)}";
        }

        private static string GetDecodedContents(ToolParams p)
        {
            bool isEncoded = p.GetBool("contents_encoded") || p.GetBool("contentsEncoded");

            if (isEncoded)
            {
                string encoded = p.Get("encoded_contents") ?? p.Get("encodedContents");
                if (!string.IsNullOrEmpty(encoded))
                {
                    try
                    {
                        return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                    }
                    catch (FormatException ex)
                    {
                        throw new ArgumentException(
                            "Parameter 'encodedContents' must be valid base64 when 'contentsEncoded' is true.",
                            ex);
                    }
                }
            }

            return p.Get("contents");
        }

        /// <summary>
        /// Validates UXML content before writing to disk.
        /// Returns null if valid, or an error message if malformed.
        /// Populates warnings list with non-fatal issues.
        /// Uses XmlParserContext to pre-declare common UXML namespace prefixes
        /// (ui, uie, engine, editor) since Unity's parser is more lenient than System.Xml.
        /// </summary>

        /// <summary>
        /// Ensures the root UXML element has editor-extension-mode attribute.
        /// UI Builder requires this to open the file. Injects "False" if missing.
        /// </summary>
        private static string EnsureEditorExtensionMode(string contents)
        {
            if (!FindUxmlRootTag(contents, out _, out int closeTag, out _))
                return contents;

            using (var reader = CreateUxmlReader(contents))
            {
                reader.MoveToContent();
                if (reader.GetAttribute("editor-extension-mode") != null)
                    return contents;
            }

            int insertPos = contents[closeTag - 1] == '/' ? closeTag - 1 : closeTag;
            return contents.Insert(insertPos, " editor-extension-mode=\"False\"");
        }

        private static XmlReader CreateUxmlReader(string contents)
        {
            var nt = new NameTable();
            var nsMgr = new XmlNamespaceManager(nt);
            nsMgr.AddNamespace("ui", "UnityEngine.UIElements");
            nsMgr.AddNamespace("uie", "UnityEditor.UIElements");
            nsMgr.AddNamespace("engine", "UnityEngine.UIElements");
            nsMgr.AddNamespace("editor", "UnityEditor.UIElements");
            var ctx = new XmlParserContext(nt, nsMgr, null, XmlSpace.Default);
            return XmlReader.Create(new StringReader(contents), null, ctx);
        }

        private static string ValidateUxmlContent(string contents, List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(contents))
                return "UXML content is empty.";

            string rootLocalName = null;
            try
            {
                using (var reader = CreateUxmlReader(contents))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element && rootLocalName == null)
                            rootLocalName = reader.LocalName;
                    }
                }
            }
            catch (XmlException ex)
            {
                return $"Malformed XML at line {ex.LineNumber}, position {ex.LinePosition}: {ex.Message}";
            }

            if (rootLocalName == null)
                return "UXML content has no root element.";

            if (rootLocalName != "UXML")
                warnings.Add($"Root element is <{rootLocalName}>, expected <UXML> or <ui:UXML>.");

            if (!contents.Contains("UnityEngine.UIElements"))
            {
                warnings.Add("Missing namespace declaration xmlns:ui=\"UnityEngine.UIElements\". " +
                              "UI Builder may fail to open this file.");
            }

            return null;
        }

        /// <summary>
        /// Validates a UXML asset after import by attempting to load it as a VisualTreeAsset.
        /// </summary>
        private static void ValidateUxmlPostImport(string assetPath, List<string> warnings)
        {
            var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(assetPath);
            if (asset == null)
            {
                warnings.Add("Unity failed to parse the UXML file. " +
                              "The file was written but UI Builder will not be able to open it. " +
                              "Check the console for details.");
            }
        }
    }
}
