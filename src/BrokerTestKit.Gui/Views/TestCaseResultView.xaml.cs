using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BrokerTestKit.Engine;
using Microsoft.Win32;

namespace BrokerTestKit.Gui.Views
{
    /// <summary>
    /// Test case result (PRP §3.6): the live run as it fills in, or any saved run of the test case.
    /// </summary>
    public partial class TestCaseResultView : UserControl, IPageView
    {
        private readonly AppServices _s;
        private long _caseId;
        private long _runId;
        private TestCaseRecord _case;
        private PairRecord _pair;
        private RunState _live;                 // non-null while showing the live run
        private RunReport _report;              // what Export writes
        private List<string> _context = new List<string>();
        private ObservableCollection<ResultRow> _rows = new ObservableCollection<ResultRow>();

        public TestCaseResultView(AppServices s, long testCaseId, long runId)
        {
            _s = s;
            InitializeComponent();

            _runId = runId;
            _caseId = testCaseId;
            if (_caseId == 0 && runId > 0)
            {
                var r = _s.Store.RunById(runId);
                if (r != null && r.TestCaseId.HasValue) _caseId = r.TestCaseId.Value;
            }
            _case = _caseId == 0 ? null : _s.Store.TestCase(_caseId);
            _pair = _case == null ? null : _s.Store.Pair(_case.PairId);

            Header();
            Show();

            _s.Runs.Changed += OnRunsChanged;
            _s.Runs.Finished += OnFinished;
            _s.Runs.LogLine += OnLog;
            Unloaded += (sender, e) =>
            {
                _s.Runs.Changed -= OnRunsChanged;
                _s.Runs.Finished -= OnFinished;
                _s.Runs.LogLine -= OnLog;
            };
        }

        public bool CanLeave() { return true; }

        private void Header()
        {
            if (_case == null)
            {
                Title.Text = "Run";
                CrumbHere.Text = "Run #" + _runId;
                CrumbPair.Visibility = Visibility.Collapsed;
                EditButton.Visibility = RunButton.Visibility = Visibility.Collapsed;
                Sides.Text = "This run is not linked to a test case.";
                return;
            }
            Title.Text = _case.Name;
            CrumbHere.Text = _case.Name;
            CrumbPair.Content = _pair == null ? "?" : _pair.Name;

            ConnectionRecord a, b;
            _s.SidesOf(_pair, out a, out b);
            var send = new List<string>(); var recv = new List<string>(); int rows = 0;
            foreach (var sh in _case.Sheets)
            {
                if (sh.IsSend) { send.Add(sh.ClassName + " (" + sh.Rows + ")"); rows += sh.Rows; }
                else recv.Add(sh.ClassName + " (" + sh.Rows + ")");
            }
            Sides.Text = "Send · A  " + Side(a) + " — " + string.Join(", ", send.ToArray()) +
                         "    →  " + (_pair != null && _pair.SelfTest ? "self-test" : "VR-Exchange") + "  →    " +
                         "Receive · B  " + Side(b) + " — " + string.Join(", ", recv.ToArray());
        }

        private static string Side(ConnectionRecord c)
        {
            if (c == null) return "(missing)";
            return (c.FomName ?? "") + " · " + AppServices.Describe(c) + " · " + c.ExecName;
        }

        /// <summary>Pick what to show: the live run of this case, a given saved run, or the latest.</summary>
        private void Show()
        {
            var cur = _s.Runs.Current;
            if (cur != null && cur.Running && cur.TestCaseId == _caseId && (_runId == 0 || _runId == cur.RunId))
            {
                ShowLive(cur);
            }
            else
            {
                if (_runId == 0 && _caseId != 0)
                {
                    var latest = _s.Store.RunsOfTestCase(_caseId, 1);
                    if (latest.Count > 0) _runId = latest[0].Id;
                }
                if (_runId > 0) ShowSaved(_runId);
                else ShowNothing();
            }
            History();
            Buttons();
        }

        private void ShowNothing()
        {
            _live = null; _report = null;
            Verdict.Text = "Not run yet"; Verdict.Tag = null;
            VerdictSub.Text = "Run it to see what B receives for every row.";
            RowsGrid.ItemsSource = null;
            TranscriptBox.Visibility = Visibility.Collapsed;
        }

        private void ShowLive(RunState st)
        {
            _live = st;
            _runId = st.RunId;
            _rows = st.Rows;
            RowsGrid.ItemsSource = _rows;
            _report = st.Report;
            _context = st.Context;
            TranscriptBox.Visibility = Visibility.Visible;
            LogBox.Text = st.Log.ToString();
            LogBox.ScrollToEnd();
            Tally();
        }

        private void ShowSaved(long runId)
        {
            _live = null;
            var run = _s.Store.RunById(runId);
            if (run == null) { ShowNothing(); return; }
            List<StepResult> steps;
            try { steps = _s.Store.StepsOf(runId); }
            catch (Exception ex) { _s.Fail("Could not load run " + runId, ex); return; }

            _report = new RunReport
            {
                StartedUtc = run.StartedUtc,
                Duration = TimeSpan.FromSeconds(run.Seconds),
                AbortReason = string.IsNullOrEmpty(run.AbortReason) ? null : run.AbortReason,
                TestCase = run.TestCaseName,
            };
            _report.Steps.AddRange(steps);
            _report.Classes.AddRange(ClassTallies(steps));

            _rows = new ObservableCollection<ResultRow>();
            foreach (var st in steps) _rows.Add(ResultRow.From(st));
            RowsGrid.ItemsSource = _rows;
            _context = new List<string>
            {
                "run      " + run.When + "   (#" + run.Id + ")",
                "pair     " + run.PairDisplay,
                "send     " + run.ConnA,
                "receive  " + run.ConnB,
                "testcase " + run.TestCaseDisplay,
            };
            TranscriptBox.Visibility = Visibility.Collapsed;      // transcripts are not kept in history
            Tally(run);
        }

        /// <summary>Per-class roll-up recomputed from the saved parts.</summary>
        private static List<ClassTally> ClassTallies(List<StepResult> steps)
        {
            var list = new List<ClassTally>();
            var by = new Dictionary<string, ClassTally>(StringComparer.OrdinalIgnoreCase);
            foreach (var st in steps)
                foreach (var p in st.Parts)
                {
                    ClassTally t;
                    if (!by.TryGetValue(p.ClassName, out t)) { t = new ClassTally { ClassName = p.ClassName }; by[p.ClassName] = t; list.Add(t); }
                    t.Expected++;
                    if (p.Outcome == Outcome.Pass) t.Matched++;
                    else if (p.Outcome == Outcome.Timeout) t.Missing++;
                    else t.Wrong++;
                }
            return list;
        }

        private void Tally(RunSummary saved = null)
        {
            if (_report == null) return;
            NPass.Text = _report.Passed.ToString(CultureInfo.InvariantCulture);
            NFail.Text = _report.Failed.ToString(CultureInfo.InvariantCulture);
            NTimeout.Text = _report.TimedOut.ToString(CultureInfo.InvariantCulture);

            var ms = new List<double>();
            foreach (var st in _report.Steps) if (st.Latency.HasValue) ms.Add(st.Latency.Value.TotalMilliseconds);
            ms.Sort();
            P50.Text = ms.Count == 0 ? "—" : Pct(ms, 0.5).ToString("F0", CultureInfo.InvariantCulture) + " ms";
            P95.Text = ms.Count == 0 ? "—" : Pct(ms, 0.95).ToString("F0", CultureInfo.InvariantCulture) + " ms";

            var total = _live != null ? _live.Total : _report.Steps.Count;
            if (_live != null && _live.Running)
            {
                Verdict.Text = total == 0 ? "Joining the federations…" : "Running · " + _live.Done + " / " + total;
                Verdict.Tag = null;
                VerdictSub.Text = _live.PairName + " · started " + Fmt.When(_report.StartedUtc);
            }
            else
            {
                string result;
                if (_report.Aborted) result = "Fail";
                else if (_report.Failed > 0) result = "Fail";
                else if (_report.TimedOut > 0) result = "Timeout";
                else result = _report.Passed > 0 ? "Pass" : "Fail";
                Verdict.Tag = result;
                Verdict.Text = (_report.Aborted ? "Aborted" : result == "Timeout" ? "Timed out" : result) +
                               " · " + _report.Passed + " / " + total;
                VerdictSub.Text = _report.Steps.Count + " rows in " +
                                  _report.Duration.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " s · " +
                                  (saved != null ? saved.When : Fmt.When(_report.StartedUtc));
            }

            var classes = _report.Classes.Count > 0 ? _report.Classes : ClassTallies(_report.Steps);
            if (classes.Count > 1)
            {
                var parts = new List<string>();
                foreach (var c in classes) parts.Add(c.ClassName + " " + c.Matched + "/" + c.Expected +
                    (c.Missing > 0 ? " (" + c.Missing + " never arrived)" : "") + (c.Wrong > 0 ? " (" + c.Wrong + " wrong)" : ""));
                ClassLine.Text = "By class:  " + string.Join("  ·  ", parts.ToArray());
                ClassLine.Visibility = Visibility.Visible;
            }
            else ClassLine.Visibility = Visibility.Collapsed;

            AbortLine.Text = _report.Aborted ? "Stopped early: " + _report.AbortReason : "";
            AbortLine.Visibility = _report.Aborted ? Visibility.Visible : Visibility.Collapsed;
            ExportButton.IsEnabled = _report.Steps.Count > 0;
        }

        private static double Pct(List<double> sorted, double p)
        {
            var i = (int)Math.Ceiling(p * sorted.Count) - 1;
            return sorted[Math.Max(0, Math.Min(sorted.Count - 1, i))];
        }

        private void History()
        {
            if (_caseId == 0) { HistoryGrid.ItemsSource = null; return; }
            var runs = _s.Store.RunsOfTestCase(_caseId, 20);
            HistoryGrid.ItemsSource = runs;
            NoHistory.Visibility = runs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var r in runs) if (r.Id == _runId) { HistoryGrid.SelectedItem = r; break; }
        }

        private void Buttons()
        {
            var running = _s.Runs.IsRunning;
            var thisRunning = running && _live != null && _live.Running;
            StopButton.Visibility = thisRunning ? Visibility.Visible : Visibility.Collapsed;
            RunButton.IsEnabled = _case != null && !running;
            RunButton.ToolTip = running && !thisRunning ? "Another test case is running." : null;
        }

        // ------------------------------------------------------------ live events --

        private void OnRunsChanged()
        {
            var cur = _s.Runs.Current;
            if (_live == null && cur != null && cur.Running && cur.TestCaseId == _caseId) { ShowLive(cur); History(); }
            if (_live != null) Tally();
            Buttons();
        }

        private void OnFinished(RunState st)
        {
            if (_live != st) { History(); Buttons(); return; }
            _report = st.Report;
            Tally();
            History();
            Buttons();
        }

        private void OnLog(string text)
        {
            if (_live == null || !_live.Running) return;
            LogBox.AppendText(text);
            if (LogBox.Text.Length > 400000) LogBox.Text = _live.Log.ToString();
            LogBox.ScrollToEnd();
        }

        // ---------------------------------------------------------------- actions --

        private void RowSelected(object sender, SelectionChangedEventArgs e)
        {
            var row = RowsGrid.SelectedItem as ResultRow;
            FieldsGrid.ItemsSource = row == null ? null : row.Fields;
            PartsList.ItemsSource = row == null || row.Parts.Count < 2 ? null : row.Parts;
            FieldTitle.Text = row == null ? "Field comparison" : "Field comparison — row " + row.Step + " · " + row.EntityId;
            RowMessage.Text = row == null ? "Select a row to see what was expected and what arrived." : row.Message;
        }

        private void ViewRun(object sender, RoutedEventArgs e)
        {
            var r = ((FrameworkElement)sender).DataContext as RunSummary;
            if (r == null) return;
            _runId = r.Id;
            var cur = _s.Runs.Current;
            if (cur != null && cur.Running && cur.RunId == r.Id) ShowLive(cur); else ShowSaved(r.Id);
            FieldsGrid.ItemsSource = null;
            PartsList.ItemsSource = null;
            Buttons();
        }

        private void OpenPairs(object sender, RoutedEventArgs e) { _s.Navigate(Page.Pairs); }
        private void OpenPair(object sender, RoutedEventArgs e) { if (_pair != null) _s.Navigate(new NavTarget { Page = Page.PairDetail, PairId = _pair.Id }); }
        private void EditCase(object sender, RoutedEventArgs e) { if (_case != null) _s.Navigate(new NavTarget { Page = Page.TestCaseEdit, PairId = _case.PairId, TestCaseId = _case.Id }); }
        private void Stop(object sender, RoutedEventArgs e) { _s.Runs.Stop(); _s.Status("Stopping after the current row…"); }

        private void RunAgain(object sender, RoutedEventArgs e)
        {
            if (_case == null) return;
            ConnectionRecord a, b;
            _s.SidesOf(_pair, out a, out b);
            var clash = ConnectionRegistry.CrossVersionWarning(a, b);
            if (clash != null && !Dialogs.Confirm(_s.Owner, "Two RTI versions", clash, "Run anyway")) return;
            var why = _s.Runs.Start(new List<long> { _case.Id });
            if (why != null) { _s.Fail("Can't run", why); return; }
            _runId = 0;
            Show();
        }

        private void Export(object sender, RoutedEventArgs e)
        {
            if (_report == null || _report.Steps.Count == 0) { _s.Status("Nothing to export yet."); return; }
            var dlg = new SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv|HTML report (*.html)|*.html|JUnit XML (*.xml)|*.xml",
                FileName = "run-" + (_runId > 0 ? _runId.ToString(CultureInfo.InvariantCulture) : DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)),
                AddExtension = true,
                Title = "Export run results",
            };
            if (dlg.ShowDialog() != true) return;
            try { RunReportWriter.Write(dlg.FileName, _report, _context, _case == null ? "Broker test run" : _case.Name); }
            catch (Exception ex) { _s.Fail("Could not export the results", ex); return; }
            _s.Status("Exported " + System.IO.Path.GetFileName(dlg.FileName) + "  ·  " + _report.Steps.Count + " row(s).");
        }
    }
}
