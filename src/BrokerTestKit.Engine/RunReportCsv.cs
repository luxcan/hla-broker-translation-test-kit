using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BrokerTestKit.Engine
{
    /// <summary>
    /// A run's results as CSV (REQ-05) - the artefact that outlives the screen.
    ///
    /// One writer for the CLI's --report and the GUI's Export, so a run exported from either
    /// surface reads the same and diffs cleanly against the other.
    ///
    /// Header lines start with "# ": the summary, plus any context the caller passes (which pair
    /// was tested, which sheets). Then one line per row. Failed fields are folded into one cell
    /// so the file stays one-row-per-entity; the full field-by-field detail stays in the app.
    /// </summary>
    public static class RunReportCsv
    {
        public static void Write(string path, RunReport report, IEnumerable<string> context = null)
        {
            var inv = CultureInfo.InvariantCulture;
            // WITH a byte-order mark: Excel reads a BOM-less UTF-8 CSV as the local code page, and the
            // app's connection names ("MAK-RPR1-1-1 · RTI 4.5.1") then come out as mojibake.
            using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                if (context != null)
                    foreach (var c in context) w.WriteLine("# " + c);
                foreach (var line in report.Summary().Split('\n')) w.WriteLine("# " + line.TrimEnd('\r'));

                w.WriteLine("row,entityId,outcome,seconds,latencyMs,message,failedFields");
                foreach (var st in report.Steps)
                {
                    var bad = new List<string>();
                    foreach (var f in st.Fields) if (!f.Ok) bad.Add(f.ToString());
                    w.WriteLine(string.Format(inv, "{0},{1},{2},{3:F3},{4},{5},{6}",
                        st.Step, Csv(st.EntityId), st.Outcome, st.Elapsed.TotalSeconds,
                        st.Latency.HasValue ? st.Latency.Value.TotalMilliseconds.ToString("F1", inv) : "",
                        Csv(st.Message), Csv(string.Join(" | ", bad.ToArray()))));
                }
            }
        }

        private static string Csv(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return v;
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        }
    }
}
