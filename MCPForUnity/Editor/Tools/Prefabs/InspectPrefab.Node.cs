using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Editor.Tools.Prefabs
{
    public static partial class InspectPrefab
    {
        private const int ArrayPreview = 4;
        private const int ArrayElementsExpanded = 3;
        private const int MaxFieldDepth = 3;

        /// <summary>
        /// mode=node: the components on one object with their serialized fields. Unless all_fields is set,
        /// fields equal to a freshly added component's value are hidden.
        /// </summary>
        private static string RunNode(ToolParams p, int maxChars)
        {
            using (Target target = Target.Open(p.Get("prefab_path"), loadContents: false))
            using (var defaults = new DefaultComponents())
            {
                Transform root = target.Root.transform;
                Transform t = ResolvePath(root, p.Get("path"));
                bool allFields = p.GetBool("all_fields");
                var types = new TypeNamer();
                types.AddHierarchy(root);
                var refs = new RefPrinter(root, types);

                Component[] all = t.GetComponents<Component>();
                var comps = all.Where(c => c != null).ToList();
                int missing = all.Length - comps.Count;

                string componentArg = p.Get("component");
                if (!string.IsNullOrWhiteSpace(componentArg))
                    comps = new List<Component> { FindComponent(comps, componentArg.Trim(), types, ShowPath(RelPath(t, root), root)) };

                string header = NodeHeader(target, t, root, missing);
                var output = new TextOut(maxChars);
                int rendered = 0;
                foreach (Component c in comps)
                {
                    var lines = ComponentLines(c, defaults, allFields, refs, out string title);
                    if (!output.Line("  " + title)) break;
                    bool complete = true;
                    foreach (string line in lines)
                    {
                        if (!output.Line(line)) { complete = false; break; }
                    }
                    if (!complete) break;
                    rendered++;
                }

                string note = null;
                if (output.Full)
                {
                    var rest = comps.Skip(rendered).Select(c => types.Name(c.GetType())).ToList();
                    note = $"... truncated at {rest.FirstOrDefault()}; not shown: {string.Join(", ", rest.Take(8))}{(rest.Count > 8 ? ", ..." : "")}. Use component=\"{rest.FirstOrDefault()}\" or a larger max_chars.";
                }
                return output.Finish(header, note);
            }
        }

        private static string NodeHeader(Target target, Transform t, Transform root, int missingScripts)
        {
            GameObject go = t.gameObject;
            var sb = new StringBuilder();
            sb.Append(target.Label).Append(':').Append(ShowPath(RelPath(t, root), root));
            if (!go.activeSelf) sb.Append(" (-)");
            if (t != root && PrefabUtility.IsAnyPrefabInstanceRoot(go))
            {
                string asset = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                sb.Append(string.IsNullOrEmpty(asset) ? " @MISSING-PREFAB" : " @" + Path.GetFileName(asset));
            }
            if (!go.CompareTag("Untagged")) sb.Append(" tag=").Append(go.tag);
            if (go.layer != 0) sb.Append(" layer=").Append(LayerMask.LayerToName(go.layer) is string ln && ln.Length > 0 ? ln : go.layer.ToString());
            if (go.isStatic) sb.Append(" static");
            if (t.childCount > 0) sb.Append(" children=").Append(t.childCount);
            if (missingScripts > 0) sb.Append(missingScripts == 1 ? " !missing script" : $" !missing scripts x{missingScripts}");
            return sb.ToString();
        }

        /// <summary>Matches "Door", "Door#2" (second Door) or a full type name.</summary>
        private static Component FindComponent(List<Component> comps, string query, TypeNamer types, string where)
        {
            var (name, index) = ParseSegment(query);
            var matches = comps.Where(c =>
                string.Equals(types.Name(c.GetType()), name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.GetType().Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.GetType().FullName, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count >= index) return matches[index - 1];

            var names = comps.Select(c => types.Name(c.GetType())).ToList();
            throw new InspectError($"No component '{query}' on {where}. Components: {string.Join(", ", names)}.");
        }

        /// <summary>Title and field lines for one component (fields indented 4, nested fields deeper).</summary>
        private static List<string> ComponentLines(Component c, DefaultComponents defaults, bool allFields, RefPrinter refs, out string title)
        {
            var lines = new List<string>();
            using var so = new SerializedObject(c);
            SerializedObject def = allFields ? null : defaults.For(c.GetType());

            title = refs.Types.Name(c.GetType());
            SerializedProperty enabled = so.FindProperty("m_Enabled");
            if (enabled != null && enabled.propertyType == SerializedPropertyType.Boolean && !enabled.boolValue) title += " (disabled)";
            if (!allFields && def == null) title += " (no default to compare; all fields)";

            SerializedProperty it = so.GetIterator();
            if (it.NextVisible(true))
            {
                do
                {
                    if (it.propertyPath == "m_Script" || it.propertyPath == "m_Enabled") continue;
                    SerializedProperty dp = def?.FindProperty(it.propertyPath);
                    if (SameAsDefault(it, dp)) continue;
                    RenderProperty(it.Copy(), dp, 4, MaxFieldDepth, lines, refs, null);
                }
                while (it.NextVisible(false));
            }

            if (lines.Count == 0 && !allFields) title += " (all default)";
            return lines;
        }

        /// <summary>Renders one property; <paramref name="dp"/> is the default-valued counterpart, if any.</summary>
        private static void RenderProperty(SerializedProperty p, SerializedProperty dp, int indent, int depthLeft,
            List<string> lines, RefPrinter refs, string label)
        {
            string pad = new string(' ', indent);
            string name = label ?? FieldName(p.name);

            if (IsUnityEvent(p))
            {
                var listeners = Listeners(p, refs);
                lines.Add($"{pad}{name}: {listeners.Count} listener{(listeners.Count == 1 ? "" : "s")}");
                lines.AddRange(listeners.Select(l => pad + "  " + l));
                return;
            }

            if (p.propertyType == SerializedPropertyType.ObjectReference)
            {
                lines.Add($"{pad}{name} -> {refs.Describe(p)}");
                return;
            }

            if (p.isArray && p.propertyType == SerializedPropertyType.Generic)
            {
                RenderArray(p, dp, pad, name, indent, depthLeft, lines, refs);
                return;
            }

            if (p.propertyType == SerializedPropertyType.ManagedReference)
            {
                string typeName = p.managedReferenceFullTypename;
                if (string.IsNullOrEmpty(typeName))
                {
                    lines.Add($"{pad}{name}: null");
                    return;
                }
                if (depthLeft <= 0)
                {
                    lines.Add($"{pad}{name}: {{...}}");
                    return;
                }
                string shortType = typeName.Substring(typeName.LastIndexOfAny(new[] { ' ', '.', '/' }) + 1);
                lines.Add($"{pad}{name}: <{shortType}>");
                RenderChildren(p, null, indent + 2, depthLeft - 1, lines, refs);
                return;
            }

            if (p.propertyType == SerializedPropertyType.Generic && p.hasVisibleChildren)
            {
                if (depthLeft <= 0)
                {
                    lines.Add($"{pad}{name}: {{...}}");
                    return;
                }
                var children = new List<string>();
                RenderChildren(p, dp, indent + 2, depthLeft - 1, children, refs);
                if (children.Count == 0 && dp != null) return; // differed only by float noise
                lines.Add($"{pad}{name}:");
                lines.AddRange(children);
                return;
            }

            lines.Add($"{pad}{name}: {FormatValue(p, refs)}");
        }

        /// <summary>
        /// Equal to the default: identical data, or a leaf value that prints the same (float noise such as
        /// -0 vs 0 or a 1e-8 rotation is not worth a line). References are compared exactly.
        /// </summary>
        private static bool SameAsDefault(SerializedProperty p, SerializedProperty dp)
        {
            if (dp == null) return false;
            if (SerializedProperty.DataEquals(p, dp)) return true;
            if (p.propertyType != dp.propertyType || p.isArray || p.hasVisibleChildren) return false;
            // Compact strings, curves and gradients are lossy previews, not equality proofs.
            // Only floating-point leaves may use display precision to suppress harmless noise.
            return p.propertyType == SerializedPropertyType.Float && FormatValue(p, null) == FormatValue(dp, null);
        }

        private static void RenderChildren(SerializedProperty p, SerializedProperty dp, int indent, int depthLeft,
            List<string> lines, RefPrinter refs)
        {
            SerializedProperty child = p.Copy();
            SerializedProperty end = p.GetEndProperty();
            bool enter = true;
            while (child.NextVisible(enter) && !SerializedProperty.EqualContents(child, end))
            {
                enter = false;
                SerializedProperty dc = dp?.FindPropertyRelative(child.name);
                if (SameAsDefault(child, dc)) continue;
                RenderProperty(child.Copy(), dc, indent, depthLeft, lines, refs, null);
            }
        }

        private static void RenderArray(SerializedProperty p, SerializedProperty dp, string pad, string name, int indent,
            int depthLeft, List<string> lines, RefPrinter refs)
        {
            int n = p.arraySize;
            if (n == 0)
            {
                lines.Add($"{pad}{name}: []");
                return;
            }

            SerializedProperty first = p.GetArrayElementAtIndex(0);
            bool simple = first.propertyType != SerializedPropertyType.Generic &&
                          first.propertyType != SerializedPropertyType.ManagedReference;
            if (simple)
            {
                var items = new List<string>();
                for (int i = 0; i < Math.Min(n, ArrayPreview); i++) items.Add(FormatValue(p.GetArrayElementAtIndex(i), refs));
                string arrow = first.propertyType == SerializedPropertyType.ObjectReference ? " ->" : "";
                lines.Add($"{pad}{name}: [{n}]{arrow} {string.Join(", ", items)}{(n > ArrayPreview ? ", ..." : "")}");
                return;
            }

            lines.Add($"{pad}{name}: [{n}]");
            if (depthLeft <= 0) return;
            for (int i = 0; i < Math.Min(n, ArrayElementsExpanded); i++)
            {
                SerializedProperty element = p.GetArrayElementAtIndex(i);
                SerializedProperty defElement = dp != null && dp.isArray && i < dp.arraySize ? dp.GetArrayElementAtIndex(i) : null;
                RenderProperty(element, defElement, indent + 2, depthLeft - 1, lines, refs, $"[{i}]");
            }
            if (n > ArrayElementsExpanded) lines.Add($"{pad}  ... +{n - ArrayElementsExpanded} more");
        }

        /// <summary>
        /// Freshly added components, used as the "default" to diff against. Each type gets its own inactive,
        /// hidden GameObject in a preview scene, so Awake never runs, RequireComponent/DisallowMultipleComponent
        /// cannot clash, and the open scenes are never touched. Types that cannot be added return null.
        /// </summary>
        internal sealed class DefaultComponents : IDisposable
        {
            private readonly Dictionary<Type, SerializedObject> _cache = new Dictionary<Type, SerializedObject>();
            private Scene _scene;
            private bool _open;

            public SerializedObject For(Type type)
            {
                if (_cache.TryGetValue(type, out SerializedObject so)) return so;
                so = Create(type);
                _cache[type] = so;
                return so;
            }

            private SerializedObject Create(Type type)
            {
                // AddComponent invokes editor Reset/validation hooks even on inactive objects.
                // Inspecting a user script must never execute its arbitrary initialization code.
                if (typeof(MonoBehaviour).IsAssignableFrom(type) || type.IsAbstract ||
                    type.IsGenericTypeDefinition || !typeof(Component).IsAssignableFrom(type)) return null;
                try
                {
                    if (!_open)
                    {
                        _scene = EditorSceneManager.NewPreviewScene();
                        _open = true;
                    }
                    GameObject go = EditorUtility.CreateGameObjectWithHideFlags("__inspect_prefab_default", HideFlags.HideAndDontSave);
                    SceneManager.MoveGameObjectToScene(go, _scene);
                    go.SetActive(false);
                    // GetComponent returns Unity's fake-null for a missing component, so compare with ==, not ??.
                    Component c = type == typeof(Transform) ? go.transform : go.GetComponent(type);
                    if (c == null) c = go.AddComponent(type);
                    return c != null ? new SerializedObject(c) : null;
                }
                catch (Exception e)
                {
                    McpLog.Warn($"[InspectPrefab] No default instance for {type.Name}: {e.Message}");
                    return null;
                }
            }

            public void Dispose()
            {
                foreach (SerializedObject so in _cache.Values) so?.Dispose();
                _cache.Clear();
                if (_open)
                {
                    EditorSceneManager.ClosePreviewScene(_scene);
                    _open = false;
                }
            }
        }
    }
}
