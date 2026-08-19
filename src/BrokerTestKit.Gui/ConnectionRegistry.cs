using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BrokerTestKit.Engine;

namespace BrokerTestKit.Gui
{
    /// <summary>
    /// One named way to reach a federation: a registered FOM, the RTI that carries it, and the
    /// execution to join. "Customised RPR 1.0 · RTI 5.0.1", picked as a single item at run time.
    ///
    /// The FOM is part of the connection rather than a separate choice because a federation
    /// execution is CREATED with a FOM — every federate joining has to supply a compatible one, so
    /// a connection that did not name its FOM would be only half an answer. It also settles the
    /// HLA version, and therefore which agent binary runs: <c>btk-agent13.exe</c> or
    /// <c>btk-agent1516e.exe</c>. One less thing to get wrong at run time.
    /// </summary>
    public sealed class ConnectionRecord : Notify
    {
        private string _name = "", _fomName = "", _exec = "", _federate = "", _status = "";
        private bool _ok;

        public string Name { get { return _name; } set { _name = value; Raise("Name"); } }

        /// <summary>The FOM registry entry, by name. Resolved on load — see <see cref="Fom"/>.</summary>
        public string FomName
        {
            get { return _fomName; }
            set { _fomName = value; Raise("FomName"); Raise("Version"); }
        }

        public string ExecName { get { return _exec; } set { _exec = value; Raise("ExecName"); } }
        public string FederateName { get { return _federate; } set { _federate = value; Raise("FederateName"); } }

        /// <summary>RTI install + RID file. Reuses the agent-argument logic already in SideSettings.</summary>
        public readonly SideSettings Rti = new SideSettings();

        /// <summary>Resolved against the FOM registry; null when the FOM has been removed.</summary>
        public FomEntryRecord Fom;

        public string Status { get { return _status; } set { _status = value; Raise("Status"); } }
        public bool Ok { get { return _ok; } set { _ok = value; Raise("Ok"); } }

        public string Version { get { return Fom == null ? "?" : Fom.Version; } }

        /// <summary>Which RTI, read off the install folder — <c>…\makRti5.0.1\bin</c> → "RTI 5.0.1".</summary>
        public string RtiVersion { get { return RtiVersionOf(Rti.RtiBin); } }

        public string Where
        {
            get
            {
                return ExecName + (string.IsNullOrEmpty(RtiVersion) ? "" : "  ·  " + RtiVersion) +
                       (string.IsNullOrWhiteSpace(Rti.AssistantPort) ? "" : "  ·  asst " + Rti.AssistantPort);
            }
        }

        /// <summary>
        /// Pull "5.0.1" out of a MAK install path. Names like <c>makRti4.5.1(vc14)</c> matter —
        /// the VC build is part of the product identity, not decoration, so it is kept.
        /// </summary>
        public static string RtiVersionOf(string binPath)
        {
            if (string.IsNullOrWhiteSpace(binPath)) return "";
            try
            {
                var dir = binPath.TrimEnd('\\', '/');
                if (string.Equals(Path.GetFileName(dir), "bin", StringComparison.OrdinalIgnoreCase))
                    dir = Path.GetDirectoryName(dir) ?? dir;
                var leaf = Path.GetFileName(dir) ?? "";
                var m = Regex.Match(leaf, @"^makRti(.+)$", RegexOptions.IgnoreCase);
                return m.Success ? "RTI " + m.Groups[1].Value : leaf;
            }
            catch { return ""; }
        }

        /// <summary>The name to offer when the user has not typed one — their own convention.</summary>
        public static string SuggestName(string fomName, string rtiBin)
        {
            var v = RtiVersionOf(rtiBin);
            if (string.IsNullOrWhiteSpace(fomName)) return v;
            return string.IsNullOrEmpty(v) ? fomName : fomName + " · " + v;
        }

        public override string ToString() { return Name; }
    }

    /// <summary>The registered connections, held in the workspace database alongside the FOMs.</summary>
    public sealed class ConnectionRegistry
    {
        public readonly ObservableCollection<ConnectionRecord> Items =
            new ObservableCollection<ConnectionRecord>();

        private readonly Store _store;
        private readonly FomRegistry _foms;

        public ConnectionRegistry(Store store, FomRegistry foms) { _store = store; _foms = foms; }

        /// <summary>
        /// Resolve the FOM and check the RTI is actually there. Never throws — the reason lands in
        /// Status, so a connection broken by a moved RTI install shows up in the list rather than
        /// half-way through a run.
        /// </summary>
        public bool Validate(ConnectionRecord rec)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(rec.Name))
                    throw new InvalidOperationException("give it a name");

                rec.Fom = _foms.ByName(rec.FomName);
                if (rec.Fom == null)
                    throw new InvalidOperationException("no registered FOM named \"" + rec.FomName + "\"");
                if (!rec.Fom.Ok)
                    throw new InvalidOperationException("its FOM does not load: " + rec.Fom.Status);
                if (string.IsNullOrWhiteSpace(rec.ExecName))
                    throw new InvalidOperationException("give the federation execution a name");
                if (string.IsNullOrWhiteSpace(rec.Rti.RtiBin) || !Directory.Exists(rec.Rti.RtiBin))
                    throw new InvalidOperationException("RTI bin folder not found: " + rec.Rti.RtiBin);
                if (string.IsNullOrWhiteSpace(rec.Rti.RidFile) || !File.Exists(rec.Rti.RidFile))
                    throw new InvalidOperationException("RID file not found: " + rec.Rti.RidFile);

                rec.Ok = true;
                rec.Status = rec.Fom.Version + " · " + rec.Fom.Status;
                return true;
            }
            catch (Exception ex)
            {
                rec.Ok = false;
                rec.Status = ex.Message;
                return false;
            }
        }

        /// <summary>Add or replace by name. The RID file is imported, like every other input.</summary>
        public ConnectionRecord Save(ConnectionRecord rec, out bool ok)
        {
            rec.Rti.RidFile = _store.ImportRid(rec.Rti.RidFile);
            ok = Validate(rec);
            if (!ok) return rec;

            _store.UpsertConnection(rec);
            var existing = ByName(rec.Name);
            if (existing != null && !ReferenceEquals(existing, rec)) Items.Remove(existing);
            if (!Items.Contains(rec)) Items.Add(rec);
            return rec;
        }

        public void Load()
        {
            Items.Clear();
            foreach (var rec in _store.LoadConnections())
            {
                Validate(rec);
                Items.Add(rec);
            }
        }

        public void Remove(ConnectionRecord rec)
        {
            if (rec == null) return;
            _store.DeleteConnection(rec.Name);
            Items.Remove(rec);
        }

        public ConnectionRecord ByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var r in Items)
                if (string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) return r;
            return null;
        }

        /// <summary>
        /// Build both federations from a bridge profile - the same file the CLI runs from.
        ///
        /// A rig is a dozen fields per side and every one of them is a way to lose an afternoon:
        /// the wrong RTI bin, a shared Assistant port, a FOM module that does not resolve. Having
        /// the GUI and the CI runner read ONE file means a rig that passes headless is the same rig
        /// the operator sees, rather than a second copy of it that has drifted.
        ///
        /// FOM files are imported into the workspace as usual, so a run can still say exactly which
        /// bytes it used.
        /// </summary>
        public string ImportProfile(BridgeProfile profile)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            var report = new StringBuilder();

            foreach (var side in new[] { profile.A, profile.B })
            {
                var evolved = (side.Agent ?? "").IndexOf("1516", StringComparison.OrdinalIgnoreCase) >= 0;
                var structure = Resolve(side, side.FedFile);
                if (structure == null)
                    throw new FileNotFoundException("[" + side.Label + "] fedFile not found: " + side.FedFile);

                // The .omt carries the datatypes an HLA 1.3 .fed does not have. On Evolved the FDD
                // is both, so there is nothing separate to register.
                var datatypes = evolved ? null : side.Fom;
                if (!evolved && datatypes != null &&
                    string.Equals(Path.GetFullPath(datatypes), Path.GetFullPath(structure),
                                  StringComparison.OrdinalIgnoreCase)) datatypes = null;

                var modules = new List<string>();
                foreach (var m in (side.JoinFom ?? "").Split(','))
                {
                    var name = m.Trim();
                    if (name.Length == 0) continue;
                    var full = Resolve(side, name);
                    if (full == null)
                        throw new FileNotFoundException("[" + side.Label + "] FOM module not found: " + name);
                    modules.Add(full);
                }

                var fomName = Path.GetFileNameWithoutExtension(structure);
                bool fomOk;
                var fom = _foms.Register(fomName, evolved, structure, datatypes,
                                         string.Join(",", modules.ToArray()),
                                         Resolve(side, side.Mim), out fomOk);
                report.Append(side.Label).Append(": FOM \"").Append(fom.Name).Append("\" ")
                      .Append(fomOk ? "ok" : fom.Status);
                if (modules.Count > 0) report.Append(" (+").Append(modules.Count).Append(" modules)");
                report.AppendLine();

                var rec = ByName(ConnectionRecord.SuggestName(fomName, side.RtiBin))
                          ?? new ConnectionRecord();
                rec.Name = ConnectionRecord.SuggestName(fomName, side.RtiBin);
                rec.FomName = fom.Name;
                rec.ExecName = side.Exec;
                rec.FederateName = string.IsNullOrEmpty(side.FederateName)
                                 ? "BtkRunner" + side.Label : side.FederateName;
                rec.Rti.RtiBin = side.RtiBin;
                rec.Rti.RidFile = side.RidFile;
                rec.Rti.AssistantPort = side.AssistantPort;
                rec.Rti.ConfigDir = side.ConfigDir;

                bool connOk;
                Save(rec, out connOk);
                report.Append("   connection \"").Append(rec.Name).Append("\" ")
                      .AppendLine(connOk ? "ok" : rec.Status);
            }

            return report.ToString().TrimEnd();
        }

        /// <summary>A profile names FOM files bare; they live under that side's configDir.</summary>
        private static string Resolve(BridgeProfile.Side side, string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return null;
            if (Path.IsPathRooted(file)) return File.Exists(file) ? file : null;
            if (!string.IsNullOrWhiteSpace(side.ConfigDir))
            {
                var candidate = Path.Combine(side.ConfigDir, file);
                if (File.Exists(candidate)) return candidate;
            }
            return File.Exists(file) ? Path.GetFullPath(file) : null;
        }

        /// <summary>
        /// Give a fresh workspace one connection per registered FOM, so the app is runnable without
        /// the user first filling in four fields twice.
        /// </summary>
        public void SeedFromFoms(string rtiBin, string ridFile)
        {
            if (Items.Count > 0 || _foms.Items.Count == 0) return;

            int n = 0;
            foreach (var fom in _foms.Items)
            {
                var rec = new ConnectionRecord
                {
                    Name = ConnectionRecord.SuggestName(fom.Name, rtiBin),
                    FomName = fom.Name,
                    ExecName = "BTK-Fed" + (char)('A' + n),
                    FederateName = "BtkRunner" + (char)('A' + n),
                };
                rec.Rti.RtiBin = rtiBin ?? "";
                rec.Rti.RidFile = ridFile ?? "";
                // Distinct from the start: the moment two connections name different RTI versions
                // a shared Assistant port silently breaks the second one.
                rec.Rti.AssistantPort = (6003 + n).ToString(CultureInfo.InvariantCulture);
                bool ok;
                Save(rec, out ok);
                n++;
            }
        }

        /// <summary>
        /// Two different RTI VERSIONS run side by side happily — but only if each has its own
        /// <c>RTI_ASSISTANT_PORT</c>. Sharing one (including sharing the 6003 default) means the
        /// second version talks to the first version's Assistant and is dropped with
        /// <i>"Invalid message version from Assistant"</i>, which names neither the port nor the
        /// version. Catch it at setup instead.
        ///
        /// Note this is about RTI *versions*, not VC builds: federates linking different VC builds
        /// of the SAME version share a federation normally — the VC decides what a process can
        /// link, the version decides what goes on the wire.
        ///
        /// Returns null when the pair is fine.
        /// </summary>
        public static string CrossVersionWarning(ConnectionRecord a, ConnectionRecord b)
        {
            if (a == null || b == null || ReferenceEquals(a, b)) return null;
            var va = a.RtiVersion;
            var vb = b.RtiVersion;
            if (string.IsNullOrEmpty(va) || string.IsNullOrEmpty(vb)) return null;
            if (string.Equals(va, vb, StringComparison.OrdinalIgnoreCase)) return null;

            var pa = (a.Rti.AssistantPort ?? "").Trim();
            var pb = (b.Rti.AssistantPort ?? "").Trim();
            if (pa.Length > 0 && pb.Length > 0 && !string.Equals(pa, pb, StringComparison.Ordinal))
                return null;                                   // distinct ports — this is fine

            var which = pa.Length == 0 && pb.Length == 0
                ? "Neither sets one, so both default to 6003."
                : (pa.Length == 0 ? "A does not set one, so it defaults to 6003."
                                  : (pb.Length == 0 ? "B does not set one, so it defaults to 6003."
                                                    : "Both use " + pa + "."));

            return "A uses " + va + " and B uses " + vb + " — two RTI versions at once. " + which +
                   " Give each connection its own RTI Assistant port (for example 6003 and 6004), " +
                   "or the second RTI is dropped with \"Invalid message version from Assistant\".";
        }
    }
}
