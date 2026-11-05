using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace BrokerTestKit.Gui.Views
{
    /// <summary>All results (PRP §3.10): filters, tally for the filter, 25 per page, newest first.</summary>
    public partial class AllResultsView : UserControl, IPageView
    {
        private const int PageSize = 25;
        private readonly AppServices _s;
        private int _offset;
        private bool _ready;
        private readonly DispatcherTimer _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };

        private sealed class PairItem
        {
            public long? Id; public string Name;
            public override string ToString() { return Name; }
        }

        public AllResultsView(AppServices s, long? pairId)
        {
            _s = s;
            InitializeComponent();
            var items = new List<PairItem> { new PairItem { Id = null, Name = "All pairs" } };
            PairItem select = items[0];
            foreach (var p in _s.Store.Pairs())
            {
                var it = new PairItem { Id = p.Id, Name = p.Name };
                items.Add(it);
                if (pairId.HasValue && p.Id == pairId.Value) select = it;
            }
            PairFilter.ItemsSource = items;
            PairFilter.SelectedItem = select;
            _debounce.Tick += (sender, e) => { _debounce.Stop(); _offset = 0; Load(); };
            _ready = true;
            Load();
        }

        public bool CanLeave() { return true; }

        private RunFilter Filter()
        {
            var p = PairFilter.SelectedItem as PairItem;
            var r = ResultFilter.SelectedItem as ComboBoxItem;
            return new RunFilter
            {
                PairId = p == null ? null : p.Id,
                Outcome = r == null || string.IsNullOrEmpty(r.Tag as string) ? null : (string)r.Tag,
                TestCaseText = TextFilter.Text,
            };
        }

        private void Load()
        {
            if (!_ready) return;
            var f = Filter();
            RunTally t;
            List<RunSummary> runs;
            try { t = _s.Store.Tally(f); runs = _s.Store.Runs(f, _offset, PageSize); }
            catch (Exception ex) { _s.Fail("Could not read runs", ex); return; }

            Count.Text = "Runs · " + t.Total;
            TallyText.Text = "Pass " + t.Pass + " · Fail " + t.Fail + " · Timed out " + t.TimedOut;
            RunsGrid.ItemsSource = runs;
            PageText.Text = t.Total == 0 ? "No runs match." : (_offset + 1) + "–" + (_offset + runs.Count) + " of " + t.Total;
            PrevButton.IsEnabled = _offset > 0;
            NextButton.IsEnabled = _offset + runs.Count < t.Total;
        }

        private void FilterChanged(object sender, SelectionChangedEventArgs e) { _offset = 0; Load(); }
        private void TextChanged(object sender, TextChangedEventArgs e) { if (_ready) { _debounce.Stop(); _debounce.Start(); } }
        private void Prev(object sender, RoutedEventArgs e) { _offset = Math.Max(0, _offset - PageSize); Load(); }
        private void Next(object sender, RoutedEventArgs e) { _offset += PageSize; Load(); }
        private void OpenDashboard(object sender, RoutedEventArgs e) { _s.Navigate(Page.Dashboard); }

        private void OpenRun(object sender, RoutedEventArgs e) { Open(((FrameworkElement)sender).DataContext as RunSummary); }
        private void RowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { Open(RunsGrid.SelectedItem as RunSummary); }

        private void Open(RunSummary r)
        {
            if (r != null) _s.Navigate(new NavTarget { Page = Page.TestCaseResult, TestCaseId = r.TestCaseId ?? 0, RunId = r.Id });
        }

        private void OpenPair(object sender, RoutedEventArgs e)
        {
            var r = ((FrameworkElement)sender).DataContext as RunSummary;
            if (r != null && r.PairId.HasValue) _s.Navigate(new NavTarget { Page = Page.PairDetail, PairId = r.PairId.Value });
        }
    }
}
