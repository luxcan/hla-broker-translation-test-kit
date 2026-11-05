using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace BrokerTestKit.Engine
{
    /// <summary>
    /// Runs a multi-sheet <see cref="TestCase"/> (REQ-13) over the native agents.
    ///
    /// Same loop as <see cref="NativeTestRunner"/> (DESIGN 9.1) - quiesce B, publish, match on
    /// content or time out, record - with three differences:
    ///   1. a step publishes SEVERAL rows, possibly of several classes, referenced rows first;
    ///   2. it waits for EVERY Receive row of the step, across every class, and each observed
    ///      object or interaction can satisfy only one of them;
    ///   3. reference cells compare against the instance name of the object that matched the
    ///      referenced row ON B, so the check is "B's IFF points at B's aircraft".
    ///
    /// The verdict belongs to the step. A missing IFF fails the aircraft's step even though the
    /// aircraft itself arrived intact - that is the one-to-many defect this exists to catch.
    /// </summary>
    public sealed class TestCaseRunner
    {
        private readonly NativeAgentHost _a;
        private readonly NativeAgentHost _b;
        private readonly NativeSideConfig _cfgA;
        private readonly NativeSideConfig _cfgB;
        private readonly RunOptions _opt;
        private readonly HashSet<string> _registered = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<CaseRow, string> _sendNames = new Dictionary<CaseRow, string>();
        private int _consecutiveTimeouts;

        /// <summary>
        /// Receive row -&gt; instance name of the B object that matched it, for the whole run. A
        /// reference to a row of an EARLIER step resolves through this; one in the same step
        /// resolves through that step's live matches.
        /// </summary>
        private readonly Dictionary<CaseRow, string> _matchedNames = new Dictionary<CaseRow, string>();

        public event Action<int, string> Progress;

        /// <summary>Raised on the runner's thread as each step finishes, so a GUI can fill in live.</summary>
        public event Action<StepResult> StepCompleted;

        /// <summary>Checked before every step; return true to stop cleanly after the current one.</summary>
        public Func<bool> ShouldStop;

        /// <param name="cfgA">Join parameters for A. Its class fields are ignored: each sheet names its own.</param>
        public TestCaseRunner(NativeAgentHost a, NativeAgentHost b, NativeSideConfig cfgA, NativeSideConfig cfgB,
                              RunOptions options = null)
        {
            if (a == null) throw new ArgumentNullException("a");
            if (b == null) throw new ArgumentNullException("b");
            _a = a; _b = b; _cfgA = cfgA; _cfgB = cfgB;
            _opt = options ?? new RunOptions();
        }

        // ------------------------------------------------------------- prepare ---

        public void Prepare(TestCase tc)
        {
            AssignSendNames(tc);

            _a.Join(_cfgA.FederationName, _cfgA.FedFile, _cfgA.FederateName,
                    _cfgA.FederateType, _cfgA.JoinFomModules, _cfgA.Mim);
            Report(0, _cfgA.Label + " joined " + _cfgA.FederationName);

            _b.Join(_cfgB.FederationName, _cfgB.FedFile, _cfgB.FederateName,
                    _cfgB.FederateType, _cfgB.JoinFomModules, _cfgB.Mim);
            Report(0, _cfgB.Label + " joined " + _cfgB.FederationName);

            Declare(_a, tc.Send, "pub", _cfgA.Label, "publishes");
            Declare(_b, tc.Receive, "sub", _cfgB.Label, "subscribes");
        }

        /// <summary>
        /// One declaration per CLASS, not per sheet: two sheets of one class share its
        /// publication, and declaring it twice would replace the first attribute set.
        /// </summary>
        private void Declare(NativeAgentHost host, List<CaseSheet> sheets, string mode, string label, string verb)
        {
            var order = new List<CaseSheet>();
            var attrs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sheets)
            {
                List<string> list;
                if (!attrs.TryGetValue(s.Alias, out list)) { list = new List<string>(); attrs[s.Alias] = list; order.Add(s); }
                foreach (var a in NativeTestRunner.BaseAttributes(new List<string>(s.Sheet.Columns)))
                    if (!list.Contains(a)) list.Add(a);
            }

            foreach (var s in order)
            {
                var list = attrs[s.Alias];
                if (s.IsInteraction) host.DeclareInteraction(s.RtiClass, list, mode, s.Alias);
                else                 host.DeclareClass(s.RtiClass, list, mode, s.Alias);
                Report(0, label + " " + verb + " " + (s.IsInteraction ? "interaction " : "object ") +
                          s.RtiClass + " [" + string.Join(",", list.ToArray()) + "]");
            }
        }

        /// <summary>
        /// Instance names on A. "BTK-&lt;id&gt;" as in a single-sheet run, unless two Send sheets
        /// share the id - then the class goes in too, because an instance name is federation-unique.
        /// </summary>
        private void AssignSendNames(TestCase tc)
        {
            var count = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var s in tc.Send)
                if (!s.IsInteraction)
                    foreach (var r in s.Rows)
                    {
                        int n; count.TryGetValue(r.EntityId, out n); count[r.EntityId] = n + 1;
                    }

            foreach (var s in tc.Send)
                if (!s.IsInteraction)
                    foreach (var r in s.Rows)
                        _sendNames[r] = NativeTestRunner.SafeInstanceName(
                            count[r.EntityId] > 1 ? s.Alias + "-" + r.EntityId : r.EntityId);
        }

        // ----------------------------------------------------------------- run ---

        public RunReport Run(TestCase tc)
        {
            var report = new RunReport { TestCase = tc.Name };
            var sw = Stopwatch.StartNew();

            foreach (var step in tc.Steps)
            {
                var stop = ShouldStop;
                if (stop != null && stop())
                {
                    report.AbortReason = "stopped by the operator";
                    break;
                }

                var result = RunStep(step);
                Report(step.Step, result.Outcome + " - " + result.Message);
                report.Steps.Add(result);
                var done = StepCompleted;
                if (done != null) done(result);

                if (result.Outcome == Outcome.Timeout) _consecutiveTimeouts++;
                else _consecutiveTimeouts = 0;

                if (_opt.AbortAfterConsecutiveTimeouts > 0 &&
                    _consecutiveTimeouts >= _opt.AbortAfterConsecutiveTimeouts)
                {
                    report.AbortReason = _consecutiveTimeouts + " consecutive timeout(s) - " +
                        "federation B has stopped producing anything, so the broker or its RTI is " +
                        "most likely gone. The remaining steps were not attempted.";
                    Report(step.Step, "ABORT - " + report.AbortReason);
                    break;
                }
            }

            Tally(tc, report);
            sw.Stop();
            report.Duration = sw.Elapsed;
            return report;
        }

        private StepResult RunStep(CaseStep step)
        {
            var result = new StepResult { Step = step.Step, EntityId = step.Key };
            var stepSw = Stopwatch.StartNew();

            // 1) quiesce B
            _b.ClearObserved();
            var quiesceUntil = DateTime.UtcNow + _opt.Quiesce;
            while (DateTime.UtcNow < quiesceUntil) System.Threading.Thread.Sleep(20);

            // 2) publish every Send row of the group, referenced rows first
            DateTime firstSent = DateTime.MaxValue;
            try
            {
                foreach (var row in step.Send)
                {
                    var t = Publish(row, step.Step);
                    if (t < firstSent) firstSent = t;
                }
            }
            catch (Exception ex)
            {
                stepSw.Stop();
                result.Outcome = Outcome.Fail;
                result.Elapsed = stepSw.Elapsed;
                result.Message = "publish failed: " + ex.Message;
                return result;
            }

            // 3) match every Receive row, or time out
            var negative = step.Receive.Count == 0;
            var budget = negative ? _opt.NegativeTimeout : _opt.Timeout;
            var deadline = DateTime.UtcNow + budget;

            var matched = new Dictionary<CaseRow, Observed>();
            var used = new HashSet<object>();
            var best = new Dictionary<CaseRow, List<FieldResult>>();
            var bestScore = new Dictionary<CaseRow, int>();
            var sawClass = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Observed violation = null;

            while (DateTime.UtcNow < deadline)
            {
                var seen = Snapshot();
                foreach (var o in seen) sawClass.Add(o.ClassName ?? "");

                if (negative)
                {
                    if (seen.Count > 0) { violation = seen[0]; break; }
                }
                else
                {
                    // Rows are topologically ordered, but an object can arrive before the one it
                    // references, so sweep until a pass makes no progress.
                    bool progress = true;
                    while (progress)
                    {
                        progress = false;
                        foreach (var row in step.Receive)
                        {
                            if (matched.ContainsKey(row) || !RefsReady(row, step, matched)) continue;
                            foreach (var cand in seen)
                            {
                                if (used.Contains(cand.Item) ||
                                    !string.Equals(cand.ClassName, row.Sheet.Alias, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                var fields = CompareRow(row, cand, matched, _matchedNames);
                                var score = CountOk(fields);
                                int prev;
                                if (!bestScore.TryGetValue(row, out prev) || score > prev)
                                { bestScore[row] = score; best[row] = fields; }

                                if (score == fields.Count && fields.Count > 0)
                                {
                                    matched[row] = cand; used.Add(cand.Item); best[row] = fields;
                                    progress = true;
                                    break;
                                }
                            }
                        }
                    }
                    if (matched.Count == step.Receive.Count) break;
                }
                System.Threading.Thread.Sleep(25);
            }

            stepSw.Stop();
            result.Elapsed = stepSw.Elapsed;

            // Latency of a step = until its LAST expected item landed: that is when B was complete.
            if (firstSent != DateTime.MaxValue && matched.Count > 0)
            {
                DateTime last = DateTime.MinValue;
                foreach (var m in matched.Values) if (m.Arrived.HasValue && m.Arrived.Value > last) last = m.Arrived.Value;
                if (last != DateTime.MinValue)
                {
                    var d = last - firstSent;
                    result.Latency = d < TimeSpan.Zero ? TimeSpan.Zero : d;
                }
            }

            // 4) record
            if (negative)
            {
                if (violation == null)
                {
                    result.Outcome = Outcome.Pass;
                    result.Message = "negative test: nothing arrived (as expected)";
                }
                else
                {
                    result.Outcome = Outcome.Fail;
                    result.Message = "negative test violated: " + violation.Label + " arrived";
                }
                return result;
            }

            int ok = 0;
            foreach (var row in step.Receive)
            {
                var part = new PartResult { ClassName = row.Sheet.ClassName, EntityId = row.EntityId };
                Observed m;
                List<FieldResult> fields;
                best.TryGetValue(row, out fields);

                if (matched.TryGetValue(row, out m))
                {
                    _matchedNames[row] = m.InstanceName;
                    part.Outcome = Outcome.Pass;
                    part.Message = "matched " + m.Label;
                    ok++;
                }
                else if (!RefsReady(row, step, matched))
                {
                    part.Outcome = sawClass.Contains(row.Sheet.Alias) ? Outcome.Fail : Outcome.Timeout;
                    part.Message = "not checked - it references " + UnmatchedTargets(row, step, matched) +
                                   ", which never matched";
                }
                else if (fields != null)
                {
                    int score; bestScore.TryGetValue(row, out score);
                    part.Outcome = Outcome.Fail;
                    part.Message = (row.Sheet.IsInteraction ? "interaction" : "object") + " arrived but " +
                                   (fields.Count - score) + " of " + fields.Count + " field(s) differ";
                }
                else
                {
                    part.Outcome = Outcome.Timeout;
                    part.Message = "no " + row.Sheet.ClassName + " arrived on " + _cfgB.Label;
                }
                result.Parts.Add(part);

                if (fields != null)
                    foreach (var f in fields)
                        result.Fields.Add(new FieldResult
                        {
                            Name = row.Sheet.ClassName + " " + row.EntityId + " › " + f.Name,
                            Expected = f.Expected, Actual = f.Actual, Ok = f.Ok, Note = f.Note,
                        });
            }

            if (ok == step.Receive.Count)
            {
                result.Outcome = Outcome.Pass;
                result.Message = "all " + ok + " expected item(s) matched across " + ClassCount(step) + " class(es)";
                if (result.Elapsed.TotalSeconds > budget.TotalSeconds * _opt.NearTimeoutFraction)
                    result.Message += "  [WARN near timeout - margin is eroding]";
                return result;
            }

            // Timeout only when B produced nothing at all for any class this step expects; a
            // partial arrival means the broker is alive and translating incompletely - a Fail.
            bool anyArrived = false;
            foreach (var row in step.Receive) if (sawClass.Contains(row.Sheet.Alias)) anyArrived = true;

            result.Outcome = anyArrived ? Outcome.Fail : Outcome.Timeout;
            var missing = new List<string>();
            foreach (var p in result.Parts) if (p.Outcome != Outcome.Pass) missing.Add(p.ClassName + " " + p.EntityId);
            result.Message = anyArrived
                ? ok + " of " + step.Receive.Count + " expected item(s) matched; not matched: " + string.Join(", ", missing.ToArray())
                : "nothing arrived on " + _cfgB.Label + " within " +
                  budget.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s";
            return result;
        }

        // ------------------------------------------------------------- publish ---

        /// <returns>When the row was handed to A (see NativeTestRunner.Publish for why: before the call).</returns>
        private DateTime Publish(CaseRow row, int stepNo)
        {
            var buffers = NativeTestRunner.AssembleAttributes(row.Cells);
            foreach (var rc in row.Refs)
            {
                string name;
                if (!_sendNames.TryGetValue(rc.Target, out name))
                    throw new InvalidOperationException(row.Where + ": reference target has no instance name");
                buffers[rc.Cell.Name] = EncodeReference(name);
            }

            var sheet = row.Sheet;
            DateTime sent;
            if (sheet.IsInteraction)
            {
                foreach (var kv in buffers) _a.SetParam(sheet.Alias, kv.Key, kv.Value);
                sent = DateTime.UtcNow;
                _a.SendInteraction(sheet.Alias);
                Report(stepNo, "sent " + sheet.ClassName + " with " + buffers.Count + " parameter(s) [" + row.EntityId + "]");
                return sent;
            }

            var name0 = _sendNames[row];
            if (_registered.Add(name0)) _a.Register(name0, sheet.Alias, name0);
            foreach (var kv in buffers) _a.SetAttr(name0, kv.Key, kv.Value);
            sent = DateTime.UtcNow;
            _a.Update(name0);
            Report(stepNo, "published " + sheet.ClassName + " " + row.EntityId + " as '" + name0 + "' (" +
                           buffers.Count + " attribute(s))");
            return sent;
        }

        /// <summary>
        /// An RTIobjectId on the wire. RPR 2.0 declares it RPRnullTerminatedArray of HLAASCIIchar,
        /// so it goes out NUL-terminated.
        /// ⚠️ RPR 1.0's RTIObjectIdStruct is assumed to read the same way; not yet seen across the broker.
        /// </summary>
        internal static byte[] EncodeReference(string instanceName)
        {
            var raw = Encoding.ASCII.GetBytes(instanceName ?? "");
            var b = new byte[raw.Length + 1];
            Array.Copy(raw, b, raw.Length);
            return b;
        }

        /// <summary>Read an RTIobjectId back as text, NUL padding ignored either way.</summary>
        internal static string DecodeReference(byte[] v)
        {
            if (v == null) return null;
            return Encoding.ASCII.GetString(v).TrimEnd('\0');
        }

        // ------------------------------------------------------------- compare ---

        private sealed class Observed
        {
            public object Item;
            public string ClassName;
            public string InstanceName;
            public string Label;
            public DateTime? Arrived;
            public Func<string, byte[]> Get;
        }

        private List<Observed> Snapshot()
        {
            var list = new List<Observed>();
            foreach (var o in _b.Objects)
            {
                var c = o;
                list.Add(new Observed
                {
                    Item = c, ClassName = c.ClassName, InstanceName = c.InstanceName, Arrived = c.LastSeenUtc, Get = c.Get,
                    Label = "object " + c.Handle + (string.IsNullOrEmpty(c.InstanceName) ? "" : " '" + c.InstanceName + "'"),
                });
            }
            foreach (var i in _b.Interactions)
            {
                var c = i;
                list.Add(new Observed
                {
                    Item = c, ClassName = c.ClassName, Arrived = c.ReceivedUtc, Get = c.Get,
                    Label = "interaction " + c.ClassName,
                });
            }
            return list;
        }

        /// <summary>Only a target in the SAME step has to match first; any other is already settled.</summary>
        private static bool RefsReady(CaseRow row, CaseStep step, Dictionary<CaseRow, Observed> matched)
        {
            foreach (var rc in row.Refs)
                if (step.Receive.Contains(rc.Target) && !matched.ContainsKey(rc.Target)) return false;
            return true;
        }

        private static string UnmatchedTargets(CaseRow row, CaseStep step, Dictionary<CaseRow, Observed> matched)
        {
            var names = new List<string>();
            foreach (var rc in row.Refs)
                if (step.Receive.Contains(rc.Target) && !matched.ContainsKey(rc.Target)) names.Add(rc.Target.Label);
            return string.Join(", ", names.ToArray());
        }

        private static List<FieldResult> CompareRow(CaseRow row, Observed cand, Dictionary<CaseRow, Observed> matched,
                                                    Dictionary<CaseRow, string> earlier)
        {
            var fields = NativeTestRunner.CompareCells(row.Cells, cand.Get);
            foreach (var rc in row.Refs)
            {
                Observed target;
                string want = null;
                bool known = false;
                if (matched.TryGetValue(rc.Target, out target)) { want = target.InstanceName; known = true; }
                else if (earlier.TryGetValue(rc.Target, out want)) known = true;
                var fr = new FieldResult { Name = rc.Cell.Name, Expected = rc.Text + " = '" + (want ?? "?") + "'" };
                var actual = DecodeReference(cand.Get(rc.Cell.Name));
                fr.Actual = actual ?? "(not received)";

                if (actual == null) fr.Note = "not received";
                else if (!known)
                    fr.Note = "points at " + rc.Target.Label + ", which has not matched on B in any step so far";
                else if (string.IsNullOrEmpty(want))
                    fr.Note = "the referenced object's instance name is unknown (no discovery seen on B)";
                else if (string.Equals(actual, want, StringComparison.Ordinal)) fr.Ok = true;
                else fr.Note = "points at '" + actual + "', not at " + rc.Target.Label + " as seen on B";
                fields.Add(fr);
            }
            return fields;
        }

        private static int CountOk(List<FieldResult> f)
        {
            int n = 0;
            foreach (var x in f) if (x.Ok) n++;
            return n;
        }

        private static int ClassCount(CaseStep step)
        {
            var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in step.Receive) s.Add(r.Sheet.ClassName);
            return s.Count;
        }

        private static void Tally(TestCase tc, RunReport report)
        {
            var byClass = new Dictionary<string, ClassTally>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in tc.Receive)
                if (!byClass.ContainsKey(s.ClassName))
                {
                    var t = new ClassTally { ClassName = s.ClassName };
                    byClass[s.ClassName] = t;
                    report.Classes.Add(t);
                }

            foreach (var st in report.Steps)
                foreach (var p in st.Parts)
                {
                    ClassTally t;
                    if (!byClass.TryGetValue(p.ClassName, out t)) continue;
                    t.Expected++;
                    if (p.Outcome == Outcome.Pass) t.Matched++;
                    else if (p.Outcome == Outcome.Timeout) t.Missing++;
                    else t.Wrong++;
                }
        }

        private void Report(int step, string msg)
        {
            var h = Progress;
            if (h != null) h(step, msg);
        }
    }
}
