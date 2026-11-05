using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace BrokerTestKit.Gui.Views
{
    /// <summary>One row of the Pairs table.</summary>
    public sealed class PairRow
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string SendFom { get; set; }
        public string SendWhere { get; set; }
        public string ReceiveFom { get; set; }
        public string ReceiveWhere { get; set; }
        public string Arrow { get { return "→"; } }
        public int TestCases { get; set; }
        public string LastResult { get; set; }
        public string LastTag { get; set; }
        public string When { get; set; }
        public string SearchText { get; set; }

        public static PairRow From(AppServices s, PairRecord p)
        {
            ConnectionRecord a, b;
            s.SidesOf(p, out a, out b);
            var row = new PairRow
            {
                Id = p.Id, Name = p.Name, TestCases = p.TestCaseCount,
                SendFom = a == null ? "(missing: " + p.ConnA + ")" : a.FomName,
                SendWhere = AppServices.Describe(a),
                ReceiveFom = p.SelfTest ? "self-test — same as A" : b == null ? "(missing: " + p.ConnB + ")" : b.FomName,
                ReceiveWhere = p.SelfTest ? "no broker" : AppServices.Describe(b),
                LastResult = p.LastRun == null ? "—" : p.LastRun.ResultText,
                LastTag = p.LastRun == null ? "" : p.LastRun.Result,
                When = Fmt.When(p.LastRun),
            };
            row.SearchText = (row.Name + " " + row.SendFom + " " + row.SendWhere + " " + row.ReceiveFom + " " + row.ReceiveWhere).ToLowerInvariant();
            return row;
        }
    }

    /// <summary>Pairs (PRP §3.3). Filtered in memory: tens of rows, not thousands.</summary>
    public partial class PairsView : UserControl, IPageView
    {
        private readonly AppServices _s;
        private readonly List<PairRow> _all = new List<PairRow>();

        public PairsView(AppServices s)
        {
            _s = s;
            InitializeComponent();
            foreach (var p in _s.Store.Pairs()) _all.Add(PairRow.From(_s, p));
            Apply();
        }

        public bool CanLeave() { return true; }

        private void SearchChanged(object sender, TextChangedEventArgs e) { Apply(); }

        private void Apply()
        {
            var q = (Search == null ? "" : Search.Text ?? "").Trim().ToLowerInvariant();
            var shown = new List<PairRow>();
            foreach (var r in _all) if (q.Length == 0 || r.SearchText.Contains(q)) shown.Add(r);
            PairsGrid.ItemsSource = shown;
            EmptyText.Visibility = _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_all.Count > 0 && shown.Count == 0) { EmptyText.Text = "No pair matches \"" + q + "\"."; EmptyText.Visibility = Visibility.Visible; }
        }

        private void NewPair(object sender, RoutedEventArgs e) { _s.Navigate(new NavTarget { Page = Page.PairEdit }); }

        private void OpenPair(object sender, RoutedEventArgs e) { Open(((FrameworkElement)sender).DataContext as PairRow); }

        private void RowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { Open(PairsGrid.SelectedItem as PairRow); }

        private void Open(PairRow r)
        {
            if (r != null) _s.Navigate(new NavTarget { Page = Page.PairDetail, PairId = r.Id });
        }
    }
}
