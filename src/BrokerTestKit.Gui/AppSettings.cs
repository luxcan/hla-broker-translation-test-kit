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

        /// <summary>FOM files are resolved against this; defaults to the RID file's folder.</summary>
        public string ConfigDir
        {
            get
            {
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
                var cfg = ConfigDir;
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
            var cfg = ConfigDir;
            if (!string.IsNullOrWhiteSpace(cfg)) env["RTI_CONFIG"] = cfg;
            if (!string.IsNullOrWhiteSpace(licenceServer)) env["MAKLMGRD_LICENSE_FILE"] = licenceServer;
            return env;
        }

        public string Describe()
        {
            if (string.IsNullOrWhiteSpace(RidFile)) return "(inherited from the app environment)";
            return Path.GetFileName(RidFile) +
                   (string.IsNullOrWhiteSpace(RtiBin) ? "" : "  ·  " + RtiBin);
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
