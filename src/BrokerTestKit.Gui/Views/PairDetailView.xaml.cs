using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace BrokerTestKit.Gui.Views
{
    public sealed class CaseRow
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Note { get; set; }
        public string Classes { get; set; }
        public int Rows { get; set; }
        public string LastResult { get; set; }
        public string LastTag { get; set; }
    }

    /// <summary>Pair details (PRP §3.4).</summary>
    public partial class PairDetailView : UserControl, IPageView
    {
        private readonly AppServices _s;
        private readonly long _pairId;
        private PairRecord _pair;
        private List<TestCaseRecord> _cases = new List<TestCaseRecord>();

        public PairDetailView(AppServices s, long pairId)
        {
            _s = s;
            _pairId = pairId;
            InitializeComponent();
            Load();
            _s.Runs.Finished += OnFinished;
            Unloaded += (sender, e) => _s.Runs.Finished -= OnFinished;
        }

        public bool CanLeave() { return true; }

        private void OnFinished(RunState st) { if (st.PairId == _pairId) Load(); }

        private void Load()
        {
            _pair = _s.Store.Pair(_pairId);
            if (_pair == null) { Title.Text = "Pair not found"; return; }

            Crumb.Text = _pair.Name;
            Title.Text = _pair.Name;
            _cases = _s.Store.TestCases(_pairId);
            Sub.Text = _cases.Count + (_cases.Count == 1 ? " test case" : " test cases") +
                       (_pair.LastRun == null ? " · not run yet"
                                              : " · last run " + _pair.LastRun.When + " · " + _pair.LastRun.ResultText);

            ConnectionRecord a, b;
            _s.SidesOf(_pair, out a, out b);
            Side(a, _pair.ConnA, AFom, AWhat, AFed);
            Side(b, _pair.ConnB, BFom, BWhat, BFed);
            BrokerText.Text = _pair.SelfTest ? "self-test (no broker)" : "VR-Exchange";
            if (b != null && b.Fom != null && b.Fom.IsEvolved && b.Fom.ModuleCount > 0)
                BWhat.Text += " · " + b.Fom.ModuleCount + " join modules";

            var why = _s.WhyNotRunnable(_pair);
            Problem.Text = why == null ? "" : "Can't run yet: " + why + ".";
            Problem.Visibility = why == null ? Visibility.Collapsed : Visibility.Visible;

            var rows = new List<CaseRow>();
            foreach (var t in _cases)
            {
                var send = new List<string>(); var recv = new List<string>(); int n = 0;
                foreach (var sh in t.Sheets)
                {
                    if (sh.IsSend) { send.Add(sh.ClassName); n += sh.Rows; } else recv.Add(sh.ClassName);
                }
                rows.Add(new CaseRow
                {
                    Id = t.Id, Name = t.Name, Note = t.Note,
                    Classes = (send.Count == 0 ? "—" : string.Join(", ", send.ToArray())) + "  →  " +
                              (recv.Count == 0 ? "—" : string.Join(", ", recv.ToArray())),
                    Rows = n,
                    LastResult = t.LastRun == null ? "—" : t.LastRun.ResultText,
                    LastTag = t.LastRun == null ? "" : t.LastRun.Result,
                });
            }
            CasesGrid.ItemsSource = rows;
            CasesGrid.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            NoCases.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RunAllButton.IsEnabled = rows.Count > 0;

            var runs = _s.Store.RunsOfPair(_pairId, 10);
            RunsGrid.ItemsSource = runs;
            RunsGrid.Visibility = runs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            NoRuns.Visibility = runs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void Side(ConnectionRecord c, string name, TextBlock fom, TextBlock what, TextBlock fed)
        {
            if (c == null) { fom.Text = "Missing connection"; what.Text = name; fed.Text = "Edit the pair to choose a FOM and RTI."; return; }
            fom.Text = string.IsNullOrEmpty(c.FomName) ? "(no FOM)" : c.FomName;
            what.Text = AppServices.Describe(c);
            fed.Text = c.ExecName + (string.IsNullOrWhiteSpace(c.Rti.AssistantPort) ? "" : " · port " + c.Rti.AssistantPort);
        }

        private long IdOf(object sender)
        {
            var r = ((FrameworkElement)sender).DataContext as CaseRow;
            return r == null ? 0 : r.Id;
        }

        private void OpenPairs(object sender, RoutedEventArgs e) { _s.Navigate(Page.Pairs); }
        private void EditPair(object sender, RoutedEventArgs e) { _s.Navigate(new NavTarget { Page = Page.PairEdit, PairId = _pairId }); }
        private void AddTestCase(object sender, RoutedEventArgs e) { _s.Navigate(new NavTarget { Page = Page.TestCaseEdit, PairId = _pairId }); }
        private void OpenAllResults(object sender, RoutedEventArgs e) { _s.Navigate(new NavTarget { Page = Page.AllResults, PairId = _pairId, FilterToPair = true }); }

        private void EditCase(object sender, RoutedEventArgs e)
        {
            _s.Navigate(new NavTarget { Page = Page.TestCaseEdit, PairId = _pairId, TestCaseId = IdOf(sender) });
        }

        private void OpenResult(object sender, RoutedEventArgs e)
        {
            _s.Navigate(new NavTarget { Page = Page.TestCaseResult, TestCaseId = IdOf(sender) });
        }

        private void OpenRun(object sender, RoutedEventArgs e)
        {
            var r = ((FrameworkElement)sender).DataContext as RunSummary;
            if (r != null) _s.Navigate(new NavTarget { Page = Page.TestCaseResult, TestCaseId = r.TestCaseId ?? 0, RunId = r.Id });
        }

        private void RunCase(object sender, RoutedEventArgs e) { Start(new List<long> { IdOf(sender) }); }

        private void RunAll(object sender, RoutedEventArgs e)
        {
            var ids = new List<long>();
            foreach (var t in _cases) ids.Add(t.Id);
            Start(ids);
        }

        private void Start(List<long> ids)
        {
            if (!ConfirmVersions()) return;
            var why = _s.Runs.Start(ids);
            if (why != null) { _s.Fail("Can't run", why); return; }
            var st = _s.Runs.Current;
            if (st != null) _s.Navigate(new NavTarget { Page = Page.TestCaseResult, TestCaseId = st.TestCaseId, RunId = st.RunId });
        }

        /// <summary>Two RTI versions on one Assistant port fail obscurely - ask before running (REQ-02).</summary>
        private bool ConfirmVersions()
        {
            ConnectionRecord a, b;
            _s.SidesOf(_pair, out a, out b);
            var clash = ConnectionRegistry.CrossVersionWarning(a, b);
            return clash == null || Dialogs.Confirm(_s.Owner, "Two RTI versions", clash, "Run anyway");
        }

        private void DeleteCase(object sender, RoutedEventArgs e)
        {
            var id = IdOf(sender);
            var tc = _s.Store.TestCase(id);
            if (tc == null) return;
            if (_s.Runs.IsRunning && _s.Runs.Current.TestCaseId == id)
            { Dialogs.Inform(_s.Owner, "Test case is running", "Stop the run before deleting this test case."); return; }

            var runs = _s.Store.RunCountOfTestCase(id);
            var msg = "Delete '" + tc.Name + "'? " +
                      (runs == 0 ? "It has no saved runs." : "This also deletes its " + runs + (runs == 1 ? " saved run." : " saved runs.")) +
                      " This can't be undone.";
            if (!Dialogs.Confirm(_s.Owner, "Delete this test case?", msg,
                                 runs == 0 ? "Delete test case" : "Delete test case and " + runs + (runs == 1 ? " run" : " runs")))
                return;
            _s.Store.DeleteTestCase(id);
            _s.Status("Deleted '" + tc.Name + "'" + (runs == 0 ? "." : " and " + runs.ToString(CultureInfo.InvariantCulture) + " run(s)."));
            Load();
        }
    }
}
