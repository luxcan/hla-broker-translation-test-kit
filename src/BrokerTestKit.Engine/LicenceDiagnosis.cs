using System;
using System.Diagnostics;
using System.IO;

namespace BrokerTestKit.Engine
{
    /// <summary>
    /// REQ-07: say what is wrong with the MAK licence in words, not FlexNet codes.
    ///
    /// The hard case is the common one. With the licence server unreachable or its vendor daemon
    /// down, a native agent does NOT report an error: it connects to the RTI Assistant, loads the
    /// rid, and then blocks inside the RTI waiting for a seat. Nothing reaches stdout or stderr. The
    /// only symptom is a JOIN that never answers (measured 2026-11-05, mak-rti-knowledge §6). So a
    /// JOIN timeout is the moment to probe the licence server and name the cause.
    /// </summary>
    public static class LicenceDiagnosis
    {
        public enum State { Ok, Down, NoServer, CannotCheck }

        public const string LmUtil = @"C:\MAK\MAKLicenseManager\lmutil.exe";
        public const string DefaultServer = "27000@127.0.0.1";

        /// <summary>
        /// Ask the licence server what state it is in: `lmutil lmstat -c &lt;server&gt;` must say
        /// "maklmgrd: UP". lmgrd answering is not enough - vrx and the agents stall without the
        /// vendor daemon.
        /// </summary>
        public static State Probe(string server, out string detail)
        {
            detail = null;
            server = string.IsNullOrWhiteSpace(server) ? DefaultServer : server.Trim();
            if (!File.Exists(LmUtil)) { detail = "lmutil.exe not found at " + LmUtil; return State.CannotCheck; }
            try
            {
                var psi = new ProcessStartInfo(LmUtil, "lmstat -c " + server)
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    var outText = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } detail = "lmutil timed out"; return State.CannotCheck; }
                    var text = outText.Result ?? "";
                    if (text.IndexOf("maklmgrd: UP", StringComparison.OrdinalIgnoreCase) >= 0) return State.Ok;
                    if (text.IndexOf("maklmgrd", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        detail = "the licence server at " + server + " is up, but its MAK vendor daemon (maklmgrd) is down";
                        return State.Down;
                    }
                    detail = "no licence server answered at " + server;
                    return State.NoServer;
                }
            }
            catch (Exception ex) { detail = ex.Message; return State.CannotCheck; }
        }

        /// <summary>What to do about a probed state, in one sentence. Null for Ok.</summary>
        public static string Advice(State state, string server, string detail)
        {
            server = string.IsNullOrWhiteSpace(server) ? DefaultServer : server.Trim();
            switch (state)
            {
                case State.Down:
                    return "The MAK licence vendor daemon is down (" + detail + "). Run  lmutil lmreread -c " + server +
                           "  - and if  lmutil lmhostid -flexid  prints nothing, re-seat the USB licence dongle first.";
                case State.NoServer:
                    return "No MAK licence server answered at " + server + ". Check the licence server setting " +
                           "(MAKLMGRD_LICENSE_FILE, or the GUI's licence server) and that the MAK License Manager (lmgrd) is running.";
                case State.CannotCheck:
                    return "The licence could not be checked (" + detail + "); confirm by hand with  lmutil lmstat -c " + server + ".";
            }
            return null;
        }

        /// <summary>
        /// A plain-words reading of a FlexNet or RTI error message, or null when it is not about
        /// the licence. The raw text should still be shown underneath: it is the evidence.
        /// </summary>
        public static string Explain(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            var m = message;
            if (Has(m, "-4,132") || Has(m, "Licensed number of users already reached"))
                return "Every licence seat is in use: another RTI or VR-Link process already holds them. Close other " +
                       "agents, brokers or MAK tools and run again. (The native agents draw on the makrtiLib pool; " +
                       "VR-Link tools on the 2-seat vrl_run pool.)";
            if (Has(m, "-97,") || Has(m, "vendor daemon is down"))
                return Advice(State.Down, null, "FlexNet -97");
            if (Has(m, "-15,") || Has(m, "-96,") || Has(m, "Cannot connect to license server"))
                return Advice(State.NoServer, null, null);
            if (Has(m, "-10,") || Has(m, "License server system does not support this version") || Has(m, "expired"))
                return "The MAK licence has expired, or does not cover this version. Check the expiry in  lmutil lmstat -a.";
            if (Has(m, "-5,") || Has(m, "No such feature exists"))
                return "The licence has no seat for this product (FlexNet feature missing). Check which features " +
                       "lmutil lmstat -a lists.";
            if (Has(m, "-1,") && Has(m, "license file"))
                return "No licence server is configured. Set MAKLMGRD_LICENSE_FILE (e.g. " + DefaultServer + ").";
            return null;
        }

        /// <summary>
        /// The explanation for a JOIN that never answered: probe the licence, and say either what is
        /// wrong with it, or that it is fine and the cause is elsewhere.
        /// </summary>
        public static string DiagnoseJoinTimeout(string server)
        {
            string detail;
            var state = Probe(server, out detail);
            if (state == State.Ok)
                return "The licence is fine (maklmgrd is UP), so look elsewhere: an RTI Assistant port shared by two " +
                       "RTI versions, a rid that names an unreachable forwarder, or a FOM the RTI cannot find.";
            return "A JOIN that never answers is the RTI waiting for a licence seat. " + Advice(state, server, detail);
        }

        private static bool Has(string s, string what)
        {
            return s.IndexOf(what, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
