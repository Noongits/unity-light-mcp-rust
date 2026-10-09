using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MCPForUnity.Editor.Helpers;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Prefabs
{
    public static partial class InspectPrefab
    {
        private const int TreeDefaultDepth = 2;
        private const int MaxComponentsPerLine = 10;
        private const int MaxGroupsPerParent = 40;

        /// <summary>
        /// mode=tree: indented hierarchy with short component names. Identical sibling subtrees are shown
        /// once with a count; nested prefab roots, inactive objects and missing scripts are marked.
        /// </summary>
        private static string RunTree(ToolParams p, int maxChars)
        {
            using (Target target = Target.Open(p.Get("prefab_path"), loadContents: false))
            {
                Transform top = target.Root.transform;
                string rootArg = p.Get("root");
                Transform start = string.IsNullOrWhiteSpace(rootArg) ? top : ResolvePath(top, rootArg);
                HNode snap = Snapshot(start, top);

                string filter = p.Get("filter");
                bool filtering = !string.IsNullOrWhiteSpace(filter);
                int depth = p.GetInt("depth") ?? (filtering ? int.MaxValue : TreeDefaultDepth);
                if (depth < 0) depth = 0;

                var types = new TypeNamer();
                foreach (HNode n in Walk(snap))
                {
                    foreach (Component c in n.Comps) types.Add(c.GetType());
                }

                int matches = filtering ? ApplyFilter(snap, filter.Trim(), types) : 0;
                var writer = new TreeWriter(maxChars, depth, filtering, types);
                if (!filtering || snap.Keep) writer.Emit(snap, 0, snap.Seg, 1, 1);

                string where = start == top ? target.Label : $"{target.Label}:{snap.Rel}";
                string depthText = depth == int.MaxValue ? "all" : depth.ToString();
                string filterText = filtering ? $"  filter={filter.Trim()} ({matches} match)" : "";
                string header = $"{where}  {snap.Count} objs  depth<={depthText}{filterText}  (shown {writer.Out.Lines}){PrefabHeaderInfo(target)}";
                if (filtering && matches == 0) return header + "\nno objects match the filter.";
                return writer.Out.Finish(header, writer.TruncationNote(snap));
            }
        }

        /// <summary>Marks nodes that match (name, component or nested prefab) or contain a match.</summary>
        private static int ApplyFilter(HNode n, string filter, TypeNamer types)
        {
            int matches = 0;
            bool keep = Matches(n, filter, types);
            if (keep) matches++;
            foreach (HNode c in n.Children)
            {
                matches += ApplyFilter(c, filter, types);
                keep |= c.Keep;
            }
            n.Keep = keep;
            return matches;
        }

        private static bool Matches(HNode n, string filter, TypeNamer types)
        {
            if (n.T.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (n.NestedAsset != null && Path.GetFileName(n.NestedAsset).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (n.MissingScripts > 0 && "missing".IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return n.Comps.Any(c => types.Name(c.GetType()).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private sealed class TreeWriter
        {
            public readonly TextOut Out;
            private readonly int _depth;
            private readonly bool _filtering;
            private readonly TypeNamer _types;
            private HNode _cut;
            private int _covered;

            public TreeWriter(int maxChars, int depth, bool filtering, TypeNamer types)
            {
                Out = new TextOut(maxChars);
                _depth = depth;
                _filtering = filtering;
                _types = types;
            }

            /// <param name="count">Identical siblings this line stands for.</param>
            /// <param name="mult">Copies of this subtree represented by collapsed ancestors.</param>
            public bool Emit(HNode n, int level, string name, int count, int mult)
            {
                var kids = VisibleChildren(n);
                bool atLimit = level >= _depth && kids.Count > 0;

                var sb = new StringBuilder();
                sb.Append(' ', level * 2).Append(name);
                if (count > 1) sb.Append(" x").Append(count);
                if (!n.Go.activeSelf) sb.Append(" (-)");
                if (n.NestedMissing) sb.Append(" @MISSING-PREFAB");
                else if (n.NestedAsset != null) sb.Append(" @").Append(Path.GetFileName(n.NestedAsset));
                AppendComponents(sb, n);
                if (atLimit) AppendCollapsed(sb, n);

                if (!Out.Line(sb.ToString()))
                {
                    _cut = n;
                    return false;
                }
                _covered += count * mult * (atLimit ? n.Count : 1);
                if (atLimit) return true;

                // Group identical siblings (same structure and components), keeping first-seen order.
                var groups = new List<List<HNode>>();
                var bySig = new Dictionary<int, List<HNode>>();
                foreach (HNode k in kids)
                {
                    // A filtered subtree can differ even when the unfiltered structural hash matches.
                    // Keep each matching path visible instead of collapsing away another match.
                    if (_filtering)
                    {
                        groups.Add(new List<HNode> { k });
                        continue;
                    }
                    if (!bySig.TryGetValue(k.Sig, out var g))
                    {
                        bySig[k.Sig] = g = new List<HNode>();
                        groups.Add(g);
                    }
                    g.Add(k);
                }

                for (int i = 0; i < groups.Count; i++)
                {
                    if (i == MaxGroupsPerParent)
                    {
                        int restObjs = groups.Skip(i).Sum(g => g.Sum(x => x.Count));
                        int restKids = groups.Skip(i).Sum(g => g.Count);
                        if (!Out.Line(new string(' ', (level + 1) * 2) + $"...{restKids} more children ({restObjs} objs). Use root=\"{ShowPath(n.Rel, n.T)}\" with filter=")) { _cut = n; return false; }
                        _covered += restObjs * count * mult;
                        break;
                    }
                    List<HNode> group = groups[i];
                    string label = group.Count == 1 ? group[0].Seg : RangeName(group);
                    if (!Emit(group[0], level + 1, label, group.Count, count * mult)) return false;
                }
                return true;
            }

            private List<HNode> VisibleChildren(HNode n) =>
                _filtering ? n.Children.Where(c => c.Keep).ToList() : n.Children;

            private void AppendComponents(StringBuilder sb, HNode n)
            {
                var names = n.Comps.Where(c => !(c is Transform) || c is RectTransform)
                    .Select(c => _types.Name(c.GetType())).ToList();
                if (n.MissingScripts > 0) names.Add(n.MissingScripts == 1 ? "!missing" : $"!missing x{n.MissingScripts}");
                if (names.Count == 0) return;
                sb.Append(" [");
                sb.Append(string.Join(", ", names.Take(MaxComponentsPerLine)));
                if (names.Count > MaxComponentsPerLine) sb.Append(", +").Append(names.Count - MaxComponentsPerLine);
                sb.Append(']');
            }

            /// <summary>" ...118 below [@Seat.prefab x2, @Dash.prefab]": what sits under a collapsed node.</summary>
            private static void AppendCollapsed(StringBuilder sb, HNode n)
            {
                sb.Append(" ...").Append(n.Count - 1).Append(" below");
                var nested = Walk(n).Skip(1)
                    .Where(d => d.NestedAsset != null || d.NestedMissing)
                    .GroupBy(d => d.NestedMissing ? "MISSING-PREFAB" : Path.GetFileName(d.NestedAsset))
                    .Select(g => g.Count() == 1 ? "@" + g.Key : $"@{g.Key} x{g.Count()}")
                    .ToList();
                if (nested.Count == 0) return;
                sb.Append(" [").Append(string.Join(", ", nested.Take(3)));
                if (nested.Count > 3) sb.Append(", +").Append(nested.Count - 3).Append(" more prefabs");
                sb.Append(']');
            }

            public string TruncationNote(HNode start)
            {
                if (_cut == null) return null;
                HNode top = _cut;
                while (top.Parent != null && top.Parent != start) top = top.Parent;
                int notShown = Math.Max(0, start.Count - _covered);
                string rootHint = top == start ? "" : $"root=\"{top.Rel}\" or ";
                int lower = Math.Max(1, Math.Min(_depth, 50) - 1);
                return $"... truncated: {notShown} objs not shown (from {ShowPath(_cut.Rel, _cut.T)}). Use {rootHint}depth={lower} or filter=.";
            }
        }

        /// <summary>"Wheel_FL..RR" for siblings sharing a prefix; the plain name when all names match.</summary>
        internal static string RangeName(List<HNode> group)
        {
            string first = group[0].T.name;
            string last = group[group.Count - 1].T.name;
            if (group.All(g => g.T.name == first)) return first;
            int prefix = 0;
            int max = group.Min(g => g.T.name.Length);
            while (prefix < max && group.All(g => g.T.name[prefix] == first[prefix])) prefix++;
            string firstRest = first.Substring(prefix).Trim();
            string lastRest = last.Substring(prefix).Trim();
            // "Wheel_FL..RR" when both ends have their own suffix; "col*" when one is just the shared prefix.
            if (prefix >= 2 && firstRest.Length > 0 && lastRest.Length > 0) return first + ".." + last.Substring(prefix);
            if (prefix >= 2) return first.Substring(0, prefix).TrimEnd(' ', '_', '-', '(', '.') + "*";
            return first + ".." + last;
        }
    }
}
