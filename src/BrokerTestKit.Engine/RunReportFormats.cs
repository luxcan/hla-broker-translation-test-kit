using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Xml;

namespace BrokerTestKit.Engine
{
    /// <summary>
    /// The three shapes a run can be written in (REQ-05), picked by file extension so the CLI's
    /// <c>--report</c> and the GUI's Export share one rule:
    ///   .xml          JUnit XML  - what CI servers (Jenkins, GitLab, Azure DevOps) ingest
    ///   .html / .htm  HTML       - one self-contained page a person can open or attach
    ///   anything else CSV        - <see cref="RunReportCsv"/>
    /// </summary>
    public static class RunReportWriter
    {
        public enum Format { Csv, JUnit, Html }

        public static Format FormatFor(string path)
        {
            var ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            if (ext == ".xml") return Format.JUnit;
            if (ext == ".html" || ext == ".htm") return Format.Html;
            return Format.Csv;
        }

        /// <param name="suiteName">Names the run in CI and in the page title; the expected sheet's name is a good one.</param>
        public static void Write(string path, RunReport report, IEnumerable<string> context = null,
                                 string suiteName = null)
        {
            switch (FormatFor(path))
            {
                case Format.JUnit: RunReportJUnit.Write(path, report, context, suiteName); break;
                case Format.Html:  RunReportHtml.Write(path, report, context, suiteName); break;
                default:           RunReportCsv.Write(path, report, context); break;
            }
        }

        /// <summary>
        /// Context lines are "key   value" (e.g. "expected C:\...\out.csv"). Split on the first run
        /// of whitespace so the structured formats can show them as name/value pairs.
        /// </summary>
        internal static List<KeyValuePair<string, string>> SplitContext(IEnumerable<string> context)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (context == null) return list;
            foreach (var raw in context)
            {
                var c = (raw ?? "").Trim();
                if (c.Length == 0) continue;
                int i = c.IndexOfAny(new[] { ' ', '\t' });
                if (i < 0) list.Add(new KeyValuePair<string, string>(c, ""));
                else list.Add(new KeyValuePair<string, string>(c.Substring(0, i), c.Substring(i).Trim()));
            }
            return list;
        }

        internal static string RowName(StepResult st)
        {
            return string.IsNullOrEmpty(st.EntityId)
                ? "row " + st.Step.ToString(CultureInfo.InvariantCulture)
                : "row " + st.Step.ToString(CultureInfo.InvariantCulture) + " · " + st.EntityId;
        }
    }

    /// <summary>
    /// JUnit XML. One &lt;testcase&gt; per row.
    ///
    /// REQ-05 keeps Fail and Timeout apart because they are different defects, and JUnit has two
    /// buckets that fit: <b>Fail</b> → &lt;failure&gt; (it arrived, the values were wrong),
    /// <b>Timeout</b> → &lt;error&gt; (nothing arrived). CI dashboards count them separately.
    ///
    /// An aborted run adds one synthetic errored test case. Without it, a run that gave up after
    /// row 3 of 4000 would show 3 green tests in CI while the CLI exits 1.
    /// </summary>
    public static class RunReportJUnit
    {
        public static void Write(string path, RunReport report, IEnumerable<string> context = null,
                                 string suiteName = null)
        {
            var inv = CultureInfo.InvariantCulture;
            var name = string.IsNullOrEmpty(suiteName) ? "BrokerTestKit" : suiteName;
            int skipped = 0;
            foreach (var s in report.Steps) if (s.Outcome == Outcome.Skipped) skipped++;
            int tests = report.Steps.Count + (report.Aborted ? 1 : 0);
            int errors = report.TimedOut + (report.Aborted ? 1 : 0);
            string secs = report.Duration.TotalSeconds.ToString("F3", inv);

            var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
            using (var w = XmlWriter.Create(path, settings))
            {
                w.WriteStartDocument();
                w.WriteStartElement("testsuites");
                w.WriteAttributeString("name", name);
                w.WriteAttributeString("tests", tests.ToString(inv));
                w.WriteAttributeString("failures", report.Failed.ToString(inv));
                w.WriteAttributeString("errors", errors.ToString(inv));
                w.WriteAttributeString("skipped", skipped.ToString(inv));
                w.WriteAttributeString("time", secs);

                w.WriteStartElement("testsuite");
                w.WriteAttributeString("name", name);
                w.WriteAttributeString("tests", tests.ToString(inv));
                w.WriteAttributeString("failures", report.Failed.ToString(inv));
                w.WriteAttributeString("errors", errors.ToString(inv));
                w.WriteAttributeString("skipped", skipped.ToString(inv));
                w.WriteAttributeString("time", secs);
                w.WriteAttributeString("timestamp", report.StartedUtc.ToString("yyyy-MM-ddTHH:mm:ss", inv));
                w.WriteAttributeString("hostname", Environment.MachineName);

                w.WriteStartElement("properties");
                foreach (var kv in RunReportWriter.SplitContext(context)) Property(w, kv.Key, kv.Value);
                var lat = report.LatencySummary();
                if (lat != null) Property(w, "latency", lat);
                if (!string.IsNullOrEmpty(report.TestCase)) Property(w, "testcase.name", report.TestCase);
                foreach (var c in report.Classes) Property(w, "class." + c.ClassName, c.ToString());
                w.WriteEndElement();

                foreach (var st in report.Steps)
                {
                    w.WriteStartElement("testcase");
                    w.WriteAttributeString("classname", name);
                    w.WriteAttributeString("name", RunReportWriter.RowName(st));
                    w.WriteAttributeString("time", st.Elapsed.TotalSeconds.ToString("F3", inv));

                    switch (st.Outcome)
                    {
                        case Outcome.Fail:
                            Problem(w, "failure", "Fail", st);
                            break;
                        case Outcome.Timeout:
                            Problem(w, "error", "Timeout", st);
                            break;
                        case Outcome.Skipped:
                            w.WriteStartElement("skipped");
                            if (!string.IsNullOrEmpty(st.Message)) w.WriteAttributeString("message", st.Message);
                            w.WriteEndElement();
                            break;
                    }

                    if (st.Fields.Count > 0 || st.Latency.HasValue)
                    {
                        var sb = new StringBuilder();
                        if (st.Latency.HasValue)
                            sb.AppendLine("latency " + st.Latency.Value.TotalMilliseconds.ToString("F1", inv) + "ms");
                        foreach (var p in st.Parts) sb.AppendLine(p.ToString());
                        foreach (var f in st.Fields) sb.AppendLine(f.ToString());
                        w.WriteElementString("system-out", sb.ToString());
                    }
                    w.WriteEndElement();
                }

                if (report.Aborted)
                {
                    w.WriteStartElement("testcase");
                    w.WriteAttributeString("classname", name);
                    w.WriteAttributeString("name", "run aborted");
                    w.WriteAttributeString("time", "0");
                    w.WriteStartElement("error");
                    w.WriteAttributeString("type", "Aborted");
                    w.WriteAttributeString("message", report.AbortReason);
                    w.WriteString("The run stopped early; rows after the last one listed were never attempted.");
                    w.WriteEndElement();
                    w.WriteEndElement();
                }

                w.WriteEndElement(); // testsuite
                w.WriteEndElement(); // testsuites
                w.WriteEndDocument();
            }
        }

        private static void Property(XmlWriter w, string name, string value)
        {
            w.WriteStartElement("property");
            w.WriteAttributeString("name", name);
            w.WriteAttributeString("value", value ?? "");
            w.WriteEndElement();
        }

        private static void Problem(XmlWriter w, string element, string type, StepResult st)
        {
            w.WriteStartElement(element);
            w.WriteAttributeString("type", type);
            w.WriteAttributeString("message", st.Message ?? type);
            var sb = new StringBuilder();
            foreach (var p in st.Parts) if (p.Outcome != Outcome.Pass) sb.AppendLine(p.ToString());
            foreach (var f in st.Fields) if (!f.Ok) sb.AppendLine(f.ToString());
            if (sb.Length > 0) w.WriteString(sb.ToString());
            w.WriteEndElement();
        }
    }

    /// <summary>
    /// One self-contained HTML page: summary tiles, then every row, failing rows opened to show
    /// their field-level diffs. No scripts, no external files, so it survives being emailed,
    /// attached to a ticket, or archived as a CI artefact. Follows the viewer's light/dark setting.
    /// </summary>
    public static class RunReportHtml
    {
        public static void Write(string path, RunReport report, IEnumerable<string> context = null,
                                 string suiteName = null)
        {
            var inv = CultureInfo.InvariantCulture;
            var title = string.IsNullOrEmpty(suiteName) ? "Broker test run" : suiteName;
            var verdict = report.AllPassed ? "PASSED" : (report.Aborted ? "ABORTED" : "FAILED");

            var h = new StringBuilder();
            h.Append("<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">")
             .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
             .Append("<title>").Append(E(title)).Append(" – ").Append(verdict).Append("</title>\n<style>")
             .Append(Css)
             .Append("</style></head><body><main>\n");

            h.Append("<header><h1>").Append(E(title)).Append("</h1>")
             .Append("<span class=\"verdict ").Append(report.AllPassed ? "pass" : "fail").Append("\">")
             .Append(verdict).Append("</span></header>\n");

            h.Append("<p class=\"meta\">Started ")
             .Append(E(report.StartedUtc.ToString("yyyy-MM-dd HH:mm:ss", inv))).Append(" UTC · ")
             .Append(report.Duration.TotalSeconds.ToString("F1", inv)).Append(" s · ")
             .Append(E(Environment.MachineName)).Append("</p>\n");

            if (report.Aborted)
                h.Append("<p class=\"abort\"><b>Aborted:</b> ").Append(E(report.AbortReason)).Append("</p>\n");

            h.Append("<section class=\"tiles\">");
            Tile(h, "Rows", report.Steps.Count, "");
            Tile(h, "Passed", report.Passed, "pass");
            Tile(h, "Failed", report.Failed, report.Failed > 0 ? "fail" : "");
            Tile(h, "Timed out", report.TimedOut, report.TimedOut > 0 ? "timeout" : "");
            h.Append("</section>\n");

            var lat = report.LatencySummary();
            if (lat != null) h.Append("<p class=\"lat\">").Append(E(lat)).Append("</p>\n");

            if (report.Classes.Count > 0)
            {
                h.Append("<div class=\"scroll\"><table class=\"fields classes\"><thead><tr><th>Receive class</th>")
                 .Append("<th>Expected</th><th>Matched</th><th>Wrong</th><th>Never arrived</th></tr></thead><tbody>");
                foreach (var c in report.Classes)
                    h.Append("<tr class=\"").Append(c.Matched == c.Expected ? "ok" : "bad").Append("\"><td>")
                     .Append(E(c.ClassName)).Append("</td><td>").Append(c.Expected.ToString(inv))
                     .Append("</td><td>").Append(c.Matched.ToString(inv)).Append("</td><td>")
                     .Append(c.Wrong.ToString(inv)).Append("</td><td>").Append(c.Missing.ToString(inv))
                     .Append("</td></tr>");
                h.Append("</tbody></table></div>\n");
            }

            var ctx = RunReportWriter.SplitContext(context);
            if (ctx.Count > 0)
            {
                h.Append("<table class=\"ctx\">");
                foreach (var kv in ctx)
                    h.Append("<tr><th>").Append(E(kv.Key)).Append("</th><td>").Append(E(kv.Value)).Append("</td></tr>");
                h.Append("</table>\n");
            }

            h.Append("<p class=\"legend\"><b>Fail</b> = it arrived on B with wrong values. ")
             .Append("<b>Timeout</b> = nothing arrived on B.</p>\n");

            h.Append("<div class=\"rows\">\n");
            foreach (var st in report.Steps)
            {
                var cls = st.Outcome.ToString().ToLowerInvariant();
                bool open = st.Outcome == Outcome.Fail || st.Outcome == Outcome.Timeout;
                h.Append("<details class=\"row ").Append(cls).Append("\"").Append(open ? " open" : "").Append("><summary>")
                 .Append("<span class=\"badge ").Append(cls).Append("\">").Append(st.Outcome).Append("</span>")
                 .Append("<span class=\"name\">").Append(E(RunReportWriter.RowName(st))).Append("</span>")
                 .Append("<span class=\"num\">").Append(st.Elapsed.TotalSeconds.ToString("F2", inv)).Append(" s")
                 .Append(st.Latency.HasValue
                     ? " · " + st.Latency.Value.TotalMilliseconds.ToString("F1", inv) + " ms" : "")
                 .Append("</span>");
                if (!string.IsNullOrEmpty(st.Message))
                    h.Append("<span class=\"msg\">").Append(E(st.Message)).Append("</span>");
                h.Append("</summary>");

                if (st.Parts.Count > 0)
                {
                    h.Append("<ul class=\"parts\">");
                    foreach (var p in st.Parts)
                    {
                        var pc = p.Outcome.ToString().ToLowerInvariant();
                        h.Append("<li><span class=\"badge ").Append(pc).Append("\">").Append(p.Outcome)
                         .Append("</span> <b>").Append(E(p.ClassName)).Append("</b> ").Append(E(p.EntityId))
                         .Append(" <span class=\"num\">").Append(E(p.Message)).Append("</span></li>");
                    }
                    h.Append("</ul>");
                }

                if (st.Fields.Count > 0)
                {
                    h.Append("<div class=\"scroll\"><table class=\"fields\"><thead><tr><th></th><th>Field</th>")
                     .Append("<th>Expected</th><th>Actual</th><th>Note</th></tr></thead><tbody>");
                    foreach (var f in st.Fields)
                        h.Append("<tr class=\"").Append(f.Ok ? "ok" : "bad").Append("\"><td>")
                         .Append(f.Ok ? "✓" : "✗").Append("</td><td>").Append(E(f.Name))
                         .Append("</td><td><code>").Append(E(f.Expected)).Append("</code></td><td><code>")
                         .Append(E(f.Actual)).Append("</code></td><td>").Append(E(f.Note)).Append("</td></tr>");
                    h.Append("</tbody></table></div>");
                }
                h.Append("</details>\n");
            }
            h.Append("</div>\n</main></body></html>\n");

            File.WriteAllText(path, h.ToString(), new UTF8Encoding(false));
        }

        private static void Tile(StringBuilder h, string label, int n, string cls)
        {
            h.Append("<div class=\"tile ").Append(cls).Append("\"><div class=\"n\">")
             .Append(n.ToString(CultureInfo.InvariantCulture)).Append("</div><div class=\"l\">")
             .Append(E(label)).Append("</div></div>");
        }

        private static string E(string s) { return WebUtility.HtmlEncode(s ?? ""); }

        private const string Css = @"
:root{--bg:#f7f7f5;--card:#fff;--fg:#1d1d1b;--mut:#6b6b66;--line:#e2e2dd;
--pass:#1f7a3d;--pass-bg:#e5f4ea;--fail:#b3261e;--fail-bg:#fbe9e7;--to:#9a6200;--to-bg:#fdf1dc;--skip:#6b6b66;--skip-bg:#eeeeea}
@media (prefers-color-scheme:dark){:root{--bg:#161615;--card:#1f1f1d;--fg:#ececea;--mut:#a3a39d;--line:#34342f;
--pass:#6fd08f;--pass-bg:#173322;--fail:#ff8a80;--fail-bg:#3a1a17;--to:#f2b856;--to-bg:#3a2c12;--skip:#a3a39d;--skip-bg:#2a2a27}}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.45 system-ui,-apple-system,'Segoe UI',sans-serif}
main{max-width:1100px;margin:0 auto;padding:24px 16px 48px}
header{display:flex;align-items:center;gap:12px;flex-wrap:wrap}
h1{font-size:22px;margin:0;word-break:break-word}
.verdict{font-weight:700;font-size:12px;letter-spacing:.06em;padding:4px 10px;border-radius:999px}
.verdict.pass{background:var(--pass-bg);color:var(--pass)}.verdict.fail{background:var(--fail-bg);color:var(--fail)}
.meta,.lat,.legend{color:var(--mut);margin:6px 0}
.lat{font-variant-numeric:tabular-nums}
.abort{background:var(--fail-bg);color:var(--fail);padding:8px 12px;border-radius:8px}
.tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(120px,1fr));gap:10px;margin:16px 0}
.tile{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px 14px}
.tile .n{font-size:26px;font-weight:700;font-variant-numeric:tabular-nums}.tile .l{color:var(--mut)}
.tile.pass .n{color:var(--pass)}.tile.fail .n{color:var(--fail)}.tile.timeout .n{color:var(--to)}
table{border-collapse:collapse}
.ctx{margin:8px 0 12px;font-size:13px}.ctx th{text-align:left;color:var(--mut);font-weight:500;padding:2px 16px 2px 0;vertical-align:top}
.ctx td{word-break:break-all}
.rows{display:flex;flex-direction:column;gap:6px;margin-top:12px}
.row{background:var(--card);border:1px solid var(--line);border-radius:8px}
.row.fail{border-left:4px solid var(--fail)}.row.timeout{border-left:4px solid var(--to)}.row.pass{border-left:4px solid var(--pass)}
summary{cursor:pointer;padding:8px 12px;display:flex;flex-wrap:wrap;gap:4px 12px;align-items:baseline}
.badge{font-size:11px;font-weight:700;padding:2px 8px;border-radius:999px;min-width:64px;text-align:center}
.badge.pass{background:var(--pass-bg);color:var(--pass)}.badge.fail{background:var(--fail-bg);color:var(--fail)}
.badge.timeout{background:var(--to-bg);color:var(--to)}.badge.skipped{background:var(--skip-bg);color:var(--skip)}
.name{font-weight:600}.num{color:var(--mut);font-variant-numeric:tabular-nums}.msg{color:var(--mut);flex-basis:100%}
.scroll{overflow-x:auto;padding:0 12px 10px}
.fields{width:100%;font-size:13px}.fields th{text-align:left;color:var(--mut);font-weight:500;border-bottom:1px solid var(--line);padding:4px 8px}
.fields td{padding:4px 8px;border-bottom:1px solid var(--line);vertical-align:top}
.fields tr.bad td{background:var(--fail-bg)}.fields tr.bad td:first-child{color:var(--fail);font-weight:700}
.fields tr.ok td:first-child{color:var(--pass)}
code{font:12px ui-monospace,Consolas,monospace;word-break:break-all}
.parts{list-style:none;margin:0;padding:0 12px 8px;display:flex;flex-direction:column;gap:4px}
.classes{margin:4px 0 12px}
";
    }
}
