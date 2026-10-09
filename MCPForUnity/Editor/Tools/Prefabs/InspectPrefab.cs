using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MCPForUnity.Editor.Tools.Prefabs
{
    /// <summary>
    /// Read-only, token-efficient prefab inspector. Every mode renders plain indented text under a
    /// character budget. Prefab assets are read in place (LoadAssetAtPath) or, for overrides, through
    /// LoadPrefabContents/UnloadPrefabContents; nothing opens a Prefab Stage, marks anything dirty or saves.
    /// Objects are identified by hierarchy paths relative to the inspected root ("" = the root itself);
    /// duplicate sibling names get a "#n" suffix (Wheel, Wheel#2).
    /// </summary>
    [McpForUnityTool("inspect_prefab", AutoRegister = false)]
    public static partial class InspectPrefab
    {
        internal const int DefaultMaxChars = 6000;
        private const string Modes = "tree, node, refs, overrides, usages, problems";

        public static object HandleCommand(JObject @params)
        {
            if (@params == null) return new ErrorResponse("Parameters cannot be null.");
            var p = new ToolParams(@params);
            string mode = (p.Get("mode") ?? "tree").Trim().ToLowerInvariant();
            int maxChars = Math.Max(500, Math.Min(p.GetInt("max_chars") ?? DefaultMaxChars, 100000));

            try
            {
                string text;
                switch (mode)
                {
                    case "tree": text = RunTree(p, maxChars); break;
                    case "node": text = RunNode(p, maxChars); break;
                    case "refs": text = RunRefs(p, maxChars); break;
                    case "overrides": text = RunOverrides(p, maxChars); break;
                    case "usages": text = RunUsages(p, maxChars); break;
                    case "problems": text = RunProblems(p, maxChars); break;
                    default: return new ErrorResponse($"Unknown mode '{mode}'. Valid modes: {Modes}.");
                }
                text = LimitText(text, maxChars);
                return new SuccessResponse($"inspect_prefab {mode}", new { text });
            }
            catch (InspectError e)
            {
                return new ErrorResponse(e.Message);
            }
            catch (Exception e)
            {
                McpLog.Error($"[InspectPrefab] {mode} failed: {e}");
                return new ErrorResponse($"inspect_prefab {mode} failed: {e.Message}");
            }
        }

        /// <summary>A user-facing, one-line failure (bad path, bad mode, missing object).</summary>
        internal sealed class InspectError : Exception
        {
            public InspectError(string message) : base(message) { }
        }

        #region Target

        /// <summary>The GameObject being inspected: a prefab asset root, loaded prefab contents, or a scene object.</summary>
        internal sealed class Target : IDisposable
        {
            public GameObject Root;
            public string Label;      // "E36_CAR.prefab" or "scene:Car"
            public string AssetPath;  // null for scene objects
            private GameObject _contents;

            public bool IsScene => AssetPath == null;

            public void Dispose()
            {
                if (_contents != null)
                {
                    PrefabUtility.UnloadPrefabContents(_contents);
                    _contents = null;
                }
            }

            /// <param name="input">Assets/...prefab, or scene:Root/Child for a scene object.</param>
            /// <param name="loadContents">Load an editable copy (LoadPrefabContents) instead of reading the asset.</param>
            public static Target Open(string input, bool loadContents)
            {
                if (string.IsNullOrWhiteSpace(input))
                    throw new InspectError("'prefab_path' is required (Assets/...prefab or scene:Path/To/Object).");
                input = input.Trim();
                if (input.StartsWith("scene:", StringComparison.OrdinalIgnoreCase))
                    return OpenScene(input.Substring("scene:".Length));

                string path = ResolvePrefabPath(input);
                if (!loadContents)
                {
                    return new Target
                    {
                        Root = AssetDatabase.LoadAssetAtPath<GameObject>(path),
                        Label = Path.GetFileName(path),
                        AssetPath = path,
                    };
                }

                GameObject contents = PrefabUtility.LoadPrefabContents(path);
                if (contents == null) throw new InspectError($"Could not load prefab contents of '{path}'.");
                return new Target { Root = contents, Label = Path.GetFileName(path), AssetPath = path, _contents = contents };
            }

            private static Target OpenScene(string objectPath)
            {
                objectPath = objectPath.Trim().Trim('/');
                string[] segments = objectPath.Split('/');
                var roots = new List<GameObject>();
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene scene = SceneManager.GetSceneAt(i);
                    if (scene.isLoaded) roots.AddRange(scene.GetRootGameObjects());
                }

                var rootSegment = ParseSegment(segments[0]);
                IEnumerable<GameObject> candidates = roots.Where(r => r.name == rootSegment.name);
                if (rootSegment.name != segments[0]) candidates = candidates.Skip(rootSegment.index - 1).Take(1);
                foreach (GameObject go in candidates)
                {
                    Transform found = segments.Length == 1 ? go.transform : Walk(go.transform, segments.Skip(1).ToArray());
                    if (found != null)
                        return new Target { Root = found.gameObject, Label = "scene:" + objectPath };
                }

                var names = roots.Select(r => r.name).Distinct().ToList();
                throw new InspectError($"No scene object '{objectPath}' in the open scenes." + Suggest(Nearest(segments[0], names)));
            }
        }

        /// <summary>Validates a prefab asset path, throwing with up to 5 similar prefab paths when it is wrong.</summary>
        internal static string ResolvePrefabPath(string input)
        {
            string path = AssetPathUtility.SanitizeAssetPath(input);
            if (string.IsNullOrEmpty(path)) throw new InspectError($"Invalid path '{input}'.");
            if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) &&
                AssetDatabase.LoadAssetAtPath<GameObject>(path + ".prefab") != null)
            {
                path += ".prefab";
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) return path;

            string stem = Path.GetFileNameWithoutExtension(path);
            var candidates = FindPrefabPaths(stem);
            string dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && AssetDatabase.IsValidFolder(dir))
                candidates.AddRange(AssetDatabase.FindAssets("t:Prefab", new[] { dir }).Select(AssetDatabase.GUIDToAssetPath).Take(200));
            if (candidates.Count == 0 && stem.Length > 3) candidates = FindPrefabPaths(stem.Substring(0, 3));
            throw new InspectError($"No prefab at '{path}'." + Suggest(Nearest(path, candidates)));
        }

        private static List<string> FindPrefabPaths(string nameFilter)
        {
            return AssetDatabase.FindAssets($"{nameFilter} t:Prefab")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                .Take(200)
                .ToList();
        }

        #endregion

        #region Paths

        /// <summary>Path of <paramref name="t"/> relative to <paramref name="root"/>; "" for the root itself.</summary>
        internal static string RelPath(Transform t, Transform root)
        {
            if (t == root) return "";
            var parts = new List<string>();
            for (Transform cur = t; cur != null && cur != root; cur = cur.parent) parts.Add(Segment(cur));
            parts.Reverse();
            return string.Join("/", parts);
        }

        /// <summary>Name of <paramref name="t"/> within its parent, with "#n" for the n-th sibling of the same name.</summary>
        internal static string Segment(Transform t)
        {
            Transform parent = t.parent;
            if (parent == null) return t.name;
            int dup = 0;
            int index = t.GetSiblingIndex();
            for (int i = 0; i < index; i++)
            {
                if (parent.GetChild(i).name == t.name) dup++;
            }
            return dup == 0 ? t.name : $"{t.name}#{dup + 1}";
        }

        /// <summary>A relative path for display: the root is shown by its name.</summary>
        internal static string ShowPath(string rel, Transform root) => rel.Length == 0 ? root.name : rel;

        /// <summary>Resolves an object path (relative, or prefixed with the root name) or throws with nearby paths.</summary>
        internal static Transform ResolvePath(Transform root, string path)
        {
            path = (path ?? "").Trim().Trim('/');
            if (path.Length == 0 || path == root.name) return root;

            Transform found = Walk(root, path.Split('/'));
            if (found == null && path.StartsWith(root.name + "/", StringComparison.Ordinal))
                found = Walk(root, path.Substring(root.name.Length + 1).Split('/'));
            if (found != null) return found;

            var all = new List<string>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t != root) all.Add(RelPath(t, root));
            }
            throw new InspectError($"No object '{path}' in {root.name}." + Suggest(Nearest(path, all)));
        }

        private static Transform Walk(Transform from, string[] segments)
        {
            Transform cur = from;
            foreach (string raw in segments)
            {
                if (raw.Length == 0) continue;
                var (name, index) = ParseSegment(raw);
                Transform next = null;
                int seen = 0;
                for (int i = 0; i < cur.childCount; i++)
                {
                    Transform child = cur.GetChild(i);
                    if (child.name != name) continue;
                    if (++seen == index) { next = child; break; }
                }
                if (next == null) return null;
                cur = next;
            }
            return cur;
        }

        private static (string name, int index) ParseSegment(string segment)
        {
            int hash = segment.LastIndexOf('#');
            if (hash > 0 && int.TryParse(segment.Substring(hash + 1), out int n) && n >= 1)
                return (segment.Substring(0, hash), n);
            return (segment, 1);
        }

        /// <summary>Up to <paramref name="max"/> candidates closest to <paramref name="query"/>.</summary>
        internal static List<string> Nearest(string query, IEnumerable<string> candidates, int max = 5)
        {
            string q = query ?? "";
            string leaf = q.Contains('/') ? q.Substring(q.LastIndexOf('/') + 1) : q;
            string ql = q.ToLowerInvariant();
            string leafl = leaf.ToLowerInvariant();
            return candidates
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct()
                .Select(c =>
                {
                    string cl = c.ToLowerInvariant();
                    string cleaf = cl.Contains('/') ? cl.Substring(cl.LastIndexOf('/') + 1) : cl;
                    int score = Levenshtein(ql, cl);
                    if (cleaf == leafl) score -= 1000;
                    else if (leafl.Length > 0 && (cleaf.Contains(leafl) || leafl.Contains(cleaf))) score -= 500;
                    return (c, score);
                })
                .OrderBy(x => x.score)
                .ThenBy(x => x.c.Length)
                .Take(max)
                .Select(x => x.c)
                .ToList();
        }

        internal static string Suggest(List<string> options)
        {
            return options.Count == 0 ? "" : " Did you mean: " + string.Join(", ", options) + "?";
        }

        private static int Levenshtein(string a, string b)
        {
            if (a.Length > 120) a = a.Substring(0, 120);
            if (b.Length > 120) b = b.Substring(0, 120);
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                (prev, cur) = (cur, prev);
            }
            return prev[b.Length];
        }

        #endregion

        #region Output

        internal static string LimitText(string text, int maxChars)
        {
            if (text.Length <= maxChars) return text;
            const string suffix = "\n... truncated";
            return maxChars <= suffix.Length ? text.Substring(0, Math.Max(0, maxChars)) :
                text.Substring(0, maxChars - suffix.Length) + suffix;
        }

        /// <summary>Line-based output with a hard character budget; the caller appends the truncation note.</summary>
        internal sealed class TextOut
        {
            private const int Reserve = 300; // header + truncation note
            private readonly StringBuilder _sb = new StringBuilder();
            private readonly int _max;

            public TextOut(int max) { _max = max; }

            public bool Full { get; private set; }
            public int Lines { get; private set; }

            public bool Line(string line)
            {
                if (Full) return false;
                if (_sb.Length + line.Length + 1 > _max - Reserve)
                {
                    Full = true;
                    return false;
                }
                _sb.Append(line).Append('\n');
                Lines++;
                return true;
            }

            public string Finish(string header, string truncationNote)
            {
                var result = new StringBuilder(header.Length + _sb.Length + 200);
                result.Append(header).Append('\n').Append(_sb);
                if (Full && !string.IsNullOrEmpty(truncationNote)) result.Append(truncationNote).Append('\n');
                string text = result.ToString().TrimEnd('\n');
                return LimitText(text, _max);
            }
        }

        /// <summary>Short type names; falls back to the full name when two types in view share a short name.</summary>
        internal sealed class TypeNamer
        {
            private readonly Dictionary<string, HashSet<Type>> _byShort = new Dictionary<string, HashSet<Type>>();

            public void Add(Type t)
            {
                if (t == null) return;
                string s = Short(t);
                if (!_byShort.TryGetValue(s, out var set)) _byShort[s] = set = new HashSet<Type>();
                set.Add(t);
            }

            public void AddHierarchy(Transform root)
            {
                foreach (Component c in root.GetComponentsInChildren<Component>(true))
                {
                    if (c != null) Add(c.GetType());
                }
            }

            public string Name(Type t)
            {
                if (t == null) return "?";
                string s = Short(t);
                return _byShort.TryGetValue(s, out var set) && set.Count > 1 ? (t.FullName ?? s) : s;
            }

            public static string Short(Type t)
            {
                string n = t.Name;
                int tick = n.IndexOf('`');
                return tick > 0 ? n.Substring(0, tick) : n;
            }
        }

        #endregion

        #region References

        internal enum RefKind { Null, Missing, Internal, Asset, Builtin, External }

        /// <summary>Classifies and prints object references relative to the inspected root.</summary>
        internal sealed class RefPrinter
        {
            private readonly Transform _root;
            private readonly TypeNamer _types;

            public RefPrinter(Transform root, TypeNamer types)
            {
                _root = root;
                _types = types;
            }

            public TypeNamer Types => _types;

            public static RefKind Classify(Object obj, int instanceId, Transform root)
            {
                // A reference to a deleted object reads back as null but keeps a non-zero instance id.
                if (obj == null) return instanceId != 0 ? RefKind.Missing : RefKind.Null;
                GameObject go = obj as GameObject ?? (obj as Component)?.gameObject;
                if (go != null && root != null && (go.transform == root || go.transform.IsChildOf(root)))
                    return RefKind.Internal;
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) return RefKind.External;
                if (path.StartsWith("Library/", StringComparison.Ordinal) ||
                    path.StartsWith("Resources/unity_builtin", StringComparison.Ordinal))
                    return RefKind.Builtin;
                return RefKind.Asset;
            }

            public RefKind Classify(SerializedProperty p) =>
                Classify(p.objectReferenceValue, p.objectReferenceInstanceIDValue, _root);

            public string Describe(SerializedProperty p) =>
                Describe(p.objectReferenceValue, p.objectReferenceInstanceIDValue);

            public string Describe(Object obj, int instanceId = 0)
            {
                switch (Classify(obj, instanceId, _root))
                {
                    case RefKind.Null: return "null";
                    case RefKind.Missing: return "MISSING";
                    case RefKind.Internal: return DescribeInternal(obj);
                    case RefKind.Builtin: return $"builtin:{obj.name} ({TypeNamer.Short(obj.GetType())})";
                    case RefKind.Asset: return DescribeAsset(obj);
                    default: return DescribeExternal(obj);
                }
            }

            private string DescribeInternal(Object obj)
            {
                GameObject go = obj as GameObject ?? ((Component)obj).gameObject;
                string path = ShowPath(RelPath(go.transform, _root), _root);
                return obj is Component c && !(c is Transform) ? $"{path} ({_types.Name(c.GetType())})" : path;
            }

            private static string DescribeAsset(Object obj)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (AssetDatabase.IsMainAsset(obj)) return path;
                string type = obj is Component || obj is GameObject || obj is Mesh || obj is Sprite || obj is AnimationClip
                    ? $" ({TypeNamer.Short(obj.GetType())})"
                    : "";
                return $"{path}:{obj.name}{type}";
            }

            private string DescribeExternal(Object obj)
            {
                GameObject go = obj as GameObject ?? (obj as Component)?.gameObject;
                if (go == null) return $"{obj.name} ({TypeNamer.Short(obj.GetType())}, not an asset)";
                string scenePath = RelPath(go.transform, null);
                string comp = obj is Component c && !(c is Transform) ? $" ({_types.Name(c.GetType())})" : "";
                return $"scene:{scenePath}{comp}";
            }
        }

        internal static bool IsUnityEvent(SerializedProperty p)
        {
            return p.propertyType == SerializedPropertyType.Generic && !p.isArray &&
                   p.FindPropertyRelative("m_PersistentCalls.m_Calls") != null;
        }

        /// <summary>One line per persistent listener, e.g. "-> Body/Hood (Door).Toggle()".</summary>
        internal static List<string> Listeners(SerializedProperty unityEvent, RefPrinter refs)
        {
            var lines = new List<string>();
            SerializedProperty calls = unityEvent.FindPropertyRelative("m_PersistentCalls.m_Calls");
            if (calls == null || !calls.isArray) return lines;

            for (int i = 0; i < calls.arraySize; i++)
            {
                SerializedProperty call = calls.GetArrayElementAtIndex(i);
                SerializedProperty targetProp = call.FindPropertyRelative("m_Target");
                string target = targetProp != null ? refs.Describe(targetProp) : "null";
                string method = call.FindPropertyRelative("m_MethodName")?.stringValue;
                int mode = call.FindPropertyRelative("m_Mode")?.intValue ?? 1;
                int state = call.FindPropertyRelative("m_CallState")?.intValue ?? 2;
                SerializedProperty args = call.FindPropertyRelative("m_Arguments");
                string suffix = state == 0 ? " [off]" : state == 1 ? " [editor+runtime]" : "";
                string callArgs = FormatCallArgs(mode, args, refs, targetProp?.objectReferenceValue, method);
                lines.Add($"-> {target}.{(string.IsNullOrEmpty(method) ? "<no method>" : method)}{callArgs}{suffix}");
            }
            return lines;
        }

        private static string FormatCallArgs(int mode, SerializedProperty args, RefPrinter refs, Object target, string method)
        {
            // PersistentListenerMode: EventDefined, Void, Object, Int, Float, String, Bool.
            switch (mode)
            {
                case 0: return DynamicArgs(target, method);
                case 2:
                    SerializedProperty obj = args?.FindPropertyRelative("m_ObjectArgument");
                    return obj != null ? $"({refs.Describe(obj)})" : "(object)";
                case 3: return $"(int {args?.FindPropertyRelative("m_IntArgument")?.intValue})";
                case 4: return $"(float {Num(args?.FindPropertyRelative("m_FloatArgument")?.floatValue ?? 0f)})";
                case 5: return $"(string {Quote(args?.FindPropertyRelative("m_StringArgument")?.stringValue)})";
                case 6: return $"(bool {((args?.FindPropertyRelative("m_BoolArgument")?.boolValue ?? false) ? "true" : "false")})";
                default: return "()";
            }
        }

        /// <summary>
        /// An event-defined (dynamic) listener receives the event's own arguments. Zero-argument events
        /// register their listeners this way too, so print "()" unless the method actually takes arguments.
        /// </summary>
        private static string DynamicArgs(Object target, string method)
        {
            if (target == null || string.IsNullOrEmpty(method)) return "(dynamic)";
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public |
                                                         System.Reflection.BindingFlags.NonPublic |
                                                         System.Reflection.BindingFlags.Instance;
            var candidates = target.GetType().GetMethods(flags).Where(m => m.Name == method).ToList();
            if (candidates.Count == 0) return "(dynamic)";
            if (candidates.Any(m => m.GetParameters().Length == 0)) return "()";
            var parameters = candidates.OrderBy(m => m.GetParameters().Length).First().GetParameters();
            return "(dynamic " + string.Join(", ", parameters.Select(x => KeywordName(x.ParameterType))) + ")";
        }

        private static string KeywordName(Type t)
        {
            if (t == typeof(int)) return "int";
            if (t == typeof(float)) return "float";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(string)) return "string";
            if (t == typeof(double)) return "double";
            if (t == typeof(long)) return "long";
            if (t == typeof(object)) return "object";
            return TypeNamer.Short(t);
        }

        #endregion

        #region Values

        /// <summary>"m_LocalPosition" -> "localPosition"; plain field names are unchanged.</summary>
        internal static string FieldName(string name)
        {
            if (name.StartsWith("m_", StringComparison.Ordinal) && name.Length > 2)
                return char.ToLowerInvariant(name[2]) + name.Substring(3);
            return name;
        }

        /// <summary>"m_Materials.Array.data[0]" -> "materials[0]".</summary>
        internal static string FieldPath(string propertyPath)
        {
            string s = propertyPath.Replace(".Array.data[", "[");
            return string.Join(".", s.Split('.').Select(FieldName));
        }

        internal static string Num(float v) => v.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
        private static string Num4(float v) => v.ToString("G4", System.Globalization.CultureInfo.InvariantCulture);

        internal static string Quote(string s)
        {
            if (s == null) return "\"\"";
            s = s.Replace("\r", "").Replace("\n", "\\n");
            if (s.Length > 60) s = s.Substring(0, 57) + "...";
            return "\"" + s + "\"";
        }

        /// <summary>Compact value of a leaf property (object references go through <paramref name="refs"/>).</summary>
        internal static string FormatValue(SerializedProperty p, RefPrinter refs)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return p.longValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return p.boolValue ? "true" : "false";
                case SerializedPropertyType.Float:
                    return p.type == "double"
                        ? p.doubleValue.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)
                        : Num(p.floatValue);
                case SerializedPropertyType.String: return Quote(p.stringValue);
                case SerializedPropertyType.Color: return FormatColor(p.colorValue);
                case SerializedPropertyType.ObjectReference: return refs.Describe(p);
                case SerializedPropertyType.LayerMask: return FormatMask(p.intValue);
                case SerializedPropertyType.Enum:
                    int idx = p.enumValueIndex;
                    return idx >= 0 && idx < p.enumNames.Length ? p.enumNames[idx] : p.intValue.ToString();
                case SerializedPropertyType.Vector2: return Tuple(p.vector2Value.x, p.vector2Value.y);
                case SerializedPropertyType.Vector3: return Tuple(p.vector3Value.x, p.vector3Value.y, p.vector3Value.z);
                case SerializedPropertyType.Vector4: return Tuple(p.vector4Value.x, p.vector4Value.y, p.vector4Value.z, p.vector4Value.w);
                case SerializedPropertyType.Quaternion:
                    Vector3 e = p.quaternionValue.eulerAngles;
                    return "euler" + Tuple(e.x, e.y, e.z);
                case SerializedPropertyType.Rect: return Tuple(p.rectValue.x, p.rectValue.y, p.rectValue.width, p.rectValue.height);
                case SerializedPropertyType.Bounds:
                    return $"center{Tuple(p.boundsValue.center.x, p.boundsValue.center.y, p.boundsValue.center.z)} size{Tuple(p.boundsValue.size.x, p.boundsValue.size.y, p.boundsValue.size.z)}";
                case SerializedPropertyType.Vector2Int: return $"({p.vector2IntValue.x}, {p.vector2IntValue.y})";
                case SerializedPropertyType.Vector3Int: return $"({p.vector3IntValue.x}, {p.vector3IntValue.y}, {p.vector3IntValue.z})";
                case SerializedPropertyType.RectInt: return $"({p.rectIntValue.x}, {p.rectIntValue.y}, {p.rectIntValue.width}, {p.rectIntValue.height})";
                case SerializedPropertyType.ArraySize: return p.intValue.ToString();
                case SerializedPropertyType.Character: return "'" + (char)p.intValue + "'";
                case SerializedPropertyType.AnimationCurve: return $"curve({p.animationCurveValue?.length ?? 0} keys)";
                case SerializedPropertyType.Gradient: return "gradient";
                case SerializedPropertyType.FixedBufferSize: return $"buffer[{p.fixedBufferSize}]";
                default: return "<" + p.type + ">";
            }
        }

        internal static string Tuple(params float[] v) => "(" + string.Join(", ", v.Select(Num4)) + ")";

        internal static string FormatColorValue(Color c) => FormatColor(c);

        private static string FormatColor(Color c)
        {
            bool ldr = c.r >= 0 && c.r <= 1 && c.g >= 0 && c.g <= 1 && c.b >= 0 && c.b <= 1 && c.a >= 0 && c.a <= 1;
            if (!ldr) return "rgba" + Tuple(c.r, c.g, c.b, c.a);
            return "#" + (Mathf.Approximately(c.a, 1f) ? ColorUtility.ToHtmlStringRGB(c) : ColorUtility.ToHtmlStringRGBA(c));
        }

        private static string FormatMask(int mask)
        {
            if (mask == 0) return "Nothing";
            if (mask == -1) return "Everything";
            var names = new List<string>();
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                string n = LayerMask.LayerToName(i);
                names.Add(string.IsNullOrEmpty(n) ? i.ToString() : n);
            }
            return string.Join("|", names);
        }

        /// <summary>Arrays of plain values that cannot hold references; skipped when scanning for wires.</summary>
        internal static bool IsPlainArray(SerializedProperty p)
        {
            if (!p.isArray || p.propertyType == SerializedPropertyType.String) return false;
            switch (p.arrayElementType)
            {
                case "int": case "float": case "double": case "bool": case "char": case "UInt8": case "SInt8":
                case "UInt16": case "SInt16": case "UInt32": case "SInt32": case "UInt64": case "SInt64":
                case "Vector2": case "Vector3": case "Vector4": case "Vector2f": case "Vector3f": case "Vector4f":
                case "Quaternionf": case "ColorRGBA": case "Matrix4x4f": case "string":
                    return true;
                default:
                    return false;
            }
        }

        #endregion

        #region Hierarchy snapshot

        /// <summary>One object in a walked hierarchy, with the bits every mode needs.</summary>
        internal sealed class HNode
        {
            public Transform T;
            public HNode Parent;
            public readonly List<HNode> Children = new List<HNode>();
            public string Rel;           // path relative to the inspected root
            public string Seg;           // this object's segment
            public int Count = 1;        // self + descendants
            public string NestedAsset;   // nested prefab instance root: its asset path
            public bool NestedMissing;   // nested prefab whose asset is gone
            public int MissingScripts;
            public Component[] Comps;    // non-null components
            public int Sig;              // structural signature for collapsing identical siblings
            public bool Keep = true;     // tree filter result

            public GameObject Go => T.gameObject;
        }

        /// <summary>Walks <paramref name="start"/>'s subtree; paths are relative to <paramref name="root"/>.</summary>
        internal static HNode Snapshot(Transform start, Transform root)
        {
            HNode Build(Transform t, HNode parent, string rel)
            {
                var n = new HNode
                {
                    T = t,
                    Parent = parent,
                    Rel = rel,
                    Seg = t == root ? t.name : Segment(t),
                };
                GameObject go = t.gameObject;
                Component[] all = go.GetComponents<Component>();
                n.Comps = all.Where(c => c != null).ToArray();
                n.MissingScripts = all.Length - n.Comps.Length;

                if (t != root)
                {
                    if (PrefabUtility.IsAnyPrefabInstanceRoot(go))
                    {
                        n.NestedAsset = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                        n.NestedMissing = string.IsNullOrEmpty(n.NestedAsset) || PrefabUtility.IsPrefabAssetMissing(go);
                    }
                    else if (PrefabUtility.IsPrefabAssetMissing(go) &&
                             (t.parent == null || !PrefabUtility.IsPrefabAssetMissing(t.parent.gameObject)))
                    {
                        n.NestedMissing = true;
                    }
                }

                // Name each child once; tracking seen names avoids an O(n^2) Segment() per child.
                var seen = new Dictionary<string, int>();
                for (int i = 0; i < t.childCount; i++)
                {
                    Transform child = t.GetChild(i);
                    seen.TryGetValue(child.name, out int k);
                    seen[child.name] = ++k;
                    string seg = k == 1 ? child.name : $"{child.name}#{k}";
                    HNode c = Build(child, n, rel.Length == 0 ? seg : rel + "/" + seg);
                    n.Children.Add(c);
                    n.Count += c.Count;
                }

                unchecked
                {
                    int h = 17;
                    h = h * 31 + (go.activeSelf ? 1 : 0);
                    h = h * 31 + (n.NestedAsset?.GetHashCode() ?? 0);
                    h = h * 31 + n.MissingScripts;
                    foreach (Component c in n.Comps) h = h * 31 + c.GetType().GetHashCode();
                    h = h * 31 + n.Children.Count;
                    foreach (HNode c in n.Children) h = h * 31 + c.Sig;
                    n.Sig = h;
                }
                return n;
            }

            return Build(start, null, RelPath(start, root));
        }

        internal static IEnumerable<HNode> Walk(HNode n)
        {
            yield return n;
            foreach (HNode c in n.Children)
            {
                foreach (HNode d in Walk(c)) yield return d;
            }
        }

        /// <summary>" variant of @Base.prefab" / " instance of @X.prefab" for the header line.</summary>
        internal static string PrefabHeaderInfo(Target target)
        {
            GameObject root = target.Root;
            if (!target.IsScene)
            {
                GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(target.AssetPath);
                if (asset != null && PrefabUtility.GetPrefabAssetType(asset) == PrefabAssetType.Variant)
                {
                    Object source = PrefabUtility.GetCorrespondingObjectFromSource(asset);
                    return source != null ? $"  variant of @{Path.GetFileName(AssetDatabase.GetAssetPath(source))}" : "  variant";
                }
                return "";
            }
            if (PrefabUtility.IsAnyPrefabInstanceRoot(root))
            {
                string asset = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
                return string.IsNullOrEmpty(asset) ? "  instance of MISSING prefab" : $"  instance of @{Path.GetFileName(asset)}";
            }
            return "";
        }

        #endregion
    }
}
