using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BrokerTestKit.Engine;
using Microsoft.Win32;

namespace BrokerTestKit.Gui.Views
{
    /// <summary>
    /// Add New FOM (REQ-10): the registration form, on its own page. Opened with a FOM name it
    /// shows that FOM's files as "Update FOM", and Register replaces them after a confirm.
    /// </summary>
    public partial class FomAddView : UserControl, IPageView
    {
        private readonly AppServices _s;
        private readonly string _editing;
        private bool _dirty, _loading = true;

        public FomAddView(AppServices s, string fomName)
        {
            _s = s;
            InitializeComponent();
            var rec = string.IsNullOrEmpty(fomName) ? null : _s.Foms.ByName(fomName);
            if (rec != null)
            {
                _editing = rec.Name;
                CrumbHere.Text = Title.Text = "Update FOM";
                Lead.Text = "Point \"" + rec.Name + "\" at new files. Register re-imports and re-checks them; " +
                            "the pairs that use it pick up the new files on their next run.";
                FName.Text = rec.Name;
                // Update keeps the name: a changed name would register a second FOM and leave this
                // one - and every pair using it - on the old files.
                FName.IsReadOnly = true;
                FName.ToolTip = "Update keeps the name. To register these files under another name, use Add New FOM.";
                F13.IsChecked = !rec.IsEvolved;
                F1516.IsChecked = rec.IsEvolved;
                FStruct.Text = rec.StructureFile;
                FOmt.Text = rec.DatatypeFile;
                FModules.Text = rec.Modules;
                FMim.Text = rec.Mim;
                ShowResult(rec, rec.Ok);
            }
            FomVersionChanged(null, null);
            foreach (var t in new[] { FName, FStruct, FOmt, FModules, FMim })
                t.TextChanged += (o, e) => { if (!_loading) _dirty = true; };
            Loaded += (o, e) => FName.Focus();
            _loading = false;
        }

        public bool CanLeave()
        {
            if (!_dirty) return true;
            return Dialogs.Confirm(_s.Owner, "Discard this FOM?",
                "The form has details that are not registered. Leave without registering them?", "Discard", "Keep editing", true);
        }

        private void OpenRegistry(object sender, RoutedEventArgs e) { _s.Navigate(Page.FomRegistry); }

        private void FomVersionChanged(object sender, RoutedEventArgs e)
        {
            if (FStructLabel == null || FHint == null) return;     // during InitializeComponent
            var evolved = F1516.IsChecked == true;
            FStructLabel.Content = evolved ? "FDD (.xml)  — carries structure AND datatypes" : "FED (.fed)  — what the RTI joins with";
            FOmtRow.Visibility = evolved ? Visibility.Collapsed : Visibility.Visible;
            FEvolvedRows.Visibility = evolved ? Visibility.Visible : Visibility.Collapsed;
            FHint.Text = evolved
                ? "HLA 1.3 asks for the .fed and its matching .omt instead."
                : "HLA Evolved asks for the FDD (.xml), the extra modules merged at join, and an optional MIM instead.";
            if (!_loading) _dirty = true;
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
            if (name.Length == 0) { _s.Fail("Can't register", "Give the FOM a name."); return; }
            var replacing = _s.Foms.ByName(name) != null;
            if (replacing && !string.Equals(name, _editing, StringComparison.OrdinalIgnoreCase) &&
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

            if (!ok) { ShowResult(rec, false); return; }      // stay: the form still holds what to fix
            _s.Connections.Load();          // connections naming this FOM re-resolve against it
            _dirty = false;
            _s.Navigate(new NavTarget { Page = Page.FomRegistry, FomName = rec.Name });
            _s.Status("Registered '" + rec.Name + "' — " + rec.Status);
        }

        private void ShowResult(FomEntryRecord rec, bool ok)
        {
            FResultBox.Visibility = Visibility.Visible;
            FResult.Text = ok
                ? "Registered.  " + rec.Status + "\nImported into the workspace as " + rec.ShortHash +
                  "\nDatatypes read from " + Path.GetFileName(rec.ParseSource ?? "") + "."
                : "Could not register: " + rec.Status;
            FResult.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "TextPrimary" : "ValidationError");
        }
    }
}
