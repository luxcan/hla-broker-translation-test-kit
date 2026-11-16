using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using BrokerTestKit.Engine;

namespace BrokerTestKit.Gui
{
    /// <summary>The screens (PRP §3). Navigation is by name, never by index (R1).</summary>
    public enum Page
    {
        Dashboard, Pairs, PairDetail, PairEdit, TestCaseEdit, TestCaseResult, AllResults, CsvTemplate, FomRegistry, FomCompare,
    }

    /// <summary>Where to go: the page plus the ids it is about.</summary>
    public sealed class NavTarget
    {
        public Page Page;
        /// <summary>Pair id for pair pages; test case id for TestCaseResult when RunId is 0.</summary>
        public long PairId;
        public long TestCaseId;
        /// <summary>TestCaseResult: a saved run to show. 0 = the live run, or the latest.</summary>
        public long RunId;
        /// <summary>AllResults: pre-filter to this pair.</summary>
        public bool FilterToPair;
        /// <summary>TestCaseEdit, new case only: start with a matched Send (A) and Receive (B) sheet
        /// for these classes, holding these members (from Compare FOMs).</summary>
        public string PrefillA, PrefillB;
        public List<string> PrefillMembers;

        public static NavTarget To(Page p) { return new NavTarget { Page = p }; }
    }

    /// <summary>A screen in the content host.</summary>
    public interface IPageView
    {
        /// <summary>False keeps the user here (e.g. unsaved edits they chose to keep).</summary>
        bool CanLeave();
    }

    /// <summary>
    /// Everything a screen needs, created once by MainWindow. Every Store call happens on the UI
    /// thread (R2); the run worker marshals through <see cref="RunService"/>.
    /// </summary>
    public sealed class AppServices
    {
        public Workspace Workspace;
        public Store Store;
        public FomRegistry Foms;
        public ConnectionRegistry Connections;
        public AppSettings Settings;
        public RunService Runs;
        public RigMonitor Rig;
        public Window Owner;

        /// <summary>Set by MainWindow: go to a page.</summary>
        public Action<NavTarget> NavigateTo;
        public Action<string> StatusSink;

        public void Navigate(Page p) { NavigateTo(NavTarget.To(p)); }
        public void Navigate(NavTarget t) { NavigateTo(t); }

        public void Status(string text) { var s = StatusSink; if (s != null) s(text); }

        /// <summary>
        /// Show the real message. Licence and RTI failures arrive as raw FlexNet/RTI text and the
        /// user needs it verbatim - a friendly summary alone would hide the diagnosis.
        /// </summary>
        public void Fail(string what, Exception ex) { Fail(what, ex.Message); }

        public void Fail(string what, string message)
        {
            var msg = message ?? "";
            // REQ-07: the words first, the raw FlexNet/RTI text underneath as evidence.
            var licence = LicenceDiagnosis.Explain(msg);
            if (licence != null) msg = licence + "\n\n" + msg;
            Status(what + ": " + (licence ?? message));
            Dialogs.Inform(Owner, what, msg, true);
        }

        // ------------------------------------------------------------- lookups ---

        /// <summary>The two connections a pair runs over. B is A for a self-test.</summary>
        public void SidesOf(PairRecord p, out ConnectionRecord a, out ConnectionRecord b)
        {
            a = p == null ? null : Connections.ByName(p.ConnA);
            b = p == null ? null : (p.SelfTest ? a : Connections.ByName(p.ConnB));
        }

        /// <summary>Reason a pair cannot run, or null.</summary>
        public string WhyNotRunnable(PairRecord p)
        {
            if (p == null) return "pair not found";
            ConnectionRecord a, b;
            SidesOf(p, out a, out b);
            if (a == null) return "Send · A has no connection (\"" + p.ConnA + "\") - edit the pair";
            if (b == null) return "Receive · B has no connection (\"" + p.ConnB + "\") - edit the pair";
            Connections.Validate(a);
            if (!a.Ok) return "Send · A: " + a.Status;
            if (!ReferenceEquals(a, b)) { Connections.Validate(b); if (!b.Ok) return "Receive · B: " + b.Status; }
            if (a.Fom == null || a.Fom.Model == null) return "Send · A: its FOM does not load";
            if (b.Fom == null || b.Fom.Model == null) return "Receive · B: its FOM does not load";
            return null;
        }

        public static string FindAgent(string exeName)
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

        /// <summary>"HLA 1.3 · RPR 1.0"-style one-liner for a side card.</summary>
        public static string Describe(ConnectionRecord c)
        {
            if (c == null) return "not set";
            var parts = new List<string>();
            parts.Add(c.Fom != null && c.Fom.IsEvolved ? "HLA Evolved" : "HLA 1.3");
            if (!string.IsNullOrEmpty(c.RtiVersion)) parts.Add(c.RtiVersion);
            return string.Join(" · ", parts.ToArray());
        }
    }
}
