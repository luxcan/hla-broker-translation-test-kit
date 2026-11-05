using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using BrokerTestKit.Engine;
using Microsoft.Win32;

namespace BrokerTestKit.Gui.Views
{
    /// <summary>CSV template tool (PRP §3.11): unchanged behaviour, preview now inline.</summary>
    public partial class CsvTemplateView : UserControl, IPageView
    {
        private readonly AppServices _s;
        private readonly ObservableCollection<FomEntry> _all = new ObservableCollection<FomEntry>();
        private readonly ObservableCollection<FomEntry> _shown = new ObservableCollection<FomEntry>();
        private readonly ObservableCollection<MemberPick> _members = new ObservableCollection<MemberPick>();

        public CsvTemplateView(AppServices s)
        {
            _s = s;
            InitializeComponent();
            ClassList.ItemsSource = _shown;
            MemberGrid.ItemsSource = _members;
            TemplateFom.ItemsSource = _s.Foms.Items;
            if (_s.Foms.Items.Count > 0) TemplateFom.SelectedIndex = 0;
        }

        public bool CanLeave() { return true; }

        private FomModel Model
        {
            get { var rec = TemplateFom.SelectedItem as FomEntryRecord; return rec == null ? null : rec.Model; }
        }

        private void TemplateFomChanged(object sender, SelectionChangedEventArgs e)
        {
            var rec = TemplateFom.SelectedItem as FomEntryRecord;
            _all.Clear(); _members.Clear();
            if (rec == null || rec.Model == null)
            {
                FomSummary.Text = rec == null ? "" : "not loaded: " + rec.Status;
                ApplyFilter();
                return;
            }
            foreach (var entry in TemplateHelpers.Entries(rec.Model)) _all.Add(entry);
            ApplyFilter();
            FomSummary.Text = rec.Model.Summary();
        }

        private void FilterChanged(object sender, TextChangedEventArgs e) { ApplyFilter(); }

        private void ApplyFilter()
        {
            if (_shown == null) return;
            var f = (FilterBox == null ? "" : FilterBox.Text ?? "").Trim();
            _shown.Clear();
            foreach (var entry in _all)
                if (f.Length == 0 || entry.FullName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) _shown.Add(entry);
        }

        private void ClassSelected(object sender, SelectionChangedEventArgs e)
        {
            _members.Clear();
            var entry = ClassList.SelectedItem as FomEntry;
            if (entry == null || Model == null) return;
            foreach (var m in TemplateHelpers.Members(Model, entry.FullName)) _members.Add(m);
            _s.Status(entry.FullName + " — " + _members.Count + (entry.IsInteraction ? " parameters" : " attributes"));
        }

        private void PickAll(object sender, RoutedEventArgs e) { foreach (var m in _members) m.Selected = true; }
        private void PickNone(object sender, RoutedEventArgs e) { foreach (var m in _members) m.Selected = false; }

        private CsvTemplateGenerator.Options BuildOptions(bool expected)
        {
            int rows;
            if (!int.TryParse(RowCount.Text, out rows) || rows < 1) rows = 3;
            double tol;
            if (!double.TryParse(TolBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out tol)) tol = 0.001;
            return TemplateHelpers.Options(_members, expected, rows, tol);
        }

        private void PreviewTemplate(object sender, RoutedEventArgs e)
        {
            var entry = ClassList.SelectedItem as FomEntry;
            if (entry == null || Model == null) { _s.Status("Pick a class or interaction first."); return; }
            try
            {
                PreviewBox.Text = CsvTemplateGenerator.Generate(Model, entry.FullName, BuildOptions(false));
                PreviewBox.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { _s.Fail("Could not generate the template", ex); }
        }

        /// <summary>One file that serves as either sheet - the tolerance line is ignored when read as input.</summary>
        private void DownloadTemplate(object sender, RoutedEventArgs e)
        {
            var entry = ClassList.SelectedItem as FomEntry;
            if (entry == null || Model == null) { _s.Status("Pick a class or interaction first."); return; }

            var dlg = new SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = TemplateHelpers.Leaf(entry.FullName) + "-template.csv",
                Title = "Download CSV template",
            };
            if (dlg.ShowDialog() != true) { _s.Status("Not saved."); return; }

            try { File.WriteAllText(dlg.FileName, CsvTemplateGenerator.Generate(Model, entry.FullName, BuildOptions(true)), new UTF8Encoding(false)); }
            catch (Exception ex) { _s.Fail("Could not write the template", ex); return; }
            _s.Status("Saved " + Path.GetFileName(dlg.FileName) + "  ·  " + entry.FullName);
        }
    }

    /// <summary>FOM → class list → member picks → generator options. Shared by the template tool and the test case editor.</summary>
    internal static class TemplateHelpers
    {
        public static string Leaf(string fullName)
        {
            var dot = (fullName ?? "").LastIndexOf('.');
            return dot < 0 ? fullName : fullName.Substring(dot + 1);
        }

        public static List<FomEntry> Entries(FomModel model)
        {
            var list = new List<FomEntry>();
            foreach (var kv in model.Classes) list.Add(new FomEntry { FullName = kv.Key, IsInteraction = false, MemberCount = kv.Value.Count });
            foreach (var kv in model.Interactions) list.Add(new FomEntry { FullName = kv.Key, IsInteraction = true, MemberCount = kv.Value.Count });
            list.Sort((a, b) => string.Compare(Leaf(a.FullName), Leaf(b.FullName), StringComparison.OrdinalIgnoreCase));
            return list;
        }

        public static List<MemberPick> Members(FomModel model, string className)
        {
            var outp = new List<MemberPick>();
            var members = model.FindClass(className);
            if (members == null) return outp;
            var names = new List<string>(members.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var n in names)
            {
                var pick = new MemberPick
                {
                    Selected = false, Name = n, DataType = members[n],
                    Alternatives = AlternativesOf(model, members[n]),
                    IsArray = IsLayoutableArray(model, members[n]),
                };
                if (pick.IsVariant) pick.Choice = MemberPick.NoVariant;   // before the hook: no re-entry
                Describe(model, className, pick);
                var cls = className;
                pick.ChoiceChanged = m => Describe(model, cls, m);
                outp.Add(pick);
            }
            return outp;
        }

        public static CsvTemplateGenerator.Options Options(IEnumerable<MemberPick> members, bool expected, int rows, double tol)
        {
            var include = new List<string>();
            var variants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var arrays = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in members)
            {
                if (m.Selected) include.Add(m.Name);
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

        /// <summary>The alternatives a variant-typed member offers, straight from the FDD; null for an ordinary one.</summary>
        private static List<string> AlternativesOf(FomModel model, string dataType)
        {
            FomType t;
            if (string.IsNullOrEmpty(dataType) || !model.Types.TryGetValue(dataType, out t) ||
                t == null || t.Kind != FomKind.Variant || t.Alternatives.Count == 0) return null;
            var menu = new List<string>(t.Alternatives.Keys);
            menu.Sort(StringComparer.OrdinalIgnoreCase);
            menu.Insert(0, MemberPick.NoVariant);
            return menu;
        }

        /// <summary>An array the kit can lay out: fixed element size, no HLAvariableArray count prefix to synthesise.</summary>
        private static bool IsLayoutableArray(FomModel model, string dataType)
        {
            FomType t;
            if (string.IsNullOrEmpty(dataType) || !model.Types.TryGetValue(dataType, out t) || t == null || t.Kind != FomKind.Array)
                return false;
            int elementSize; string why;
            return model.ArrayIndexable(t, out elementSize, out why);
        }

        private static void Describe(FomModel model, string className, MemberPick pick)
        {
            if (pick.IsVariant)
            {
                pick.Resolved = pick.HasChoice ? pick.Choice + " → " + Cost(model, className, pick) : "variant — pick one";
                return;
            }
            if (pick.IsArray)
            {
                pick.Resolved = !pick.HasElements ? "array — how many elements?"
                              : Cost(model, className, pick) + " for " + pick.ElementCount + " element(s)";
                return;
            }
            string err;
            var r = model.Resolve(className, pick.Name, out err);
            pick.Resolved = r == null ? "(" + err + ")" : r.CodecSpec + (r.Size >= 0 ? "  " + r.Size + "B" : "  var");
        }

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
    }
}
