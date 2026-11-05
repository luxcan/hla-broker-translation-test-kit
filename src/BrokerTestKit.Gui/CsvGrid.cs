using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;

namespace BrokerTestKit.Gui
{
    /// <summary>
    /// Loads an entity-per-row CSV into a <see cref="DataTable"/> so a DataGrid can edit it, and
    /// writes it back preserving the leading <c>#</c> directive block.
    ///
    /// Editing in-app matters: opening these sheets in Excel silently strips the leading zeros
    /// from hex IDs (<c>000100010065</c> becomes <c>100010065</c>) and can turn values into dates.
    /// A golden file that is quietly wrong is worse than no golden file at all.
    /// </summary>
    public static class CsvGrid
    {
        /// <summary>The `#` lines above the header, kept verbatim so directives survive a round trip.</summary>
        public sealed class Sheet
        {
            public readonly List<string> Directives = new List<string>();
            public DataTable Table = new DataTable();
            public string Path;

            public string ClassName
            {
                get { return Directive("class"); }
                set { SetDirective("class", value); }
            }
            public string Kind
            {
                get { var k = Directive("kind"); return string.IsNullOrEmpty(k) ? "object" : k; }
                set { SetDirective("kind", value); }
            }

            public string Directive(string key)
            {
                foreach (var d in Directives)
                {
                    var body = d.TrimStart('#').Trim();
                    var colon = body.IndexOf(':');
                    if (colon < 0) continue;
                    if (string.Equals(body.Substring(0, colon).Trim(), key, StringComparison.OrdinalIgnoreCase))
                        return body.Substring(colon + 1).Trim();
                }
                return null;
            }

            private void SetDirective(string key, string value)
            {
                for (int i = 0; i < Directives.Count; i++)
                {
                    var body = Directives[i].TrimStart('#').Trim();
                    var colon = body.IndexOf(':');
                    if (colon < 0) continue;
                    if (string.Equals(body.Substring(0, colon).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    {
                        Directives[i] = "# " + key + ": " + value;
                        return;
                    }
                }
                Directives.Insert(0, "# " + key + ": " + value);
            }
        }

        public static Sheet Load(string path)
        {
            var sheet = new Sheet { Path = path };
            var lines = File.ReadAllLines(path);

            int i = 0;
            for (; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#", StringComparison.Ordinal)) { sheet.Directives.Add(line); continue; }
                break;                                   // header row
            }
            if (i >= lines.Length) return sheet;

            var header = Split(lines[i]);
            foreach (var h in header)
            {
                var name = h.Trim();
                if (name.Length == 0) name = "col" + sheet.Table.Columns.Count;
                while (sheet.Table.Columns.Contains(name)) name += "_";
                sheet.Table.Columns.Add(name, typeof(string));
            }

            for (i++; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var cells = Split(lines[i]);
                var row = sheet.Table.NewRow();
                for (int c = 0; c < sheet.Table.Columns.Count; c++)
                    row[c] = c < cells.Count ? cells[c].Trim() : "";
                sheet.Table.Rows.Add(row);
            }
            return sheet;
        }

        public static void Save(Sheet sheet, string path)
        {
            File.WriteAllText(path, ToText(sheet));
            sheet.Path = path;
        }

        /// <summary>The sheet as CSV text, directives first. What a test case stores as its asset.</summary>
        public static string ToText(Sheet sheet)
        {
            var sb = new StringBuilder();
            foreach (var d in sheet.Directives) sb.Append(d).Append('\n');
            if (sheet.Directives.Count > 0) sb.Append('\n');

            for (int c = 0; c < sheet.Table.Columns.Count; c++)
            {
                if (c > 0) sb.Append(',');
                sb.Append(Quote(sheet.Table.Columns[c].ColumnName));
            }
            sb.Append('\n');

            foreach (DataRow row in sheet.Table.Rows)
            {
                bool allEmpty = true;
                for (int c = 0; c < sheet.Table.Columns.Count; c++)
                    if (!string.IsNullOrEmpty(Convert.ToString(row[c]))) { allEmpty = false; break; }
                if (allEmpty) continue;

                for (int c = 0; c < sheet.Table.Columns.Count; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append(Quote(Convert.ToString(row[c])));
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Build an editable sheet directly from generated template text.</summary>
        public static Sheet FromText(string csvText, string path)
        {
            var tmp = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tmp, csvText);
                var sheet = Load(tmp);
                sheet.Path = path;
                return sheet;
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        private static string Quote(string s)
        {
            s = s ?? "";
            if (s.IndexOf(',') < 0 && s.IndexOf('"') < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private static List<string> Split(string line)
        {
            var outp = new List<string>();
            var sb = new StringBuilder();
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
    }
}
