using System;
using System.Collections.Generic;
using System.Text;

namespace BrokerTestKit.Engine
{
    /// <summary>How one class, attribute or parameter compares across the two sides of a pair.</summary>
    public enum DiffKind
    {
        /// <summary>Same name, same type.</summary>
        Same,
        /// <summary>The type has another name on B but the same bytes on the wire (OMT <c>boolean</c> → RPR 2.0 <c>RPRboolean</c>).</summary>
        Renamed,
        /// <summary>A different layout: the broker has to translate it.</summary>
        Differs,
        /// <summary>A class on both sides whose shared members agree, but one side has members the other lacks.</summary>
        Partial,
        OnlyA,
        OnlyB,
    }

    /// <summary>One attribute (or parameter) of a matched class.</summary>
    public sealed class MemberDiff
    {
        public string Name { get; set; }
        public string TypeA { get; set; }
        public string TypeB { get; set; }
        /// <summary>Wire size in bytes, -1 = variable, null = not on that side.</summary>
        public int? SizeA { get; set; }
        public int? SizeB { get; set; }
        /// <summary><see cref="FomModel.WireSignature"/> on each side.</summary>
        public string WireA { get; set; }
        public string WireB { get; set; }
        public DiffKind Kind { get; set; }

        public string KindText { get { return FomDiff.Words(Kind); } }
        /// <summary>The flattened wire layout of each side, for a tooltip on a row that differs.</summary>
        public string WireText
        {
            get
            {
                if (Kind == DiffKind.OnlyA) return "A: " + WireA;
                if (Kind == DiffKind.OnlyB) return "B: " + WireB;
                return Kind == DiffKind.Differs ? "A: " + WireA + "\nB: " + WireB : WireA;
            }
        }
        public string SizeText
        {
            get
            {
                if (Kind == DiffKind.OnlyA) return Size(SizeA);
                if (Kind == DiffKind.OnlyB) return Size(SizeB);
                return SizeA == SizeB ? Size(SizeA) : Size(SizeA) + " / " + Size(SizeB);
            }
        }
        private static string Size(int? s) { return s == null ? "—" : s < 0 ? "var" : s + " B"; }
    }

    /// <summary>One class or interaction: matched across the sides, or present on one side only.</summary>
    public sealed class ClassDiff
    {
        /// <summary>The FOM's own spelling on each side (null when that side has none).</summary>
        public string NameA { get; set; }
        public string NameB { get; set; }
        public bool IsInteraction { get; set; }
        /// <summary>Matched on the bare class name because the paths differ.</summary>
        public bool MatchedByLeaf { get; set; }
        public DiffKind Kind { get; set; }
        public readonly List<MemberDiff> Members = new List<MemberDiff>();

        public string Leaf { get { return FomDiff.Leaf(NameA ?? NameB); } }
        public string What { get { return IsInteraction ? "interaction" : "class"; } }
        /// <summary>The bare name, marked when it is an interaction.</summary>
        public string Display { get { return IsInteraction ? Leaf + "  (interaction)" : Leaf; } }
        public string KindText { get { return FomDiff.Words(Kind); } }
        public bool IsMatched { get { return NameA != null && NameB != null; } }

        public int Count(DiffKind k) { int n = 0; foreach (var m in Members) if (m.Kind == k) n++; return n; }

        /// <summary>The members both sides carry: what a matched Send / Receive template pair can check.</summary>
        public List<string> Shared()
        {
            var list = new List<string>();
            foreach (var m in Members) if (m.Kind != DiffKind.OnlyA && m.Kind != DiffKind.OnlyB) list.Add(m.Name);
            return list;
        }

        /// <summary>"12 shared · 2 differ · 3 only A · 1 only B" (or the one-sided equivalent).</summary>
        public string Summary
        {
            get
            {
                if (!IsMatched) return Members.Count + (IsInteraction ? " parameters" : " attributes");
                var parts = new List<string> { Count(DiffKind.Same) + " same" };
                if (Count(DiffKind.Renamed) > 0) parts.Add(Count(DiffKind.Renamed) + " renamed");
                if (Count(DiffKind.Differs) > 0) parts.Add(Count(DiffKind.Differs) + " differ");
                if (Count(DiffKind.OnlyA) > 0) parts.Add(Count(DiffKind.OnlyA) + " only A");
                if (Count(DiffKind.OnlyB) > 0) parts.Add(Count(DiffKind.OnlyB) + " only B");
                return string.Join(" · ", parts.ToArray());
            }
        }
    }

    /// <summary>
    /// A structural comparison of two FOMs (REQ-10): which classes and interactions both sides
    /// have, and per matched class which attributes match by name and wire type.
    ///
    /// The two sides of a broker crossing never spell classes the same way: an OMT stores
    /// <c>BaseEntity.PhysicalEntity.Platform.GroundVehicle</c>, an FDD stores
    /// <c>HLAobjectRoot.BaseEntity.PhysicalEntity.Platform.GroundVehicle</c>. So classes match on
    /// the path with the root stripped, then - when that fails - on the bare name, provided it
    /// is unique on both sides. A type "differs" when the datatype name or the wire size is
    /// different. That is a prompt to look, not a fault: translating between them is the
    /// broker's whole job (RPR 1.0 <c>WorldLocation</c> becomes RPR 2.0 <c>Spatial</c>).
    /// </summary>
    public static class FomDiff
    {
        public static List<ClassDiff> Compare(FomModel a, FomModel b)
        {
            if (a == null) throw new ArgumentNullException("a");
            if (b == null) throw new ArgumentNullException("b");
            var all = new List<ClassDiff>();
            all.AddRange(CompareTable(a, b, a.Classes, b.Classes, false));
            all.AddRange(CompareTable(a, b, a.Interactions, b.Interactions, true));
            all.Sort((x, y) =>
            {
                var r = Rank(x).CompareTo(Rank(y));
                if (r != 0) return r;
                r = x.IsInteraction.CompareTo(y.IsInteraction);
                return r != 0 ? r : string.Compare(x.Leaf, y.Leaf, StringComparison.OrdinalIgnoreCase);
            });
            return all;
        }

        /// <summary>Matched classes first, then A-only, then B-only.</summary>
        private static int Rank(ClassDiff d) { return d.IsMatched ? 0 : d.NameA != null ? 1 : 2; }

        private static List<ClassDiff> CompareTable(FomModel a, FomModel b,
            Dictionary<string, Dictionary<string, string>> ta,
            Dictionary<string, Dictionary<string, string>> tb, bool interactions)
        {
            var outp = new List<ClassDiff>();
            var bByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var bLeafCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var aLeafCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in tb.Keys) { bByPath[Path(k)] = k; Bump(bLeafCount, Leaf(k)); }
            foreach (var k in ta.Keys) Bump(aLeafCount, Leaf(k));

            var usedB = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pendingA = new List<string>();
            foreach (var ka in ta.Keys)
            {
                string kb;
                if (bByPath.TryGetValue(Path(ka), out kb)) { usedB.Add(kb); outp.Add(Pair(a, b, ka, kb, ta[ka], tb[kb], interactions, false)); }
                else pendingA.Add(ka);
            }
            foreach (var ka in pendingA)
            {
                var leaf = Leaf(ka);
                string kb = null;
                if (aLeafCount[leaf] == 1 && bLeafCount.ContainsKey(leaf) && bLeafCount[leaf] == 1)
                    foreach (var k in tb.Keys) if (!usedB.Contains(k) && string.Equals(Leaf(k), leaf, StringComparison.OrdinalIgnoreCase)) { kb = k; break; }
                if (kb != null) { usedB.Add(kb); outp.Add(Pair(a, b, ka, kb, ta[ka], tb[kb], interactions, true)); }
                else outp.Add(OneSided(a, ka, ta[ka], interactions, true));
            }
            foreach (var kb in tb.Keys)
                if (!usedB.Contains(kb)) outp.Add(OneSided(b, kb, tb[kb], interactions, false));
            return outp;
        }

        private static ClassDiff Pair(FomModel a, FomModel b, string ka, string kb,
            Dictionary<string, string> ma, Dictionary<string, string> mb, bool interaction, bool byLeaf)
        {
            var d = new ClassDiff { NameA = ka, NameB = kb, IsInteraction = interaction, MatchedByLeaf = byLeaf };
            foreach (var kv in ma)
            {
                var m = new MemberDiff { Name = kv.Key, TypeA = kv.Value, SizeA = SizeOf(a, kv.Value), WireA = a.WireSignature(kv.Value) };
                string tbType;
                if (mb.TryGetValue(kv.Key, out tbType))
                {
                    m.TypeB = tbType;
                    m.SizeB = SizeOf(b, tbType);
                    m.WireB = b.WireSignature(tbType);
                    var sameWire = m.SizeA == m.SizeB && string.Equals(m.WireA, m.WireB, StringComparison.Ordinal) &&
                                   m.WireA.IndexOf('?') < 0;
                    m.Kind = !sameWire ? DiffKind.Differs
                           : string.Equals(kv.Value, tbType, StringComparison.OrdinalIgnoreCase) ? DiffKind.Same : DiffKind.Renamed;
                }
                else m.Kind = DiffKind.OnlyA;
                d.Members.Add(m);
            }
            foreach (var kv in mb)
                if (!ma.ContainsKey(kv.Key))
                    d.Members.Add(new MemberDiff { Name = kv.Key, TypeB = kv.Value, SizeB = SizeOf(b, kv.Value), WireB = b.WireSignature(kv.Value), Kind = DiffKind.OnlyB });
            d.Members.Sort((x, y) =>
            {
                var r = x.Kind.CompareTo(y.Kind);
                return r != 0 ? r : string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            });
            bool layout = false, oneSided = false;
            foreach (var m in d.Members)
            {
                if (m.Kind == DiffKind.Differs) layout = true;
                if (m.Kind == DiffKind.OnlyA || m.Kind == DiffKind.OnlyB) oneSided = true;
            }
            d.Kind = layout ? DiffKind.Differs : oneSided ? DiffKind.Partial : DiffKind.Same;
            return d;
        }

        private static ClassDiff OneSided(FomModel m, string key, Dictionary<string, string> members, bool interaction, bool isA)
        {
            var d = new ClassDiff { IsInteraction = interaction, Kind = isA ? DiffKind.OnlyA : DiffKind.OnlyB };
            if (isA) d.NameA = key; else d.NameB = key;
            var names = new List<string>(members.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var n in names)
            {
                var md = new MemberDiff { Name = n, Kind = d.Kind };
                if (isA) { md.TypeA = members[n]; md.SizeA = SizeOf(m, members[n]); md.WireA = m.WireSignature(members[n]); }
                else { md.TypeB = members[n]; md.SizeB = SizeOf(m, members[n]); md.WireB = m.WireSignature(members[n]); }
                d.Members.Add(md);
            }
            return d;
        }

        private static int SizeOf(FomModel m, string type)
        {
            try { return m.SizeOf(type); } catch { return -1; }
        }

        private static void Bump(Dictionary<string, int> d, string k) { int n; d.TryGetValue(k, out n); d[k] = n + 1; }

        private static readonly string[] Roots = { "HLAobjectRoot.", "HLAinteractionRoot.", "ObjectRoot.", "InteractionRoot." };

        /// <summary>The class path without the root, so OMT and FDD spellings meet.</summary>
        public static string Path(string name)
        {
            var n = name ?? "";
            foreach (var r in Roots)
                if (n.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return n.Substring(r.Length);
            return n;
        }

        public static string Leaf(string name)
        {
            var n = name ?? "";
            var dot = n.LastIndexOf('.');
            return dot < 0 ? n : n.Substring(dot + 1);
        }

        public static string Words(DiffKind k)
        {
            switch (k)
            {
                case DiffKind.Same: return "Same";
                case DiffKind.Renamed: return "Renamed";
                case DiffKind.Partial: return "Partial";
                case DiffKind.Differs: return "Differs";
                case DiffKind.OnlyA: return "Only on A";
                default: return "Only on B";
            }
        }

        /// <summary>Plain-text report: one line per class, and the member detail of any named class.</summary>
        public static string Report(List<ClassDiff> diffs, string onlyClass)
        {
            var sb = new StringBuilder();
            int matched = 0, onlyA = 0, onlyB = 0;
            foreach (var d in diffs) { if (d.IsMatched) matched++; else if (d.NameA != null) onlyA++; else onlyB++; }
            sb.AppendFormat("{0} matched, {1} only on A, {2} only on B\n\n", matched, onlyA, onlyB);
            foreach (var d in diffs)
            {
                if (onlyClass != null &&
                    !string.Equals(d.Leaf, onlyClass, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Path(d.NameA), Path(onlyClass), StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Path(d.NameB), Path(onlyClass), StringComparison.OrdinalIgnoreCase)) continue;
                sb.AppendFormat("{0,-10} {1,-12} {2,-34} {3}{4}\n", d.KindText, d.IsInteraction ? "interaction" : "class",
                    d.Leaf, d.Summary.Replace(" · ", ", "), d.MatchedByLeaf ? "   (matched by name: " + d.NameA + " / " + d.NameB + ")" : "");
                if (onlyClass == null) continue;
                foreach (var m in d.Members)
                    sb.AppendFormat("    {0,-10} {1,-30} {2,-30} {3,-30} {4}{5}\n", m.KindText, m.Name, m.TypeA ?? "-", m.TypeB ?? "-",
                        m.SizeText.Replace("—", "-"), m.Kind == DiffKind.Differs ? "   " + m.WireA + "  vs  " + m.WireB : "");
            }
            return sb.ToString();
        }
    }
}
