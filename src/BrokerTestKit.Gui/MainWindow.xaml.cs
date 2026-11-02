using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using BrokerTestKit.Engine;
using Microsoft.Win32;

namespace BrokerTestKit.Gui
{
    /// <summary>
    /// The GUI shell over the Engine (REQ-10). It holds NO RTI connection of its own — the agents
    /// are separate processes — so it stays VC-agnostic and a native fault kills an agent, not the app.
    ///
    /// Flow: register FOMs → assign one to each federation → pick a class or interaction and
    /// generate the CSV template → fill it in → run and watch the grid.
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int PageRegistry = 0, PageFederations = 1, PageTemplate = 2, PageData = 3, PageRun = 4;

        private readonly Workspace _workspace;
        private readonly Store _store;
        private readonly FomRegistry _registry;
        private readonly AppSettings _settings;
        private readonly ConnectionRegistry _connections;
        private readonly ObservableCollection<FomEntry> _allEntries = new ObservableCollection<FomEntry>();
        private readonly ObservableCollection<FomEntry> _shownEntries = new ObservableCollection<FomEntry>();
        private readonly ObservableCollection<MemberPick> _members = new ObservableCollection<MemberPick>();
        private readonly ObservableCollection<ResultRow> _results = new ObservableCollection<ResultRow>();

        private CsvGrid.Sheet _input, _expected;
        private Thread _runThread;
        private volatile bool _abort;
        private readonly StringBuilder _log = new StringBuilder();

        private readonly string _adopted;

        public MainWindow()
        {
            // Everything lives beside the exe. Pull in an AppData workspace from an earlier build
            // first, so the move does not look like the registered FOMs vanished.
            _workspace = new Workspace();
            _adopted = _workspace.AdoptLegacyWorkspace();

            _store = new Store(_workspace);
            _registry = new FomRegistry(_store);
            _settings = new AppSettings(_store);
            _connections = new ConnectionRegistry(_store, _registry);

            InitializeComponent();

            ClassList.ItemsSource = _shownEntries;
            MemberGrid.ItemsSource = _members;
            ResultGrid.ItemsSource = _results;
            FomList.ItemsSource = _registry.Items;
            CFom.ItemsSource = _registry.Items;
            ConnList.ItemsSource = _connections.Items;
            ConnA.ItemsSource = _connections.Items;
            ConnB.ItemsSource = _connections.Items;
            TemplateFom.ItemsSource = _registry.Items;

            try { MakEnvironment.Initialize(); }
            catch (Exception ex) { Status("MAK environment: " + ex.Message); }

            _registry.Load();
            _registry.ImportLegacyTsv(Path.Combine(_workspace.Root, "foms.tsv"));
            _registry.SeedFromConfig(MakEnvironment.RtiConfigDir);

            _connections.Load();
            _connections.SeedFromFoms(MakEnvironment.RtiBin, DefaultRid());
            _settings.Load();
            _settings.SeedFrom(MakEnvironment.LicenseFile);
            SettingsToUi();
            PickDefaultFoms();

            Nav.SelectedIndex = PageRegistry;
            Loaded += (s, e) => DarkTitleBar();
            Status(_registry.Items.Count + " FOM(s) registered · workspace " + _workspace.Root +
                   (_adopted == null ? "" : " · " + _adopted));
            Closed += (s2, e2) => { try { _store.Dispose(); } catch { } };
        }

        // ------------------------------------------------------------------- shell --

        /// <summary>
        /// The nav drives the content host directly. An ElementName TwoWay binding was tried first
        /// and pushed the ListBox's initial -1 into the TabControl before the source was read,
        /// leaving a blank page; an explicit handler has no such ordering hazard.
        /// </summary>
        private void NavChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Tabs != null && Nav.SelectedIndex >= 0) Tabs.SelectedIndex = Nav.SelectedIndex;
        }

        private void GoTo(int page)
        {
            Nav.SelectedIndex = page;
            Tabs.SelectedIndex = page;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>
        /// Paint the OS caption dark to match. WPF does not own the title bar, so without this a
        /// dark window keeps a white one. Attribute 20 is the current id, 19 the early-Win10 one;
        /// both fail silently where unsupported, which is the right outcome.
        /// </summary>
        private void DarkTitleBar()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
            }
            catch { }
        }

        // ----------------------------------------------------------- connections ---

        private static string DefaultRid()
        {
            var cfg = MakEnvironment.RtiConfigDir;
            if (string.IsNullOrEmpty(cfg)) return "";
            var rid = Path.Combine(cfg, "rid.mtl");
            return File.Exists(rid) ? rid : "";
        }

        private void SettingsToUi()
        {
            Licence.Text = _settings.LicenceServer;
            ConnA.SelectedItem = _connections.ByName(_settings.ConnectionA);
            ConnB.SelectedItem = _connections.ByName(_settings.ConnectionB);
        }

        private void SaveSettings(object sender, RoutedEventArgs e)
        {
            _settings.LicenceServer = (Licence.Text ?? "").Trim();
            _settings.Save();
            Status("Licence server saved.");
        }

        private void ConnSelected(object sender, SelectionChangedEventArgs e)
        {
            var rec = ConnList.SelectedItem as ConnectionRecord;
            if (rec == null) return;
            CName.Text = rec.Name;
            CFom.SelectedItem = _registry.ByName(rec.FomName);
            CExec.Text = rec.ExecName;
            CFederate.Text = rec.FederateName;
            CRid.Text = rec.Rti.RidFile;
            CRtiBin.Text = rec.Rti.RtiBin;
            CAsstPort.Text = rec.Rti.AssistantPort;
            CConfigDir.Text = rec.Rti.ConfigDir;
            ShowConnResult(rec, rec.Ok);
        }

        /// <summary>
        /// Offer a name in the user's own convention — "Customised RPR 1.0 · RTI 5.0.1" — but only
        /// while the box still holds a suggestion. A name typed by hand is never overwritten.
        /// </summary>
        private void SuggestConnName()
        {
            if (CName == null || CFom == null) return;
            var fom = CFom.SelectedItem as FomEntryRecord;
            var suggestion = ConnectionRecord.SuggestName(fom == null ? null : fom.Name,
                                                          (CRtiBin.Text ?? "").Trim());
            var current = (CName.Text ?? "").Trim();
            if (current.Length == 0 || current == _lastSuggestion) CName.Text = suggestion;
            _lastSuggestion = suggestion;
        }

        private string _lastSuggestion = "";

        private void ConnFomChanged(object sender, SelectionChangedEventArgs e)
        {
            SuggestConnName();
            var fom = CFom.SelectedItem as FomEntryRecord;
            if (fom != null && string.IsNullOrWhiteSpace(CExec.Text))
                CExec.Text = fom.IsEvolved ? "BTK-FedB" : "BTK-FedA";
        }

        private void ConnRtiChanged(object sender, TextChangedEventArgs e) { SuggestConnName(); }

        private void SaveConn(object sender, RoutedEventArgs e)
        {
            var fom = CFom.SelectedItem as FomEntryRecord;
            var name = (CName.Text ?? "").Trim();
            var existing = _connections.ByName(name);

            var rec = existing ?? new ConnectionRecord();
            rec.Name = name;
            rec.FomName = fom == null ? "" : fom.Name;
            rec.ExecName = (CExec.Text ?? "").Trim();
            rec.FederateName = (CFederate.Text ?? "").Trim();
            rec.Rti.RidFile = (CRid.Text ?? "").Trim();
            rec.Rti.RtiBin = (CRtiBin.Text ?? "").Trim();
            rec.Rti.AssistantPort = (CAsstPort.Text ?? "").Trim();
            rec.Rti.ConfigDir = (CConfigDir.Text ?? "").Trim();

            bool ok;
            try { _connections.Save(rec, out ok); }
            catch (Exception ex) { Fail("Could not save the connection", ex); return; }

            CRid.Text = rec.Rti.RidFile;          // may now point at the imported copy
            ShowConnResult(rec, ok);
            ConnList.Items.Refresh();
            ConnPickChanged(null, null);
            PickDefaultFoms();
        }

        private void ShowConnResult(ConnectionRecord rec, bool ok)
        {
            CResultBox.Visibility = Visibility.Visible;
            CResult.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "TextPrimary" : "ValidationError");
            CResult.Text = ok
                ? "Saved.  " + rec.Status + NL +
                  "Joins " + Quote(rec.ExecName) + " as " + Quote(rec.FederateName) +
                  (string.IsNullOrEmpty(rec.RtiVersion) ? "" : " on " + rec.RtiVersion) + NL +
                  "Agent: " + (rec.Fom != null && rec.Fom.IsEvolved ? "btk-agent1516e.exe" : "btk-agent13.exe") +
                  (string.IsNullOrWhiteSpace(rec.Rti.AssistantPort) ? "" : NL + "RTI Assistant port " + rec.Rti.AssistantPort)
                : rec.Status;
        }

        private void ClearConnForm(object sender, RoutedEventArgs e)
        {
            ConnList.SelectedItem = null;
            CName.Text = "";
            CExec.Text = "";
            CFederate.Text = "";
            CAsstPort.Text = "";
            CConfigDir.Text = "";
            _lastSuggestion = "";
            CResultBox.Visibility = Visibility.Collapsed;
        }

        private void RecheckConn(object sender, RoutedEventArgs e)
        {
            var rec = ConnList.SelectedItem as ConnectionRecord;
            if (rec == null) { Status("Select a connection first."); return; }
            var ok = _connections.Validate(rec);
            ShowConnResult(rec, ok);
            ConnList.Items.Refresh();
            Status(rec.Name + ": " + rec.Status);
        }

        private void RemoveConn(object sender, RoutedEventArgs e)
        {
            var rec = ConnList.SelectedItem as ConnectionRecord;
            if (rec == null) { Status("Select a connection first."); return; }

            var inUse = (ConnA.SelectedItem == rec ? "A" : null);
            if (ConnB.SelectedItem == rec) inUse = inUse == null ? "B" : "A and B";
            var msg = "Remove " + Quote(rec.Name) + " from the connections?";
            if (inUse != null) msg += NL + NL + "It is currently assigned to federation " + inUse + ".";

            if (!Dialogs.Confirm(this, "Remove connection", msg, "Remove")) { Status("Not removed."); return; }

            if (ConnA.SelectedItem == rec) ConnA.SelectedItem = null;
            if (ConnB.SelectedItem == rec) ConnB.SelectedItem = null;
            _connections.Remove(rec);
            ClearConnForm(null, null);
            Status("Removed " + Quote(rec.Name) + ". The FOM and the imported rid stay in the workspace.");
        }

        /// <summary>
        /// Warn about a pair the RTI cannot actually serve. Two different RTI VERSIONS collide on
        /// the Assistant's fixed TCP port 6003 - proven on this machine, and the failure it
        /// produces ("Invalid message version from Assistant") does not point at the cause.
        /// </summary>
        private void ConnPickChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RunWarning == null) return;
            var a = ConnA.SelectedItem as ConnectionRecord;
            var b = SelfTest != null && SelfTest.IsChecked == true ? a : ConnB.SelectedItem as ConnectionRecord;
            var warn = ConnectionRegistry.CrossVersionWarning(a, b);
            RunWarning.Text = warn ?? "";
            RunWarning.Visibility = warn == null ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// Load a bridge profile: both FOMs registered, both connections created, in one step.
        /// The CLI reads the same file, so a rig proved headless is the rig shown here.
        /// </summary>
        private void ImportProfile(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Bridge profile",
                Filter = "Bridge profile (*.profile)|*.profile|All files (*.*)|*.*",
            };
            var cfg = MakEnvironment.RtiConfigDir;
            if (!string.IsNullOrEmpty(cfg) && Directory.Exists(cfg)) dlg.InitialDirectory = cfg;
            if (dlg.ShowDialog() != true) return;

            try
            {
                var report = _connections.ImportProfile(BridgeProfile.Load(dlg.FileName));
                CResult.Text = report;
                CResultBox.Visibility = Visibility.Visible;
                Status("Imported " + Path.GetFileName(dlg.FileName) + ".");
            }
            catch (Exception ex) { Fail("Could not import the profile", ex); }
        }

        private void BrowseCRid(object sender, RoutedEventArgs e) { PickRid(CRid); }

        private void PickRid(TextBox target)
        {
            var dlg = new OpenFileDialog { Filter = "RID (*.mtl)|*.mtl|All files (*.*)|*.*" };
            var cfg = MakEnvironment.RtiConfigDir;
            if (!string.IsNullOrEmpty(cfg) && Directory.Exists(cfg)) dlg.InitialDirectory = cfg;
            if (dlg.ShowDialog() == true) target.Text = dlg.FileName;
        }

        private void BrowseCRtiBin(object sender, RoutedEventArgs e) { PickFolder(CRtiBin); }

        private void BrowseCConfig(object sender, RoutedEventArgs e) { PickFolder(CConfigDir); }

        /// <summary>
        /// WPF has no folder picker, and adding a WinForms reference for one is not worth it.
        /// Pick any file in the folder and take its directory.
        /// </summary>
        private void PickFolder(TextBox target)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select any file inside the RTI bin folder",
                Filter = "All files (*.*)|*.*",
                CheckFileExists = false,
                FileName = "select-this-folder",
            };
            if (!string.IsNullOrWhiteSpace(target.Text) && Directory.Exists(target.Text))
                dlg.InitialDirectory = target.Text;
            if (dlg.ShowDialog() == true)
            {
                var dir = Path.GetDirectoryName(dlg.FileName);
                if (!string.IsNullOrEmpty(dir)) target.Text = dir;
            }
        }

        // ---------------------------------------------------------- FOM registry ----

        private void FomVersionChanged(object sender, RoutedEventArgs e)
        {
            if (FStructLabel == null) return;                 // during InitializeComponent

            var evolved = F1516.IsChecked == true;
            FStructLabel.Content = evolved
                ? "FDD (.xml)  — carries structure AND datatypes"
                : "FED (.fed)  — what the RTI joins with";
            FOmtRow.Visibility = evolved ? Visibility.Collapsed : Visibility.Visible;
            FEvolvedRows.Visibility = evolved ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BrowseStruct(object sender, RoutedEventArgs e)
        {
            Pick(FStruct, F1516.IsChecked == true ? "FDD (*.xml)|*.xml" : "FED (*.fed)|*.fed");
        }

        private void BrowseOmt(object sender, RoutedEventArgs e)
        {
            Pick(FOmt, "OMT (*.omt)|*.omt;*.dif");
        }

        private void Pick(TextBox target, string filter)
        {
            var dlg = new OpenFileDialog { Filter = filter + "|All files (*.*)|*.*" };
            var cfg = MakEnvironment.RtiConfigDir;
            if (!string.IsNullOrEmpty(cfg) && Directory.Exists(cfg)) dlg.InitialDirectory = cfg;
            if (dlg.ShowDialog() == true)
            {
                target.Text = dlg.FileName;
                if (target == FStruct && string.IsNullOrWhiteSpace(FName.Text))
                    FName.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
            }
        }

        private void RegisterFom(object sender, RoutedEventArgs e)
        {
            var name = (FName.Text ?? "").Trim();
            var existing = _registry.ByName(name);
            if (existing != null &&
                !Dialogs.Confirm(this, "Replace FOM",
                    "A FOM named " + Quote(name) + " is already registered." + NL + NL +
                    "Replace it with these files?", "Replace"))
            { Status("Not replaced."); return; }

            bool ok;
            FomEntryRecord rec;
            try
            {
                // Register COPIES the files into the workspace; from here on the paths the user
                // picked are provenance only.
                rec = _registry.Register(name, F1516.IsChecked == true,
                                         (FStruct.Text ?? "").Trim(), (FOmt.Text ?? "").Trim(),
                                         (FModules.Text ?? "").Trim(), (FMim.Text ?? "").Trim(),
                                         out ok);
            }
            catch (Exception ex) { Fail("Could not import the FOM", ex); return; }

            ShowFomResult(rec, ok);
            if (!ok) return;

            PickDefaultFoms();
            FomList.SelectedItem = rec;
            Status("Registered '" + rec.Name + "' — " + rec.Status);
        }

        private void ShowFomResult(FomEntryRecord rec, bool ok)
        {
            FResultBox.Visibility = Visibility.Visible;
            FResult.Text = ok
                ? "Registered.  " + rec.Status + NL +
                  "Imported into the workspace as " + rec.ShortHash + NL +
                  "Datatypes read from " + Path.GetFileName(rec.ParseSource ?? "") + "."
                : "Could not register: " + rec.Status;
            FResult.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "TextPrimary" : "ValidationError");
            FResultBox.BorderBrush = (System.Windows.Media.Brush)FindResource(ok ? "ControlBorder" : "ValidationError");
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

        private void RecheckFom(object sender, RoutedEventArgs e)
        {
            var rec = FomList.SelectedItem as FomEntryRecord;
            if (rec == null) { Status("Select a FOM first."); return; }
            // Re-check goes back to the FILE, ignoring anything stored — that is the point of it.
            var ok = _registry.Validate(rec, true);
            ShowFomResult(rec, ok);
            FomList.Items.Refresh();
            Status(rec.Name + ": " + rec.Status + " (re-parsed from " + Path.GetFileName(rec.ParseSource) + ")");
        }

        private void RemoveFom(object sender, RoutedEventArgs e)
        {
            var rec = FomList.SelectedItem as FomEntryRecord;
            if (rec == null) { Status("Select a FOM first."); return; }

            string inUse = null;
            foreach (var c in _connections.Items)
                if (string.Equals(c.FomName, rec.Name, StringComparison.OrdinalIgnoreCase))
                    inUse = inUse == null ? c.Name : inUse + ", " + c.Name;

            var msg = "Remove \"" + rec.Name + "\" from the registry?";
            if (inUse != null)
                msg += "\n\nFederation " + inUse + " is currently using it and will be left without a FOM.";
            msg += "\n\nThe FOM files themselves are not deleted — only the registry entry.";

            if (!Dialogs.Confirm(this, "Remove FOM", msg)) { Status("Not removed."); return; }

            if (CFom.SelectedItem == rec) CFom.SelectedItem = null;
            if (TemplateFom.SelectedItem == rec) TemplateFom.SelectedItem = null;

            _registry.Remove(rec);
            PickDefaultFoms();
            Status("Removed " + Quote(rec.Name) + ". The imported files stay in the workspace - older runs still reference them.");
        }

        private void ClearFomForm(object sender, RoutedEventArgs e)
        {
            FName.Text = FStruct.Text = FOmt.Text = FModules.Text = FMim.Text = "";
            F13.IsChecked = true;
            FResultBox.Visibility = Visibility.Collapsed;
            FomList.SelectedItem = null;
        }

        /// <summary>Assign sensible defaults: the first 1.3 FOM to A, the first Evolved to B.</summary>
        private void PickDefaultFoms()
        {
            if (ConnA.SelectedItem == null)
                foreach (var c in _connections.Items)
                    if (c.Fom != null && !c.Fom.IsEvolved) { ConnA.SelectedItem = c; break; }
            if (ConnB.SelectedItem == null)
                foreach (var c in _connections.Items)
                    if (c.Fom != null && c.Fom.IsEvolved) { ConnB.SelectedItem = c; break; }

            var a = ConnA.SelectedItem as ConnectionRecord;
            if (TemplateFom.SelectedItem == null && a != null) TemplateFom.SelectedItem = a.Fom;
            ConnPickChanged(null, null);
        }

        // -------------------------------------------------------------- template ----

        private FomModel TemplateModel
        {
            get
            {
                var rec = TemplateFom.SelectedItem as FomEntryRecord;
                return rec == null ? null : rec.Model;
            }
        }

        private void TemplateFomChanged(object sender, SelectionChangedEventArgs e)
        {
            var rec = TemplateFom.SelectedItem as FomEntryRecord;
            _allEntries.Clear();
            _members.Clear();

            if (rec == null || rec.Model == null)
            {
                FomSummary.Text = rec == null ? "" : "not loaded: " + rec.Status;
                ApplyFilter();
                return;
            }

            foreach (var kv in rec.Model.Classes)
                _allEntries.Add(new FomEntry { FullName = kv.Key, IsInteraction = false, MemberCount = kv.Value.Count });
            foreach (var kv in rec.Model.Interactions)
                _allEntries.Add(new FomEntry { FullName = kv.Key, IsInteraction = true, MemberCount = kv.Value.Count });

            ApplyFilter();
            FomSummary.Text = rec.Model.Summary();
        }

        private void FilterChanged(object sender, TextChangedEventArgs e) { ApplyFilter(); }

        private void ApplyFilter()
        {
            if (_shownEntries == null) return;
            var f = (FilterBox == null ? "" : FilterBox.Text ?? "").Trim();
            _shownEntries.Clear();
            foreach (var entry in _allEntries)
                if (f.Length == 0 || entry.FullName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                    _shownEntries.Add(entry);
        }

        private void ClassSelected(object sender, SelectionChangedEventArgs e)
        {
            _members.Clear();
            var entry = ClassList.SelectedItem as FomEntry;
            var model = TemplateModel;
            if (entry == null || model == null) return;

            var members = model.FindClass(entry.FullName);
            if (members == null) return;

            var names = new List<string>(members.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            var className = entry.FullName;
            foreach (var n in names)
            {
                var pick = new MemberPick
                {
                    Selected = false,
                    Name = n,
                    DataType = members[n],
                    Alternatives = AlternativesOf(model, members[n]),
                    IsArray = IsLayoutableArray(model, members[n]),
                };
                if (pick.IsVariant) pick.Choice = MemberPick.NoVariant;   // before the hook: no re-entry
                Describe(model, className, pick);
                pick.ChoiceChanged = m => Describe(model, className, m);
                _members.Add(pick);
            }
            Status(entry.FullName + " — " + _members.Count + (entry.IsInteraction ? " parameters" : " attributes"));
        }

        /// <summary>
        /// The alternatives a variant-typed member offers, or null for an ordinary one.
        ///
        /// The menu comes straight out of the FDD, so the picker never carries a hardcoded list of
        /// RPR names - feed it a different FOM and it offers that FOM's alternatives.
        /// </summary>
        private static List<string> AlternativesOf(FomModel model, string dataType)
        {
            FomType t;
            if (string.IsNullOrEmpty(dataType) || !model.Types.TryGetValue(dataType, out t) ||
                t == null || t.Kind != FomKind.Variant || t.Alternatives.Count == 0)
                return null;

            var menu = new List<string>(t.Alternatives.Keys);
            menu.Sort(StringComparer.OrdinalIgnoreCase);
            menu.Insert(0, MemberPick.NoVariant);
            return menu;
        }

        /// <summary>
        /// Whether this member is an array whose elements the kit can actually lay out.
        ///
        /// Two things disqualify one, and both are honest refusals rather than gaps: an
        /// HLAvariableArray carries a 4-byte element count this kit does not synthesise, and an
        /// element type with no fixed size has no computable stride - RPR's
        /// ArticulatedParameterStruct is the live example, because each element carries a variant
        /// and DIS lets every element choose a different one.
        /// </summary>
        private static bool IsLayoutableArray(FomModel model, string dataType)
        {
            FomType t;
            if (string.IsNullOrEmpty(dataType) || !model.Types.TryGetValue(dataType, out t) ||
                t == null || t.Kind != FomKind.Array)
                return false;

            int elementSize;
            string why;
            return model.ArrayIndexable(t, out elementSize, out why);
        }

        /// <summary>
        /// Fill in the Resolved cell for one member, given the variant choice as it stands.
        ///
        /// An unanswered variant resolves to nothing, and the engine's error for that is a
        /// paragraph naming every alternative - true, but useless in a grid cell next to the
        /// dropdown that lists them anyway. So say the short version, and once an alternative is
        /// chosen report what it actually costs: the number of columns it adds to the sheet.
        /// </summary>
        private void Describe(FomModel model, string className, MemberPick pick)
        {
            if (pick.IsVariant)
            {
                pick.Resolved = pick.HasChoice
                    ? pick.Choice + " → " + Cost(model, className, pick)
                    : "variant — pick one";
                return;
            }

            if (pick.IsArray)
            {
                if (!pick.HasElements) { pick.Resolved = "array — how many elements?"; return; }
                pick.Resolved = Cost(model, className, pick) + " for " + pick.ElementCount + " element(s)";
                return;
            }

            string err;
            var r = model.Resolve(className, pick.Name, out err);
            pick.Resolved = r == null ? "(" + err + ")"
                                      : r.CodecSpec + (r.Size >= 0 ? "  " + r.Size + "B" : "  var");
        }

        /// <summary>How many columns this member adds to the sheet, as the generator would count them.</summary>
        private static string Cost(FomModel model, string className, MemberPick pick)
        {
            try
            {
                var opt = new CsvTemplateGenerator.Options { Include = new List<string> { pick.Name } };
                if (pick.HasChoice) opt.Variants[pick.Name] = pick.Choice;
                if (pick.HasElements) opt.Arrays[pick.Name] = pick.ElementCount;
                return CsvTemplateGenerator.Columns(model, className, opt).Count + " column(s)";
            }
            catch (Exception ex) { return "(" + ex.Message + ")"; }
        }

        private void PickAll(object sender, RoutedEventArgs e) { foreach (var m in _members) m.Selected = true; }
        private void PickNone(object sender, RoutedEventArgs e) { foreach (var m in _members) m.Selected = false; }

        private CsvTemplateGenerator.Options BuildOptions(bool expected)
        {
            var include = new List<string>();
            foreach (var m in _members) if (m.Selected) include.Add(m.Name);

            int rows;
            if (!int.TryParse(RowCount.Text, out rows) || rows < 1) rows = 3;
            double tol;
            if (!double.TryParse(TolBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out tol)) tol = 0.001;

            // Without this the GUI could only ever emit hex for a variant, while the CLI's
            // --variant expanded it - the same FOM producing two different sheets depending on
            // which surface you used.
            var variants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var arrays = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in _members)
            {
                if (m.HasChoice) variants[m.Name] = m.Choice;
                if (m.HasElements) arrays[m.Name] = m.ElementCount;
            }

            return new CsvTemplateGenerator.Options
            {
                ForExpectations = expected,
                SampleRows = rows,
                Include = include.Count > 0 ? include : null,
                FloatTolerance = tol,
                Variants = variants,
                Arrays = arrays,
            };
        }

        private void PreviewTemplate(object sender, RoutedEventArgs e)
        {
            var entry = ClassList.SelectedItem as FomEntry;
            if (entry == null || TemplateModel == null) { Status("Pick a class or interaction first."); return; }
            try
            {
                var text = CsvTemplateGenerator.Generate(TemplateModel, entry.FullName, BuildOptions(false));
                var box = new TextBox { Text = text, Style = (Style)FindResource("Mono"), FontSize = 12 };
                new Window
                {
                    Title = "Template preview — " + entry.FullName,
                    Width = 940,
                    Height = 620,
                    Owner = this,
                    Background = (System.Windows.Media.Brush)FindResource("AppBackground"),
                    Content = box,
                }.ShowDialog();
            }
            catch (Exception ex) { Fail("Could not generate the template", ex); }
        }

        /// <summary>
        /// Write the template out and let the user keep it. Generating an input AND an expected
        /// sheet in one go was two files that differ by a single comment line, so this writes ONE
        /// file that serves as either — the tolerance directive is ignored when the file is read
        /// back as input.
        /// </summary>
        private void DownloadTemplate(object sender, RoutedEventArgs e)
        {
            var entry = ClassList.SelectedItem as FomEntry;
            if (entry == null || TemplateModel == null) { Status("Pick a class or interaction first."); return; }

            var leaf = entry.FullName;
            var dot = leaf.LastIndexOf('.');
            if (dot >= 0) leaf = leaf.Substring(dot + 1);

            var dlg = new SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = leaf + "-template.csv",
                Title = "Download CSV template",
            };
            if (dlg.ShowDialog() != true) { Status("Not saved."); return; }

            try
            {
                var text = CsvTemplateGenerator.Generate(TemplateModel, entry.FullName, BuildOptions(true));
                File.WriteAllText(dlg.FileName, text, new UTF8Encoding(false));
            }
            catch (Exception ex) { Fail("Could not write the template", ex); return; }

            int cols = 0, chosen = 0;
            foreach (var m in _members)
            {
                if (m.Selected) cols++;
                if (m.HasChoice) chosen++;
            }
            Status("Saved " + Path.GetFileName(dlg.FileName) + "  ·  " + entry.FullName +
                   "  ·  " + (cols > 0 ? cols.ToString() : "all") + " column(s) selected" +
                   (chosen > 0 ? "  ·  " + chosen + " variant(s) expanded." : "."));
        }

        // ------------------------------------------------------------- test data ----

        /// <summary>
        /// Fix the binding path WPF generates for a column whose NAME contains a dot.
        ///
        /// The auto-generated path is an indexer, <c>[WorldLocation.X]</c>, and WPF reads the dot
        /// inside it as a property-path separator - so the column renders EMPTY. Every struct
        /// sub-field column is spelled that way (WorldLocation.X, Orientation.Psi,
        /// VelocityVector.XVelocity), so the editor showed blanks for exactly the values a FOM-
        /// driven sheet is most likely to carry, while the data underneath was perfectly fine.
        /// A caret escapes the next character inside an indexer.
        /// </summary>
        private void CsvColumnGenerated(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            var text = e.Column as DataGridTextColumn;
            if (text == null || e.PropertyName == null || e.PropertyName.IndexOf('.') < 0) return;

            text.Binding = new System.Windows.Data.Binding("[" + e.PropertyName.Replace(".", "^.") + "]");
        }

        private void OpenInput(object sender, RoutedEventArgs e)
        {
            var sheet = OpenSheet();
            if (sheet == null) return;
            _input = sheet;
            InputGrid.ItemsSource = _input.Table.DefaultView;
            SheetInfo.Text = "input: " + Path.GetFileName(sheet.Path) + "  class=" + sheet.ClassName;
        }

        private void OpenExpected(object sender, RoutedEventArgs e)
        {
            var sheet = OpenSheet();
            if (sheet == null) return;
            _expected = sheet;
            ExpectedGrid.ItemsSource = _expected.Table.DefaultView;
            SheetInfo.Text = "expected: " + Path.GetFileName(sheet.Path) + "  class=" + sheet.ClassName;
        }

        private CsvGrid.Sheet OpenSheet()
        {
            var dlg = new OpenFileDialog { Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*" };
            if (dlg.ShowDialog() != true) return null;
            try { return CsvGrid.Load(dlg.FileName); }
            catch (Exception ex) { Fail("Could not read the CSV", ex); return null; }
        }

        private void AddRow(object sender, RoutedEventArgs e)
        {
            if (_input != null) _input.Table.Rows.Add(_input.Table.NewRow());
            if (_expected != null) _expected.Table.Rows.Add(_expected.Table.NewRow());
        }

        private void SaveBoth(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_input != null) _input.Path = SaveOne(_input, "input");
                if (_expected != null) _expected.Path = SaveOne(_expected, "expected");
                Status("Saved.");
            }
            catch (Exception ex) { Fail("Could not save", ex); }
        }

        private string SaveOne(CsvGrid.Sheet sheet, string what)
        {
            var path = sheet.Path;
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
            {
                var dlg = new SaveFileDialog
                {
                    Filter = "CSV (*.csv)|*.csv",
                    FileName = string.IsNullOrEmpty(path) ? what + ".csv" : Path.GetFileName(path),
                };
                if (dlg.ShowDialog() != true) return sheet.Path;
                path = dlg.FileName;
            }
            CsvGrid.Save(sheet, path);
            return path;
        }

        // -------------------------------------------------------------------- run ---

        private void RunTest(object sender, RoutedEventArgs e)
        {
            if (_input == null || _expected == null) { Status("Generate or open both sheets first."); return; }

            var connA = ConnA.SelectedItem as ConnectionRecord;
            var selfTest = SelfTest.IsChecked == true;
            var connB = selfTest ? connA : ConnB.SelectedItem as ConnectionRecord;

            if (connA == null || !connA.Ok)
            { Fail("Federation A", new InvalidOperationException(connA == null
                ? "pick a connection for A." : connA.Name + ": " + connA.Status)); return; }
            if (connB == null || !connB.Ok)
            { Fail("Federation B", new InvalidOperationException(connB == null
                ? "pick a connection for B." : connB.Name + ": " + connB.Status)); return; }

            var recA = connA.Fom;
            var recB = connB.Fom;
            if (recA == null || recA.Model == null)
            { Fail("Federation A", new InvalidOperationException("its FOM does not load.")); return; }
            if (recB == null || recB.Model == null)
            { Fail("Federation B", new InvalidOperationException("its FOM does not load.")); return; }

            var clash = ConnectionRegistry.CrossVersionWarning(connA, connB);
            if (clash != null && !Dialogs.Confirm(this, "Two RTI versions", clash, "Run anyway"))
            { Status("Run cancelled."); return; }

            try
            {
                if (_input.Path == null || !File.Exists(_input.Path)) _input.Path = SaveOne(_input, "input");
                if (_expected.Path == null || !File.Exists(_expected.Path)) _expected.Path = SaveOne(_expected, "expected");
                CsvGrid.Save(_input, _input.Path);
                CsvGrid.Save(_expected, _expected.Path);
            }
            catch (Exception ex) { Fail("Could not save the sheets before running", ex); return; }

            _results.Clear();
            Show(null, null);
            FieldGrid.ItemsSource = null;
            _log.Length = 0;
            LogBox.Text = "";
            SummaryText.Text = "";
            _abort = false;
            RunButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            GoTo(PageRun);

            var ctx = new RunContext
            {
                InputPath = _input.Path,
                ExpectedPath = _expected.Path,
                SelfTest = selfTest,
                A = recA,
                B = selfTest ? recA : recB,
                ConnAName = connA.Name,
                ConnBName = connB.Name,
                ExecA = connA.ExecName,
                ExecB = connB.ExecName,
                FederateA = connA.FederateName,
                FederateB = connB.FederateName,
                RtiA = connA.Rti,
                RtiB = connB.Rti,
                Licence = (Licence.Text ?? "").Trim(),
            };

            _settings.LicenceServer = ctx.Licence;
            _settings.ConnectionA = connA.Name;
            _settings.ConnectionB = connB.Name;
            _settings.Save();

            _runThread = new Thread(() => RunWorker(ctx)) { IsBackground = true };
            _runThread.Start();
        }

        private sealed class RunContext
        {
            public string InputPath, ExpectedPath, ExecA, ExecB, FederateA, FederateB, Licence;
            public string ConnAName, ConnBName;
            public bool SelfTest;
            public FomEntryRecord A, B;
            public SideSettings RtiA, RtiB;
        }

        // ------------------------------------------------------- results & history ----

        /// <summary>The run on screen - just finished, or reopened from history. What Export writes.</summary>
        private RunReport _shownReport;
        private List<string> _shownContext;

        private void Show(RunReport report, List<string> context)
        {
            _shownReport = report;
            _shownContext = context;
            ExportButton.IsEnabled = report != null && report.Steps.Count > 0;
        }

        private static List<string> RunContextLines(RunContext ctx, DateTime when)
        {
            return new List<string>
            {
                "run      " + when.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                "pair     " + (ctx.SelfTest ? ctx.ConnAName + "  (self-test)" : ctx.ConnAName + "  ->  " + ctx.ConnBName),
                "input    " + ctx.InputPath,
                "expected " + ctx.ExpectedPath,
            };
        }

        /// <summary>
        /// Content hash of a sheet, so history can tell "same test, different result" from "the
        /// test itself changed" - the file at a path can be edited between runs.
        /// </summary>
        private static string HashOf(string path)
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var f = File.OpenRead(path))
                    return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
            }
            catch { return null; }
        }

        private void ExportResults(object sender, RoutedEventArgs e)
        {
            if (_shownReport == null || _shownReport.Steps.Count == 0) { Status("Nothing to export yet."); return; }

            var dlg = new SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = "run-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".csv",
                Title = "Export run results",
            };
            if (dlg.ShowDialog() != true) { Status("Not exported."); return; }

            try { RunReportCsv.Write(dlg.FileName, _shownReport, _shownContext); }
            catch (Exception ex) { Fail("Could not export the results", ex); return; }
            Status("Exported " + Path.GetFileName(dlg.FileName) + "  ·  " + _shownReport.Steps.Count + " row(s).");
        }

        /// <summary>
        /// Every run the app has made, newest first. Opening one puts it back on the Run tab exactly
        /// as it looked - rows, field-level diffs, latency - so it can be inspected or exported later.
        /// </summary>
        private void ShowHistory(object sender, RoutedEventArgs e)
        {
            List<RunSummary> runs;
            try { runs = _store.RecentRuns(500); }
            catch (Exception ex) { Fail("Could not read run history", ex); return; }
            if (runs.Count == 0) { Status("No runs recorded yet."); return; }

            var grid = new DataGrid
            {
                ItemsSource = runs,
                IsReadOnly = true,
                AutoGenerateColumns = false,
                SelectionMode = DataGridSelectionMode.Single,
                Margin = new Thickness(0, 0, 0, 10),
            };
            foreach (var c in new[]
            {
                new { H = "Started", P = "Started", W = 130.0 },
                new { H = "Pair", P = "Pair", W = -1.0 },
                new { H = "Outcome", P = "Outcome", W = 90.0 },
                new { H = "Rows", P = "Tally", W = 250.0 },
                new { H = "Took", P = "Duration", W = 70.0 },
            })
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = c.H,
                    Binding = new System.Windows.Data.Binding(c.P),
                    Width = c.W < 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(c.W),
                });

            var open = new Button { Content = "Open", MinWidth = 90, IsEnabled = false, IsDefault = true,
                                    Style = (Style)FindResource("AccentButton") };
            var close = new Button { Content = "Close", MinWidth = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
            grid.SelectionChanged += (s2, e2) => open.IsEnabled = grid.SelectedItem != null;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(open);
            buttons.Children.Add(close);
            var root = new DockPanel { Margin = new Thickness(16) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            root.Children.Add(grid);

            var win = new Window
            {
                Title = "Run history",
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 900,
                Height = 520,
                Owner = this,
                Background = (System.Windows.Media.Brush)FindResource("AppBackground"),
                Content = root,
            };
            RunSummary picked = null;
            open.Click += (s2, e2) => { picked = grid.SelectedItem as RunSummary; win.Close(); };
            grid.MouseDoubleClick += (s2, e2) =>
            {
                if (grid.SelectedItem == null) return;
                picked = grid.SelectedItem as RunSummary;
                win.Close();
            };
            win.ShowDialog();

            if (picked != null) LoadRun(picked);
        }

        private void LoadRun(RunSummary run)
        {
            List<StepResult> steps;
            try { steps = _store.StepsOf(run.Id); }
            catch (Exception ex) { Fail("Could not load run " + run.Id, ex); return; }

            var report = new RunReport
            {
                StartedUtc = run.StartedUtc,
                Duration = TimeSpan.FromSeconds(run.Seconds),
                AbortReason = string.IsNullOrEmpty(run.AbortReason) ? null : run.AbortReason,
            };
            report.Steps.AddRange(steps);

            _results.Clear();
            FieldGrid.ItemsSource = null;
            foreach (var st in steps)
            {
                var row = new ResultRow
                {
                    Step = st.Step,
                    EntityId = st.EntityId,
                    Outcome = st.Outcome.ToString(),
                    Message = st.Message,
                    Seconds = st.Elapsed.TotalSeconds,
                    Latency = st.Latency.HasValue
                        ? st.Latency.Value.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) : "",
                };
                foreach (var f in st.Fields) row.Fields.Add(FieldRow.From(f));
                _results.Add(row);
            }

            Show(report, new List<string>
            {
                "run      " + run.Started + "   (history #" + run.Id + ")",
                "pair     " + run.Pair,
                "input    " + run.InputPath,
                "expected " + run.ExpectedPath,
            });
            SummaryText.Text = report.Summary();
            LogBox.Text = "(agent transcript is not kept in history)";
            Status("Run #" + run.Id + "  ·  " + run.Started + "  ·  " + run.Pair + "  ·  " + run.Outcome);
            GoTo(PageRun);
        }

        private void StopTest(object sender, RoutedEventArgs e)
        {
            _abort = true;
            Status("Stopping after the current row…");
        }

        private void RunWorker(RunContext ctx)
        {
            NativeAgentHost a = null, b = null;
            var report = new RunReport();
            long runId = 0;
            try
            {
                EntityCsvParser.Sheet sheetIn, sheetOut;
                var input = EntityCsvParser.ReadInput(ctx.InputPath, ctx.A.Model, out sheetIn);
                var expected = EntityCsvParser.ReadExpected(ctx.ExpectedPath, ctx.B.Model, out sheetOut);

                Dispatch(() =>
                {
                    foreach (var st in input)
                        _results.Add(new ResultRow { Step = st.Step, EntityId = st.EntityId, Outcome = "…" });
                    return true;
                });

                // Opened BEFORE the first row, and each row saved as it lands: a run that is
                // stopped, aborts or crashes still leaves every completed row on record. The store
                // is touched only on the UI thread, where every other use of it happens.
                runId = Dispatch(() => _store.BeginRun(
                    ctx.A.Name, ctx.B.Name, ctx.ExecA, ctx.ExecB, ctx.SelfTest,
                    HashOf(ctx.InputPath), HashOf(ctx.ExpectedPath),
                    ctx.ConnAName, ctx.SelfTest ? ctx.ConnAName : ctx.ConnBName,
                    ctx.InputPath, ctx.ExpectedPath));

                var cfgA = Config("A", ctx.A, ctx.ExecA, ctx.FederateA, sheetIn.ClassName, sheetIn.IsInteraction);
                var cfgB = Config("B", ctx.B, ctx.ExecB, ctx.FederateB, sheetOut.ClassName, sheetOut.IsInteraction);

                // Each agent gets ITS OWN --rid/--config and PATH, so A and B can join different RTIs.
                a = new NativeAgentHost("A", cfgA.AgentExe, ctx.RtiA.AgentArgs, ctx.RtiA.Environment(ctx.Licence));
                b = new NativeAgentHost("B", cfgB.AgentExe, ctx.RtiB.AgentArgs, ctx.RtiB.Environment(ctx.Licence));
                a.LineReceived += OnAgentLine;
                b.LineReceived += OnAgentLine;

                var runner = new NativeTestRunner(a, b, cfgA, cfgB, new RunOptions());
                runner.Progress += (step, msg) => Dispatch(() => { Status("[row " + step + "] " + msg); return true; });

                runner.Prepare(input, expected);

                // One row at a time so the grid fills in live rather than all at the end.
                //
                // Each call therefore returns its OWN report covering one row, and the totals have
                // to be assembled here. Two things do not survive that on their own: the duration
                // (every per-row report times only its own row, so the summary read "in 0.0s" for
                // every run) and the abort (the runner sets it on the per-row report and breaks a
                // loop that only ever had one row in it, so the run carried on regardless).
                var wall = System.Diagnostics.Stopwatch.StartNew();
                foreach (var step in input)
                {
                    if (_abort) break;
                    var partial = runner.Run(new List<InputStep> { step }, expected);
                    foreach (var r in partial.Steps)
                    {
                        report.Steps.Add(r);
                        var captured = r;
                        Dispatch(() =>
                        {
                            var row = FindRow(captured.Step);
                            if (row == null) return true;
                            row.Outcome = captured.Outcome.ToString();
                            row.Message = captured.Message;
                            row.Seconds = captured.Elapsed.TotalSeconds;
                            row.Latency = captured.Latency.HasValue
                                ? captured.Latency.Value.TotalMilliseconds.ToString("F1",
                                      System.Globalization.CultureInfo.InvariantCulture)
                                : "";
                            row.Fields.Clear();
                            foreach (var f in captured.Fields) row.Fields.Add(FieldRow.From(f));
                            try { _store.SaveStep(runId, captured); }
                            catch (Exception ex) { Status("Could not save row " + captured.Step + ": " + ex.Message); }
                            return true;
                        });
                    }

                    if (partial.Aborted) { report.AbortReason = partial.AbortReason; break; }
                }
                wall.Stop();
                report.Duration = wall.Elapsed;

                Dispatch(() =>
                {
                    if (_abort && !report.Aborted) report.AbortReason = "stopped by the operator";
                    try { _store.EndRun(runId, report); } catch { }
                    Show(report, RunContextLines(ctx, DateTime.Now));
                    SummaryText.Text = report.Summary();
                    Status(report.Aborted ? "ABORTED — " + report.AbortReason
                                          : report.AllPassed ? "PASS — every row matched."
                                                             : "FAILURES — see the grid.");
                    return true;
                });
            }
            catch (Exception ex)
            {
                Dispatch(() =>
                {
                    // Close the record with what DID complete, and say why it stopped.
                    if (runId > 0)
                    {
                        report.AbortReason = "run failed: " + ex.Message;
                        try { _store.EndRun(runId, report); } catch { }
                        Show(report, RunContextLines(ctx, DateTime.Now));
                    }
                    Fail("Run failed", ex);
                    return true;
                });
            }
            finally
            {
                if (a != null) try { a.Dispose(); } catch { }
                if (b != null) try { b.Dispose(); } catch { }
                Dispatch(() => { RunButton.IsEnabled = true; StopButton.IsEnabled = false; return true; });
            }
        }

        private static NativeSideConfig Config(string label, FomEntryRecord rec, string exec,
                                               string federate, string className, bool isInteraction)
        {
            // The CSV names the class the way the FOM PARSER knows it - usually the bare leaf,
            // "GroundVehicle". The RTI wants its own spelling, and the two differ per HLA version:
            // "ObjectRoot.BaseEntity....GroundVehicle" on 1.3, "HLAobjectRoot.…" on Evolved. Ask
            // the FOM rather than making the test author write it out, or type it twice.
            var rtiName = rec.Model == null ? null : rec.Model.RtiClassName(className);

            return new NativeSideConfig
            {
                Label = label,
                AgentExe = FindAgent(rec.IsEvolved ? "btk-agent1516e.exe" : "btk-agent13.exe"),
                FederationName = exec,
                FederateName = federate,
                FederateType = "Harness",
                FedFile = rec.StructureFile,
                JoinFomModules = string.IsNullOrWhiteSpace(rec.Modules) ? null : rec.Modules,
                Mim = string.IsNullOrWhiteSpace(rec.Mim) ? null : rec.Mim,
                ObjectClass = string.IsNullOrEmpty(rtiName) ? className : rtiName,
                ClassAlias = className,
                IsInteraction = isInteraction,
            };
        }

        private ResultRow FindRow(int step)
        {
            foreach (var r in _results) if (r.Step == step) return r;
            return null;
        }

        /// <summary>
        /// Append one agent line to the transcript box - WITHOUT blocking the agent.
        ///
        /// This used to go through Dispatch, i.e. Dispatcher.Invoke, which blocks the caller until
        /// the UI thread is done. The caller is the agent's stdout reader thread, and that same
        /// thread is what stamps arrival times on incoming data - so every line the agent produced
        /// waited for a full log re-render before the NEXT line could be timed. The run's own
        /// "broker ms" column was partly measuring this method: latency read min 39ms but p95 137ms,
        /// against 27-32ms for the identical rows under the CLI, which has no such handler.
        /// Rebuilding LogBox.Text from the whole 400KB buffer on every line is what made it
        /// expensive, and what made it worse as the run went on.
        ///
        /// BeginInvoke queues instead of waiting, and AppendText adds to the existing text rather
        /// than replacing all of it; the full rebuild now happens only when the buffer is trimmed.
        /// </summary>
        private void OnAgentLine(string label, string line)
        {
            var text = "[" + label + "] " + line + "\n";
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _log.Append(text);
                if (_log.Length > 400000)
                {
                    _log.Remove(0, 200000);                        // keep the box bounded
                    LogBox.Text = _log.ToString();                 // the costly path, on trim only
                }
                else LogBox.AppendText(text);
                LogBox.ScrollToEnd();
            }));
        }

        private void ResultSelected(object sender, SelectionChangedEventArgs e)
        {
            var row = ResultGrid.SelectedItem as ResultRow;
            FieldGrid.ItemsSource = row == null ? null : row.Fields;
        }

        private static string FindAgent(string exeName)
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 6 && dir != null; i++)
            {
                var candidate = Path.Combine(Path.Combine(dir, "build"), Path.Combine("native", exeName));
                if (File.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(dir.TrimEnd(Path.DirectorySeparatorChar));
                dir = parent == null ? null : parent.FullName;
            }
            throw new FileNotFoundException(
                "Native agent '" + exeName + "' not found. Run src\\BrokerTestKit.AgentNative\\build-native.bat.",
                exeName);
        }

        // ------------------------------------------------------------------ utils --

        private const string NL = "\n";
        private static string Quote(string s) { return "\"" + s + "\""; }

        private T Dispatch<T>(Func<T> fn)
        {
            if (Dispatcher.CheckAccess()) return fn();
            return (T)Dispatcher.Invoke(fn);
        }

        private void Status(string text)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(new Action<string>(Status), text); return; }
            StatusText.Text = text;
        }

        /// <summary>
        /// Show the real message. Licence and RTI failures arrive as raw FlexNet/RTI text and the
        /// user needs it verbatim — a friendly summary alone would hide the diagnosis.
        /// </summary>
        private void Fail(string what, Exception ex)
        {
            var msg = ex.Message;
            if (msg.IndexOf("-4,132", StringComparison.Ordinal) >= 0 ||
                msg.IndexOf("Licensed number of users", StringComparison.OrdinalIgnoreCase) >= 0)
                msg = "Licence seats exhausted — another VR-Link/RTI process is already running.\n\n" + msg;

            Status(what + ": " + ex.Message);
            Dialogs.Inform(this, what, msg, true);
        }
    }
}
