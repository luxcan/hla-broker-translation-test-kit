using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BrokerTestKit.Engine;
using Microsoft.Win32;

namespace BrokerTestKit.Gui.Views
{
    /// <summary>FOM registry (PRP §3.11). Same behaviour as the old tab, plus "Used by" and the remove guard.</summary>
    public partial class FomRegistryView : UserControl, IPageView
    {
        private readonly AppServices _s;

        public FomRegistryView(AppServices s)
        {
            _s = s;
            InitializeComponent();
            FomList.ItemsSource = _s.Foms.Items;
            RefreshUsage();
        }

        public bool CanLeave() { return true; }

        private void RefreshUsage()
        {
            foreach (var f in _s.Foms.Items)
            {
                try { f.UsedBy = _s.Store.FomUsage(f.Name); } catch { f.UsedBy = 0; }
            }
        }

        private void FocusForm(object sender, RoutedEventArgs e) { ClearFomForm(null, null); FName.Focus(); }

        private void FomVersionChanged(object sender, RoutedEventArgs e)
        {
            if (FStructLabel == null) return;                 // during InitializeComponent
            var evolved = F1516.IsChecked == true;
            FStructLabel.Content = evolved ? "FDD (.xml)  — carries structure AND datatypes" : "FED (.fed)  — what the RTI joins with";
            FOmtRow.Visibility = evolved ? Visibility.Collapsed : Visibility.Visible;
            FEvolvedRows.Visibility = evolved ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BrowseStruct(object sender, RoutedEventArgs e)
        {
            Pick(FStruct, F1516.IsChecked == true ? "FDD (*.xml)|*.xml" : "FED (*.fed)|*.fed");
        }

        private void BrowseOmt(object sender, RoutedEventArgs e) { Pick(FOmt, "OMT (*.omt)|*.omt;*.dif"); }

        private void Pick(TextBox target, string filter)
        {
            var dlg = new OpenFileDialog { Filter = filter + "|All files (*.*)|*.*" };
            var cfg = MakEnvironment.RtiConfigDir;
            if (!string.IsNullOrEmpty(cfg) && Directory.Exists(cfg)) dlg.InitialDirectory = cfg;
            if (dlg.ShowDialog() != true) return;
            target.Text = dlg.FileName;
            if (target == FStruct && string.IsNullOrWhiteSpace(FName.Text))
                FName.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
        }

        private void RegisterFom(object sender, RoutedEventArgs e)
        {
            var name = (FName.Text ?? "").Trim();
            if (_s.Foms.ByName(name) != null &&
                !Dialogs.Confirm(_s.Owner, "Replace FOM",
                    "A FOM named \"" + name + "\" is already registered.\n\nReplace it with these files?", "Replace"))
            { _s.Status("Not replaced."); return; }

            bool ok;
            FomEntryRecord rec;
            try
            {
                // Register COPIES the files into the workspace; the picked paths are provenance only.
                rec = _s.Foms.Register(name, F1516.IsChecked == true,
                                       (FStruct.Text ?? "").Trim(), (FOmt.Text ?? "").Trim(),
                                       (FModules.Text ?? "").Trim(), (FMim.Text ?? "").Trim(), out ok);
            }
            catch (Exception ex) { _s.Fail("Could not import the FOM", ex); return; }

            ShowFomResult(rec, ok);
            if (!ok) return;
            _s.Connections.Load();          // connections naming this FOM re-resolve against it
            RefreshUsage();
            FomList.SelectedItem = rec;
            _s.Status("Registered '" + rec.Name + "' — " + rec.Status);
        }

        private void ShowFomResult(FomEntryRecord rec, bool ok)
        {
            FResultBox.Visibility = Visibility.Visible;
            FResult.Text = ok
                ? "Registered.  " + rec.Status + "\nImported into the workspace as " + rec.ShortHash +
                  "\nDatatypes read from " + Path.GetFileName(rec.ParseSource ?? "") + "."
                : "Could not register: " + rec.Status;
            FResult.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "TextPrimary" : "ValidationError");
        }

        private void FomSelected(object sender, SelectionChangedEventArgs e)
        {
            var rec = FomList.SelectedItem as FomEntryRecord;
            if (rec == null) return;
            FName.Text = rec.Name;
            F13.IsChecked = !rec.IsEvolved;
            F1516.IsChecked = rec.IsEvolved;
            FStruct.Text = rec.StructureFile;
            FOmt.Text = rec.DatatypeFile;
            FModules.Text = rec.Modules;
            FMim.Text = rec.Mim;
            FomVersionChanged(null, null);
            ShowFomResult(rec, rec.Ok);
        }

        private void RecheckAll(object sender, RoutedEventArgs e)
        {
            int bad = 0;
            foreach (var rec in _s.Foms.Items)
                if (!_s.Foms.Validate(rec, true)) bad++;       // back to the FILE, ignoring anything stored
            FomList.Items.Refresh();
            RefreshUsage();
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
            ClearFomForm(null, null);
            _s.Status("Removed \"" + rec.Name + "\".");
        }

        private void ClearFomForm(object sender, RoutedEventArgs e)
        {
            FName.Text = FStruct.Text = FOmt.Text = FModules.Text = FMim.Text = "";
            F13.IsChecked = true;
            FResultBox.Visibility = Visibility.Collapsed;
            FomList.SelectedItem = null;
        }
    }
}
