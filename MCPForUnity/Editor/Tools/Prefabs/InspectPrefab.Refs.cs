using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Prefabs
{
    public static partial class InspectPrefab
    {
        private const int RefArraySummarizeOver = 4;

        /// <summary>
        /// mode=refs: every outgoing wire, grouped by source object. Object references and UnityEvent
        /// listeners are listed per field; asset links from built-in components (materials, meshes, clips...)
        /// are grouped per asset so a 400-object car does not print one line per renderer.
        /// </summary>
        private static string RunRefs(ToolParams p, int maxChars)
        {
            using (Target target = Target.Open(p.Get("prefab_path"), loadContents: false))
            {
                Transform top = target.Root.transform;
                string rootArg = p.Get("root");
                Transform start = string.IsNullOrWhiteSpace(rootArg) ? top : ResolvePath(top, rootArg);
                HNode snap = Snapshot(start, top);
                var types = new TypeNamer();
                types.AddHierarchy(top);
                var refs = new RefPrinter(top, types);

                string componentFilter = p.Get("component")?.Trim();
                var bySource = new List<(HNode node, List<(string comp, string line)> lines)>();
                var assets = new Dictionary<string, AssetLinks>();
                int wires = 0, events = 0, listeners = 0, missing = 0, assetLinks = 0;

                foreach (HNode n in Walk(snap))
                {
                    var tagged = new List<(string comp, string line)>();
                    foreach (Component c in n.Comps)
                    {
                        if (c is Transform) continue;
                        bool script = c is MonoBehaviour;
                        string comp = types.Name(c.GetType());
                        if (!string.IsNullOrEmpty(componentFilter) &&
                            !string.Equals(comp, componentFilter, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(c.GetType().FullName, componentFilter, StringComparison.OrdinalIgnoreCase)) continue;
                        var lines = new List<string>();
                        using var so = new SerializedObject(c);
                        SerializedProperty it = so.GetIterator();
                        bool enter = true;
                        while (it.NextVisible(enter))
                        {
                            enter = true;
                            if (it.propertyPath == "m_Script" || IsPlainArray(it)) { enter = false; continue; }

                            if (IsUnityEvent(it))
                            {
                                enter = false;
                                var ls = Listeners(it, refs);
                                if (ls.Count == 0) continue;
                                events++;
                                listeners += ls.Count;
                                wires += ls.Count;
                                lines.Add($"  {comp}.{FieldPath(it.propertyPath)}: {ls.Count} listener{(ls.Count == 1 ? "" : "s")}");
                                lines.AddRange(ls.Select(l => "    " + l));
                                continue;
                            }

                            if (it.isArray && it.propertyType == SerializedPropertyType.Generic &&
                                it.arrayElementType.StartsWith("PPtr<") && it.arraySize > RefArraySummarizeOver &&
                                SummarizeRefArray(it, comp, script, refs, lines, assets, ref wires, ref missing, ref assetLinks, n))
                            {
                                enter = false;
                                continue;
                            }

                            if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                            RefKind kind = refs.Classify(it);
                            if (kind == RefKind.Null) continue;
                            if ((kind == RefKind.Asset || kind == RefKind.Builtin) && !script)
                            {
                                AddAssetLink(assets, refs.Describe(it), comp, n);
                                assetLinks++;
                                continue;
                            }
                            if (kind == RefKind.Missing) missing++;
                            wires++;
                            lines.Add($"  {comp}.{FieldPath(it.propertyPath)} -> {refs.Describe(it)}");
                        }
                        tagged.AddRange(lines.Select(l => (comp, l)));
                    }
                    if (tagged.Count > 0) bySource.Add((n, tagged));
                }

                var output = new TextOut(maxChars);
                int shownSources = 0;
                string cutComponent = null;
                foreach (var (node, lines) in bySource)
                {
                    if (!output.Line(ShowPath(node.Rel, top))) break;
                    foreach (var (comp, line) in lines)
                    {
                        if (!output.Line(line)) { cutComponent = comp; break; }
                    }
                    if (output.Full) break;
                    shownSources++;
                }

                int shownAssets = 0;
                var sortedAssets = assets.OrderByDescending(a => a.Value.Count).ThenBy(a => a.Key).ToList();
                if (!output.Full && sortedAssets.Count > 0 && output.Line("asset links from built-in components:"))
                {
                    foreach (var a in sortedAssets)
                    {
                        string line = a.Value.Count == 1
                            ? $"  {a.Key} ({a.Value.Components}: {a.Value.FirstSource})"
                            : $"  {a.Key} x{a.Value.Count} ({a.Value.Components}, e.g. {a.Value.FirstSource})";
                        if (!output.Line(line)) break;
                        shownAssets++;
                    }
                }

                string where = start == top ? target.Label : $"{target.Label}:{snap.Rel}";
                string header = $"{where} refs  {snap.Count} objs: {wires} wires from {bySource.Count} objs" +
                                $" ({events} events, {listeners} listeners, {missing} MISSING); " +
                                $"{assetLinks} asset links to {assets.Count} assets";
                if (bySource.Count == 0 && assets.Count == 0) return header + "\nno outgoing references.";

                string note = null;
                if (output.Full)
                {
                    if (shownSources < bySource.Count)
                    {
                        var (next, nextLines) = bySource[shownSources];
                        string where2 = ShowPath(next.Rel, top);
                        var pending = nextLines.Select(l => l.comp).Distinct().ToList();
                        if (cutComponent != null) pending = pending.Skip(pending.IndexOf(cutComponent)).ToList();
                        int moreObjects = bySource.Count - shownSources - 1;
                        string comps = pending.Count > 0 ? $" ({string.Join(", ", pending.Take(6))}{(pending.Count > 6 ? ", ..." : "")})" : "";
                        HNode topLevel = next;
                        while (topLevel.Parent != null && topLevel.Parent != snap) topLevel = topLevel.Parent;
                        string rootHint = topLevel == snap ? "" : $" or root=\"{topLevel.Rel}\"";
                        note = $"... truncated at {where2}{comps}; {moreObjects} more source objs and {assets.Count} asset groups not shown. " +
                               $"Use component=\"{pending.FirstOrDefault() ?? "Name"}\"{rootHint}.";
                    }
                    else
                    {
                        note = $"... truncated: {sortedAssets.Count - shownAssets} more assets. Use root= or a larger max_chars.";
                    }
                }
                return output.Finish(header, note);
            }
        }

        private sealed class AssetLinks
        {
            public int Count;
            public string FirstSource;
            public readonly SortedSet<string> ComponentTypes = new SortedSet<string>();
            public string Components => ComponentTypes.Count <= 2 ? string.Join("/", ComponentTypes) : $"{ComponentTypes.First()}/+{ComponentTypes.Count - 1}";
        }

        private static void AddAssetLink(Dictionary<string, AssetLinks> assets, string asset, string comp, HNode source)
        {
            if (!assets.TryGetValue(asset, out AssetLinks links)) assets[asset] = links = new AssetLinks { FirstSource = source.Rel.Length == 0 ? source.T.name : source.Rel };
            links.Count++;
            links.ComponentTypes.Add(comp);
        }

        /// <summary>
        /// Long reference arrays (bones, LOD renderers, pooled objects) become one line:
        /// "SkinnedMeshRenderer.bones: [52] -> Armature/Hips, Armature/Hips/Spine, ... (52 internal)".
        /// Returns false to fall back to per-element handling.
        /// </summary>
        private static bool SummarizeRefArray(SerializedProperty array, string comp, bool script, RefPrinter refs,
            List<string> lines, Dictionary<string, AssetLinks> assets, ref int wires, ref int missing, ref int assetLinks, HNode source)
        {
            for (int i = 0; i < array.arraySize; i++)
            {
                if (array.GetArrayElementAtIndex(i).propertyType != SerializedPropertyType.ObjectReference) return false;
            }

            var counts = new Dictionary<RefKind, int>();
            var preview = new List<string>();
            for (int i = 0; i < array.arraySize; i++)
            {
                SerializedProperty el = array.GetArrayElementAtIndex(i);
                RefKind kind = refs.Classify(el);
                counts.TryGetValue(kind, out int k);
                counts[kind] = k + 1;
                if (kind == RefKind.Null) continue;
                if ((kind == RefKind.Asset || kind == RefKind.Builtin) && !script)
                {
                    AddAssetLink(assets, refs.Describe(el), comp, source);
                    assetLinks++;
                    continue;
                }
                if (kind == RefKind.Missing) missing++;
                wires++;
                if (preview.Count < 2) preview.Add(refs.Describe(el));
            }

            int shown = array.arraySize - (counts.TryGetValue(RefKind.Null, out int nulls) ? nulls : 0);
            if (!script)
            {
                shown -= counts.TryGetValue(RefKind.Asset, out int a) ? a : 0;
                shown -= counts.TryGetValue(RefKind.Builtin, out int b) ? b : 0;
            }
            if (shown <= 0) return true;
            string breakdown = string.Join(", ", counts.Where(kv => kv.Value > 0).Select(kv => $"{kv.Value} {kv.Key.ToString().ToLowerInvariant()}"));
            lines.Add($"  {comp}.{FieldPath(array.propertyPath)}: [{array.arraySize}] -> {string.Join(", ", preview)}, ... ({breakdown})");
            return true;
        }
    }
}
