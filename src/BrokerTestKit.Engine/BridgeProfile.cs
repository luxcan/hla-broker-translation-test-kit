using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BrokerTestKit.Engine
{
    /// <summary>
    /// The rig, in one file: which RTI each side links, which rid it reads, which Assistant port
    /// it talks to, which federation and FOM it joins, and which HLA class carries the test data.
    ///
    /// Before this existed those values were compiled into the CLI, which meant the tool could
    /// only ever test the one bridge it was built for. They belong in data: a broker under test is
    /// defined by its two federations, and swapping FOMs or RTI versions is the normal case, not
    /// an edge case.
    ///
    /// Format - INI, because it has to be readable and diffable by whoever owns the rig:
    /// <code>
    ///   licence = 27000@127.0.0.1
    ///
    ///   [A]
    ///   agent    = btk-agent13.exe
    ///   rtiBin   = C:\MAK\makRti4.5.1(vc14)\bin
    ///   ridFile  = rid-fedA-rti451.mtl        ; relative paths resolve against THIS file
    ///   ...
    /// </code>
    ///
    /// Every path may be absolute or relative to the profile's own directory, so a profile that
    /// sits next to its rid files stays portable.
    /// </summary>
    public sealed class BridgeProfile
    {
        public string Path { get; private set; }
        public string Licence = "";

        public readonly Side A = new Side { Label = "A" };
        public readonly Side B = new Side { Label = "B" };

        /// <summary>One federation's worth of settings.</summary>
        public sealed class Side
        {
            public string Label = "A";

            // ---- which RTI this agent process links ----
            /// <summary>Agent binary name; resolved under build\native.</summary>
            public string Agent = "";
            /// <summary>Prepended to PATH. This, not the rid, is what picks the RTI *install*.</summary>
            public string RtiBin = "";
            /// <summary>rid.mtl - ports, multicast group, forwarder, rtiexec.</summary>
            public string RidFile = "";
            /// <summary>
            /// RTI_ASSISTANT_PORT. Two different RTI VERSIONS at once must differ here or the
            /// second is dropped with "Invalid message version from Assistant".
            /// </summary>
            public string AssistantPort = "";
            /// <summary>
            /// RTI_CONFIG - where the agent resolves FOM file NAMES.
            ///
            /// Not necessarily the rid's folder. On the Evolved side it usually has to be the
            /// VR-Exchange bin64 directory, because the modules in <see cref="JoinFom"/> have to be
            /// the broker's own copies.
            /// </summary>
            public string ConfigDir = "";

            // ---- what it joins ----
            public string Exec = "";
            public string FedFile = "";
            /// <summary>Evolved only, comma separated. Passed at JOIN; create-time modules are dropped.</summary>
            public string JoinFom = "";
            public string Mim = "";
            public string FederateName = "";
            public string FederateType = "Harness";

            // ---- what carries the data ----
            /// <summary>
            /// HLA class the agent publishes/subscribes - fully qualified on Evolved.
            ///
            /// Deliberately separate from the CSV's <c># class:</c> directive. The CSV names the
            /// class as the FOM PARSER knows it (bare, e.g. GroundVehicle) so datatypes resolve;
            /// the agent needs the name the RTI knows (HLAobjectRoot.BaseEntity....GroundVehicle).
            /// Same class, two spellings, and conflating them is how you get NameNotFound.
            /// </summary>
            public string Class = "";
            public string Alias = "";
            /// <summary>FOM the harness PARSES to resolve datatypes: .omt for HLA 1.3, FDD for Evolved.</summary>
            public string Fom = "";

            /// <summary>Environment for this side's agent process.</summary>
            public Dictionary<string, string> Environment(string licence)
            {
                var env = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(RtiBin))
                {
                    var path = System.Environment.GetEnvironmentVariable("PATH") ?? "";
                    var lib = System.IO.Path.Combine(RtiBin, "..", "lib");
                    env["PATH"] = RtiBin + System.IO.Path.PathSeparator + lib +
                                  System.IO.Path.PathSeparator + path;
                }
                if (!string.IsNullOrEmpty(RidFile)) env["RTI_RID_FILE"] = RidFile;
                if (!string.IsNullOrEmpty(ConfigDir)) env["RTI_CONFIG"] = ConfigDir;
                if (!string.IsNullOrEmpty(AssistantPort)) env["RTI_ASSISTANT_PORT"] = AssistantPort;
                if (!string.IsNullOrEmpty(licence)) env["MAKLMGRD_LICENSE_FILE"] = licence;
                return env;
            }

            /// <param name="rtiClass">
            /// The class name as the RTI knows it, resolved from the FOM. Used unless the profile
            /// states <c>class=</c> explicitly, which is only needed when the FOM cannot be trusted
            /// to name it - a hand-edited FDD, say.
            /// </param>
            /// <param name="csvClass">The CSV's own spelling; becomes the short alias.</param>
            public NativeSideConfig ToSideConfig(string agentExe, string rtiClass, string csvClass,
                                                 bool isInteraction)
            {
                return new NativeSideConfig
                {
                    Label = Label,
                    AgentExe = agentExe,
                    FederationName = Exec,
                    FederateName = string.IsNullOrEmpty(FederateName) ? "BtkRunner" + Label : FederateName,
                    FederateType = FederateType,
                    FedFile = FedFile,
                    JoinFomModules = JoinFom,
                    Mim = Mim,
                    ObjectClass = !string.IsNullOrEmpty(Class) ? Class
                                : !string.IsNullOrEmpty(rtiClass) ? rtiClass : csvClass,
                    ClassAlias = string.IsNullOrEmpty(Alias) ? csvClass : Alias,
                    IsInteraction = isInteraction,
                };
            }

            public string Describe()
            {
                var sb = new StringBuilder();
                sb.Append(Label).Append(": ").Append(Exec)
                  .Append("  fed=").Append(System.IO.Path.GetFileName(FedFile));
                var n = JoinFom == null ? 0 : JoinFom.Split(',').Length;
                if (!string.IsNullOrEmpty(JoinFom)) sb.Append(" +").Append(n).Append(" modules");
                sb.Append("  class=").Append(Class);
                sb.Append(System.Environment.NewLine).Append("     rti=").Append(RtiBin);
                sb.Append(System.Environment.NewLine).Append("     rid=").Append(System.IO.Path.GetFileName(RidFile))
                  .Append("  assistant=").Append(string.IsNullOrEmpty(AssistantPort) ? "(default)" : AssistantPort);
                sb.Append(System.Environment.NewLine).Append("     RTI_CONFIG=").Append(ConfigDir);
                return sb.ToString();
            }
        }

        // ----------------------------------------------------------------- load ----

        public static BridgeProfile Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("bridge profile not found: " + path, path);

            var p = new BridgeProfile { Path = System.IO.Path.GetFullPath(path) };
            var home = System.IO.Path.GetDirectoryName(p.Path);
            Side current = null;

            int lineNo = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                lineNo++;
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                if (line[0] == '[')
                {
                    var name = line.Trim('[', ']').Trim();
                    if (string.Equals(name, "A", StringComparison.OrdinalIgnoreCase)) current = p.A;
                    else if (string.Equals(name, "B", StringComparison.OrdinalIgnoreCase)) current = p.B;
                    else throw new FormatException(FileLine(path, lineNo) +
                        "unknown section '" + name + "' - only [A] and [B] exist");
                    continue;
                }

                var eq = line.IndexOf('=');
                if (eq <= 0)
                    throw new FormatException(FileLine(path, lineNo) + "expected key = value");

                var key = line.Substring(0, eq).Trim();
                var val = line.Substring(eq + 1).Trim();

                if (current == null)
                {
                    if (Is(key, "licence", "license")) p.Licence = val;
                    else throw new FormatException(FileLine(path, lineNo) +
                        "'" + key + "' appears before any [A] / [B] section");
                    continue;
                }

                if (Is(key, "agent")) current.Agent = val;
                else if (Is(key, "rtiBin")) current.RtiBin = Abs(home, val);
                else if (Is(key, "ridFile", "rid")) current.RidFile = Abs(home, val);
                else if (Is(key, "assistantPort")) current.AssistantPort = val;
                else if (Is(key, "configDir", "rtiConfig")) current.ConfigDir = Abs(home, val);
                else if (Is(key, "exec", "execName")) current.Exec = val;
                else if (Is(key, "fedFile", "fed")) current.FedFile = val;
                else if (Is(key, "joinFom", "joinFomModules")) current.JoinFom = val;
                else if (Is(key, "mim")) current.Mim = val;
                else if (Is(key, "federateName")) current.FederateName = val;
                else if (Is(key, "federateType")) current.FederateType = val;
                else if (Is(key, "class", "objectClass")) current.Class = val;
                else if (Is(key, "alias", "classAlias")) current.Alias = val;
                else if (Is(key, "fom", "fomFile")) current.Fom = Abs(home, val);
                else throw new FormatException(FileLine(path, lineNo) + "unknown key '" + key + "'");
            }

            p.Validate();
            return p;
        }

        /// <summary>
        /// Fail at load, not three minutes into a run. Every check here corresponds to a failure
        /// that is otherwise silent or badly misreported.
        /// </summary>
        private void Validate()
        {
            foreach (var s in new[] { A, B })
            {
                if (string.IsNullOrEmpty(s.Agent)) throw new FormatException("[" + s.Label + "] needs agent=");
                if (string.IsNullOrEmpty(s.Exec)) throw new FormatException("[" + s.Label + "] needs exec=");
                if (string.IsNullOrEmpty(s.FedFile)) throw new FormatException("[" + s.Label + "] needs fedFile=");
                if (!string.IsNullOrEmpty(s.RtiBin) && !Directory.Exists(s.RtiBin))
                    throw new DirectoryNotFoundException("[" + s.Label + "] rtiBin does not exist: " + s.RtiBin);
                if (!string.IsNullOrEmpty(s.RidFile) && !File.Exists(s.RidFile))
                    throw new FileNotFoundException("[" + s.Label + "] ridFile does not exist: " + s.RidFile);
                if (!string.IsNullOrEmpty(s.ConfigDir) && !Directory.Exists(s.ConfigDir))
                    throw new DirectoryNotFoundException("[" + s.Label + "] configDir does not exist: " + s.ConfigDir);
                if (!string.IsNullOrEmpty(s.Fom) && !File.Exists(s.Fom))
                    throw new FileNotFoundException("[" + s.Label + "] fom does not exist: " + s.Fom);

                // A FOM module that does not resolve is the failure mode that costs the most time:
                // the agent joins cleanly and then receives NOTHING, with no error anywhere.
                if (!string.IsNullOrEmpty(s.JoinFom) && !string.IsNullOrEmpty(s.ConfigDir))
                {
                    var missing = new List<string>();
                    foreach (var m in s.JoinFom.Split(','))
                    {
                        var name = m.Trim();
                        if (name.Length == 0) continue;
                        if (!File.Exists(System.IO.Path.Combine(s.ConfigDir, name))) missing.Add(name);
                    }
                    if (missing.Count > 0)
                        throw new FileNotFoundException("[" + s.Label + "] " + missing.Count +
                            " joinFom module(s) not under configDir '" + s.ConfigDir + "': " +
                            string.Join(", ", missing.ToArray()) +
                            "  - unresolved modules mean the agent joins and then sees nothing, silently");
                }
            }

            if (!string.IsNullOrEmpty(A.AssistantPort) && A.AssistantPort == B.AssistantPort &&
                !string.Equals(A.RtiBin, B.RtiBin, StringComparison.OrdinalIgnoreCase))
                throw new FormatException(
                    "A and B are different RTI installs but share assistantPort=" + A.AssistantPort +
                    " - the second one is dropped with 'Invalid message version from Assistant'");
        }

        private static bool Is(string key, params string[] names)
        {
            foreach (var n in names) if (string.Equals(key, n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string Abs(string home, string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (System.IO.Path.IsPathRooted(value)) return System.IO.Path.GetFullPath(value);
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(home, value));
        }

        private static string FileLine(string path, int line)
        {
            return System.IO.Path.GetFileName(path) + ":" +
                   line.ToString(CultureInfo.InvariantCulture) + ": ";
        }

        public string Describe()
        {
            return "profile " + System.IO.Path.GetFileName(Path) + Environment.NewLine +
                   "  " + A.Describe() + Environment.NewLine +
                   "  " + B.Describe();
        }
    }
}
