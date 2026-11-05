using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace BrokerTestKit.Gui.Views
{
    /// <summary>Dashboard (PRP §3.2), including the first-launch empty state.</summary>
    public partial class DashboardView : UserControl, IPageView
    {
        private readonly AppServices _s;
        private RunSummary _last;

        public DashboardView(AppServices s)
        {
            _s = s;
            InitializeComponent();
            Load();
            _s.Runs.Finished += OnRunFinished;
            Unloaded += (sender, e) => _s.Runs.Finished -= OnRunFinished;
        }

        public bool CanLeave() { return true; }

        private void OnRunFinished(RunState st) { Load(); }

        private void Load()
        {
            int foms = _s.Store.CountFoms(), pairs = _s.Store.CountPairs();
            FomCount.Text = foms.ToString(CultureInfo.InvariantCulture);
            PairCount.Text = pairs.ToString(CultureInfo.InvariantCulture);
            LibraryCounts.Opacity = foms + pairs == 0 ? 0.45 : 1;
            NewPairButton.Content = pairs == 0 ? "Create your first pair" : "+ New pair";

            List<RunSummary> runs;
            try { runs = _s.Store.RecentRunsLinked(10); }
            catch (Exception ex) { runs = new List<RunSummary>(); _s.Status("Could not read runs: " + ex.Message); }

            RunsGrid.ItemsSource = runs;
            RunsGrid.Visibility = runs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            EmptyRuns.Visibility = runs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            _last = runs.Count == 0 ? null : runs[0];
            LastRunNone.Visibility = _last == null ? Visibility.Visible : Visibility.Collapsed;
            LastRunSome.Visibility = _last == null ? Visibility.Collapsed : Visibility.Visible;
            if (_last != null)
            {
                LastResult.Text = _last.ResultText;
                LastResult.Tag = _last.Result;
                LastIcon.Tag = _last.Result;
                LastIcon.Text = _last.Result == "Pass" ? "" : _last.Result == "Timeout" ? "" : "";
                LastWhat.Text = _last.PairDisplay + " · " + _last.TestCaseDisplay + " · " + _last.When;
            }
        }

        private void NewPair(object sender, RoutedEventArgs e) { _s.Navigate(new NavTarget { Page = Page.PairEdit, PairId = 0 }); }
        private void OpenRegistry(object sender, RoutedEventArgs e) { _s.Navigate(Page.FomRegistry); }
        private void OpenPairs(object sender, RoutedEventArgs e) { _s.Navigate(Page.Pairs); }
        private void OpenAllResults(object sender, RoutedEventArgs e) { _s.Navigate(Page.AllResults); }
        private void OpenLastRun(object sender, RoutedEventArgs e) { Open(_last); }

        private void OpenRun(object sender, RoutedEventArgs e) { Open(((FrameworkElement)sender).DataContext as RunSummary); }

        private void RunDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { Open(RunsGrid.SelectedItem as RunSummary); }

        private void OpenPairOfRun(object sender, RoutedEventArgs e)
        {
            var r = ((FrameworkElement)sender).DataContext as RunSummary;
            if (r == null || !r.PairId.HasValue) return;
            _s.Navigate(new NavTarget { Page = Page.PairDetail, PairId = r.PairId.Value });
        }

        private void Open(RunSummary r)
        {
            if (r == null) return;
            _s.Navigate(new NavTarget { Page = Page.TestCaseResult, TestCaseId = r.TestCaseId ?? 0, RunId = r.Id });
        }
    }
}
