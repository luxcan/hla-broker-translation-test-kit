using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using BrokerTestKit.Engine;

namespace BrokerTestKit.Gui
{
    /// <summary>The run on screen in the sidebar box and the result view: live or just finished.</summary>
    public sealed class RunState : Notify
    {
        public long RunId;
        public long PairId, TestCaseId;
        public string PairName = "", TestCaseName = "";
        public readonly ObservableCollection<ResultRow> Rows = new ObservableCollection<ResultRow>();
        public readonly StringBuilder Log = new StringBuilder();
        public RunReport Report = new RunReport();
        public List<string> Context = new List<string>();

        private bool _running;
        private int _total, _done, _pass, _fail, _timeout;
        public bool Running { get { return _running; } set { _running = value; Raise("Running"); } }
        public int Total { get { return _total; } set { _total = value; Raise("Total"); Raise("ProgressText"); Raise("Fraction"); } }
        public int Done { get { return _done; } set { _done = value; Raise("Done"); Raise("ProgressText"); Raise("Fraction"); } }
        public int Pass { get { return _pass; } set { _pass = value; Raise("Pass"); Raise("PassText"); } }
        public int Fail { get { return _fail; } set { _fail = value; Raise("Fail"); Raise("FailText"); } }
        public int TimedOut { get { return _timeout; } set { _timeout = value; Raise("TimedOut"); Raise("FailText"); } }

        public string ProgressText { get { return Done + " / " + Total + " rows"; } }
        public double Fraction { get { return Total == 0 ? 0 : (double)Done / Total; } }
        public string PassText { get { return Pass + " pass"; } }
        public string FailText { get { return (Fail + TimedOut) + " fail"; } }
    }

    /// <summary>
    /// Owns the run thread, the queue for "Run all test cases" and the stop flag (PRP T7).
    ///
    /// Two rules carried over from the old Run tab, both about latency accuracy:
    ///   * rows are reported one at a time as each step completes (TestCaseRunner.StepCompleted);
    ///   * agent lines reach the UI through BeginInvoke - never Invoke. The agent's stdout reader
    ///     thread is what stamps arrival times, so blocking it on a UI re-render skewed latency.
    /// Store calls are marshalled to the UI thread with Invoke (R2), as before.
    /// </summary>
    public sealed class RunService
    {
        private readonly AppServices _s;
        private readonly Dispatcher _ui;
        private readonly Queue<long> _queue = new Queue<long>();
        private volatile bool _abort;
        private Thread _thread;

        public RunState Current { get; private set; }
        /// <summary>Test case names waiting behind the current run.</summary>
        public readonly ObservableCollection<string> Queued = new ObservableCollection<string>();

        public event Action Changed;                   // UI thread
        public event Action<RunState> Finished;        // UI thread
        public event Action<string> LogLine;           // UI thread

        public bool IsRunning { get { return Current != null && Current.Running; } }

        public RunService(AppServices s, Dispatcher ui) { _s = s; _ui = ui; }

        /// <summary>Queue test cases and start if idle. Returns a reason it cannot, or null.</summary>
        public string Start(IList<long> testCaseIds)
        {
            if (testCaseIds == null || testCaseIds.Count == 0) return "Nothing to run - add a test case first.";
            if (_s.Rig.Licence == RigMonitor.State.Down) return _s.Rig.Banner;

            foreach (var id in testCaseIds)
            {
                var tc = _s.Store.TestCase(id);
                if (tc == null) continue;
                var why = WhyNot(tc);
                if (why != null) return tc.Name + ": " + why;
            }
            foreach (var id in testCaseIds)
            {
                _queue.Enqueue(id);
                var tc = _s.Store.TestCase(id);
                Queued.Add(tc == null ? "#" + id : tc.Name);
            }
            if (!IsRunning) Next();
            Raise();
            return null;
        }

        public void Stop()
        {
            _abort = true;
            _queue.Clear();
            Queued.Clear();
            Raise();
        }

        private string WhyNot(TestCaseRecord tc)
        {
            if (!string.IsNullOrEmpty(tc.Note)) return tc.Note;
            bool send = false, recv = false;
            foreach (var s in tc.Sheets) { if (s.IsSend) send = true; else recv = true; }
            if (!send) return "it has no Send sheet";
            if (!recv) return "it has no Receive sheet";
            return _s.WhyNotRunnable(_s.Store.Pair(tc.PairId));
        }

        private void Raise() { var h = Changed; if (h != null) h(); }

        /// <summary>UI thread: take the next queued test case, prepare it, start the worker.</summary>
        private void Next()
        {
            if (_queue.Count == 0) { Raise(); return; }
            var tcId = _queue.Dequeue();
            if (Queued.Count > 0) Queued.RemoveAt(0);

            var tc = _s.Store.TestCase(tcId);
            var pair = tc == null ? null : _s.Store.Pair(tc.PairId);
            if (tc == null || pair == null) { Next(); return; }

            ConnectionRecord a, b;
            _s.SidesOf(pair, out a, out b);

            var state = new RunState
            {
                PairId = pair.Id, TestCaseId = tc.Id, PairName = pair.Name, TestCaseName = tc.Name, Running = true,
            };
            Current = state;
            _abort = false;

            string manifest;
            try { manifest = WriteCase(tc); }
            catch (Exception ex) { _s.Fail("Could not prepare " + tc.Name, ex); state.Running = false; Next(); return; }

            state.RunId = _s.Store.BeginRun(pair, tc, a, b);
            state.Context = new List<string>
            {
                "run      " + Fmt.When(DateTime.UtcNow) + "   (#" + state.RunId + ")",
                "pair     " + pair.Name + (pair.SelfTest ? "  (self-test)" : ""),
                "send     " + a.Name,
                "receive  " + b.Name,
                "testcase " + tc.Name,
            };

            var job = new Job
            {
                State = state, Manifest = manifest, A = a, B = b, SelfTest = pair.SelfTest,
                Licence = _s.Settings.LicenceServer,
                Timeout = tc.TimeoutSeconds,
            };
            _thread = new Thread(() => Work(job)) { IsBackground = true, Name = "btk-run" };
            _thread.Start();
            Raise();
        }

        private sealed class Job
        {
            public RunState State;
            public string Manifest, Licence;
            public ConnectionRecord A, B;
            public bool SelfTest;
            public double? Timeout;
        }

        /// <summary>Sheets come out of the workspace into a temp folder, with a manifest, so the engine reads paths.</summary>
        private string WriteCase(TestCaseRecord tc)
        {
            var dir = Path.Combine(Path.GetTempPath(), "btk-runs", tc.Id.ToString(CultureInfo.InvariantCulture) + "-" +
                                   DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            sb.AppendLine("name: " + tc.Name.Replace("\n", " "));
            int n = 0;
            foreach (var s in tc.Sheets)
            {
                var text = _s.Store.ReadAssetText(s.AssetHash);
                if (text == null) throw new FileNotFoundException("sheet for " + s.ClassName + " is missing from the workspace");
                var file = (s.IsSend ? "send-" : "receive-") + (n++).ToString(CultureInfo.InvariantCulture) + "-" + Safe(s.ClassName) + ".csv";
                File.WriteAllText(Path.Combine(dir, file), text, new UTF8Encoding(false));
                sb.AppendLine((s.IsSend ? "send: " : "receive: ") + file);
            }
            var manifest = Path.Combine(dir, "case.testcase");
            File.WriteAllText(manifest, sb.ToString(), new UTF8Encoding(false));
            return manifest;
        }

        private static string Safe(string s)
        {
            var b = new StringBuilder();
            foreach (var c in s ?? "") b.Append(char.IsLetterOrDigit(c) ? c : '_');
            return b.ToString();
        }

        // ----------------------------------------------------------------- worker ---

        private void Work(Job job)
        {
            var st = job.State;
            NativeAgentHost a = null, b = null;
            var report = new RunReport();
            try
            {
                var problem = _s.Rig.PreflightProblem(job.Licence, !job.SelfTest);
                if (problem != null) throw new InvalidOperationException(problem);

                var tc = TestCase.Load(job.Manifest, job.A.Fom.Model, job.B.Fom.Model);
                report.TestCase = st.TestCaseName;

                Ui(() =>
                {
                    st.Total = tc.Steps.Count;
                    foreach (var step in tc.Steps)
                        st.Rows.Add(new ResultRow { Step = step.Step, EntityId = step.Key, Outcome = "…" });
                });

                var cfgA = Side("A", job.A);
                var cfgB = Side("B", job.B);
                if (job.SelfTest)
                {
                    // Both agents join A's federation; a federate name must be unique in it.
                    cfgB.FederationName = cfgA.FederationName;
                    cfgB.FederateName = cfgA.FederateName + "-Check";
                }

                a = new NativeAgentHost("A", cfgA.AgentExe, job.A.Rti.AgentArgs, job.A.Rti.Environment(job.Licence));
                b = new NativeAgentHost("B", cfgB.AgentExe, job.B.Rti.AgentArgs, job.B.Rti.Environment(job.Licence));
                a.LineReceived += (l, line) => OnAgentLine(st, l, line);
                b.LineReceived += (l, line) => OnAgentLine(st, l, line);

                var opt = new RunOptions();
                if (job.Timeout.HasValue && job.Timeout.Value > 0) opt.Timeout = TimeSpan.FromSeconds(job.Timeout.Value);

                var runner = new TestCaseRunner(a, b, cfgA, cfgB, opt) { ShouldStop = () => _abort };
                runner.Progress += (step, msg) => _ui.BeginInvoke(new Action(() => _s.Status("[step " + step + "] " + msg)));
                runner.StepCompleted += r => Ui(() =>
                {
                    st.Report.Steps.Add(r);
                    foreach (var row in st.Rows)
                        if (row.Step == r.Step) { row.Apply(r); break; }
                    st.Done++;
                    if (r.Outcome == Outcome.Pass) st.Pass++;
                    else if (r.Outcome == Outcome.Timeout) st.TimedOut++;
                    else st.Fail++;
                    try { _s.Store.SaveStep(st.RunId, r); }
                    catch (Exception ex) { _s.Status("Could not save row " + r.Step + ": " + ex.Message); }
                    Raise();
                });

                runner.Prepare(tc);
                report = runner.Run(tc);
            }
            catch (Exception ex)
            {
                report.AbortReason = "run failed: " + ex.Message;
                var msg = ex.Message;
                Ui(() => _s.Fail("Run failed — " + st.TestCaseName, msg));
            }
            finally
            {
                if (a != null) try { a.Dispose(); } catch { }
                if (b != null) try { b.Dispose(); } catch { }
            }

            Ui(() =>
            {
                // The rows already on st.Report are the ones saved; the runner's report adds the
                // duration, the abort reason and the per-class tallies.
                st.Report.Duration = report.Duration;
                st.Report.AbortReason = report.AbortReason;
                st.Report.TestCase = st.TestCaseName;
                st.Report.Classes.Clear();
                st.Report.Classes.AddRange(report.Classes);
                try { _s.Store.EndRun(st.RunId, st.Report); } catch { }
                st.Running = false;
                _s.Status(st.Report.Aborted ? "ABORTED — " + st.Report.AbortReason
                        : st.Report.AllPassed ? "PASS — " + st.TestCaseName
                        : "FAILURES — " + st.TestCaseName);
                var f = Finished; if (f != null) f(st);
                Raise();
                if (!_abort) Next();
                else { _queue.Clear(); Queued.Clear(); Raise(); }
            });
        }

        private static NativeSideConfig Side(string label, ConnectionRecord c)
        {
            var rec = c.Fom;
            return new NativeSideConfig
            {
                Label = label,
                AgentExe = AppServices.FindAgent(rec.IsEvolved ? "btk-agent1516e.exe" : "btk-agent13.exe"),
                FederationName = c.ExecName,
                FederateName = string.IsNullOrWhiteSpace(c.FederateName) ? "BtkRunner" + label : c.FederateName,
                FederateType = "Harness",
                FedFile = rec.StructureFile,
                JoinFomModules = string.IsNullOrWhiteSpace(rec.Modules) ? null : rec.Modules,
                Mim = string.IsNullOrWhiteSpace(rec.Mim) ? null : rec.Mim,
            };
        }

        private void OnAgentLine(RunState st, string label, string line)
        {
            var text = "[" + label + "] " + line + "\n";
            _ui.BeginInvoke(new Action(() =>
            {
                st.Log.Append(text);
                if (st.Log.Length > 400000) st.Log.Remove(0, 200000);
                var h = LogLine; if (h != null) h(text);
            }));
        }

        private void Ui(Action a)
        {
            if (_ui.CheckAccess()) a(); else _ui.Invoke(a);
        }
    }
}
