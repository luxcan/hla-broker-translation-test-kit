using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BrokerTestKit.Engine
{
    /// <summary>
    /// Reads the golden-file CSVs.
    ///
    /// input.csv   step,action,objClass,entityId,attribute,value,datatype
    /// output.csv  step,objClass,entityId,attribute,expected,tolerance,datatype
    ///
    /// Rows sharing a <c>step</c> are ONE stimulus / ONE expectation set. The A-side and
    /// B-side attribute NAMES may differ — that difference is exactly the broker translation
    /// under test, expressed by pairing input step N with output step N.
    ///
    /// Blank lines and lines starting with '#' are ignored. Header row is required.
    /// </summary>
    public static class CsvParser
    {
        public static List<InputStep> ReadInput(string path)
        {
            var byStep = new Dictionary<int, InputStep>();
            var order = new List<int>();

            foreach (var row in ReadRows(path))
            {
                var step = IntOf(row, "step", 0);
                InputStep s;
                if (!byStep.TryGetValue(step, out s))
                {
                    s = new InputStep { Step = step };
                    byStep[step] = s;
                    order.Add(step);
                }
                if (Has(row, "action")) s.Action = Get(row, "action");
                if (Has(row, "objclass")) s.ObjClass = Get(row, "objclass");
                if (Has(row, "entityid")) s.EntityId = Get(row, "entityid");

                var name = Get(row, "attribute");
                if (name.Length == 0) continue;

                if (name.Equals("marking", StringComparison.OrdinalIgnoreCase))
                    s.Marking = Get(row, "value");

                s.Attributes.Add(new AttributeCell
                {
                    Name = name,
                    Value = Get(row, "value"),
                    DataType = Get(row, "datatype"),
                });
            }

            var list = new List<InputStep>();
            order.Sort();
            foreach (var k in order) list.Add(byStep[k]);
            return list;
        }

        public static List<ExpectedStep> ReadExpected(string path)
        {
            var byStep = new Dictionary<int, ExpectedStep>();
            var order = new List<int>();

            foreach (var row in ReadRows(path))
            {
                var step = IntOf(row, "step", 0);
                ExpectedStep s;
                if (!byStep.TryGetValue(step, out s))
                {
                    s = new ExpectedStep { Step = step };
                    byStep[step] = s;
                    order.Add(step);
                }
                if (Has(row, "objclass")) s.ObjClass = Get(row, "objclass");
                if (Has(row, "entityid")) s.EntityId = Get(row, "entityid");

                var name = Get(row, "attribute");
                if (name.Length == 0) continue;

                double tol = 0;
                var tolText = Get(row, "tolerance");
                var hasTol = tolText.Length > 0 &&
                             double.TryParse(tolText, NumberStyles.Any, CultureInfo.InvariantCulture, out tol);

                s.Expected.Add(new AttributeCell
                {
                    Name = name,
                    Value = Get(row, "expected"),
                    DataType = Get(row, "datatype"),
                    Tolerance = hasTol ? (double?)tol : null,
                });
            }

            var list = new List<ExpectedStep>();
            order.Sort();
            foreach (var k in order) list.Add(byStep[k]);
            return list;
        }

        // ---- minimal CSV reader (quoted fields supported) ---------------------------

        private static IEnumerable<Dictionary<string, string>> ReadRows(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("CSV not found: " + path);

            string[] header = null;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                var cells = SplitCsv(line);
                if (header == null)
                {
                    header = new string[cells.Count];
                    for (int i = 0; i < cells.Count; i++) header[i] = cells[i].Trim().ToLowerInvariant();
                    continue;
                }

                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < header.Length && i < cells.Count; i++)
                    row[header[i]] = cells[i].Trim();
                yield return row;
            }
        }

        private static List<string> SplitCsv(string line)
        {
            var outp = new List<string>();
            var sb = new System.Text.StringBuilder();
            bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (q)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else q = false;
                    }
                    else sb.Append(c);
                }
                else if (c == '"') q = true;
                else if (c == ',') { outp.Add(sb.ToString()); sb.Length = 0; }
                else sb.Append(c);
            }
            outp.Add(sb.ToString());
            return outp;
        }

        private static bool Has(Dictionary<string, string> r, string k)
        {
            string v;
            return r.TryGetValue(k, out v) && v.Length > 0;
        }

        private static string Get(Dictionary<string, string> r, string k)
        {
            string v;
            return r.TryGetValue(k, out v) && v != null ? v : "";
        }

        private static int IntOf(Dictionary<string, string> r, string k, int dflt)
        {
            int i;
            return int.TryParse(Get(r, k), out i) ? i : dflt;
        }
    }
}
