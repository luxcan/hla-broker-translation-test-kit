using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BrokerTestKit.Engine;
using Microsoft.Win32;

namespace BrokerTestKit.Gui.Views
{
    /// <summary>
    /// New / edit pair (PRP §3.9, D1). Each side is stored as a connection row the pair owns
    /// (<c>pair-&lt;id&gt;-A/B</c>), so the run path and REQ-02's checks are unchanged. A pair made
    /// by the migration points at shared, older connections until it is first saved here.
    /// </summary>
    public partial class PairEditView : UserControl, IPageView
    {
        private readonly AppServices _s;
        private readonly long _pairId;
        private PairRecord _pair;
        private string _lastSuggestion = "";
        private bool _loading = true;

        public PairEditView(AppServices s, long pairId)
        {
            _s = s;
            _pairId = pairId;
            InitializeComponent();
            AFom.ItemsSource = _s.Foms.Items;
            BFom.ItemsSource = _s.Foms.Items;
            Load();
            _loading = false;
            Refresh();
        }

        public bool CanLeave() { return true; }

        private bool IsNew { get { return _pairId == 0; } }

        private void Load()
        {
            Licence.Text = _s.Settings.LicenceServer;
            if (IsNew)
            {
                _pair = new PairRecord();
                Title.Text = "New pair";
                CrumbHere.Text = "New pair";
                CrumbSep.Visibility = CrumbPair.Visibility = Visibility.Collapsed;
                DeleteButton.Visibility = Visibility.Collapsed;

                FomEntryRecord a13 = null, evo = null;
                foreach (var f in _s.Foms.Items)
                {
                    if (!f.IsEvolved && a13 == null) a13 = f;
                    if (f.IsEvolved && evo == null) evo = f;
                }
                AFom.SelectedItem = a13;
                BFom.SelectedItem = evo;
                var bin = MakEnvironment.RtiBin ?? "";
                var rid = DefaultRid();
                Fill("A", null, bin, rid, "BTK-FedA", "BtkRunnerA", "6003");
                Fill("B", null, bin, rid, "BTK-FedB", "BtkRunnerB", "6004");
                return;
            }

            _pair = _s.Store.Pair(_pairId);
            if (_pair == null) { Title.Text = "Pair not found"; return; }
            Title.Text = "Edit pair";
            CrumbPair.Content = _pair.Name;
            CrumbHere.Text = "Edit pair";
            PairName.Text = _pair.Name;
            _lastSuggestion = "\u0000";             // the stored name is the user's, never a suggestion
            SelfTest.IsChecked = _pair.SelfTest;

            ConnectionRecord a, b;
            _s.SidesOf(_pair, out a, out b);
            if (_pair.SelfTest) b = _s.Connections.ByName(_pair.ConnB);
            Fill("A", a, null, null, null, null, null);
            Fill("B", b ?? a, null, null, null, null, null);
            if (b == null && a != null) { BExec.Text = "BTK-FedB"; BFederate.Text = "BtkRunnerB"; BPort.Text = "6004"; }
        }

        private void Fill(string side, ConnectionRecord c, string bin, string rid, string exec, string federate, string port)
        {
            var fom = side == "A" ? AFom : BFom;
            if (c != null) fom.SelectedItem = _s.Foms.ByName(c.FomName);
            T(side, "Exec").Text = c != null ? c.ExecName : exec;
            T(side, "Federate").Text = c != null ? c.FederateName : federate;
            T(side, "Bin").Text = c != null ? c.Rti.RtiBin : bin;
            T(side, "Rid").Text = c != null ? c.Rti.RidFile : rid;
            T(side, "Config").Text = c != null ? c.Rti.ConfigDir : "";
            T(side, "Port").Text = c != null ? c.Rti.AssistantPort : port;
        }

        private TextBox T(string side, string field) { return (TextBox)FindName(side + field); }

        private static string DefaultRid()
        {
            var cfg = MakEnvironment.RtiConfigDir;
            if (string.IsNullOrEmpty(cfg)) return "";
            var rid = Path.Combine(cfg, "rid.mtl");
            return File.Exists(rid) ? rid : "";
        }

        // ------------------------------------------------------------ live state --

        private void FomChanged(object sender, SelectionChangedEventArgs e) { SuggestName(); Refresh(); }
        private void RtiChanged(object sender, TextChangedEventArgs e) { if (!_loading) Refresh(); }
        private void SelfTestChanged(object sender, RoutedEventArgs e) { SuggestName(); Refresh(); }

        private void NameTyped(object sender, TextChangedEventArgs e) { }

        /// <summary>Offer "&lt;FOM A&gt; → &lt;FOM B&gt;" only while the box still holds a suggestion.</summary>
        private void SuggestName()
        {
            if (PairName == null || AFom == null) return;
            var a = AFom.SelectedItem as FomEntryRecord;
            var b = BFom.SelectedItem as FomEntryRecord;
            var suggestion = (a == null ? "?" : a.Name) + " → " + (SelfTest.IsChecked == true ? "self-test" : (b == null ? "?" : b.Name));
            var current = (PairName.Text ?? "").Trim();
            if (current.Length == 0 || current == _lastSuggestion) PairName.Text = suggestion;
            _lastSuggestion = suggestion;
        }

        private void Refresh()
        {
            if (_loading || AHla == null) return;
            var self = SelfTest.IsChecked == true;
            BFields.Visibility = self ? Visibility.Collapsed : Visibility.Visible;
            SelfTestNote.Visibility = self ? Visibility.Visible : Visibility.Collapsed;
            ArrowText.Text = self ? "self-test — no broker" : "VR-Exchange 2.9 translates";

            var fa = AFom.SelectedItem as FomEntryRecord;
            var fb = BFom.SelectedItem as FomEntryRecord;
            AHla.Text = fa == null ? "—" : fa.Version;
            BHla.Text = self ? "—" : fb == null ? "—" : fb.Version;

            JoinBox.Visibility = !self && fb != null && fb.IsEvolved ? Visibility.Visible : Visibility.Collapsed;
            if (fb != null && fb.IsEvolved)
                JoinText.Text = fb.ModuleCount == 0
                    ? "None registered with this FOM. The VR-Exchange 1516e broker joins with 16 modules from bin64\\MAK-1516e-Config.xml — add them to the FOM."
                    : fb.ModuleCount + " modules, registered with the FOM \"" + fb.Name + "\".";

            AStatus.Text = Check(Build("A", "probe-A"));
            BStatus.Text = self ? "" : Check(Build("B", "probe-B"));

            var ca = Build("A", "A"); var cb = Build("B", "B");
            var warn = self ? null : ConnectionRegistry.CrossVersionWarning(ca, cb);
            var samePort = !self && ca.RtiVersion.Length > 0 && ca.RtiVersion != cb.RtiVersion &&
                           (APort.Text ?? "").Trim() == (BPort.Text ?? "").Trim();
            PortLine.Text = self ? "" : warn ?? (samePort ? "Two RTI versions share one Assistant port." :
                            "✓ RTI versions and Assistant ports are compatible.");
            PortLine.Foreground = (System.Windows.Media.Brush)FindResource(warn == null && !samePort ? "OutcomePassText" : "OutcomeWarnText");
        }

        private ConnectionRecord Build(string side, string name)
        {
            var fom = (side == "A" ? AFom : BFom).SelectedItem as FomEntryRecord;
            var c = new ConnectionRecord
            {
                Name = name,
                FomName = fom == null ? "" : fom.Name,
                ExecName = (T(side, "Exec").Text ?? "").Trim(),
                FederateName = (T(side, "Federate").Text ?? "").Trim(),
            };
            c.Rti.RtiBin = (T(side, "Bin").Text ?? "").Trim();
            c.Rti.RidFile = (T(side, "Rid").Text ?? "").Trim();
            c.Rti.ConfigDir = (T(side, "Config").Text ?? "").Trim();
            c.Rti.AssistantPort = (T(side, "Port").Text ?? "").Trim();
            return c;
        }

        /// <summary>"RTI found · rid parses · FOM resolves", or the first thing wrong.</summary>
        private string Check(ConnectionRecord c)
        {
            if (_s.Connections.Validate(c))
                return "✓ RTI found · rid found · FOM loads" + (string.IsNullOrEmpty(c.RtiVersion) ? "" : " · " + c.RtiVersion);
            return "✗ " + c.Status;
        }

        private void Recheck(object sender, RoutedEventArgs e) { Refresh(); }

        // ---------------------------------------------------------------- pickers --

        private void BrowseRid(object sender, RoutedEventArgs e)
        {
            var target = (TextBox)FindName((string)((FrameworkElement)sender).Tag);
            var dlg = new OpenFileDialog { Filter = "RID (*.mtl)|*.mtl|All files (*.*)|*.*" };
            var cfg = MakEnvironment.RtiConfigDir;
            if (!string.IsNullOrEmpty(cfg) && Directory.Exists(cfg)) dlg.InitialDirectory = cfg;
            if (dlg.ShowDialog() == true) { target.Text = dlg.FileName; Refresh(); }
        }

        /// <summary>WPF has no folder picker: pick any file in the folder and take its directory.</summary>
        private void BrowseFolder(object sender, RoutedEventArgs e)
        {
            var target = (TextBox)FindName((string)((FrameworkElement)sender).Tag);
            var dlg = new OpenFileDialog
            {
                Title = "Select any file inside the folder", Filter = "All files (*.*)|*.*",
                CheckFileExists = false, FileName = "select-this-folder",
            };
            if (!string.IsNullOrWhiteSpace(target.Text) && Directory.Exists(target.Text)) dlg.InitialDirectory = target.Text;
            if (dlg.ShowDialog() != true) return;
            var dir = Path.GetDirectoryName(dlg.FileName);
            if (!string.IsNullOrEmpty(dir)) { target.Text = dir; Refresh(); }
        }

        // ------------------------------------------------------------------ footer --

        private void OpenPairs(object sender, RoutedEventArgs e) { _s.Navigate(Page.Pairs); }
        private void OpenPair(object sender, RoutedEventArgs e) { Back(); }
        private void ManageFoms(object sender, RoutedEventArgs e) { _s.Navigate(Page.FomRegistry); }
        private void Cancel(object sender, RoutedEventArgs e) { Back(); }

        private void Back()
        {
            if (IsNew) _s.Navigate(Page.Pairs);
            else _s.Navigate(new NavTarget { Page = Page.PairDetail, PairId = _pairId });
        }

        private void Save(object sender, RoutedEventArgs e)
        {
            var name = (PairName.Text ?? "").Trim();
            if (name.Length == 0) { _s.Fail("Can't save the pair", "Give the pair a name."); return; }
            foreach (var other in _s.Store.Pairs())
                if (other.Id != _pairId && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase))
                { _s.Fail("Can't save the pair", "Another pair is already called \"" + name + "\"."); return; }

            var self = SelfTest.IsChecked == true;
            var probeA = Build("A", "probe-A");
            if (!_s.Connections.Validate(probeA)) { _s.Fail("Send · A is not ready", probeA.Status); return; }
            if (!self)
            {
                var probeB = Build("B", "probe-B");
                if (!_s.Connections.Validate(probeB)) { _s.Fail("Receive · B is not ready", probeB.Status); return; }
                var clash = ConnectionRegistry.CrossVersionWarning(probeA, probeB);
                if (clash != null && !Dialogs.Confirm(_s.Owner, "Two RTI versions", clash, "Save anyway")) return;
            }

            try
            {
                _pair.Name = name;
                _pair.SelfTest = self;
                if (_pair.Id == 0) { _pair.ConnA = "pending"; _pair.ConnB = "pending"; _s.Store.SavePair(_pair); }

                bool ok;
                var a = Build("A", PairRecord.OwnConnectionName(_pair.Id, "A"));
                _s.Connections.Save(a, out ok);
                if (!ok) throw new InvalidOperationException("Send · A: " + a.Status);
                _pair.ConnA = a.Name;

                if (self) _pair.ConnB = a.Name;
                else
                {
                    var b = Build("B", PairRecord.OwnConnectionName(_pair.Id, "B"));
                    _s.Connections.Save(b, out ok);
                    if (!ok) throw new InvalidOperationException("Receive · B: " + b.Status);
                    _pair.ConnB = b.Name;
                }
                _s.Store.SavePair(_pair);

                _s.Settings.LicenceServer = (Licence.Text ?? "").Trim();
                _s.Settings.Save();
            }
            catch (Exception ex) { _s.Fail("Could not save the pair", ex); return; }

            _s.Rig.CheckAsync();
            _s.Status("Saved pair \"" + name + "\".");
            _s.Navigate(new NavTarget { Page = Page.PairDetail, PairId = _pair.Id });
        }

        private void DeletePair(object sender, RoutedEventArgs e)
        {
            if (_pair == null || _pair.Id == 0) return;
            if (_s.Runs.IsRunning && _s.Runs.Current.PairId == _pair.Id)
            { Dialogs.Inform(_s.Owner, "A test is running", "Stop the run before deleting this pair."); return; }

            var cases = _s.Store.TestCases(_pair.Id).Count;
            var runs = _s.Store.RunCountOfPair(_pair.Id);
            var msg = "Delete '" + _pair.Name + "'? This also deletes its " +
                      cases + (cases == 1 ? " test case and " : " test cases and ") +
                      runs + (runs == 1 ? " saved run." : " saved runs.") + " This can't be undone.\n\n" +
                      "Export any results you want to keep first. Uploaded sheets stay on disk.";
            if (!Dialogs.Confirm(_s.Owner, "Delete this pair?", msg,
                    runs == 0 ? "Delete pair" : "Delete pair and " + runs + (runs == 1 ? " run" : " runs"), "Keep pair", true))
                return;

            try
            {
                _s.Store.DeletePair(_pair.Id);
                _s.Connections.Load();
            }
            catch (Exception ex) { _s.Fail("Could not delete the pair", ex); return; }
            _s.Status("Deleted pair \"" + _pair.Name + "\".");
            _s.Navigate(Page.Pairs);
        }
    }
}
