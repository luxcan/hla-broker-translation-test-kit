using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BrokerTestKit.Gui.Views
{
    /// <summary>
    /// FOM Registry (PRP §3.11): the registered FOMs only. Registering happens on its own page,
    /// Add New FOM (<see cref="FomAddView"/>), which also updates a FOM's files.
    /// </summary>
    public partial class FomRegistryView : UserControl, IPageView
    {
        private readonly AppServices _s;

        /// <param name="select">A FOM to select on arrival, e.g. the one just registered.</param>
        public FomRegistryView(AppServices s, string select)
        {
            _s = s;
            InitializeComponent();
            FomList.ItemsSource = _s.Foms.Items;
            Refresh();
            if (!string.IsNullOrEmpty(select))
            {
                var rec = _s.Foms.ByName(select);
                if (rec != null) { FomList.SelectedItem = rec; FomList.ScrollIntoView(rec); }
            }
        }

        public bool CanLeave() { return true; }

        private void Refresh()
        {
            foreach (var f in _s.Foms.Items)
            {
                try { f.UsedBy = _s.Store.FomUsage(f.Name); } catch { f.UsedBy = 0; }
            }
            var n = _s.Foms.Items.Count;
            Count.Text = "Registered FOMs · " + n;
            NoFoms.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AddFom(object sender, RoutedEventArgs e) { _s.Navigate(Page.FomAdd); }

        private void UpdateFom(object sender, RoutedEventArgs e)
        {
            var rec = FomList.SelectedItem as FomEntryRecord;
            if (rec == null) { _s.Status("Select a FOM first."); return; }
            _s.Navigate(new NavTarget { Page = Page.FomAdd, FomName = rec.Name });
        }

        private void RowDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // only a double-click on a row, not on the header or the empty space below
            var row = ItemsControl.ContainerFromElement(FomList, e.OriginalSource as DependencyObject) as DataGridRow;
            if (row != null) UpdateFom(sender, e);
        }

        private void RecheckAll(object sender, RoutedEventArgs e)
        {
            int bad = 0;
            foreach (var rec in _s.Foms.Items)
                if (!_s.Foms.Validate(rec, true)) bad++;       // back to the FILE, ignoring anything stored
            FomList.Items.Refresh();
            Refresh();
            _s.Status("Re-checked " + _s.Foms.Items.Count + " FOM(s)" + (bad == 0 ? " — all load." : " — " + bad + " with problems."));
        }

        private void RemoveFom(object sender, RoutedEventArgs e)
        {
            var rec = FomList.SelectedItem as FomEntryRecord;
            if (rec == null) { _s.Status("Select a FOM first."); return; }

            var used = _s.Store.FomUsage(rec.Name);
            if (used > 0)
            {
                Dialogs.Inform(_s.Owner, "FOM in use",
                    "\"" + rec.Name + "\" is used by " + used + (used == 1 ? " pair" : " pairs") +
                    ". Change those pairs to another FOM, or delete them, before removing it.");
                return;
            }
            if (!Dialogs.Confirm(_s.Owner, "Remove FOM",
                    "Remove \"" + rec.Name + "\" from the registry?\n\nThe imported files stay in the workspace — older runs still reference them."))
            { _s.Status("Not removed."); return; }

            _s.Foms.Remove(rec);
            Refresh();
            _s.Status("Removed \"" + rec.Name + "\".");
        }
    }
}
