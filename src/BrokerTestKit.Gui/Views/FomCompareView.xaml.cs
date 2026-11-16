using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using BrokerTestKit.Engine;

namespace BrokerTestKit.Gui.Views
{
    /// <summary>
    /// Compare FOMs (REQ-10 polish): the A and B models of one pair side by side, class by class,
    /// and from a matched class a test case whose Send and Receive sheets share their columns.
    /// </summary>
    public partial class FomCompareView : UserControl, IPageView
    {
        private readonly AppServices _s;
        private readonly long _pairId;
        private PairRecord _pair;
        private List<ClassDiff> _all = new List<ClassDiff>();

        public FomCompareView(AppServices s, long pairId)
        {
            _s = s;
            _pairId = pairId;
            InitializeComponent();
            Load();
        }

        public bool CanLeave() { return true; }

        private void Load()
        {
            _pair = _s.Store.Pair(_pairId);
            if (_pair == null) { Lead.Text = "Pair not found."; return; }
            CrumbPair.Content = _pair.Name;

            ConnectionRecord a, b;
            _s.SidesOf(_pair, out a, out b);
            var ma = a == null || a.Fom == null ? null : a.Fom.Model;
            var mb = b == null || b.Fom == null ? null : b.Fom.Model;
            Lead.Text = "Send · A  " + FomName(a) + "   →   Receive · B  " + FomName(b) +
                        ". Classes match on their path without the root, or on the bare name when that is unique. " +
                        "\"Renamed\" is another type name with the same bytes on the wire; \"Differs\" is a layout " +
                        "the broker has to translate; \"Partial\" means one side has members the other lacks.";
            if (ma == null || mb == null)
            {
                Show("Can't compare: " + (ma == null ? "A" : "B") + "'s FOM does not load. Edit the pair, or fix it in the FOM registry.");
                return;
            }
            try { _all = FomDiff.Compare(ma, mb); }
            catch (Exception ex) { Show("Can't compare: " + ex.Message); return; }
            Apply();
        }

        private static string FomName(ConnectionRecord c)
        {
            if (c == null || c.Fom == null) return "(no FOM)";
            return c.Fom.Name + " (" + (c.Fom.IsEvolved ? "HLA Evolved" : "HLA 1.3") + ")";
        }

        private void Show(string problem)
        {
            Problem.Text = problem;
            Problem.Visibility = Visibility.Visible;
        }

        private void FilterChanged(object sender, SelectionChangedEventArgs e) { Apply(); }
        private void TextChanged(object sender, TextChangedEventArgs e) { Apply(); }

        private void Apply()
        {
            if (ClassGrid == null || KindFilter == null) return;
            var tag = KindFilter.SelectedItem is ComboBoxItem ? (string)((ComboBoxItem)KindFilter.SelectedItem).Tag : "";
            var text = (TextFilter.Text ?? "").Trim();
            int matched = 0, onlyA = 0, onlyB = 0;
            var shown = new List<ClassDiff>();
            foreach (var d in _all)
            {
                if (d.IsMatched) matched++; else if (d.NameA != null) onlyA++; else onlyB++;
                if (!Passes(d, tag)) continue;
                if (text.Length > 0 &&
                    (d.NameA ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (d.NameB ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                shown.Add(d);
            }
            Count.Text = shown.Count + " of " + _all.Count + "  ·  " + matched + " on both sides · " + onlyA + " only on A · " + onlyB + " only on B";
            var keep = ClassGrid.SelectedItem;
            ClassGrid.ItemsSource = shown;
            if (keep != null && shown.Contains((ClassDiff)keep)) ClassGrid.SelectedItem = keep;
        }

        private static bool Passes(ClassDiff d, string tag)
        {
            switch (tag ?? "")
            {
                case "Matched": return d.IsMatched;
                case "Differs": return d.Kind == DiffKind.Differs;
                case "Partial": return d.Kind == DiffKind.Partial;
                case "OnlyA": return d.Kind == DiffKind.OnlyA;
                case "OnlyB": return d.Kind == DiffKind.OnlyB;
                default: return true;
            }
        }

        private ClassDiff Selected { get { return ClassGrid.SelectedItem as ClassDiff; } }

        private void ClassSelected(object sender, SelectionChangedEventArgs e)
        {
            var d = Selected;
            if (d == null)
            {
                DetailTitle.Text = "Pick a class"; DetailNames.Text = ""; DetailHint.Text = "";
                MemberGrid.ItemsSource = null; CreateButton.IsEnabled = false;
                return;
            }
            DetailTitle.Text = d.Leaf + "  ·  " + d.KindText;
            DetailNames.Text = "A: " + (d.NameA ?? "—") + "\nB: " + (d.NameB ?? "—") +
                               (d.MatchedByLeaf ? "\nMatched on the bare name: the paths differ." : "");
            MemberGrid.ItemsSource = d.Members;

            var usable = Usable(d);
            CreateButton.IsEnabled = d.IsMatched && usable.Count > 0;
            if (!d.IsMatched)
                DetailHint.Text = "Only one side has this " + d.What + ", so there is nothing to pair it with. " +
                                  "If the broker maps it to another name, build the test case by hand.";
            else if (usable.Count == 0)
                DetailHint.Text = "No member has the same name and wire layout on both sides, so there is no shared column to check.";
            else
                DetailHint.Text = "The test case gets the " + usable.Count + " member(s) marked Same or Renamed, on both sheets. " +
                                  "Members that differ or exist on one side only are left out: add them by hand, " +
                                  "with the value the broker should produce.";
        }

        /// <summary>Members that can share a column: the same name and the same bytes on both sides.</summary>
        private static List<string> Usable(ClassDiff d)
        {
            var list = new List<string>();
            foreach (var m in d.Members)
                if (m.Kind == DiffKind.Same || m.Kind == DiffKind.Renamed) list.Add(m.Name);
            return list;
        }

        private void CreateTestCase(object sender, RoutedEventArgs e)
        {
            var d = Selected;
            if (d == null || !d.IsMatched) return;
            _s.Navigate(new NavTarget
            {
                Page = Page.TestCaseEdit, PairId = _pairId,
                PrefillA = d.NameA, PrefillB = d.NameB, PrefillMembers = Usable(d),
            });
        }

        private void OpenPairs(object sender, RoutedEventArgs e) { _s.Navigate(Page.Pairs); }
        private void OpenPair(object sender, RoutedEventArgs e) { _s.Navigate(new NavTarget { Page = Page.PairDetail, PairId = _pairId }); }
    }
}
