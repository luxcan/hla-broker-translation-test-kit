using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using BrokerTestKit.Engine;

namespace BrokerTestKit.Gui
{
    public class Notify : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>One class or interaction offered in the picker after a FOM is loaded.</summary>
    public sealed class FomEntry
    {
        public string FullName { get; set; }
        public bool IsInteraction { get; set; }
        public int MemberCount { get; set; }

        /// <summary>Leaf name, for a readable list on FDDs where everything is fully qualified.</summary>
        public string Display
        {
            get
            {
                var dot = FullName.LastIndexOf('.');
                var leaf = dot < 0 ? FullName : FullName.Substring(dot + 1);
                return leaf + "   (" + MemberCount + (IsInteraction ? " params)" : " attrs)");
            }
        }

        public string Kind { get { return IsInteraction ? "interaction" : "object"; } }
    }

    /// <summary>A tickable attribute/parameter in the template builder.</summary>
    public sealed class MemberPick : Notify
    {
        private bool _selected;
        public bool Selected
        {
            get { return _selected; }
            set { _selected = value; Raise("Selected"); }
        }
        public string Name { get; set; }
        public string DataType { get; set; }

        private string _resolved;
        /// <summary>What this member will put in the sheet. Recomputed when Choice changes.</summary>
        public string Resolved
        {
            get { return _resolved; }
            set { _resolved = value; Raise("Resolved"); }
        }

        /// <summary>Shown first in the variant menu: leave the attribute as one hex blob.</summary>
        public const string NoVariant = "(none)";

        /// <summary>
        /// The alternatives this member's variant record offers, NoVariant first; null when it is
        /// not a variant at all.
        ///
        /// A variant record has no layout until one alternative is named - which is why an
        /// unanswered one can only be written as hex. The FOM states the whole menu, so the picker
        /// can offer it rather than making the test author look it up.
        /// </summary>
        public List<string> Alternatives { get; set; }

        public bool IsVariant { get { return Alternatives != null && Alternatives.Count > 0; } }

        /// <summary>True once a real alternative (not NoVariant) is chosen.</summary>
        public bool HasChoice
        {
            get { return IsVariant && !string.IsNullOrEmpty(_choice) && _choice != NoVariant; }
        }

        private string _choice;
        /// <summary>The chosen alternative, e.g. DRM_FPW.</summary>
        public string Choice
        {
            get { return _choice; }
            set
            {
                _choice = value;
                Raise("Choice"); Raise("HasChoice");
                var h = ChoiceChanged;
                if (h != null) h(this);
            }
        }

        /// <summary>
        /// True when this member is a dynamic array whose elements CAN be laid out - fixed element
        /// size and a publishable encoding. False for an ordinary member, and false for an array
        /// the kit refuses to index (the reason then shows in Resolved).
        /// </summary>
        public bool IsArray { get; set; }

        private string _elements = "";
        /// <summary>
        /// How many elements to lay out. Blank leaves the attribute as one hex value.
        ///
        /// Nothing in the FOM can supply this: RPR encodes these arrays "without array length,
        /// containing only the elements", so the length IS the content and only the test author
        /// knows how many the case needs.
        /// </summary>
        public string Elements
        {
            get { return _elements; }
            set
            {
                _elements = value;
                Raise("Elements"); Raise("HasElements");
                var h = ChoiceChanged;
                if (h != null) h(this);
            }
        }

        /// <summary>The element count as a number, or 0 when unset or not a number.</summary>
        public int ElementCount
        {
            get
            {
                int n;
                return int.TryParse((_elements ?? "").Trim(), NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out n) && n > 0 ? n : 0;
            }
        }

        public bool HasElements { get { return IsArray && ElementCount > 0; } }

        /// <summary>Owner hook, so the Resolved column can answer for the new choice at once.</summary>
        public Action<MemberPick> ChoiceChanged;
    }

    /// <summary>
    /// Per-federation settings. REQ-01 lives here: which FOM files to ask for depends on the
    /// HLA version, and the GUI must not offer 1516-only fields to a 1.3 federation.
    /// </summary>
    public sealed class SideVm : Notify
    {
        private string _label = "A";
        private bool _isEvolved;
        private string _federationName = "BTK-FedA";
        private string _federateName = "BtkRunnerA";
        private string _fedFile = "";
        private string _omtFile = "";
        private string _fomModules = "";
        private string _mim = "";
        private string _objectClass = "";
        private string _agentExe = "";

        public string Label { get { return _label; } set { _label = value; Raise("Label"); } }

        /// <summary>False = HLA 1.3 (.fed + .omt); true = Evolved (FDD .xml [+ modules] [+ MIM]).</summary>
        public bool IsEvolved
        {
            get { return _isEvolved; }
            set
            {
                _isEvolved = value;
                Raise("IsEvolved"); Raise("IsHla13"); Raise("VersionText");
                Raise("StructureFileLabel"); Raise("DatatypeFileLabel");
            }
        }
        public bool IsHla13 { get { return !_isEvolved; } }
        public string VersionText { get { return _isEvolved ? "HLA Evolved (1516-2010)" : "HLA 1.3"; } }

        /// <summary>The file the RTI joins with: .fed for 1.3, FDD .xml for Evolved.</summary>
        public string StructureFileLabel { get { return _isEvolved ? "FDD (.xml)" : "FED (.fed)"; } }
        /// <summary>Where datatypes come from. For 1.3 this is a SECOND file; the .fed has none.</summary>
        public string DatatypeFileLabel { get { return _isEvolved ? "(datatypes are in the FDD)" : "OMT (.omt)  REQUIRED"; } }

        public string FederationName { get { return _federationName; } set { _federationName = value; Raise("FederationName"); } }
        public string FederateName { get { return _federateName; } set { _federateName = value; Raise("FederateName"); } }
        public string FedFile { get { return _fedFile; } set { _fedFile = value; Raise("FedFile"); } }
        public string OmtFile { get { return _omtFile; } set { _omtFile = value; Raise("OmtFile"); } }
        public string FomModules { get { return _fomModules; } set { _fomModules = value; Raise("FomModules"); } }
        public string Mim { get { return _mim; } set { _mim = value; Raise("Mim"); } }
        public string ObjectClass { get { return _objectClass; } set { _objectClass = value; Raise("ObjectClass"); } }
        public string AgentExe { get { return _agentExe; } set { _agentExe = value; Raise("AgentExe"); } }

        /// <summary>The file the FOM parser should read for datatypes.</summary>
        public string DatatypeSource { get { return _isEvolved ? _fedFile : _omtFile; } }

        public NativeSideConfig ToConfig(bool isInteraction)
        {
            return new NativeSideConfig
            {
                Label = Label,
                AgentExe = AgentExe,
                FederationName = FederationName,
                FederateName = FederateName,
                FederateType = "Harness",
                FedFile = FedFile,
                JoinFomModules = string.IsNullOrWhiteSpace(FomModules) ? null : FomModules,
                Mim = string.IsNullOrWhiteSpace(Mim) ? null : Mim,
                ObjectClass = ObjectClass,
                ClassAlias = ObjectClass != null && ObjectClass.Contains(".") ? "Target" : null,
                IsInteraction = isInteraction,
            };
        }
    }

    /// <summary>One row in the live results grid.</summary>
    public sealed class ResultRow : Notify
    {
        private string _outcome = "";
        private string _message = "";
        private double _seconds;
        private string _latency = "";

        public int Step { get; set; }
        public string EntityId { get; set; }
        public string Outcome { get { return _outcome; } set { _outcome = value; Raise("Outcome"); } }
        public string Message { get { return _message; } set { _message = value; Raise("Message"); } }
        public double Seconds { get { return _seconds; } set { _seconds = value; Raise("Seconds"); } }

        /// <summary>
        /// Broker latency for this row, ms - update acknowledged on A to matching state seen on B.
        /// Separate from <see cref="Seconds"/>, which is the whole step including the harness's own
        /// quiesce and command round-trips.
        /// </summary>
        public string Latency { get { return _latency; } set { _latency = value; Raise("Latency"); } }
        public ObservableCollection<FieldRow> Fields { get; private set; }

        public ResultRow() { Fields = new ObservableCollection<FieldRow>(); }
    }

    public sealed class FieldRow
    {
        public string Ok { get; set; }
        public string Name { get; set; }
        public string Expected { get; set; }
        public string Actual { get; set; }
        public string Note { get; set; }

        public static FieldRow From(FieldResult f)
        {
            return new FieldRow
            {
                Ok = f.Ok ? "OK" : "FAIL",
                Name = f.Name,
                Expected = f.Expected,
                Actual = f.Actual,
                Note = f.Note ?? "",
            };
        }
    }
}
