using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MCPForUnity.Editor.Tools.Prefabs
{
    public static partial class InspectPrefab
    {
        private const int MaxPathsPerFile = 5;

        /// <summary>
        /// mode=usages: every prefab and scene under Assets/ that uses a script (script=ClassName) or an asset
        /// (asset=Assets/...). Direct users reference the target's GUID; the use is then followed through
        /// nested/referenced prefabs (and, for assets, materials, controllers and data assets) to report
        /// "via @X.prefab". GUIDs come from <see cref="GuidIndex"/>; only direct users are loaded, to list the
        /// object paths inside them. Scenes are listed by file; paths only for scenes that are already open.
        /// </summary>
        private static string RunUsages(ToolParams p, int maxChars)
        {
            string script = p.Get("script");
            string assetArg = p.Get("asset");
            if (string.IsNullOrWhiteSpace(script) && string.IsNullOrWhiteSpace(assetArg))
                throw new InspectError("usages needs script= (class name) or asset= (Assets/... path).");

            Type type = null;
            string targetPath;
            string what;
            if (!string.IsNullOrWhiteSpace(script))
            {
                MonoScript ms = FindMonoScript(script.Trim());
                type = ms.GetClass();
                targetPath = AssetDatabase.GetAssetPath(ms);
                what = $"{(type != null ? type.Name : ms.name)} ({targetPath})";
            }
            else
            {
                targetPath = AssetPathUtility.SanitizeAssetPath(assetArg.Trim());
                if (string.IsNullOrEmpty(targetPath) || string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(targetPath)) ||
                    AssetDatabase.LoadMainAssetAtPath(targetPath) == null)
                {
                    string stem = Path.GetFileNameWithoutExtension(targetPath ?? assetArg);
                    var similar = AssetDatabase.FindAssets(stem).Select(AssetDatabase.GUIDToAssetPath).Take(200);
                    throw new InspectError($"No asset at '{assetArg}'." + Suggest(Nearest(targetPath ?? assetArg, similar)));
                }
                what = targetPath;
            }

            bool dll = targetPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            string[] folders = { "Assets" };
            var prefabs = AssetDatabase.FindAssets("t:Prefab", folders).Select(AssetDatabase.GUIDToAssetPath).Distinct().ToList();
            var scenes = AssetDatabase.FindAssets("t:Scene", folders).Select(AssetDatabase.GUIDToAssetPath).Distinct().ToList();
            var dataAssets = new List<string>();
            if (type != null && typeof(ScriptableObject).IsAssignableFrom(type))
            {
                dataAssets = AssetDatabase.FindAssets("t:" + type.Name, folders).Select(AssetDatabase.GUIDToAssetPath)
                    .Where(a => !a.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
            }

            // Files that can pass a use on to whatever references them: nested/referenced prefabs for scripts
            // (plus data assets for ScriptableObject types); for assets also materials, controllers, atlases...
            var intermediates = new List<string>();
            if (type == null)
            {
                intermediates = AssetDatabase.FindAssets("t:Material t:AnimatorController t:AnimatorOverrideController t:SpriteAtlas t:ScriptableObject", folders)
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(a => !a.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && SmallerThan(a, MaxIntermediateBytes))
                    .Distinct().ToList();
            }

            var reported = new HashSet<string>(prefabs.Concat(scenes).Concat(dataAssets));
            var all = reported.Concat(intermediates).Where(f => f != targetPath).Distinct().ToList();
            Dictionary<string, string[]> refsByFile = GuidIndex.Get(all);
            string targetGuid = AssetDatabase.AssetPathToGUID(targetPath);

            bool CanPropagate(string file) =>
                file.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ||
                (type == null && !file.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) ||
                (type != null && dataAssets.Count > 0 && file.EndsWith(".asset", StringComparison.OrdinalIgnoreCase));

            // Direct users reference the target GUID; then follow users through files that reference them.
            var usedBy = new Dictionary<string, string>(); // file -> "" (direct) or the file it uses the target through
            var carrierGuids = new Dictionary<string, string>(); // guid of a using file -> that file
            foreach (string file in all)
            {
                if (Array.IndexOf(refsByFile[file], targetGuid) < 0) continue;
                // A DLL GUID identifies the assembly, not one script. Filter before propagating
                // or unrelated components in this DLL make every ancestor a false positive.
                if (dll && type != null && file.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(file);
                    if (asset == null || asset.GetComponentsInChildren(type, true).Length == 0) continue;
                }
                usedBy[file] = "";
                if (CanPropagate(file)) carrierGuids[AssetDatabase.AssetPathToGUID(file)] = file;
            }
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (string file in all)
                {
                    if (usedBy.ContainsKey(file)) continue;
                    foreach (string guid in refsByFile[file])
                    {
                        if (!carrierGuids.TryGetValue(guid, out string through) || through == file) continue;
                        usedBy[file] = through;
                        if (CanPropagate(file)) carrierGuids[AssetDatabase.AssetPathToGUID(file)] = file;
                        changed = true;
                        break;
                    }
                }
            }

            var direct = usedBy.Where(kv => kv.Value.Length == 0 && reported.Contains(kv.Key)).Select(kv => kv.Key).ToList();
            var via = usedBy.Where(kv => kv.Value.Length > 0 && reported.Contains(kv.Key)).Select(kv => (file: kv.Key, through: kv.Value)).ToList();

            // Every class in a DLL shares the DLL path, so confirm DLL-script prefab users by their components.
            if (dll && type != null)
            {
                direct = direct.Where(f => !f.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ||
                                           (AssetDatabase.LoadAssetAtPath<GameObject>(f)?.GetComponentsInChildren(type, true).Length ?? 0) > 0).ToList();
            }

            int prefabCount = direct.Concat(via.Select(v => v.file)).Count(f => f.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase));
            int sceneCount = direct.Concat(via.Select(v => v.file)).Count(f => f.EndsWith(".unity", StringComparison.OrdinalIgnoreCase));
            int otherCount = direct.Count + via.Count - prefabCount - sceneCount;
            string header = $"usages of {what}: {prefabCount} prefabs, {sceneCount} scenes" +
                            (otherCount > 0 ? $", {otherCount} assets" : "") +
                            $" ({direct.Count} direct, {via.Count} via nested/dependencies; searched {prefabs.Count} prefabs, {scenes.Count} scenes under Assets/)";
            if (direct.Count == 0 && via.Count == 0) return header + "\nnothing under Assets/ uses it.";

            var output = new TextOut(maxChars);
            int shown = 0;
            foreach (string file in direct.OrderBy(SortKey).ThenBy(f => f))
            {
                if (!output.Line($"{file}: {DescribeDirectUse(file, type, targetPath)}")) break;
                shown++;
            }
            if (!output.Full)
            {
                foreach (var (file, through) in via.OrderBy(v => SortKey(v.file)).ThenBy(v => v.file))
                {
                    string throughText = through.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ? "@" + Path.GetFileName(through) : through;
                    if (!output.Line($"{file}: via {throughText}")) break;
                    shown++;
                }
            }

            string note = output.Full ? $"... truncated: {direct.Count + via.Count - shown} more files. Use a larger max_chars." : null;
            return output.Finish(header, note);
        }

        private const long MaxIntermediateBytes = 16L * 1024 * 1024; // skip lighting/terrain data blobs

        private static bool SmallerThan(string file, long bytes)
        {
            try { return new FileInfo(file).Length < bytes; }
            catch { return false; }
        }

        private static int SortKey(string file) =>
            file.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ? 0 :
            file.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ? 1 : 2;

        /// <summary>Finds the MonoScript for a class name (short or full), throwing with similar names.</summary>
        private static MonoScript FindMonoScript(string name)
        {
            MonoScript[] scripts = MonoImporter.GetAllRuntimeMonoScripts();
            var matches = scripts.Where(ms =>
            {
                Type c = ms != null ? ms.GetClass() : null;
                return c != null && (c.Name == name || c.FullName == name);
            }).ToList();
            if (matches.Count == 0)
                matches = scripts.Where(ms => ms != null && ms.GetClass() == null && ms.name == name).ToList();

            var classes = matches.Select(ms => ms.GetClass()).Where(c => c != null).Distinct().ToList();
            if (classes.Count > 1)
                throw new InspectError($"'{name}' is ambiguous: {string.Join(", ", classes.Select(c => c.FullName))}. Pass the full name.");
            if (matches.Count > 0) return matches[0];

            var names = scripts.Where(ms => ms != null).Select(ms => ms.GetClass()?.Name ?? ms.name);
            throw new InspectError($"No MonoBehaviour or ScriptableObject class '{name}'." + Suggest(Nearest(name, names)));
        }

        /// <summary>Object paths inside a directly-using file, e.g. "E36_CAR, Body/Doors/FL_Door (+2 in nested prefabs)".</summary>
        private static string DescribeDirectUse(string file, Type type, string targetPath)
        {
            List<GameObject> roots;
            if (file.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(file);
                if (asset == null) return "direct";
                roots = new List<GameObject> { asset };
            }
            else if (file.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                Scene scene = SceneManager.GetSceneByPath(file);
                if (!scene.IsValid() || !scene.isLoaded) return "direct (scene not open; object paths not resolved)";
                roots = scene.GetRootGameObjects().ToList();
            }
            else
            {
                return "direct";
            }

            var paths = new List<string>();
            int nested = 0;
            bool prefabTarget = type == null && targetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
            foreach (GameObject root in roots)
            {
                if (prefabTarget)
                {
                    // A prefab is "used" as a nested instance: list the instance roots, not internal link fields.
                    Transform scope = file.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ? null : root.transform;
                    foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (t == root.transform && scope != null) continue;
                        if (!PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject) ||
                            PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject) != targetPath) continue;
                        string p = scope != null ? ShowPath(RelPath(t, scope), scope) : RelPath(t, null);
                        if (!paths.Contains(p)) paths.Add(p);
                    }
                    continue;
                }

                Transform rootT = file.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ? null : root.transform;
                foreach (Component c in root.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || !Uses(c, type, targetPath, out string field)) continue;
                    GameObject nearest = PrefabUtility.GetNearestPrefabInstanceRoot(c.gameObject);
                    if (nearest != null && nearest != root && rootT != null)
                    {
                        nested++;
                        continue;
                    }
                    string path = rootT != null ? ShowPath(RelPath(c.transform, rootT), rootT) : RelPath(c.transform, null);
                    string entry = field != null ? $"{path} ({TypeNamer.Short(c.GetType())}).{field}" : path;
                    if (!paths.Contains(entry)) paths.Add(entry);
                }
            }

            if (paths.Count == 0 && nested == 0) return "direct";
            string list = string.Join(", ", paths.Take(MaxPathsPerFile));
            if (paths.Count > MaxPathsPerFile) list += $", +{paths.Count - MaxPathsPerFile} more";
            if (nested > 0) list += (list.Length > 0 ? " " : "") + $"(+{nested} in nested prefabs)";
            return list;
        }

        /// <summary>Whether a component is the script (type) or references the asset; field names the reference.</summary>
        private static bool Uses(Component c, Type type, string targetPath, out string field)
        {
            field = null;
            if (type != null) return type.IsInstanceOfType(c);

            using (var so = new SerializedObject(c))
            {
                SerializedProperty it = so.GetIterator();
                bool enter = true;
                while (it.Next(enter))
                {
                    enter = !IsPlainArray(it);
                    if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (it.propertyPath == "m_CorrespondingSourceObject" || it.propertyPath == "m_PrefabInstance" ||
                        it.propertyPath == "m_PrefabAsset" || it.propertyPath == "m_GameObject" || it.propertyPath == "m_Script") continue;
                    Object obj = it.objectReferenceValue;
                    if (obj == null || AssetDatabase.GetAssetPath(obj) != targetPath) continue;
                    field = FieldPath(it.propertyPath);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// GUIDs referenced by each file, read straight from the text-serialized YAML ("guid: ..."), in
        /// parallel, and cached for the editor session by file size and write time. A project-wide
        /// AssetDatabase.GetDependencies pass costs several ms per file; this reads 8.5k prefabs in seconds
        /// once and then only re-reads files that changed. Non-text files fall back to GetDependencies.
        /// </summary>
        private static class GuidIndex
        {
            private static readonly Dictionary<string, (long stamp, string[] guids)> Cache =
                new Dictionary<string, (long stamp, string[] guids)>();
            private static readonly byte[] Token = { (byte)'g', (byte)'u', (byte)'i', (byte)'d', (byte)':', (byte)' ' };

            public static Dictionary<string, string[]> Get(List<string> files)
            {
                var result = new Dictionary<string, string[]>(files.Count);
                var stale = new List<(string file, long stamp)>();
                lock (Cache)
                {
                    foreach (string file in files)
                    {
                        long stamp = Stamp(file);
                        if (Cache.TryGetValue(file, out var entry) && entry.stamp == stamp) result[file] = entry.guids;
                        else stale.Add((file, stamp));
                    }
                }

                var scanned = new string[stale.Count][];
                System.Threading.Tasks.Parallel.For(0, stale.Count, i => scanned[i] = ScanText(stale[i].file));

                lock (Cache)
                {
                    for (int i = 0; i < stale.Count; i++)
                    {
                        string file = stale[i].file;
                        // Binary-serialized or unreadable: ask the AssetDatabase (main thread only).
                        string[] guids = scanned[i] ?? AssetDatabase.GetDependencies(file, false)
                            .Where(d => d != file).Select(AssetDatabase.AssetPathToGUID).Where(g => !string.IsNullOrEmpty(g)).ToArray();
                        Cache[file] = (stale[i].stamp, guids);
                        result[file] = guids;
                    }
                }
                return result;
            }

            private static long Stamp(string file)
            {
                try
                {
                    var info = new FileInfo(file);
                    return info.Exists ? info.LastWriteTimeUtc.Ticks ^ (info.Length << 1) : 0;
                }
                catch
                {
                    return 0;
                }
            }

            /// <summary>Distinct 32-char GUIDs after each "guid: " token; null when the file is not YAML text.</summary>
            private static string[] ScanText(string file)
            {
                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(file);
                }
                catch
                {
                    return null;
                }
                if (bytes.Length < 5 || bytes[0] != (byte)'%' || bytes[1] != (byte)'Y') return null;

                var set = new HashSet<string>();
                int end = bytes.Length - Token.Length - 32;
                for (int k = 0; k <= end; k++)
                {
                    if (bytes[k] != Token[0] || bytes[k + 1] != Token[1] || bytes[k + 2] != Token[2] ||
                        bytes[k + 3] != Token[3] || bytes[k + 4] != Token[4] || bytes[k + 5] != Token[5]) continue;
                    set.Add(System.Text.Encoding.ASCII.GetString(bytes, k + Token.Length, 32));
                    k += Token.Length + 31;
                }
                return set.ToArray();
            }
        }
    }
}
