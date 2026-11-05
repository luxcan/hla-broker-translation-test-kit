using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace BrokerTestKit.Engine
{
    /// <summary>How to join one federation with a native agent.</summary>
    public sealed class NativeSideConfig
    {
        public string Label = "A";
        /// <summary>btk-agent13.exe or btk-agent1516e.exe.</summary>
        public string AgentExe;
        public string FederationName = "BTK-FedA";
        public string FederateName = "BtkHarnessA";
        public string FederateType;
        /// <summary>.fed for HLA 1.3, FDD .xml for Evolved.</summary>
        public string FedFile;
        /// <summary>Evolved only. Comma separated. Passed at JOIN - create-time modules are dropped.</summary>
        public string JoinFomModules;
        public string Mim;
        /// <summary>HLA class to publish/subscribe. Fully qualified on Evolved.</summary>
        public string ObjectClass = "BaseEntity";
        /// <summary>Short alias for a fully-qualified 1516 class name. Defaults to ObjectClass.</summary>
        public string ClassAlias;

        /// <summary>
        /// True when <see cref="ObjectClass"/> names an INTERACTION rather than an object class.
        /// Set from the CSV's `# kind:` directive (REQ-12).
        /// </summary>
        public bool IsInteraction;

        public string Alias { get { return string.IsNullOrEmpty(ClassAlias) ? ObjectClass : ClassAlias; } }
    }

    /// <summary>
    /// The golden-file runner over the NATIVE agents - the byte-level counterpart of
    /// <see cref="TestRunner"/>.
    ///
    /// What changes versus the managed runner:
    ///   * attribute values are RAW BYTES, so CUSTOM attributes work (REQ-08);
    ///   * nothing about any FOM is compiled in - class and attribute names come from the CSV
    ///     and datatypes come from the CSV's datatype column (REQ-09);
    ///   * matching is by CONTENT, not by instance name. The broker creates its own objects on
    ///     side B with its own names and handles, so identity has to be inferred from the values
    ///     themselves: an observed object matches a step when EVERY expected field matches.
    ///
    /// Per step (DESIGN 9.1): quiesce B -&gt; publish on A -&gt; match-or-timeout on content -&gt; record.
    /// </summary>
    public sealed class NativeTestRunner
    {
        private readonly NativeAgentHost _a;
        private readonly NativeAgentHost _b;
        private readonly NativeSideConfig _cfgA;
        private readonly NativeSideConfig _cfgB;
        private readonly RunOptions _opt;
        private readonly HashSet<string> _registered = new HashSet<string>(StringComparer.Ordinal);

        // Instance state, not per-Run state: the GUI calls Run() one row at a time so it can fill
        // the grid live, and a run-scoped counter would reset on every row and never trip.
        private int _consecutiveTimeouts;

        public event Action<int, string> Progress;

        public NativeTestRunner(NativeAgentHost a, NativeAgentHost b,
                                NativeSideConfig cfgA, NativeSideConfig cfgB,
                                RunOptions options = null)
        {
            if (a == null) throw new ArgumentNullException("a");
            if (b == null) throw new ArgumentNullException("b");
            _a = a; _b = b; _cfgA = cfgA; _cfgB = cfgB;
            _opt = options ?? new RunOptions();
        }

        /// <summary>
        /// Join both federations and declare exactly the attributes the CSVs actually use.
        /// Deriving the attribute list from the test data is what keeps the tool FOM-agnostic:
        /// add a column to the CSV and the agent subscribes to it, with no code change.
        /// </summary>
        public void Prepare(List<InputStep> input, List<ExpectedStep> expected)
        {
            _a.Join(_cfgA.FederationName, _cfgA.FedFile, _cfgA.FederateName,
                    _cfgA.FederateType, _cfgA.JoinFomModules, _cfgA.Mim);
            Report(0, _cfgA.Label + " joined " + _cfgA.FederationName);

            _b.Join(_cfgB.FederationName, _cfgB.FedFile, _cfgB.FederateName,
                    _cfgB.FederateType, _cfgB.JoinFomModules, _cfgB.Mim);
            Report(0, _cfgB.Label + " joined " + _cfgB.FederationName);

            var pub = BaseAttributes(AllInputAttributes(input));
            var sub = BaseAttributes(AllExpectedAttributes(expected));

            if (_cfgA.IsInteraction) _a.DeclareInteraction(_cfgA.ObjectClass, pub, "pub", _cfgA.ClassAlias);
            else                     _a.DeclareClass(_cfgA.ObjectClass, pub, "pub", _cfgA.ClassAlias);
            Report(0, _cfgA.Label + " publishes " + Noun(_cfgA) + " " + _cfgA.ObjectClass +
                      " [" + string.Join(",", pub.ToArray()) + "]");

            if (_cfgB.IsInteraction) _b.DeclareInteraction(_cfgB.ObjectClass, sub, "sub", _cfgB.ClassAlias);
            else                     _b.DeclareClass(_cfgB.ObjectClass, sub, "sub", _cfgB.ClassAlias);
            Report(0, _cfgB.Label + " subscribes " + Noun(_cfgB) + " " + _cfgB.ObjectClass +
                      " [" + string.Join(",", sub.ToArray()) + "]");
        }

        public RunReport Run(List<InputStep> input, List<ExpectedStep> expected)
        {
            var report = new RunReport();
            var sw = Stopwatch.StartNew();

            var expectedByStep = new Dictionary<int, ExpectedStep>();
            foreach (var e in expected) expectedByStep[e.Step] = e;

            foreach (var step in input)
            {
                var result = new StepResult { Step = step.Step, EntityId = step.EntityId };
                var stepSw = Stopwatch.StartNew();

                ExpectedStep exp;
                var haveExpectation = expectedByStep.TryGetValue(step.Step, out exp);

                // 1) quiesce B, so a straggler from step N-1 cannot be mistaken for step N
                _b.ClearObserved();
                var quiesceUntil = DateTime.UtcNow + _opt.Quiesce;
                while (DateTime.UtcNow < quiesceUntil) System.Threading.Thread.Sleep(20);

                // 2) stimulus on A.
                // The clock for BROKER latency starts when A has acknowledged the update - not
                // before, or the agent's own command round-trip is charged to the broker.
                string publishError = null;
                DateTime sentUtc = DateTime.MaxValue;
                try { Publish(step, out sentUtc); }
                catch (Exception ex) { publishError = ex.Message; }

                if (publishError != null)
                {
                    stepSw.Stop();
                    result.Outcome = Outcome.Fail;
                    result.Elapsed = stepSw.Elapsed;
                    result.Message = "publish failed: " + publishError;
                    Report(step.Step, result.Outcome + " - " + result.Message);
                    report.Steps.Add(result);
                    continue;
                }

                // 3) match-or-timeout on CONTENT
                var budget = haveExpectation ? _opt.Timeout : _opt.NegativeTimeout;
                var deadline = DateTime.UtcNow + budget;

                object match = null;
                string matchLabel = null;
                List<FieldResult> best = null;
                int bestScore = -1;
                bool sawAnyObject = false;      // distinguishes "wrong values" from "nothing arrived"

                while (DateTime.UtcNow < deadline)
                {
                    // Objects and interactions differ only in where the bytes come from; the
                    // match-on-content rule is identical for both.
                    foreach (var candidate in Candidates())
                    {
                        sawAnyObject = true;
                        if (!haveExpectation) { match = candidate.Item; matchLabel = candidate.Label; break; }

                        var fields = Compare(exp, candidate);
                        var score = CountOk(fields);
                        if (score > bestScore) { bestScore = score; best = fields; }
                        if (score == fields.Count && fields.Count > 0)
                        { match = candidate.Item; matchLabel = candidate.Label; break; }
                    }
                    if (match != null) break;
                    System.Threading.Thread.Sleep(25);
                }

                stepSw.Stop();
                result.Elapsed = stepSw.Elapsed;

                // Arrival is stamped by the reader thread as the line comes off the agent, so this
                // is independent of how coarsely the loop above polls.
                if (match != null && sentUtc != DateTime.MaxValue)
                {
                    var arrived = ArrivalOf(match);
                    if (arrived.HasValue)
                    {
                        var d = arrived.Value - sentUtc;
                        result.Latency = d < TimeSpan.Zero ? TimeSpan.Zero : d;
                    }
                }

                // 4) record
                if (!haveExpectation)
                {
                    if (match == null)
                    {
                        result.Outcome = Outcome.Pass;
                        result.Message = "negative test: nothing arrived (as expected)";
                    }
                    else
                    {
                        result.Outcome = Outcome.Fail;
                        result.Message = "negative test violated: " + matchLabel + " arrived";
                    }
                }
                else if (match != null)
                {
                    result.Outcome = Outcome.Pass;
                    result.Message = "matched " + matchLabel;
                    if (best != null) result.Fields.AddRange(best);
                    if (result.Elapsed.TotalSeconds > budget.TotalSeconds * _opt.NearTimeoutFraction)
                        result.Message += "  [WARN near timeout - margin is eroding]";
                }
                else if (sawAnyObject && best != null)
                {
                    // An object DID arrive - the broker is alive and translating, it just produced
                    // the wrong values. That is a different defect from silence and must read
                    // differently in the report.
                    result.Outcome = Outcome.Fail;
                    result.Message = Noun(_cfgB) + " arrived on " + _cfgB.Label + " but " +
                                     (best.Count - Math.Max(bestScore, 0)) + " of " + best.Count +
                                     " field(s) differ";
                    result.Fields.AddRange(best);
                }
                else
                {
                    result.Outcome = Outcome.Timeout;
                    result.Message = "nothing arrived on " + _cfgB.Label + " within " +
                                     budget.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s";
                    if (best != null) result.Fields.AddRange(best);
                }

                Report(step.Step, result.Outcome + " - " + result.Message);
                report.Steps.Add(result);

                if (result.Outcome == Outcome.Timeout) _consecutiveTimeouts++;
                else _consecutiveTimeouts = 0;

                if (_opt.AbortAfterConsecutiveTimeouts > 0 &&
                    _consecutiveTimeouts >= _opt.AbortAfterConsecutiveTimeouts)
                {
                    report.AbortReason = _consecutiveTimeouts + " consecutive timeout(s) - " +
                        "federation B has stopped producing anything, so the broker or its RTI is " +
                        "most likely gone. The remaining rows were not attempted.";
                    Report(step.Step, "ABORT - " + report.AbortReason);
                    break;
                }
            }

            sw.Stop();
            report.Duration = sw.Elapsed;
            return report;
        }

        // ------------------------------------------------------------- publish ----

        /// <summary>
        /// Assemble the step's rows into whole ATTRIBUTE values and send them as one update.
        ///
        /// Several CSV rows can address ONE attribute - WorldLocation.x/.y/.z are three rows but a
        /// single 24-byte value. They are merged here by byte offset; writing only some sub-fields
        /// still produces a correctly sized value, zero-filled where the test said nothing.
        /// </summary>
        private void Publish(InputStep step) { DateTime ignored; Publish(step, out ignored); }

        /// <param name="sentUtc">
        /// When the update was handed to federation A - i.e. the instant the stimulus left the
        /// harness. Stamped just BEFORE the UPDATE/SENDINT call, not after it: the agent replies
        /// only when its tick loop next comes round, and on this rig that reply is slower than the
        /// whole bridge, so stamping afterwards produced a negative latency on every row.
        /// </param>
        private void Publish(InputStep step, out DateTime sentUtc)
        {
            sentUtc = DateTime.MaxValue;
            var buffers = AssembleAttributes(step.Attributes);

            var id = string.IsNullOrEmpty(step.EntityId) ? "e" + step.Step : step.EntityId;

            if (_cfgA.IsInteraction)
            {
                // No instance to register: parameters stage, then one SENDINT puts them on the wire.
                foreach (var kv in buffers) _a.SetParam(_cfgA.Alias, kv.Key, kv.Value);
                sentUtc = DateTime.UtcNow;
                _a.SendInteraction(_cfgA.Alias);
                Report(step.Step, "sent interaction with " + buffers.Count + " parameter(s) [" + id + "]");
                return;
            }

            if (_registered.Add(id))
                _a.Register(id, _cfgA.Alias, SafeInstanceName(id));

            foreach (var kv in buffers) _a.SetAttr(id, kv.Key, kv.Value);
            sentUtc = DateTime.UtcNow;
            _a.Update(id);

            Report(step.Step, "published " + buffers.Count + " attribute(s) on " + id);
        }

        /// <summary>
        /// CSV cells -&gt; whole attribute (or parameter) values, merged by byte offset. Shared with
        /// <see cref="TestCaseRunner"/> so both runners put identical bytes on the wire.
        /// </summary>
        internal static Dictionary<string, byte[]> AssembleAttributes(IEnumerable<AttributeCell> cells)
        {
            var buffers = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var cell in cells)
            {
                string attr, sub;
                ValueCodec.SplitPath(cell.Name, out attr, out sub);

                var datatype = ValueCodec.ResolveDatatype(cell.Name, cell.DataType);
                if (datatype == null)
                    throw new FormatException("cannot resolve sub-field '" + cell.Name +
                        "': give an explicit offset, e.g. datatype=float64@8");

                var bytes = ValueCodec.Encode(cell.Value, datatype);
                var spec = ValueCodec.ParseSpec(datatype);

                if (!spec.HasOffset)
                {
                    buffers[attr] = bytes;      // whole attribute in one row
                    continue;
                }

                byte[] buf;
                buffers.TryGetValue(attr, out buf);

                // The FOM's size for the whole attribute wins. KnownStructSize is a four-struct
                // stopgap that answers -1 for everything else - EntityType, Marking and Spatial
                // included - and that is how a partly-filled struct used to go on the wire
                // truncated to its last written field, silently. It stays only as the fallback
                // for sheets published without a FOM (the `native` mode path).
                var whole = cell.AttributeSize > 0 ? cell.AttributeSize
                                                   : ValueCodec.KnownStructSize(attr);
                var needed = Math.Max(spec.Offset + bytes.Length, whole);
                if (buf == null || buf.Length < needed)
                {
                    var grown = new byte[Math.Max(needed, buf == null ? 0 : buf.Length)];
                    if (buf != null) Array.Copy(buf, grown, buf.Length);
                    buf = grown;
                }
                Array.Copy(bytes, 0, buf, spec.Offset, bytes.Length);
                buffers[attr] = buf;
            }
            return buffers;
        }

        /// <summary>Instance names may not contain spaces (the line protocol is space delimited).</summary>
        internal static string SafeInstanceName(string id)
        {
            var sb = new StringBuilder("BTK-");
            foreach (var c in id) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
            return sb.ToString();
        }

        // ------------------------------------------------------------- compare ----

        /// <summary>One thing observed on B - an object or an interaction - with a display label.</summary>
        private struct Candidate
        {
            public object Item;
            public string Label;
            public Func<string, byte[]> Get;
        }

        private List<Candidate> Candidates()
        {
            var list = new List<Candidate>();
            if (_cfgB.IsInteraction)
            {
                foreach (var i in _b.Interactions)
                {
                    var captured = i;
                    list.Add(new Candidate
                    {
                        Item = captured,
                        Label = "interaction " + captured.ClassName,
                        Get = captured.Get,
                    });
                }
            }
            else
            {
                foreach (var o in _b.Objects)
                {
                    var captured = o;
                    list.Add(new Candidate
                    {
                        Item = captured,
                        Label = "object " + captured.Handle +
                                (string.IsNullOrEmpty(captured.InstanceName) ? "" : " '" + captured.InstanceName + "'"),
                        Get = captured.Get,
                    });
                }
            }
            return list;
        }

        /// <summary>When the matched thing actually landed on B, as stamped on arrival.</summary>
        private static DateTime? ArrivalOf(object item)
        {
            var o = item as NativeObject;
            if (o != null) return o.LastSeenUtc;
            var i = item as NativeInteraction;
            if (i != null) return i.ReceivedUtc;
            return null;
        }

        private static string Noun(NativeSideConfig cfg)
        {
            return cfg.IsInteraction ? "interaction" : "object";
        }

        private static List<FieldResult> Compare(ExpectedStep exp, Candidate candidate)
        {
            return CompareCells(exp.Expected, candidate.Get);
        }

        /// <summary>Compare expected cells against one observed object/interaction. Shared with <see cref="TestCaseRunner"/>.</summary>
        internal static List<FieldResult> CompareCells(IEnumerable<AttributeCell> cells, Func<string, byte[]> get)
        {
            var results = new List<FieldResult>();
            foreach (var e in cells)
            {
                var fr = new FieldResult { Name = e.Name, Expected = e.Value };

                string attr, sub;
                ValueCodec.SplitPath(e.Name, out attr, out sub);

                var datatype = ValueCodec.ResolveDatatype(e.Name, e.DataType);
                if (datatype == null)
                {
                    fr.Ok = false;
                    fr.Actual = "(unresolvable)";
                    fr.Note = "sub-field '" + e.Name + "' needs an explicit offset, e.g. float64@8";
                    results.Add(fr);
                    continue;
                }

                var actual = get(attr);
                string actualText, note;
                fr.Ok = ValueCodec.Compare(actual, e.Value, datatype, e.Tolerance, out actualText, out note);
                fr.Actual = actualText ?? "(not received)";
                fr.Note = note;
                results.Add(fr);
            }
            return results;
        }

        private static int CountOk(List<FieldResult> f)
        {
            int n = 0;
            foreach (var x in f) if (x.Ok) n++;
            return n;
        }

        // --------------------------------------------------------------- attrs ----

        private static List<string> AllInputAttributes(List<InputStep> steps)
        {
            var names = new List<string>();
            foreach (var s in steps)
                foreach (var a in s.Attributes)
                    if (!names.Contains(a.Name)) names.Add(a.Name);
            return names;
        }

        private static List<string> AllExpectedAttributes(List<ExpectedStep> steps)
        {
            var names = new List<string>();
            foreach (var s in steps)
                foreach (var a in s.Expected)
                    if (!names.Contains(a.Name)) names.Add(a.Name);
            return names;
        }

        /// <summary>Strip sub-field paths: the RTI only knows whole attributes.</summary>
        internal static List<string> BaseAttributes(List<string> paths)
        {
            var outp = new List<string>();
            foreach (var p in paths)
            {
                string attr, sub;
                ValueCodec.SplitPath(p, out attr, out sub);
                if (attr.Length > 0 && !outp.Contains(attr)) outp.Add(attr);
            }
            return outp;
        }

        private void Report(int step, string msg)
        {
            var h = Progress;
            if (h != null) h(step, msg);
        }
    }
}
