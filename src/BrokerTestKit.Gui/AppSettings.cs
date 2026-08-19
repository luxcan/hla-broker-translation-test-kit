using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BrokerTestKit.Gui
{
    /// <summary>
    /// Which RTI each federation joins, plus the licence server.
    ///
    /// This is per-federation on purpose. The RID file is what actually decides WHICH RTI a
    /// federate joins — it carries the UDP/TCP ports, the multicast address, the forwarder address
    /// and whether an rtiexec is expected. Two federations on two different RTIs therefore need two
    /// different RID files, and because each agent is its own PROCESS it can also have its own PATH
    /// and so a different RTI *install* entirely. That was always possible in the design; until now
    /// it just was not reachable from the UI.
    /// </summary>
    public sealed class SideSettings : Notify
    {
        private string _rtiBin = "";
        private string _ridFile = "";

        /// <summary>The RTI install's bin folder. Goes on the agent's PATH.</summary>
        public string RtiBin { get { return _rtiBin; } set { _rtiBin = value; Raise("RtiBin"); } }

        /// <summary>rid.mtl — ports, multicast address, forwarder. Passed as <c>--rid</c>.</summary>
        public string RidFile { get { return _ridFile; } set { _ridFile = value; Raise("RidFile"); } }

        /// <summary>
        /// <c>RTI_ASSISTANT_PORT</c> — the port this RTI's Assistant listens on. Default 6003.
        ///
        /// Two DIFFERENT RTI VERSIONS running at once must have different values here, or the
        /// second one talks to the first one's Assistant and is dropped with
        /// <i>"Invalid message version from Assistant"</i> — a message that names neither the port
        /// nor the version. It is an environment variable, not a rid setting, and it is documented
        /// only in the VR-Exchange Users Guide §5.3.
        ///
        /// Blank means "leave it alone", which is correct when only one RTI version is in play.
        /// </summary>
        public string AssistantPort { get { return _assistantPort; } set { _assistantPort = value; Raise("AssistantPort"); } }
        private string _assistantPort = "";

        /// <summary>
        /// <c>RTI_CONFIG</c> — the folder the agent resolves FOM file NAMES against. Blank means
        /// "the RID file's folder", which is right most of the time and wrong in the one case that
        /// matters: an Evolved side joining with the VR-Exchange broker's FOM MODULES has to
        /// resolve them to the BROKER'S OWN copies, i.e. to <c>…\vrexchange2.9\bin64</c>. Modules
        /// that do not resolve produce no error — the federate joins and then sees nothing.
        /// </summary>
        public string ConfigDir { get { return _configDir; } set { _configDir = value; Raise("ConfigDir"); } }
        private string _configDir = "";

        /// <summary>The folder actually used: the explicit one, else the RID file's.</summary>
        public string EffectiveConfigDir
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_configDir)) return _configDir.Trim();
                try { return string.IsNullOrEmpty(RidFile) ? "" : Path.GetDirectoryName(RidFile); }
                catch { return ""; }
            }
        }

        /// <summary>Command-line arguments for the native agent.</summary>
        public string AgentArgs
        {
            get
            {
                var sb = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(RidFile)) sb.Append("--rid \"").Append(RidFile).Append("\" ");
                var cfg = EffectiveConfigDir;
                if (!string.IsNullOrWhiteSpace(cfg)) sb.Append("--config \"").Append(cfg).Append("\" ");
                return sb.ToString().Trim();
            }
        }

        /// <summary>
        /// Environment for the agent process. Prepending this side's RTI bin to PATH is what lets
        /// A and B run against different RTI installs at the same time.
        /// </summary>
        public Dictionary<string, string> Environment(string licenceServer)
        {
            var env = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(RtiBin) && Directory.Exists(RtiBin))
            {
                var path = System.Environment.GetEnvironmentVariable("PATH") ?? "";
                env["PATH"] = RtiBin + Path.PathSeparator +
                              Path.Combine(RtiBin, "..", "lib") + Path.PathSeparator + path;
            }
            if (!string.IsNullOrWhiteSpace(RidFile)) env["RTI_RID_FILE"] = RidFile;
            if (!string.IsNullOrWhiteSpace(AssistantPort)) env["RTI_ASSISTANT_PORT"] = AssistantPort.Trim();
            var cfg = EffectiveConfigDir;
            if (!string.IsNullOrWhiteSpace(cfg)) env["RTI_CONFIG"] = cfg;
            if (!string.IsNullOrWhiteSpace(licenceServer)) env["MAKLMGRD_LICENSE_FILE"] = licenceServer;
            return env;
        }

        public string Describe()
        {
            if (string.IsNullOrWhiteSpace(RidFile)) return "(inherited from the app environment)";
            return Path.GetFileName(RidFile) +
                   (string.IsNullOrWhiteSpace(RtiBin) ? "" : "  ·  " + RtiBin) +
                   (string.IsNullOrWhiteSpace(AssistantPort) ? "" : "  ·  assistant " + AssistantPort) +
                   (string.IsNullOrWhiteSpace(ConfigDir) ? "" : "  ·  FOMs from " + ConfigDir);
        }
    }

    /// <summary>
    /// What is left once the RTI details moved into named connections: the licence server, and
    /// which connection each side was last run with.
    /// </summary>
    public sealed class AppSettings
    {
        public string LicenceServer = "";
        public string ConnectionA = "";
        public string ConnectionB = "";

        private readonly Store _store;
        public AppSettings(Store store) { _store = store; }

        public void Load()
        {
            LicenceServer = _store.Get("licence");
            ConnectionA = _store.Get("run.a");
            ConnectionB = _store.Get("run.b");
        }

        public void Save()
        {
            _store.Set("licence", LicenceServer);
            _store.Set("run.a", ConnectionA);
            _store.Set("run.b", ConnectionB);
        }

        public void SeedFrom(string licence)
        {
            if (string.IsNullOrWhiteSpace(LicenceServer)) LicenceServer = licence ?? "";
        }
    }
}
