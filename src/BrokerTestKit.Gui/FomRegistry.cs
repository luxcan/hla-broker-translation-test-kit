using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using BrokerTestKit.Engine;

namespace BrokerTestKit.Gui
{
    /// <summary>
    /// One registered FOM.
    ///
    /// The files are IMPORTED into the workspace, not referenced where the user found them: the
    /// record stores content hashes, and the paths below point at the workspace's own copies. So a
    /// registered FOM keeps working after somebody moves or deletes the original, and a recorded
    /// run stays reproducible because it names the exact bytes it ran against.
    /// <see cref="OriginalPath"/> is kept for provenance only — never for loading.
    /// </summary>
    public sealed class FomEntryRecord : Notify
    {
        private string _name = "";
        private bool _isEvolved;
        private string _status = "not loaded";
        private bool _ok;

        public string Name { get { return _name; } set { _name = value; Raise("Name"); } }

        public bool IsEvolved
        {
            get { return _isEvolved; }
            set { _isEvolved = value; Raise("IsEvolved"); Raise("Version"); }
        }
        public string Version { get { return _isEvolved ? "HLA Evolved" : "HLA 1.3"; } }

        // ---- content identity (what is stored) ------------------------------------
        public string StructureHash;      // .fed (1.3) or FDD .xml (Evolved)
        public string DatatypeHash;       // .omt — 1.3 only
        public string MimHash;            // Evolved, optional
        public string ModuleHashes = "";  // Evolved, comma separated

        // ---- workspace paths (what is used) ---------------------------------------
        public string StructureFile;
        public string DatatypeFile;
        public string Mim;
        public string Modules = "";

        /// <summary>Where it was imported from. Provenance for the UI; never loaded from.</summary>
        public string OriginalPath;

        public string Status { get { return _status; } set { _status = value; Raise("Status"); } }
        public bool Ok { get { return _ok; } set { _ok = value; Raise("Ok"); } }

        /// <summary>Populated by <see cref="FomRegistry.Validate"/>; null until it parses.</summary>
        public FomModel Model;

        /// <summary>True when <see cref="Model"/> was read back from the store rather than parsed.</summary>
        public bool FromStore;

        /// <summary>Where the model in hand came from — shown so a stale store is never invisible.</summary>
        public string ModelSource { get { return FromStore ? "stored" : "parsed"; } }

        /// <summary>Which file the datatypes actually come from, per HLA version (REQ-01).</summary>
        public string ParseSource { get { return IsEvolved ? StructureFile : DatatypeFile; } }

        /// <summary>Short, readable file list for the grid.</summary>
        public string Files
        {
            get
            {
                var sb = new StringBuilder(Leaf(OriginalPath) ?? Leaf(StructureFile) ?? "");
                if (!IsEvolved && !string.IsNullOrEmpty(DatatypeFile)) sb.Append(" + .omt");
                var n = ModuleCount;
                if (n > 0) sb.Append(" + ").Append(n).Append(" module").Append(n == 1 ? "" : "s");
                if (!string.IsNullOrEmpty(MimHash)) sb.Append(" + MIM");
                return sb.ToString();
            }
        }

        public int ModuleCount
        {
            get
            {
                int n = 0;
                foreach (var h in (ModuleHashes ?? "").Split(','))
                    if (!string.IsNullOrWhiteSpace(h)) n++;
                return n;
            }
        }

        /// <summary>First 8 hex of the structure hash — enough to eyeball two runs as identical.</summary>
        public string ShortHash
        {
            get
            {
                return string.IsNullOrEmpty(StructureHash) ? "" : StructureHash.Substring(0, 8);
            }
        }

        private static string Leaf(string p)
        {
            try { return string.IsNullOrEmpty(p) ? null : Path.GetFileName(p); }
            catch { return null; }
        }

        public override string ToString() { return Name; }
    }

    /// <summary>
    /// The registered FOMs, held in the workspace database with the files imported alongside.
    /// </summary>
    public sealed class FomRegistry
    {
        public readonly ObservableCollection<FomEntryRecord> Items = new ObservableCollection<FomEntryRecord>();
        private readonly Store _store;

        public FomRegistry(Store store) { _store = store; }

        /// <summary>
        /// Identifies a parse result: every file that fed it, plus the parser that produced it.
        ///
        /// The parser version is the half that is easy to forget. Content hashes catch an edited
        /// FOM; only the version catches a FIXED PARSER, and a stored model built by a parser with
        /// an offset bug would resolve fields to the wrong bytes while its hashes still matched.
        /// </summary>
        private static string ModelKey(FomEntryRecord rec)
        {
            return "v" + FomModelCodec.Version +
                   "|" + (rec.IsEvolved ? "fdd" : "omt") +
                   "|" + (rec.StructureHash ?? "") +
                   "|" + (rec.DatatypeHash ?? "") +
                   "|" + (rec.MimHash ?? "") +
                   "|" + (rec.ModuleHashes ?? "");
        }

        /// <summary>
        /// Load the FOM and record what came out. Never throws — the reason lands in Status.
        ///
        /// The parse happens once, at import; after that the result is read back from the store.
        /// <paramref name="forceReparse"/> is what the Re-check button uses, so there is always a
        /// way to go back to the file itself.
        /// </summary>
        public bool Validate(FomEntryRecord rec, bool forceReparse = false)
        {
            rec.Model = null;
            try
            {
                if (string.IsNullOrWhiteSpace(rec.Name))
                    throw new InvalidOperationException("give it a name");
                if (string.IsNullOrWhiteSpace(rec.StructureFile))
                    throw new InvalidOperationException(rec.IsEvolved
                        ? "select the FDD (.xml)" : "select the .fed");
                if (!rec.IsEvolved && string.IsNullOrWhiteSpace(rec.DatatypeFile))
                    throw new InvalidOperationException(
                        "HLA 1.3 also needs the .omt — the .fed contains no datatypes at all");

                // Checked even on a store hit: the agents hand the FOM file itself to the RTI, so
                // a missing asset has to surface here rather than half-way through a run.
                if (!File.Exists(rec.ParseSource))
                    throw new FileNotFoundException(
                        "the imported FOM is missing from the workspace: " + rec.ParseSource);

                var key = ModelKey(rec);
                if (forceReparse) _store.DeleteModel(key);

                rec.Model = forceReparse ? null : _store.GetModel(key);
                rec.FromStore = rec.Model != null;

                if (rec.Model == null)
                {
                    var files = new List<string> { rec.ParseSource };
                    if (rec.IsEvolved && !string.IsNullOrWhiteSpace(rec.Modules))
                        foreach (var m in rec.Modules.Split(','))
                            if (!string.IsNullOrWhiteSpace(m)) files.Add(m.Trim());

                    rec.Model = FomLoader.Load(files.ToArray());
                    _store.PutModel(key, rec.Model);
                }

                rec.Ok = true;
                rec.Status = string.Format(CultureInfo.InvariantCulture,
                    "{0} classes · {1} interactions · {2} datatypes",
                    rec.Model.Classes.Count, rec.Model.Interactions.Count, rec.Model.Types.Count);
                return true;
            }
            catch (Exception ex)
            {
                rec.Ok = false;
                rec.Status = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Import the chosen files into the workspace, then register. The record that comes back
        /// refers to the workspace copies, so the caller's paths stop mattering the moment this
        /// returns.
        /// </summary>
        public FomEntryRecord Register(string name, bool isEvolved, string structurePath,
                                       string omtPath, string modulesCsv, string mimPath,
                                       out bool ok)
        {
            var rec = new FomEntryRecord { Name = (name ?? "").Trim(), IsEvolved = isEvolved };

            var structure = _store.ImportAsset(structurePath);
            var omt = isEvolved ? null : _store.ImportAsset(omtPath);
            var mim = isEvolved ? _store.ImportAsset(mimPath) : null;

            var hashes = new List<string>();
            if (isEvolved && !string.IsNullOrWhiteSpace(modulesCsv))
                foreach (var m in modulesCsv.Split(','))
                {
                    if (string.IsNullOrWhiteSpace(m)) continue;
                    var a = _store.ImportAsset(m.Trim());
                    if (a != null) hashes.Add(a.Hash);
                }

            rec.StructureHash = structure == null ? null : structure.Hash;
            rec.DatatypeHash = omt == null ? null : omt.Hash;
            rec.MimHash = mim == null ? null : mim.Hash;
            rec.ModuleHashes = string.Join(",", hashes.ToArray());

            _store.Rehydrate(rec);
            rec.OriginalPath = structurePath;

            ok = Validate(rec);
            if (!ok) return rec;                      // do not persist something that will not load

            _store.UpsertFom(rec);
            var existing = ByName(rec.Name);
            if (existing != null) Items.Remove(existing);
            Items.Add(rec);
            return rec;
        }

        public void Load()
        {
            // A parser change invalidates every model it produced; drop them once, here, rather
            // than leaving unreadable rows behind for the life of the workspace.
            _store.PurgeStaleModels();

            Items.Clear();
            foreach (var rec in _store.LoadFoms())
            {
                Validate(rec);        // served from the store unless the FOM or the parser changed
                Items.Add(rec);
            }
        }

        public void Remove(FomEntryRecord rec)
        {
            if (rec == null) return;
            _store.DeleteFom(rec.Name);
            Items.Remove(rec);
            // The imported asset is deliberately LEFT in place: an older run may still reference
            // those exact bytes, and orphan cleanup is a separate, explicit operation.
        }

        public FomEntryRecord ByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var r in Items)
                if (string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) return r;
            return null;
        }

        /// <summary>Import the in-repo samples so a fresh install has something that works.</summary>
        public void SeedFromConfig(string configDir)
        {
            if (Items.Count > 0 || string.IsNullOrEmpty(configDir) || !Directory.Exists(configDir)) return;

            var fed = Path.Combine(configDir, "BTK-CustomRPR1.fed");
            var omt = Path.Combine(configDir, "BTK-CustomRPR1.omt");
            var fdd = Path.Combine(configDir, "BTK-CustomRPR2.xml");
            bool ok;

            if (File.Exists(fed) && File.Exists(omt))
                Register("Customised RPR 1.0 (sample)", false, fed, omt, null, null, out ok);
            if (File.Exists(fdd))
                Register("Customised RPR 2.0 (sample)", true, fdd, null, null, null, out ok);
        }

        /// <summary>One-time migration from the pre-SQLite TSV file, then rename it aside.</summary>
        public void ImportLegacyTsv(string tsvPath)
        {
            if (!File.Exists(tsvPath) || Items.Count > 0) return;
            foreach (var line in File.ReadAllLines(tsvPath))
            {
                if (line.Trim().Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var f = line.Split('\t');
                if (f.Length < 6) continue;
                try
                {
                    bool ok;
                    Register(f[0], f[1] == "1", f[2], f[3], f[4], f[5], out ok);
                }
                catch { /* a legacy row whose files have gone is not worth failing startup over */ }
            }
            try { File.Move(tsvPath, tsvPath + ".migrated"); } catch { }
        }
    }
}
