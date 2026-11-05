using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BrokerTestKit.Engine
{
    /// <summary>
    /// A multi-sheet TEST CASE (REQ-13): one behaviour of the broker, spread over N Send sheets on
    /// federation A and M Receive sheets on federation B. Each sheet is an ordinary entity CSV
    /// (REQ-03) and still resolves against its own side's FOM (REQ-09).
    ///
    /// The manifest is a small text file, paths relative to itself:
    /// <code>
    /// # One aircraft on A becomes an aircraft AND an IFF object on B.
    /// name:    aircraft-iff
    /// send:    aircraft-in.csv
    /// receive: aircraft-out.csv
    /// receive: iff-out.csv
    /// </code>
    ///
    /// <b>How rows become steps.</b> Every row belongs to a GROUP, and one group is one step:
    /// publish every Send row in it, then wait until every Receive row in it has matched.
    ///   * A SEND row joins the group named by its own <c>entityId</c>, unless it holds a
    ///     REFERENCE cell - then it joins the group of the row it references (an emitter joins
    ///     its host aircraft's step).
    ///   * A RECEIVE row joins the step of the Send row with the same <c>entityId</c>. Only when
    ///     there is none - an IFF object the broker creates out of an aircraft's IFFCode1 - does
    ///     it follow its reference instead. A wrong reference therefore fails in the right step.
    ///   * Steps run in the order their groups first appear in the Send sheets, sheets in manifest
    ///     order. A group with no Receive rows is a negative test: nothing may arrive.
    ///   * A Receive row whose group has no Send row is a load error, not a silent skip.
    ///
    /// <b>Reference cells.</b> <c>@&lt;entityId&gt;</c> or <c>@&lt;Class&gt;/&lt;entityId&gt;</c> means
    /// "the RTI instance name of that row's object ON THIS SIDE" - RPR's <c>RTIobjectId</c>
    /// (<c>HostObjectIdentifier</c>, <c>FiringObjectIdentifier</c>, ...). On A it is the name the
    /// harness registered; on B it is the name of whichever object MATCHED that row, because
    /// VR-Exchange re-registers everything on B under its own names. Comparing against what A
    /// sent would be wrong (DESIGN §10, linked objects). Write <c>@@</c> for a literal leading @.
    ///
    /// An entityId may appear only once per sheet in a test case: one row is one object in one step.
    /// </summary>
    public sealed class TestCase
    {
        public string Name;
        public string ManifestPath;
        public readonly List<CaseSheet> Send = new List<CaseSheet>();
        public readonly List<CaseSheet> Receive = new List<CaseSheet>();
        public readonly List<CaseStep> Steps = new List<CaseStep>();

        public IEnumerable<CaseSheet> AllSheets
        {
            get { foreach (var s in Send) yield return s; foreach (var s in Receive) yield return s; }
        }

        /// <summary>Is this file a test-case manifest rather than a CSV sheet?</summary>
        public static bool LooksLikeManifest(string path)
        {
            return string.Equals(Path.GetExtension(path), ".testcase", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------- loading ---

        public static TestCase Load(string manifestPath, FomModel fomA, FomModel fomB)
        {
            if (!File.Exists(manifestPath)) throw new FileNotFoundException("test case not found: " + manifestPath);

            var tc = new TestCase
            {
                ManifestPath = Path.GetFullPath(manifestPath),
                Name = Path.GetFileNameWithoutExtension(manifestPath),
            };
            var dir = Path.GetDirectoryName(tc.ManifestPath) ?? ".";
            var file = Path.GetFileName(manifestPath);

            int lineNo = 0;
            foreach (var raw in File.ReadAllLines(manifestPath))
            {
                lineNo++;
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                var colon = line.IndexOf(':');
                if (colon <= 0)
                    throw new FormatException(file + ":" + lineNo + ": expected 'key: value', found '" + line + "'");
                var key = line.Substring(0, colon).Trim().ToLowerInvariant();
                var value = line.Substring(colon + 1).Trim();

                switch (key)
                {
                    case "name": tc.Name = value; break;
                    case "send":
                        tc.Send.Add(CaseSheet.Load(Rooted(dir, value), "A", fomA, isSend: true, index: tc.Send.Count));
                        break;
                    case "receive":
                        tc.Receive.Add(CaseSheet.Load(Rooted(dir, value), "B", fomB, isSend: false, index: tc.Receive.Count));
                        break;
                    default:
                        throw new FormatException(file + ":" + lineNo + ": unknown key '" + key +
                                                  "' (expected name, send or receive)");
                }
            }

            if (tc.Send.Count == 0) throw new FormatException(file + ": a test case needs at least one 'send:' sheet");

            tc.ResolveReferences();
            tc.BuildSteps();
            return tc;
        }

        private static string Rooted(string dir, string p)
        {
            return Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(dir, p));
        }

        // ---------------------------------------------------------- references ---

        private void ResolveReferences()
        {
            ResolveSide(Send);
            ResolveSide(Receive);
        }

        private static void ResolveSide(List<CaseSheet> sheets)
        {
            foreach (var sheet in sheets)
                foreach (var row in sheet.Rows)
                    foreach (var rc in row.Refs)
                    {
                        var hits = new List<CaseRow>();
                        foreach (var other in sheets)
                        {
                            if (rc.TargetClass != null &&
                                !string.Equals(other.ClassName, rc.TargetClass, StringComparison.OrdinalIgnoreCase))
                                continue;
                            foreach (var cand in other.Rows)
                                if (cand != row && string.Equals(cand.EntityId, rc.TargetId, StringComparison.Ordinal))
                                    hits.Add(cand);
                        }

                        var where = row.Where + " column '" + rc.Cell.Name + "'";
                        if (hits.Count == 0)
                            throw new FormatException(where + ": reference '" + rc.Text + "' names no row on side " +
                                                      sheet.Side + " (references stay on their own side)");
                        if (hits.Count > 1)
                            throw new FormatException(where + ": reference '" + rc.Text + "' is ambiguous - " +
                                                      hits.Count + " sheets have that entityId; write @<Class>/" + rc.TargetId);
                        if (hits[0].Sheet.IsInteraction)
                            throw new FormatException(where + ": reference '" + rc.Text + "' names an interaction - " +
                                                      "only objects have an RTI instance name to point at");
                        rc.Target = hits[0];
                    }
        }

        // --------------------------------------------------------------- steps ---

        private void BuildSteps()
        {
            var byKey = new Dictionary<string, CaseStep>(StringComparer.Ordinal);

            foreach (var sheet in Send)
                foreach (var row in sheet.Rows)
                {
                    var key = GroupOf(row, new HashSet<CaseRow>());
                    CaseStep step;
                    if (!byKey.TryGetValue(key, out step))
                    {
                        step = new CaseStep { Step = Steps.Count + 1, Key = key };
                        byKey[key] = step;
                        Steps.Add(step);
                    }
                    step.Send.Add(row);
                }

            // A Receive row whose entityId names a Send row belongs to THAT row's step, whatever
            // it references. Grouping it by its reference instead would let a wrong reference
            // (the very defect under test) quietly move the expectation into another step.
            var sendGroupById = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var step in Steps)
                foreach (var r in step.Send)
                    if (!sendGroupById.ContainsKey(r.EntityId)) sendGroupById[r.EntityId] = step.Key;

            foreach (var sheet in Receive)
                foreach (var row in sheet.Rows)
                {
                    string key;
                    if (!sendGroupById.TryGetValue(row.EntityId, out key))
                        key = ReceiveGroupOf(row, sendGroupById, new HashSet<CaseRow>());
                    CaseStep step;
                    if (!byKey.TryGetValue(key, out step))
                        throw new FormatException(row.Where + ": entity '" + key + "' ties to no Send row - " +
                                                  "give it the entityId of a Send row, or reference a Receive row that has one");
                    step.Receive.Add(row);
                }

            // A referenced row must go out (and be matched) before the row that points at it.
            foreach (var step in Steps)
            {
                var send = new List<CaseRow>(step.Send);
                step.Send.Clear(); step.Send.AddRange(TopoSort(send, byKey, step));
                var recv = new List<CaseRow>(step.Receive);
                step.Receive.Clear(); step.Receive.AddRange(TopoSort(recv, byKey, step));
            }
        }

        private static string GroupOf(CaseRow row, HashSet<CaseRow> seen)
        {
            if (!seen.Add(row))
                throw new FormatException(row.Where + ": references form a cycle");
            foreach (var rc in row.Refs)
                if (rc.Target != null) return GroupOf(rc.Target, seen);
            return row.EntityId;
        }

        /// <summary>
        /// A Receive row with no Send twin (e.g. the IFF object the broker creates) follows its
        /// reference to a row that has one.
        /// </summary>
        private static string ReceiveGroupOf(CaseRow row, Dictionary<string, string> sendGroupById, HashSet<CaseRow> seen)
        {
            if (!seen.Add(row)) throw new FormatException(row.Where + ": references form a cycle");
            string key;
            if (sendGroupById.TryGetValue(row.EntityId, out key)) return key;
            foreach (var rc in row.Refs)
                if (rc.Target != null) return ReceiveGroupOf(rc.Target, sendGroupById, seen);
            return row.EntityId;
        }

        private static List<CaseRow> TopoSort(List<CaseRow> rows, Dictionary<string, CaseStep> byKey, CaseStep step)
        {
            var placed = new HashSet<CaseRow>();
            var outp = new List<CaseRow>();
            var pending = new List<CaseRow>(rows);
            while (pending.Count > 0)
            {
                bool moved = false;
                for (int i = 0; i < pending.Count; i++)
                {
                    var r = pending[i];
                    bool ready = true;
                    foreach (var rc in r.Refs)
                        if (rc.Target != null && rows.Contains(rc.Target) && !placed.Contains(rc.Target)) ready = false;
                    if (!ready) continue;
                    placed.Add(r); outp.Add(r); pending.RemoveAt(i); i--; moved = true;
                }
                if (!moved) throw new FormatException("step '" + step.Key + "': references form a cycle");
            }
            return outp;
        }

        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("TEST CASE  " + Name + "   (" + ManifestPath + ")");
            foreach (var s in AllSheets)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0,-7} {1}  {2,-22} {3} row(s)  {4}",
                    s.IsSend ? "send" : "receive", s.Side, s.ClassName + (s.IsInteraction ? " (int)" : ""),
                    s.Rows.Count, Path.GetFileName(s.Sheet.SourceFile)));
            int neg = 0, refs = 0;
            foreach (var st in Steps)
            {
                if (st.Receive.Count == 0) neg++;
                foreach (var r in st.Send) refs += r.Refs.Count;
                foreach (var r in st.Receive) refs += r.Refs.Count;
            }
            sb.Append(string.Format(CultureInfo.InvariantCulture,
                "  {0} step(s), {1} negative, {2} reference cell(s)", Steps.Count, neg, refs));
            return sb.ToString();
        }
    }

    /// <summary>One sheet of a test case, with the class names the RTI and the agent will use.</summary>
    public sealed class CaseSheet
    {
        public string Side;               // "A" (send) or "B" (receive)
        public bool IsSend;
        public EntityCsvParser.Sheet Sheet;
        public readonly List<CaseRow> Rows = new List<CaseRow>();

        /// <summary>The name the RTI knows (fully qualified). Set from the FOM at load.</summary>
        public string RtiClass;
        /// <summary>The agent-side alias; the CSV's class name. Also what observed objects report.</summary>
        public string Alias;

        public string ClassName { get { return Sheet.ClassName; } }
        public bool IsInteraction { get { return Sheet.IsInteraction; } }

        internal static CaseSheet Load(string path, string side, FomModel fom, bool isSend, int index)
        {
            var cs = new CaseSheet { Side = side, IsSend = isSend };

            if (isSend)
            {
                EntityCsvParser.Sheet sh;
                var steps = EntityCsvParser.ReadInput(path, fom, out sh);
                cs.Sheet = sh;
                foreach (var s in steps) cs.Rows.Add(new CaseRow(cs, s.Step, s.EntityId, s.Attributes));
            }
            else
            {
                EntityCsvParser.Sheet sh;
                var steps = EntityCsvParser.ReadExpected(path, fom, out sh);
                cs.Sheet = sh;
                foreach (var s in steps) cs.Rows.Add(new CaseRow(cs, s.Step, s.EntityId, s.Expected));
            }

            cs.RtiClass = fom.RtiClassName(cs.ClassName) ?? cs.ClassName;
            cs.Alias = cs.ClassName;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in cs.Rows)
            {
                if (string.IsNullOrEmpty(r.EntityId))
                    throw new FormatException(r.Where + ": every row in a test case needs an entityId");
                if (!seen.Add(r.EntityId))
                    throw new FormatException(r.Where + ": entityId '" + r.EntityId + "' appears twice in this sheet " +
                                              "(in a test case one row is one object in one step)");
            }
            return cs;
        }
    }

    /// <summary>One row of one sheet.</summary>
    public sealed class CaseRow
    {
        public readonly CaseSheet Sheet;
        /// <summary>1-based data row within its sheet.</summary>
        public readonly int Row;
        public readonly string EntityId;
        /// <summary>Plain cells: encoded/compared exactly as in a single-sheet run.</summary>
        public readonly List<AttributeCell> Cells = new List<AttributeCell>();
        /// <summary>Reference cells: resolved to an instance name at run time.</summary>
        public readonly List<RefCell> Refs = new List<RefCell>();

        internal CaseRow(CaseSheet sheet, int row, string entityId, List<AttributeCell> cells)
        {
            Sheet = sheet; Row = row; EntityId = entityId;
            foreach (var c in cells)
            {
                var v = c.Value ?? "";
                if (v.StartsWith("@@", StringComparison.Ordinal)) { c.Value = v.Substring(1); Cells.Add(c); continue; }
                if (!v.StartsWith("@", StringComparison.Ordinal)) { Cells.Add(c); continue; }

                if (c.Name.IndexOf('.') >= 0)
                    throw new FormatException(Where + " column '" + c.Name + "': a reference must fill a WHOLE " +
                                              "attribute (an RTIobjectId), not a sub-field");

                var body = v.Substring(1).Trim();
                string cls = null, id = body;
                var slash = body.IndexOf('/');
                if (slash > 0) { cls = body.Substring(0, slash).Trim(); id = body.Substring(slash + 1).Trim(); }
                if (id.Length == 0) throw new FormatException(Where + " column '" + c.Name + "': empty reference '" + v + "'");

                Refs.Add(new RefCell { Cell = c, Text = v, TargetClass = cls, TargetId = id });
            }
        }

        /// <summary>"iff-out.csv row 2 (iff-1)" - for error messages.</summary>
        public string Where
        {
            get
            {
                return Path.GetFileName(Sheet.Sheet.SourceFile) + " row " + Row.ToString(CultureInfo.InvariantCulture) +
                       (string.IsNullOrEmpty(EntityId) ? "" : " (" + EntityId + ")");
            }
        }

        /// <summary>"IFF iff-1" - for reports.</summary>
        public string Label { get { return Sheet.ClassName + " " + EntityId; } }
    }

    /// <summary>A cell whose value is the instance name of another row's object.</summary>
    public sealed class RefCell
    {
        public AttributeCell Cell;
        public string Text;
        public string TargetClass;
        public string TargetId;
        public CaseRow Target;
    }

    /// <summary>One step of a test case: everything published together, everything waited for together.</summary>
    public sealed class CaseStep
    {
        public int Step;
        /// <summary>The group's entityId - names the step in the report.</summary>
        public string Key;
        public readonly List<CaseRow> Send = new List<CaseRow>();
        public readonly List<CaseRow> Receive = new List<CaseRow>();
    }
}
