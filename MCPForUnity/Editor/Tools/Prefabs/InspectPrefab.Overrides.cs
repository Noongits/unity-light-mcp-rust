using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MCPForUnity.Editor.Tools.Prefabs
{
    public static partial class InspectPrefab
    {
        private const int MaxModsPerObject = 8;

        /// <summary>
        /// mode=overrides: for a variant (and for every nested prefab instance inside the prefab) the source
        /// prefab, then changed properties (old -> new), added/removed components and added/removed objects.
        /// Default overrides (root name/position/rotation) are counted, not listed.
        /// </summary>
        private static string RunOverrides(ToolParams p, int maxChars)
        {
            using (Target target = Target.Open(p.Get("prefab_path"), loadContents: true))
            {
                Transform root = target.Root.transform;
                var types = new TypeNamer();
                types.AddHierarchy(root);
                var refs = new RefPrinter(root, types);

                string chain = target.IsScene ? "" : VariantChain(target.AssetPath);
                var instances = root.GetComponentsInChildren<Transform>(true)
                    .Select(t => t.gameObject)
                    .Where(PrefabUtility.IsOutermostPrefabInstanceRoot)
                    .ToList();

                string header = $"{target.Label} overrides{(chain.Length > 0 ? "  " + chain : "")}  " +
                                $"({instances.Count(i => i.transform != root)} nested prefab instances)";
                if (instances.Count == 0) return header + "\nnot a variant and contains no nested prefab instances.";

                var output = new TextOut(maxChars);
                var unchanged = new List<string>();
                int shown = 0;
                foreach (GameObject inst in instances)
                {
                    var section = OverrideSection(inst, root, refs, out bool hasChanges);
                    string source = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(inst);
                    string sourceName = string.IsNullOrEmpty(source) ? "MISSING-PREFAB" : Path.GetFileName(source);
                    if (!hasChanges)
                    {
                        unchanged.Add(sourceName);
                        shown++;
                        continue;
                    }
                    bool complete = true;
                    foreach (string line in section)
                    {
                        if (!output.Line(line)) { complete = false; break; }
                    }
                    if (!complete) break;
                    shown++;
                }

                if (!output.Full && unchanged.Count > 0)
                {
                    string list = string.Join(", ", unchanged.GroupBy(u => u).Select(g => g.Count() == 1 ? "@" + g.Key : $"@{g.Key} x{g.Count()}").Take(10));
                    output.Line($"{unchanged.Count} nested instances without changes: {list}{(unchanged.Distinct().Count() > 10 ? ", ..." : "")}");
                }

                string note = output.Full
                    ? $"... truncated: {instances.Count - shown} more prefab instances not shown. Use inspect_prefab tree filter=@ to locate them, or a larger max_chars."
                    : null;
                return output.Finish(header, note);
            }
        }

        /// <summary>"variant of @B.prefab <- @A.prefab": the base chain of a prefab variant asset.</summary>
        private static string VariantChain(string assetPath)
        {
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (asset == null || PrefabUtility.GetPrefabAssetType(asset) != PrefabAssetType.Variant) return "";
            var names = new List<string>();
            Object current = asset;
            for (int guard = 0; guard < 32; guard++)
            {
                Object source = PrefabUtility.GetCorrespondingObjectFromSource(current);
                if (source == null) break;
                names.Add("@" + Path.GetFileName(AssetDatabase.GetAssetPath(source)));
                current = source;
            }
            return names.Count == 0 ? "variant (base missing)" : "variant of " + string.Join(" <- ", names);
        }

        private static List<string> OverrideSection(GameObject inst, Transform root, RefPrinter refs, out bool hasChanges)
        {
            var lines = new List<string>();
            string instPath = ShowPath(RelPath(inst.transform, root), root);
            string source = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(inst);
            string sourceName = string.IsNullOrEmpty(source) ? "MISSING-PREFAB" : Path.GetFileName(source);

            // Map source-prefab objects to their counterparts in this instance.
            var map = new Dictionary<Object, Object>();
            foreach (Transform t in inst.GetComponentsInChildren<Transform>(true))
            {
                Object sg = PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject);
                if (sg != null) map[sg] = t.gameObject;
                foreach (Component c in t.GetComponents<Component>())
                {
                    if (c == null) continue;
                    Object sc = PrefabUtility.GetCorrespondingObjectFromSource(c);
                    if (sc != null) map[sc] = c;
                }
            }

            PropertyModification[] mods = PrefabUtility.GetPropertyModifications(inst) ?? new PropertyModification[0];
            int defaultOverrides = 0;
            var byTarget = new List<(Object target, List<PropertyModification> mods)>();
            var index = new Dictionary<Object, int>();
            foreach (PropertyModification mod in mods)
            {
                if (PrefabUtility.IsDefaultOverride(mod))
                {
                    defaultOverrides++;
                    continue;
                }
                Object key = mod.target;
                if (key == null)
                {
                    byTarget.Add((null, new List<PropertyModification> { mod }));
                    continue;
                }
                if (!index.TryGetValue(key, out int i))
                {
                    index[key] = i = byTarget.Count;
                    byTarget.Add((key, new List<PropertyModification>()));
                }
                byTarget[i].mods.Add(mod);
            }

            var added = PrefabUtility.GetAddedComponents(inst);
            var removed = PrefabUtility.GetRemovedComponents(inst);
            var addedObjects = PrefabUtility.GetAddedGameObjects(inst);
            var removedObjects = RemovedObjectLines(inst, root);

            int unchangedMods = 0, editorHints = 0, propCount = 0, objCount = 0;
            var propLines = new List<string>();
            foreach (var (targetObj, list) in byTarget)
            {
                var changes = ModificationLines(targetObj, list, refs, ref unchangedMods, ref editorHints);
                if (changes.Count == 0) continue;
                objCount++;
                propCount += changes.Count;
                propLines.Add("  " + DescribeModTarget(targetObj, map, root, refs));
                propLines.AddRange(changes.Take(MaxModsPerObject).Select(c => "    " + c));
                if (changes.Count > MaxModsPerObject) propLines.Add($"    ... +{changes.Count - MaxModsPerObject} more");
            }
            hasChanges = propCount > 0 || added.Count > 0 || removed.Count > 0 || addedObjects.Count > 0 || removedObjects.Count > 0;

            var hidden = new List<string>();
            if (defaultOverrides > 0) hidden.Add($"{defaultOverrides} default");
            if (unchangedMods > 0) hidden.Add($"{unchangedMods} equal to source");
            if (editorHints > 0) hidden.Add($"{editorHints} editor-only");
            string where = inst.transform == root ? $"root ({sourceName} base)" : $"@{sourceName} at {instPath}";
            lines.Add($"{where}: {propCount} changes on {objCount} objs, +{added.Count}/-{removed.Count} comps, " +
                      $"+{addedObjects.Count}/-{removedObjects.Count} objs{(hidden.Count > 0 ? $" (hidden overrides: {string.Join(", ", hidden)})" : "")}");
            lines.AddRange(propLines);

            foreach (AddedComponent a in added)
            {
                if (a.instanceComponent == null) continue;
                lines.Add($"  + {ShowPath(RelPath(a.instanceComponent.transform, root), root)} ({refs.Types.Name(a.instanceComponent.GetType())})");
            }
            foreach (RemovedComponent r in removed)
            {
                string owner = r.containingInstanceGameObject != null ? ShowPath(RelPath(r.containingInstanceGameObject.transform, root), root) : "?";
                string type = r.assetComponent != null ? TypeNamer.Short(r.assetComponent.GetType()) : "?";
                lines.Add($"  - {owner} ({type})");
            }
            foreach (AddedGameObject a in addedObjects)
            {
                if (a.instanceGameObject == null) continue;
                int count = a.instanceGameObject.GetComponentsInChildren<Transform>(true).Length;
                lines.Add($"  + {ShowPath(RelPath(a.instanceGameObject.transform, root), root)} (object{(count > 1 ? $" + {count - 1} children" : "")})");
            }
            lines.AddRange(removedObjects);
            return lines;
        }

        /// <summary>
        /// "field: old -> new" lines for one modified object. Vector/color/quaternion components (.x/.y/.z/.w,
        /// .r/.g/.b/.a) are merged into one line, rotations print as euler angles, the editor-only
        /// m_LocalEulerAnglesHint is dropped, and overrides equal to the source value are counted, not listed.
        /// </summary>
        private static List<string> ModificationLines(Object target, List<PropertyModification> mods, RefPrinter refs,
            ref int unchanged, ref int editorHints)
        {
            var lines = new List<string>();
            SerializedObject so = target != null ? new SerializedObject(target) : null;
            try
            {
                foreach (var group in mods.GroupBy(m => VectorStem(m.propertyPath) ?? m.propertyPath))
                {
                    if (group.Key.StartsWith("m_LocalEulerAnglesHint", StringComparison.Ordinal))
                    {
                        editorHints += group.Count();
                        continue;
                    }
                    SerializedProperty stem = so?.FindProperty(group.Key);
                    if (stem != null && group.All(m => VectorStem(m.propertyPath) == group.Key) && TryMergeVector(stem, group, refs, out string oldV, out string newV))
                    {
                        if (oldV == newV) unchanged += group.Count();
                        else lines.Add($"{FieldPath(group.Key)}: {oldV} -> {newV}");
                        continue;
                    }
                    foreach (PropertyModification mod in group)
                    {
                        string oldValue = OldValue(mod, refs);
                        string newValue = NewValue(mod, refs);
                        if (oldValue == newValue) unchanged++;
                        else lines.Add($"{FieldPath(mod.propertyPath)}: {oldValue} -> {newValue}");
                    }
                }
            }
            finally
            {
                so?.Dispose();
            }
            return lines;
        }

        /// <summary>"m_LocalPosition.x" -> "m_LocalPosition"; null when the path is not a vector component.</summary>
        private static string VectorStem(string propertyPath)
        {
            int dot = propertyPath.LastIndexOf('.');
            if (dot <= 0 || dot != propertyPath.Length - 2) return null;
            return "xyzwrgba".IndexOf(propertyPath[dot + 1]) >= 0 ? propertyPath.Substring(0, dot) : null;
        }

        private static bool TryMergeVector(SerializedProperty stem, IEnumerable<PropertyModification> mods, RefPrinter refs,
            out string oldValue, out string newValue)
        {
            oldValue = newValue = null;
            var values = new Dictionary<char, float>();
            foreach (PropertyModification mod in mods)
            {
                if (!float.TryParse(mod.value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v)) return false;
                values[mod.propertyPath[mod.propertyPath.Length - 1]] = v;
            }
            float Pick(char c, float current) => values.TryGetValue(c, out float v) ? v : current;

            switch (stem.propertyType)
            {
                case SerializedPropertyType.Vector2:
                    Vector2 v2 = stem.vector2Value;
                    oldValue = FormatValue(stem, refs);
                    newValue = Tuple(Pick('x', v2.x), Pick('y', v2.y));
                    return true;
                case SerializedPropertyType.Vector3:
                    Vector3 v3 = stem.vector3Value;
                    oldValue = FormatValue(stem, refs);
                    newValue = Tuple(Pick('x', v3.x), Pick('y', v3.y), Pick('z', v3.z));
                    return true;
                case SerializedPropertyType.Vector4:
                    Vector4 v4 = stem.vector4Value;
                    oldValue = FormatValue(stem, refs);
                    newValue = Tuple(Pick('x', v4.x), Pick('y', v4.y), Pick('z', v4.z), Pick('w', v4.w));
                    return true;
                case SerializedPropertyType.Quaternion:
                    Quaternion q = stem.quaternionValue;
                    var nq = new Quaternion(Pick('x', q.x), Pick('y', q.y), Pick('z', q.z), Pick('w', q.w));
                    oldValue = FormatValue(stem, refs);
                    Vector3 e = nq.eulerAngles;
                    newValue = "euler" + Tuple(e.x, e.y, e.z);
                    return true;
                case SerializedPropertyType.Color:
                    Color c = stem.colorValue;
                    var nc = new Color(Pick('r', c.r), Pick('g', c.g), Pick('b', c.b), Pick('a', c.a));
                    oldValue = FormatValue(stem, refs);
                    newValue = FormatColorValue(nc);
                    return true;
                default:
                    return false;
            }
        }

        private static List<string> RemovedObjectLines(GameObject inst, Transform root)
        {
            var lines = new List<string>();
#if UNITY_2022_2_OR_NEWER
            foreach (RemovedGameObject r in PrefabUtility.GetRemovedGameObjects(inst))
            {
                string parent = r.parentOfRemovedGameObjectInInstance != null
                    ? ShowPath(RelPath(r.parentOfRemovedGameObjectInInstance.transform, root), root)
                    : "?";
                lines.Add($"  - {parent}/{(r.assetGameObject != null ? r.assetGameObject.name : "?")} (object)");
            }
#endif
            return lines;
        }

        private static string DescribeModTarget(Object sourceObj, Dictionary<Object, Object> map, Transform root, RefPrinter refs)
        {
            if (sourceObj == null) return "MISSING target";
            if (map.TryGetValue(sourceObj, out Object instanceObj) && instanceObj != null)
            {
                GameObject go = instanceObj as GameObject ?? ((Component)instanceObj).gameObject;
                string path = ShowPath(RelPath(go.transform, root), root);
                return instanceObj is Component c ? $"{path} ({refs.Types.Name(c.GetType())})" : $"{path} (GameObject)";
            }
            return $"{sourceObj.name} ({TypeNamer.Short(sourceObj.GetType())}, not in this prefab)";
        }

        private static string OldValue(PropertyModification mod, RefPrinter refs)
        {
            if (mod.target == null) return "?";
            using (var so = new SerializedObject(mod.target))
            {
                SerializedProperty sp = so.FindProperty(mod.propertyPath);
                if (sp == null) return "?";
                if (sp.propertyType == SerializedPropertyType.ObjectReference)
                {
                    Object old = sp.objectReferenceValue;
                    return old == null ? "null" : AssetOrName(old);
                }
                return sp.propertyType == SerializedPropertyType.Generic ? "{...}" : FormatValue(sp, refs);
            }
        }

        private static string NewValue(PropertyModification mod, RefPrinter refs)
        {
            if (mod.objectReference != null) return refs.Describe(mod.objectReference);
            string value = mod.value ?? "";
            if (mod.target == null) return value;
            using (var so = new SerializedObject(mod.target))
            {
                SerializedProperty sp = so.FindProperty(mod.propertyPath);
                if (sp == null) return value;
                switch (sp.propertyType)
                {
                    case SerializedPropertyType.ObjectReference: return "null";
                    case SerializedPropertyType.Boolean: return value == "0" ? "false" : value == "1" ? "true" : value;
                    case SerializedPropertyType.String: return Quote(value);
                    case SerializedPropertyType.Enum:
                        return int.TryParse(value, out int i) && i >= 0 && i < sp.enumNames.Length ? sp.enumNames[i] : value;
                    default: return value;
                }
            }
        }

        /// <summary>A reference living in the source prefab: its path in that asset, or the asset path.</summary>
        private static string AssetOrName(Object obj)
        {
            GameObject go = obj as GameObject ?? (obj as Component)?.gameObject;
            if (go != null)
            {
                string path = RelPath(go.transform, go.transform.root);
                string comp = obj is Component c && !(c is Transform) ? $" ({TypeNamer.Short(c.GetType())})" : "";
                return (path.Length == 0 ? go.name : path) + comp;
            }
            string assetPath = AssetDatabase.GetAssetPath(obj);
            return string.IsNullOrEmpty(assetPath) ? obj.name : assetPath;
        }
    }
}
