using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using BrokerTestKit.Engine;
using Microsoft.Win32;

namespace BrokerTestKit.Gui.Views
{
    /// <summary>
    /// Add / edit test case (PRP §3.5, REQ-13). Several classes per side: one sheet card each.
    /// Sheets are kept as CsvGrid sheets while editing and saved as content-addressed assets.
    /// </summary>
    public partial class TestCaseEditView : UserControl, IPageView
    {
        private readonly AppServices _s;
        private readonly long _pairId, _caseId;
        private PairRecord _pair;
        private TestCaseRecord _case;
        private ConnectionRecord _a, _b;
        private readonly List<SheetCard> _cards = new List<SheetCard>();
        private bool _dirty, _loading = true;

        public TestCaseEditView(AppServices s, long pairId, long caseId, NavTarget prefill = null)
        {
            _s = s;
            _caseId = caseId;
            InitializeComponent();

            _case = caseId == 0 ? null : _s.Store.TestCase(caseId);
            _pairId = _case != null ? _case.PairId : pairId;
            _pair = _s.Store.Pair(_pairId);
            if (_pair == null) { Title.Text = "Pair not found"; return; }
            _s.SidesOf(_pair, out _a, out _b);

            CrumbPair.Content = _pair.Name;
            CrumbHere.Text = _case == null ? "Add test case" : "Edit test case";
            Title.Text = _case == null ? "Add test case" : "Edit test case";
            SaveButton.Content = _case == null ? "Save test case" : "Save changes";
            DeleteButton.Visibility = _case == null ? Visibility.Collapsed : Visibility.Visible;
            Lead.Text = "One behaviour to check on " + _pair.Name + ": what you Send on A, and what B must Receive " +
                        (_pair.SelfTest ? "(self-test: B is A, no broker)." : "after the broker translates it.");
            SendWhere.Text = "on A · " + Where(_a);
            ReceiveWhere.Text = "on B · " + Where(_b);

            if (_case != null)
            {
                CaseName.Text = _case.Name;
                Timeout.Text = _case.TimeoutSeconds.HasValue ? _case.TimeoutSeconds.Value.ToString(CultureInfo.InvariantCulture) : "";
                foreach (var sh in _case.Sheets)
                {
                    var text = _s.Store.ReadAssetText(sh.AssetHash);
                    if (text == null) { _s.Status("Sheet for " + sh.ClassName + " is missing from the workspace."); continue; }
                    AddCard(sh.IsSend, CsvGrid.FromText(text, null));
                }
            }
            else
            {
                Timeout.Text = "15";
                if (prefill != null && prefill.PrefillA != null && prefill.PrefillB != null) Prefill(prefill);
            }

            Rebuild();
            _loading = false;
        }

        /// <summary>
        /// A matched template pair in one step (REQ-10): a Send sheet for A's class and a Receive
        /// sheet for B's, both holding the same members, so each Receive column has a Send twin.
        /// </summary>
        private void Prefill(NavTarget p)
        {
            var ma = ModelOf(true); var mb = ModelOf(false);
            if (ma == null || mb == null) return;
            try
            {
                foreach (var send in new[] { true, false })
                {
                    var opt = new CsvTemplateGenerator.Options
                    {
                        ForExpectations = !send, SampleRows = 2, FloatTolerance = 0.001,
                        Include = p.PrefillMembers != null && p.PrefillMembers.Count > 0 ? new List<string>(p.PrefillMembers) : null,
                    };
                    var model = send ? ma : mb;
                    var text = CsvTemplateGenerator.Generate(model, Bare(model, send ? p.PrefillA : p.PrefillB), opt);
                    AddCard(send, CsvGrid.FromText(text, null));
                }
                CaseName.Text = FreeName(TemplateHelpers.Leaf(p.PrefillA));
                _dirty = true;          // generated, not saved: leaving should ask
                _s.Status("Matched sheets for " + TemplateHelpers.Leaf(p.PrefillA) + ": " +
                          (p.PrefillMembers == null ? 0 : p.PrefillMembers.Count) + " shared member(s). Fill in the rows, then save.");
            }
            catch (Exception ex) { _s.Fail("Could not generate the matched sheets", ex); }
        }

        /// <summary>"GroundVehicle", or "GroundVehicle 2" when the pair already has one.</summary>
        private string FreeName(string stem)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in _s.Store.TestCases(_pairId)) taken.Add(t.Name);
            if (!taken.Contains(stem)) return stem;
            for (int n = 2; ; n++) if (!taken.Contains(stem + " " + n)) return stem + " " + n;
        }

        /// <summary>The bare class name when it still names the same class - what a hand-written sheet says.</summary>
        private static string Bare(FomModel m, string full)
        {
            var leaf = TemplateHelpers.Leaf(full);
            return string.Equals(m.RtiClassName(leaf), m.RtiClassName(full), StringComparison.OrdinalIgnoreCase) ? leaf : full;
        }

        private static string Where(ConnectionRecord c)
        {
            if (c == null || c.Fom == null) return "no FOM";
            return (c.Fom.IsEvolved ? "HLA Evolved" : "HLA 1.3") + " · " + c.Fom.Name;
        }

        private FomModel ModelOf(bool send) { var c = send ? _a : _b; return c == null || c.Fom == null ? null : c.Fom.Model; }

        public bool CanLeave()
        {
            if (!_dirty) return true;
            return Dialogs.Confirm(_s.Owner, "Discard changes?",
                "This test case has unsaved changes. Leave without saving them?", "Discard", "Keep editing", true);
        }

        private void Dirty(object sender, TextChangedEventArgs e) { if (!_loading) _dirty = true; }
        private void MarkDirty() { if (!_loading) _dirty = true; }

        // ------------------------------------------------------------- the cards --

        private void AddCard(bool send, CsvGrid.Sheet sheet)
        {
            var card = new SheetCard(this, send, sheet, ModelOf(send));
            _cards.Add(card);
        }

        /// <summary>Re-lay out both columns: chips, then one card per class.</summary>
        private void Rebuild()
        {
            foreach (var send in new[] { true, false })
            {
                var chips = send ? SendChips : ReceiveChips;
                var sheets = send ? SendSheets : ReceiveSheets;
                chips.Children.Clear();
                sheets.Children.Clear();
                foreach (var c in _cards)
                {
                    if (c.Send != send) continue;
                    chips.Children.Add(Chip(c));
                    sheets.Children.Add(c.Root);
                }
                var add = new Button { Content = "+ Class", Style = (Style)FindResource("DashedButton"), MinWidth = 0, Margin = new Thickness(0, 0, 0, 6) };
                var sideSend = send;
                add.Click += (s2, e2) => PickClass(sideSend);
                if (ModelOf(send) == null) { add.IsEnabled = false; add.ToolTip = "This side's FOM does not load - fix the pair first."; }
                chips.Children.Add(add);
                if (!_cards.Exists(c => c.Send == send))
                    sheets.Children.Add(new TextBlock
                    {
                        Text = send ? "Add the class (or interaction) A publishes." : "Add each class B must show - one sheet per class.",
                        Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 6, 0, 0),
                    });
            }
        }

        private Border Chip(SheetCard c)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock { Text = c.ClassName, VerticalAlignment = VerticalAlignment.Center });
            var x = new Button
            {
                Content = "×", Style = (Style)FindResource("SubtleButton"), Height = 24, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0),
            };
            System.Windows.Automation.AutomationProperties.SetName(x, "Remove " + c.ClassName + " from " + (c.Send ? "Send" : "Receive"));
            x.Click += (s2, e2) =>
            {
                if (!Dialogs.Confirm(_s.Owner, "Remove " + c.ClassName + "?",
                        "Remove the " + c.ClassName + " sheet from " + (c.Send ? "Send" : "Receive") + "? Its rows go with it.", "Remove")) return;
                _cards.Remove(c);
                MarkDirty();
                Rebuild();
            };
            panel.Children.Add(x);
            return new Border { Style = (Style)FindResource("Chip"), Child = panel };
        }

        private void PickClass(bool send)
        {
            var model = ModelOf(send);
            if (model == null) return;
            var picked = ClassPicker.Show(_s.Owner, model, send ? "Send · A" : "Receive · B");
            if (picked == null) return;

            CsvGrid.Sheet sheet;
            if (picked.UploadPath != null)
            {
                try { sheet = CsvGrid.Load(picked.UploadPath); }
                catch (Exception ex) { _s.Fail("Could not read the CSV", ex); return; }
                if (string.IsNullOrWhiteSpace(sheet.ClassName))
                { _s.Fail("Can't use this CSV", "It has no '# class:' line, so there is no telling what it publishes."); return; }
            }
            else
            {
                try
                {
                    var opt = TemplateHelpers.Options(picked.Members, !send, 2, 0.001);
                    sheet = CsvGrid.FromText(CsvTemplateGenerator.Generate(model, picked.Entry.FullName, opt), null);
                }
                catch (Exception ex) { _s.Fail("Could not generate the sheet", ex); return; }
            }

            if (_cards.Exists(c => c.Send == send && SameClass(c.ClassName, sheet.ClassName)))
            { _s.Fail("Already there", sheet.ClassName + " already has a sheet on this side. Edit that one, or remove it first."); return; }

            AddCard(send, sheet);
            MarkDirty();
            Rebuild();
        }

        internal static bool SameClass(string a, string b)
        {
            return string.Equals(TemplateHelpers.Leaf(a ?? ""), TemplateHelpers.Leaf(b ?? ""), StringComparison.OrdinalIgnoreCase);
        }

        internal void CardChanged() { MarkDirty(); }
        internal AppServices Services { get { return _s; } }
        internal void Relayout() { Rebuild(); }

        // ------------------------------------------------------------------ footer --

        private void OpenPairs(object sender, RoutedEventArgs e) { _s.Navigate(Page.Pairs); }
        private void OpenPair(object sender, RoutedEventArgs e) { _s.Navigate(new NavTarget { Page = Page.PairDetail, PairId = _pairId }); }
        private void Cancel(object sender, RoutedEventArgs e) { OpenPair(null, null); }

        private void DeleteCase(object sender, RoutedEventArgs e)
        {
            if (_case == null) return;
            var runs = _s.Store.RunCountOfTestCase(_case.Id);
            var msg = "Delete '" + _case.Name + "'? " +
                      (runs == 0 ? "It has no saved runs." : "This also deletes its " + runs + (runs == 1 ? " saved run." : " saved runs.")) +
                      " This can't be undone.";
            if (!Dialogs.Confirm(_s.Owner, "Delete this test case?", msg,
                    runs == 0 ? "Delete test case" : "Delete test case and " + runs + (runs == 1 ? " run" : " runs"), "Keep test case", true))
                return;
            _s.Store.DeleteTestCase(_case.Id);
            _dirty = false;
            _s.Status("Deleted '" + _case.Name + "'.");
            OpenPair(null, null);
        }

        private void Save(object sender, RoutedEventArgs e)
        {
            var name = (CaseName.Text ?? "").Trim();
            if (name.Length == 0) { _s.Fail("Can't save", "Give the test case a name."); return; }
            foreach (var other in _s.Store.TestCases(_pairId))
                if (other.Id != _caseId && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase))
                { _s.Fail("Can't save", "This pair already has a test case called \"" + name + "\"."); return; }

            double? timeout = null;
            var tt = (Timeout.Text ?? "").Trim();
            if (tt.Length > 0)
            {
                double v;
                if (!double.TryParse(tt, NumberStyles.Any, CultureInfo.InvariantCulture, out v) || v <= 0)
                { _s.Fail("Can't save", "Timeout per row must be a number of seconds, or blank for the default."); return; }
                timeout = v;
            }
            if (!_cards.Exists(c => c.Send)) { _s.Fail("Can't save", "Add at least one Send class."); return; }
            if (!_cards.Exists(c => !c.Send)) { _s.Fail("Can't save", "Add at least one Receive class - otherwise there is nothing to check on B."); return; }

            // A Send row with no values publishes nothing, so its step can only fail - and a
            // freshly generated sheet (Compare FOMs, + Class) starts exactly like that.
            var empty = new List<string>();
            foreach (var c in _cards)
                foreach (var id in c.EmptyRows()) empty.Add(TemplateHelpers.Leaf(c.ClassName) + " " + id);
            if (empty.Count > 0 &&
                !Dialogs.Confirm(_s.Owner, "Rows with no values",
                    (empty.Count == 1 ? "This Send row has" : "These Send rows have") + " only an entityId, so A would publish no attribute " +
                    "and the step can only fail: " + string.Join(", ", empty.Count > 6 ? empty.GetRange(0, 6).ToArray() : empty.ToArray()) +
                    (empty.Count > 6 ? " and " + (empty.Count - 6) + " more" : "") + ". Save anyway?",
                    "Save anyway", "Keep editing", false))
                return;

            // Validate exactly as a run will read it: every sheet against its own side's FOM,
            // ids unique, references resolvable, every Receive row tied to a Send row.
            var texts = new List<KeyValuePair<SheetCard, string>>();
            foreach (var c in _cards) texts.Add(new KeyValuePair<SheetCard, string>(c, c.ToText()));
            var problem = Validate(name, texts);
            if (problem != null) { _s.Fail("This test case would not run", problem); return; }

            try
            {
                var rec = _case ?? new TestCaseRecord { PairId = _pairId };
                rec.Name = name;
                rec.TimeoutSeconds = timeout;
                rec.Note = null;
                rec.Sheets.Clear();
                foreach (var kv in texts)
                {
                    var cls = kv.Key.ClassName;
                    rec.Sheets.Add(new TestCaseSheet
                    {
                        Side = kv.Key.Send ? "send" : "receive",
                        ClassName = cls,
                        AssetHash = _s.Store.ImportText(kv.Value, TemplateHelpers.Leaf(cls) + (kv.Key.Send ? "-send" : "-receive") + ".csv"),
                    });
                }
                // send sheets first, in the order shown
                rec.Sheets.Sort((x, y) => x.IsSend == y.IsSend ? 0 : x.IsSend ? -1 : 1);
                _s.Store.SaveTestCase(rec);
                _dirty = false;
                _s.Status("Saved test case \"" + name + "\".");
            }
            catch (Exception ex) { _s.Fail("Could not save the test case", ex); return; }

            OpenPair(null, null);
        }

        private string Validate(string name, List<KeyValuePair<SheetCard, string>> texts)
        {
            var dir = Path.Combine(Path.GetTempPath(), "btk-validate", Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                var sb = new StringBuilder("name: check\n");
                int n = 0;
                foreach (var kv in texts)
                {
                    var file = (kv.Key.Send ? "send-" : "receive-") + (n++) + "-" + TemplateHelpers.Leaf(kv.Key.ClassName) + ".csv";
                    File.WriteAllText(Path.Combine(dir, file), kv.Value, new UTF8Encoding(false));
                    sb.Append(kv.Key.Send ? "send: " : "receive: ").Append(file).Append('\n');
                }
                var manifest = Path.Combine(dir, "check.testcase");
                File.WriteAllText(manifest, sb.ToString());
                var tc = TestCase.Load(manifest, ModelOf(true), ModelOf(false));
                if (tc.Steps.Count == 0) return "The Send sheets have no rows.";
                return null;
            }
            catch (Exception ex) { return ex.Message; }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }

    /// <summary>
    /// One class's sheet: header, editable grid, + Add row. On a Receive sheet the first grid row is
    /// "± tolerance" (D2): editable for float columns, read from and written back to the sheet's
    /// own <c># tolerance:</c> directive, so storage does not change.
    /// </summary>
    internal sealed class SheetCard
    {
        public const string TolMarker = "± tolerance";
        private const string NotFloat = "—";

        private readonly TestCaseEditView _owner;
        private readonly CsvGrid.Sheet _sheet;
        private readonly FomModel _model;
        private readonly HashSet<string> _floatCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private double? _defaultTol;
        private DataRow _tolRow;
        private TextBlock _info;

        public readonly bool Send;
        public readonly Border Root;
        public string ClassName { get { return _sheet.ClassName ?? "?"; } }

        public SheetCard(TestCaseEditView owner, bool send, CsvGrid.Sheet sheet, FomModel model)
        {
            _owner = owner;
            Send = send;
            _sheet = sheet;
            _model = model;
            if (!Send) PrepareTolerances();
            Root = Build();
            _sheet.Table.RowChanged += (s, e) => _owner.CardChanged();
            _sheet.Table.RowDeleted += (s, e) => { _owner.CardChanged(); UpdateInfo(); };
            _sheet.Table.TableNewRow += (s, e) => UpdateInfo();
        }

        /// <summary>The ids of Send rows with nothing but an entityId: A would publish no attribute.</summary>
        internal List<string> EmptyRows()
        {
            var ids = new List<string>();
            if (!Send) return ids;
            foreach (DataRow r in _sheet.Table.Rows)
            {
                if (r.RowState == DataRowState.Deleted || r == _tolRow) continue;
                string id = null; var any = false;
                foreach (DataColumn c in _sheet.Table.Columns)
                {
                    var v = Convert.ToString(r[c], CultureInfo.InvariantCulture);
                    if (string.Equals(c.ColumnName, "entityId", StringComparison.OrdinalIgnoreCase)) { id = v; continue; }
                    if (!string.IsNullOrWhiteSpace(v)) { any = true; break; }
                }
                if (!any && !string.IsNullOrWhiteSpace(id)) ids.Add(id.Trim());
            }
            return ids;
        }

        // ------------------------------------------------------------ tolerance --

        private void PrepareTolerances()
        {
            var tolerances = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var variants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ReadDirectives(tolerances, variants);

            foreach (DataColumn col in _sheet.Table.Columns)
            {
                var name = BaseName(col.ColumnName);
                if (string.Equals(name, "entityId", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsFloat(name, variants)) _floatCols.Add(col.ColumnName);
            }

            _tolRow = _sheet.Table.NewRow();
            foreach (DataColumn col in _sheet.Table.Columns)
            {
                if (string.Equals(col.ColumnName, "entityId", StringComparison.OrdinalIgnoreCase)) { _tolRow[col] = TolMarker; continue; }
                if (!_floatCols.Contains(col.ColumnName)) { _tolRow[col] = NotFloat; continue; }
                var t = ToleranceFor(tolerances, BaseName(col.ColumnName));
                _tolRow[col] = t.HasValue ? t.Value.ToString("R", CultureInfo.InvariantCulture) : "";
            }
            _sheet.Table.Rows.InsertAt(_tolRow, 0);
        }

        /// <summary>"BtkCustomTag:string" → "BtkCustomTag".</summary>
        private static string BaseName(string header)
        {
            var i = header.IndexOf(':');
            return i > 0 ? header.Substring(0, i) : header;
        }

        private void ReadDirectives(Dictionary<string, double> tol, Dictionary<string, string> variants)
        {
            var t = _sheet.Directive("tolerance");
            if (!string.IsNullOrEmpty(t))
                foreach (var part in t.Split(','))
                {
                    var p = part.Trim(); if (p.Length == 0) continue;
                    var eq = p.IndexOf('=');
                    double d;
                    if (eq < 0) { if (double.TryParse(p, NumberStyles.Any, CultureInfo.InvariantCulture, out d)) _defaultTol = d; continue; }
                    if (double.TryParse(p.Substring(eq + 1).Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out d))
                        tol[p.Substring(0, eq).Trim()] = d;
                }
            var v = _sheet.Directive("variant");
            if (!string.IsNullOrEmpty(v))
                foreach (var part in v.Split(','))
                {
                    var eq = part.IndexOf('=');
                    if (eq > 0) variants[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
                }
        }

        private static double? ToleranceFor(Dictionary<string, double> tol, string column)
        {
            double t;
            if (tol.TryGetValue(column, out t)) return t;
            foreach (var kv in tol)
                if (kv.Key.EndsWith("*", StringComparison.Ordinal) &&
                    column.StartsWith(kv.Key.Substring(0, kv.Key.Length - 1), StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        private bool IsFloat(string column, Dictionary<string, string> variants)
        {
            if (_model == null) return false;
            try
            {
                string err;
                var r = _model.Resolve(ClassName, column, variants, out err);
                if (r == null) return false;
                var kind = ValueCodec.ParseSpec(r.CodecSpec).Kind;
                return kind == "float32" || kind == "float64";
            }
            catch { return false; }
        }

        /// <summary>The sheet as stored: tolerance row folded back into the directive, then removed.</summary>
        public string ToText()
        {
            if (_tolRow == null) return CsvGrid.ToText(_sheet);

            var parts = new List<string>();
            if (_defaultTol.HasValue) parts.Add(_defaultTol.Value.ToString("R", CultureInfo.InvariantCulture));
            foreach (var col in _floatCols)
            {
                var text = Convert.ToString(_tolRow[col]).Trim();
                double d;
                if (text.Length > 0 && double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out d))
                    parts.Add(BaseName(col) + "=" + d.ToString("R", CultureInfo.InvariantCulture));
            }

            var copy = new CsvGrid.Sheet { Table = _sheet.Table.Copy() };
            copy.Directives.AddRange(_sheet.Directives);
            copy.Table.Rows.RemoveAt(_sheet.Table.Rows.IndexOf(_tolRow));
            copy.Directives.RemoveAll(d => d.TrimStart('#').Trim().StartsWith("tolerance:", StringComparison.OrdinalIgnoreCase));
            if (parts.Count > 0)
            {
                var at = copy.Directives.FindIndex(d => d.TrimStart('#').Trim().StartsWith("kind:", StringComparison.OrdinalIgnoreCase));
                if (at < 0) at = copy.Directives.FindIndex(d => d.TrimStart('#').Trim().StartsWith("class:", StringComparison.OrdinalIgnoreCase));
                copy.Directives.Insert(at + 1, "# tolerance: " + string.Join(", ", parts.ToArray()));
            }
            return CsvGrid.ToText(copy);
        }

        // ------------------------------------------------------------------- UI --

        private int DataRows { get { return _sheet.Table.Rows.Count - (_tolRow == null ? 0 : 1); } }

        private void UpdateInfo()
        {
            if (_info == null) return;
            var kind = string.Equals(_sheet.Kind, "interaction", StringComparison.OrdinalIgnoreCase) ? "interaction" : "object";
            _info.Text = DataRows + (DataRows == 1 ? " row" : " rows") + " · " + kind +
                         (Send ? " · published in this order"
                               : _floatCols.Count == 0 ? " · no float columns"
                                                       : " · ± row = how far a float may differ, blank = exact");
        }

        private Border Build()
        {
            var s = _owner.Services;
            var head = new DockPanel { Margin = new Thickness(12, 10, 12, 10) };
            var links = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(links, Dock.Right);
            links.Children.Add(Link("Upload CSV…", Upload));
            links.Children.Add(Link("Download template", Download));
            head.Children.Add(links);
            var titles = new StackPanel();
            titles.Children.Add(new TextBlock { Text = TemplateHelpers.Leaf(ClassName), FontWeight = FontWeights.SemiBold });
            _info = new TextBlock { Style = (Style)_owner.FindResource("Muted") };
            titles.Children.Add(_info);
            head.Children.Add(titles);
            UpdateInfo();

            var grid = new DataGrid
            {
                AutoGenerateColumns = true, CanUserAddRows = false, IsReadOnly = false, CanUserSortColumns = false,
                ItemsSource = _sheet.Table.DefaultView, MaxHeight = 360, Margin = new Thickness(0),
                BorderThickness = new Thickness(0, 1, 0, 1), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            grid.AutoGeneratingColumn += Column;
            if (_tolRow != null)
            {
                var style = new Style(typeof(DataGridRow), (Style)_owner.FindResource(typeof(DataGridRow)));
                var trig = new DataTrigger { Binding = new Binding("[entityId]"), Value = TolMarker };
                trig.Setters.Add(new Setter(Control.ForegroundProperty, _owner.FindResource("OutcomeWarnText")));
                trig.Setters.Add(new Setter(Control.BackgroundProperty, _owner.FindResource("SurfaceAltBackground")));
                style.Triggers.Add(trig);
                grid.RowStyle = style;
                // Cell text is a TextBlock with the app's implicit style, which ignores the row's
                // foreground - so give the tolerance row its own implicit TextBlock style. Rows are
                // recycled, hence set-or-remove on every load.
                var tb = new Style(typeof(TextBlock), (Style)_owner.FindResource(typeof(TextBlock)));
                tb.Setters.Add(new Setter(TextBlock.ForegroundProperty, _owner.FindResource("OutcomeWarnText")));
                grid.LoadingRow += (o, e) =>
                {
                    var rv = e.Row.Item as DataRowView;
                    var isTol = rv != null && rv.Row == _tolRow;
                    if (isTol) e.Row.Resources[typeof(TextBlock)] = tb; else e.Row.Resources.Remove(typeof(TextBlock));
                };
                grid.BeginningEdit += (o, e) =>
                {
                    // Only float columns take a tolerance; the marker cell itself is not data.
                    var row = e.Row.Item as DataRowView;
                    if (row == null || row.Row != _tolRow) return;
                    var col = e.Column.Header as string;
                    if (col == null || !_floatCols.Contains(col)) e.Cancel = true;
                };
            }

            var add = new Button
            {
                Content = "+ Add row", Style = (Style)_owner.FindResource("LinkButton"), Margin = new Thickness(12, 10, 12, 10),
            };
            add.Click += (o, e) => { _sheet.Table.Rows.Add(_sheet.Table.NewRow()); _owner.CardChanged(); UpdateInfo(); };

            var body = new DockPanel();
            DockPanel.SetDock(head, Dock.Top);
            DockPanel.SetDock(add, Dock.Bottom);
            body.Children.Add(head);
            body.Children.Add(add);
            body.Children.Add(grid);

            return new Border
            {
                Background = (Brush)_owner.FindResource("AppBackground"),
                BorderBrush = (Brush)_owner.FindResource("Divider"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 8, 0, 0), Child = body,
            };
        }

        private Button Link(string text, Action act)
        {
            var b = new Button { Content = text, Style = (Style)_owner.FindResource("LinkButton"), Margin = new Thickness(14, 0, 0, 0) };
            b.Click += (o, e) => act();
            return b;
        }

        /// <summary>A dot in a column name breaks WPF's indexer path; escape it (as on the old Test data tab).</summary>
        private static void Column(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            var text = e.Column as DataGridTextColumn;
            if (text == null || e.PropertyName == null) return;
            if (e.PropertyName.IndexOf('.') >= 0)
                text.Binding = new Binding("[" + e.PropertyName.Replace(".", "^.") + "]");
        }

        private void Upload()
        {
            var s = _owner.Services;
            var dlg = new OpenFileDialog { Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*", Title = "Upload " + TemplateHelpers.Leaf(ClassName) + " sheet" };
            if (dlg.ShowDialog() != true) return;
            CsvGrid.Sheet loaded;
            try { loaded = CsvGrid.Load(dlg.FileName); }
            catch (Exception ex) { s.Fail("Could not read the CSV", ex); return; }
            if (!TestCaseEditView.SameClass(loaded.ClassName, ClassName))
            {
                s.Fail("Wrong class", "That sheet is for '" + (loaded.ClassName ?? "no # class: line") + "', not " + ClassName +
                                      ". Use + Class to add a different class.");
                return;
            }
            _sheet.Directives.Clear();
            _sheet.Directives.AddRange(loaded.Directives);
            _sheet.Table = loaded.Table;
            _floatCols.Clear(); _tolRow = null; _defaultTol = null;
            if (!Send) PrepareTolerances();
            _sheet.Table.RowChanged += (o, e) => _owner.CardChanged();
            _sheet.Table.RowDeleted += (o, e) => { _owner.CardChanged(); UpdateInfo(); };
            var fresh = Build();
            var child = fresh.Child;
            fresh.Child = null;                    // a visual can have only one parent
            Root.Child = child;
            _owner.CardChanged();
            s.Status("Loaded " + Path.GetFileName(dlg.FileName) + " into " + ClassName + ".");
        }

        private void Download()
        {
            var s = _owner.Services;
            if (_model == null) return;
            var dlg = new SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv", FileName = TemplateHelpers.Leaf(ClassName) + "-template.csv", Title = "Download template",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var roots = new List<string>();
                foreach (DataColumn c in _sheet.Table.Columns)
                {
                    var n = BaseName(c.ColumnName);
                    if (string.Equals(n, "entityId", StringComparison.OrdinalIgnoreCase)) continue;
                    var dot = n.IndexOf('.'); var br = n.IndexOf('[');
                    var cut = dot < 0 ? br : br < 0 ? dot : Math.Min(dot, br);
                    var root = cut > 0 ? n.Substring(0, cut) : n;
                    if (!roots.Contains(root)) roots.Add(root);
                }
                var variants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                ReadDirectives(new Dictionary<string, double>(), variants);
                var opt = new CsvTemplateGenerator.Options
                {
                    ForExpectations = !Send, SampleRows = 2, Include = roots.Count > 0 ? roots : null,
                    FloatTolerance = 0.001, Variants = variants,
                };
                File.WriteAllText(dlg.FileName, CsvTemplateGenerator.Generate(_model, ClassName, opt), new UTF8Encoding(false));
                s.Status("Saved " + Path.GetFileName(dlg.FileName) + ".");
            }
            catch (Exception ex) { s.Fail("Could not write the template", ex); }
        }
    }

    /// <summary>The "+ Class" dialog: pick a class or interaction from that side's FOM and tick its columns.</summary>
    internal sealed class ClassPicker
    {
        public FomEntry Entry;
        public List<MemberPick> Members = new List<MemberPick>();
        /// <summary>Set instead when the user chose "Upload a CSV instead…".</summary>
        public string UploadPath;

        public static ClassPicker Show(Window owner, FomModel model, string side)
        {
            ClassPicker result = null;
            var all = TemplateHelpers.Entries(model);
            var shown = new ObservableCollection<FomEntry>(all);
            var members = new ObservableCollection<MemberPick>();

            var filter = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
            System.Windows.Automation.AutomationProperties.SetName(filter, "Filter classes");
            var list = new ListBox { ItemsSource = shown, DisplayMemberPath = "Display" };
            var grid = new DataGrid { ItemsSource = members, CanUserAddRows = false, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column };
            grid.Columns.Add(new DataGridCheckBoxColumn { Binding = new Binding("Selected") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 34 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding("Name"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "FOM datatype", Binding = new Binding("DataType"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Resolved", Binding = new Binding("Resolved"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

            filter.TextChanged += (s, e) =>
            {
                var f = filter.Text.Trim();
                shown.Clear();
                foreach (var x in all) if (f.Length == 0 || x.FullName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) shown.Add(x);
            };
            list.SelectionChanged += (s, e) =>
            {
                members.Clear();
                var entry = list.SelectedItem as FomEntry;
                if (entry != null) foreach (var m in TemplateHelpers.Members(model, entry.FullName)) members.Add(m);
            };

            var ok = new Button { Content = "Add class", Style = (Style)owner.FindResource("AccentButton"), IsDefault = true, MinWidth = 110 };
            var cancel = new Button { Content = "Cancel", IsCancel = true };
            var upload = new Button { Content = "Upload a CSV instead…", Style = (Style)owner.FindResource("LinkButton") };
            var all1 = new Button { Content = "All", MinWidth = 56 };
            var none = new Button { Content = "None", MinWidth = 56 };
            all1.Click += (s, e) => { foreach (var m in members) m.Selected = true; };
            none.Click += (s, e) => { foreach (var m in members) m.Selected = false; };

            var left = new DockPanel();
            var lt = new TextBlock { Text = "Class or interaction", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(lt, Dock.Top); DockPanel.SetDock(filter, Dock.Top);
            left.Children.Add(lt); left.Children.Add(filter); left.Children.Add(list);

            var right = new DockPanel { Margin = new Thickness(14, 0, 0, 0) };
            var rt = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var rb = new StackPanel { Orientation = Orientation.Horizontal };
            rb.Children.Add(all1); rb.Children.Add(none);
            DockPanel.SetDock(rb, Dock.Right);
            rt.Children.Add(rb);
            rt.Children.Add(new TextBlock { Text = "Columns — none ticked = all", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(rt, Dock.Top);
            right.Children.Add(rt); right.Children.Add(grid);

            var body = new Grid { Margin = new Thickness(18, 16, 18, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(right, 1);
            body.Children.Add(left); body.Children.Add(right);

            var foot = new DockPanel { Margin = new Thickness(18, 12, 18, 16) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(cancel); buttons.Children.Add(ok);
            DockPanel.SetDock(upload, Dock.Left);
            foot.Children.Add(upload); foot.Children.Add(buttons);

            var root = new DockPanel();
            DockPanel.SetDock(foot, Dock.Bottom);
            root.Children.Add(foot); root.Children.Add(body);

            var win = new Window
            {
                Title = "Add a class — " + side, Width = 980, Height = 620, Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root, ShowInTaskbar = false,
            };
            ok.Click += (s, e) =>
            {
                var entry = list.SelectedItem as FomEntry;
                if (entry == null) { Dialogs.Inform(win, "Pick a class", "Choose a class or interaction on the left first."); return; }
                result = new ClassPicker { Entry = entry, Members = new List<MemberPick>(members) };
                win.Close();
            };
            upload.Click += (s, e) =>
            {
                var dlg = new OpenFileDialog { Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*" };
                if (dlg.ShowDialog(win) != true) return;
                result = new ClassPicker { UploadPath = dlg.FileName };
                win.Close();
            };
            list.MouseDoubleClick += (s, e) => ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            win.Loaded += (s, e) => filter.Focus();
            win.ShowDialog();
            return result;
        }
    }
}
