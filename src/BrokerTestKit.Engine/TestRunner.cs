using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace BrokerTestKit.Engine
{
    /// <summary>
    /// Drives the golden-file regression run (DESIGN 9.1).
    ///
    /// For each step, in strict order:
    ///   1. quiesce B (so a straggler from step N-1 is not mis-attributed to N)
    ///   2. publish the step's attributes on Federation A
    ///   3. loop until B's observed state MATCHES the expectation, or the timeout expires
    ///      — match on CONTENT, never a fixed sleep, so passes are fast and only failures
    ///        pay the full timeout
    ///   4. record pass / fail / timeout with the actual values
    ///
    /// The broker sits between A and B and is the thing under test. For a rig self-test,
    /// point both agents at the SAME federation: then A-&gt;B should be an identity mapping.
    /// </summary>
    public sealed class TestRunner
    {
        private readonly AgentHost _a;
        private readonly AgentHost _b;
        private readonly RunOptions _opt;

        /// <summary>Progress callback: (step, message). Used by the CLI and the GUI grid.</summary>
        public event Action<int, string> Progress;

        public TestRunner(AgentHost fedA, AgentHost fedB, RunOptions options = null)
        {
            if (fedA == null) throw new ArgumentNullException("fedA");
            if (fedB == null) throw new ArgumentNullException("fedB");
            _a = fedA;
            _b = fedB;
            _opt = options ?? new RunOptions();
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

                // 1) quiesce B before the stimulus
                var quiesceUntil = DateTime.UtcNow + _opt.Quiesce;
                while (DateTime.UtcNow < quiesceUntil) System.Threading.Thread.Sleep(20);

                // 2) stimulus on A
                Publish(step);
                Report(step.Step, "published " + step.Attributes.Count + " attribute(s)");

                // 3) match-or-timeout on CONTENT
                // A step with no expectation is a NEGATIVE test: nothing should arrive.
                var budget = haveExpectation ? _opt.Timeout : _opt.NegativeTimeout;
                var targetId = haveExpectation && !string.IsNullOrEmpty(exp.EntityId)
                    ? exp.EntityId : step.EntityId;

                ObservedEntity match = null;
                List<FieldResult> best = null;
                var deadline = DateTime.UtcNow + budget;

                while (DateTime.UtcNow < deadline)
                {
                    var candidate = _b.WaitForEntity(e => Same(e.Id, targetId),
                                                     TimeSpan.FromMilliseconds(150));
                    if (candidate != null)
                    {
                        if (!haveExpectation) { match = candidate; break; }   // negative test violated
                        var fields = Compare(exp, candidate);
                        best = fields;
                        if (AllOk(fields)) { match = candidate; break; }
                    }
                }
                stepSw.Stop();
                result.Elapsed = stepSw.Elapsed;

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
                        result.Message = "negative test violated: entity " + match.Id + " arrived";
                    }
                }
                else if (match != null)
                {
                    result.Outcome = Outcome.Pass;
                    result.Message = "matched entity " + match.Id;
                    if (best != null) result.Fields.AddRange(best);
                    if (result.Elapsed.TotalSeconds > budget.TotalSeconds * _opt.NearTimeoutFraction)
                        result.Message += "  [WARN near timeout - margin is eroding]";
                }
                else if (best != null)
                {
                    result.Outcome = Outcome.Fail;
                    result.Message = "entity seen but values differ";
                    result.Fields.AddRange(best);
                }
                else
                {
                    result.Outcome = Outcome.Timeout;
                    result.Message = "no matching entity '" + targetId + "' within " +
                                     budget.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s";
                }

                Report(step.Step, result.Outcome + " - " + result.Message);
                report.Steps.Add(result);
            }

            sw.Stop();
            report.Duration = sw.Elapsed;
            return report;
        }

        private void Publish(InputStep step)
        {
            double x = 0, y = 0, z = 0;
            var marking = step.Marking ?? "";
            var custom = new List<AttributeCell>();

            foreach (var a in step.Attributes)
            {
                switch (Canonical(a.Name))
                {
                    case "worldposition.x": x = Num(a.Value); break;
                    case "worldposition.y": y = Num(a.Value); break;
                    case "worldposition.z": z = Num(a.Value); break;
                    case "marking": marking = a.Value; break;
                    default: custom.Add(a); break;
                }
            }

            _a.Publish(step.EntityId, x, y, z, string.IsNullOrEmpty(marking) ? "-" : marking);

            // Custom (unmodelled) attributes go through the generic channel.
            // NOTE: publishing these is NOT supported by the C# binding today — it needs a
            // FOM-Mapper encoder, which only the C++ API exposes. The agent accepts the
            // command so the wiring is ready for a C++ agent; see docs/vr-link-knowledge.md.
            foreach (var c in custom)
            {
                _a.Send("SETATTR name=" + c.Name + " value=" + c.Value);
                Report(step.Step, "custom attr '" + c.Name + "' sent via generic channel (see notes)");
            }
        }

        private List<FieldResult> Compare(ExpectedStep exp, ObservedEntity got)
        {
            var results = new List<FieldResult>();
            foreach (var e in exp.Expected)
            {
                var fr = new FieldResult { Name = e.Name, Expected = e.Value };
                switch (Canonical(e.Name))
                {
                    case "worldposition.x": Numeric(fr, e, got.X); break;
                    case "worldposition.y": Numeric(fr, e, got.Y); break;
                    case "worldposition.z": Numeric(fr, e, got.Z); break;
                    case "marking":
                        fr.Actual = got.Marking;
                        fr.Ok = string.Equals((got.Marking ?? "").Trim(), (e.Value ?? "").Trim(),
                                              StringComparison.OrdinalIgnoreCase);
                        break;
                    case "entityid":
                        fr.Actual = got.Id;
                        fr.Ok = Same(got.Id, e.Value);
                        break;
                    default:
                        // custom / unmodelled attribute — not observable from C# yet
                        fr.Actual = "(not available)";
                        fr.Ok = false;
                        fr.Note = "custom attribute: needs generic channel (C++ agent)";
                        break;
                }
                results.Add(fr);
            }
            return results;
        }

        private void Numeric(FieldResult fr, AttributeCell e, double actual)
        {
            var tol = e.Tolerance ?? _opt.DefaultTolerance;
            var want = Num(e.Value);
            fr.Actual = actual.ToString("R", CultureInfo.InvariantCulture);
            fr.Ok = Math.Abs(actual - want) <= tol;
            if (!fr.Ok)
                fr.Note = "delta=" + Math.Abs(actual - want).ToString("G6", CultureInfo.InvariantCulture) +
                          " tol=" + tol.ToString("G6", CultureInfo.InvariantCulture);
        }

        private static bool AllOk(List<FieldResult> f)
        {
            if (f == null || f.Count == 0) return false;
            foreach (var x in f) if (!x.Ok) return false;
            return true;
        }

        private static string Canonical(string n)
        {
            return (n ?? "").Trim().ToLowerInvariant().Replace("_", "");
        }

        private static bool Same(string a, string b)
        {
            return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static double Num(string s)
        {
            double d;
            return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        private void Report(int step, string msg)
        {
            var h = Progress;
            if (h != null) h(step, msg);
        }
    }
}
