using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MCPForUnity.Editor.Tools.Prefabs
{
    public static partial class InspectPrefab
    {
        /// <summary>
        /// mode=problems: missing scripts, references to deleted objects (MISSING), broken nested prefab
        /// links, broken UnityEvent listeners, empty material slots and renderers with more materials than
        /// submeshes. Never-assigned (null) fields on scripts are listed separately, since many are optional.
        /// prefab_path may be a prefab, a folder of prefabs, or scene:Path.
        /// </summary>
        private static string RunProblems(ToolParams p, int maxChars)
        {
            string input = p.Get("prefab_path");
            if (string.IsNullOrWhiteSpace(input))
                throw new InspectError("'prefab_path' is required (a prefab, a folder, or scene:Path/To/Object).");

            string folder = AssetPathUtility.SanitizeAssetPath(input.Trim().TrimEnd('/'));
            if (!input.Trim().StartsWith("scene:", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(folder) && AssetDatabase.IsValidFolder(folder))
            {
                return ProblemsInFolder(folder, maxChars);
            }

            using (Target target = Target.Open(input, loadContents: false))
            {
                ProblemReport report = CheckProblems(target.Root.transform);
                var output = new TextOut(maxChars);
                foreach (string line in report.Lines())
                {
                    if (!output.Line(line)) break;
                }
                string header = $"{target.Label} problems  {report.Objects} objs: {report.Summary()}";
                if (report.Total == 0 && report.Unset.Count == 0) return header;
                return output.Finish(header, output.Full ? "... truncated. Use a larger max_chars or inspect_prefab node on the listed paths." : null);
            }
        }

        private static string ProblemsInFolder(string folder, int maxChars)
        {
            var prefabs = AssetDatabase.FindAssets("t:Prefab", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath).Distinct().OrderBy(x => x).ToList();
            var output = new TextOut(maxChars);
            int withProblems = 0, shown = 0;
            var totals = new ProblemReport();
            foreach (string path in prefabs)
            {
                GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset == null) continue;
                ProblemReport report = CheckProblems(asset.transform);
                totals.Add(report);
                if (report.Total == 0) continue;
                withProblems++;
                if (output.Full) continue;
                if (!output.Line($"{path}: {report.Summary()}")) continue;
                foreach (string line in report.Lines(includeUnset: false))
                {
                    if (!output.Line("  " + line)) break;
                }
                if (!output.Full) shown++;
            }

            string header = $"{folder} problems  {prefabs.Count} prefabs scanned, {withProblems} with problems: {totals.Summary()}";
            if (withProblems == 0) return header;
            return output.Finish(header, output.Full ? $"... truncated: {withProblems - shown} more prefabs with problems. Inspect one prefab at a time, or use a larger max_chars." : null);
        }

        private const int MaxUnsetListed = 15;

        private sealed class ProblemReport
        {
            public int Objects;
            public readonly List<string> MissingScripts = new List<string>();
            public readonly List<string> MissingRefs = new List<string>();
            public readonly List<string> BrokenPrefabs = new List<string>();
            public readonly List<string> EventIssues = new List<string>();
            public readonly List<string> Materials = new List<string>();
            public readonly List<string> Unset = new List<string>();

            public int Total => MissingScripts.Count + MissingRefs.Count + BrokenPrefabs.Count + EventIssues.Count + Materials.Count;

            public void Add(ProblemReport other)
            {
                Objects += other.Objects;
                MissingScripts.AddRange(other.MissingScripts);
                MissingRefs.AddRange(other.MissingRefs);
                BrokenPrefabs.AddRange(other.BrokenPrefabs);
                EventIssues.AddRange(other.EventIssues);
                Materials.AddRange(other.Materials);
                Unset.AddRange(other.Unset);
            }

            public string Summary()
            {
                var parts = new List<string>();
                void Part(int n, string label) { if (n > 0) parts.Add($"{n} {label}"); }
                Part(MissingScripts.Count, "missing script objs");
                Part(MissingRefs.Count, "MISSING refs");
                Part(BrokenPrefabs.Count, "broken prefab links");
                Part(EventIssues.Count, "event issues");
                Part(Materials.Count, "material issues");
                string main = parts.Count == 0 ? "no problems" : string.Join(", ", parts);
                return Unset.Count > 0 ? $"{main}; {Unset.Count} never-assigned script refs" : main;
            }

            public IEnumerable<string> Lines(bool includeUnset = true)
            {
                foreach (string s in MissingScripts) yield return s;
                foreach (string s in BrokenPrefabs) yield return s;
                foreach (string s in MissingRefs) yield return s;
                foreach (string s in EventIssues) yield return s;
                foreach (string s in Materials) yield return s;
                if (!includeUnset || Unset.Count == 0) yield break;
                yield return "never assigned (null) script refs, may be intentional:";
                foreach (string s in Unset.Take(MaxUnsetListed)) yield return "  " + s;
                if (Unset.Count > MaxUnsetListed)
                    yield return $"  ... +{Unset.Count - MaxUnsetListed} more; use inspect_prefab node on an object to see its unset fields";
            }
        }

        private static ProblemReport CheckProblems(Transform root)
        {
            var report = new ProblemReport();
            var types = new TypeNamer();
            types.AddHierarchy(root);
            var refs = new RefPrinter(root, types);
            HNode snap = Snapshot(root, root);

            foreach (HNode n in Walk(snap))
            {
                report.Objects++;
                string path = ShowPath(n.Rel, root);
                if (n.MissingScripts > 0)
                    report.MissingScripts.Add(n.MissingScripts == 1 ? $"missing script: {path}" : $"missing script x{n.MissingScripts}: {path}");
                if (n.NestedMissing)
                    report.BrokenPrefabs.Add($"broken nested prefab (asset missing): {path}");

                foreach (Component c in n.Comps)
                {
                    if (c is Transform) continue;
                    CheckComponent(c, path, report, refs);
                }
            }
            return report;
        }

        private static void CheckComponent(Component c, string path, ProblemReport report, RefPrinter refs)
        {
            string where = $"{path} ({refs.Types.Name(c.GetType())})";
            bool script = c is MonoBehaviour;
            bool renderer = c is Renderer;

            using (var so = new SerializedObject(c))
            {
                SerializedProperty it = so.GetIterator();
                bool enter = true;
                while (it.NextVisible(enter))
                {
                    enter = true;
                    if (it.propertyPath == "m_Script" || IsPlainArray(it)) { enter = false; continue; }
                    if (IsUnityEvent(it))
                    {
                        enter = false;
                        CheckListeners(it, where, report, refs);
                        continue;
                    }
                    if (it.propertyType != SerializedPropertyType.ObjectReference) continue;

                    RefKind kind = refs.Classify(it);
                    string field = FieldPath(it.propertyPath);
                    if (kind == RefKind.Missing)
                        report.MissingRefs.Add($"MISSING ref: {where}.{field}");
                    else if (kind == RefKind.Null && renderer && it.propertyPath.StartsWith("m_Materials.Array.data", StringComparison.Ordinal))
                        report.Materials.Add($"empty material slot: {where}.{field}");
                    else if (kind == RefKind.Null && script)
                        report.Unset.Add($"{where}.{field}");
                }
            }

            if (c is MeshRenderer || c is SkinnedMeshRenderer) CheckSubmeshes((Renderer)c, where, report);
            if (c is MeshFilter mf && mf.sharedMesh == null && mf.GetComponent<MeshRenderer>() != null)
            {
                using (var so = new SerializedObject(mf))
                {
                    if (so.FindProperty("m_Mesh")?.objectReferenceInstanceIDValue == 0)
                        report.Materials.Add($"no mesh: {where}");
                }
            }
        }

        private static void CheckSubmeshes(Renderer r, string where, ProblemReport report)
        {
            Mesh mesh;
            if (r is SkinnedMeshRenderer smr) mesh = smr.sharedMesh;
            else
            {
                // Unity returns a fake-null MeshFilter when none is attached; compare with ==, not ?.
                MeshFilter filter = r.GetComponent<MeshFilter>();
                mesh = filter != null ? filter.sharedMesh : null;
            }
            if (mesh == null) return;
            int materials = r.sharedMaterials.Length;
            if (materials > mesh.subMeshCount)
                report.Materials.Add($"extra materials: {where} has {materials} materials, mesh '{mesh.name}' has {mesh.subMeshCount} submeshes");
        }

        private static void CheckListeners(SerializedProperty unityEvent, string where, ProblemReport report, RefPrinter refs)
        {
            SerializedProperty calls = unityEvent.FindPropertyRelative("m_PersistentCalls.m_Calls");
            if (calls == null || !calls.isArray) return;
            string field = FieldPath(unityEvent.propertyPath);
            for (int i = 0; i < calls.arraySize; i++)
            {
                SerializedProperty call = calls.GetArrayElementAtIndex(i);
                SerializedProperty target = call.FindPropertyRelative("m_Target");
                string method = call.FindPropertyRelative("m_MethodName")?.stringValue;
                string label = $"{where}.{field}[{i}]";
                RefKind kind = target != null ? refs.Classify(target) : RefKind.Null;
                if (kind == RefKind.Missing) report.EventIssues.Add($"listener target MISSING: {label}.{method}");
                else if (kind == RefKind.Null) report.EventIssues.Add($"listener has no target: {label}");
                else if (string.IsNullOrEmpty(method)) report.EventIssues.Add($"listener has no method: {label} -> {refs.Describe(target)}");
                else if (!HasMethod(target.objectReferenceValue, method))
                    report.EventIssues.Add($"listener method not found: {label} -> {refs.Describe(target)}.{method}");
            }
        }

        private static bool HasMethod(Object target, string method)
        {
            if (target == null) return true;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            for (Type t = target.GetType(); t != null; t = t.BaseType)
            {
                if (t.GetMethods(flags | BindingFlags.DeclaredOnly).Any(m => m.Name == method)) return true;
            }
            return false;
        }
    }
}
