using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace BrokerTestKit.Engine
{
    /// <summary>
    /// The entity-per-row CSV format: ONE class per file, one COLUMN per attribute, one ROW per
    /// entity. It reads like a spreadsheet of CGF entities, which is what a test author actually
    /// has in mind.
    ///
    /// <code>
    /// # class: Aircraft
    /// # tolerance: WorldLocation.*=0.5, BtkCustomValue=0.001
    /// entityId,Marking,WorldLocation.X,WorldLocation.Y,WorldLocation.Z,BtkCustomValue,BtkCustomTag
    /// 1:1:101,TANK-01,-2696048.0,1234.5,5500000.0,1234.5,CUSTOM-TAG-01
    /// 1:1:102,TANK-02,100.25,,,-0.5,SECOND-ENTITY
    /// </code>
    ///
    /// There is NO datatype column, by design. The `.omt` (HLA 1.3) or FDD (Evolved) already
    /// states every attribute's datatype and every struct's field offsets, so restating them in
    /// the test data would be duplication that can silently drift out of step with the FOM.
    /// Datatypes are resolved from <see cref="FomModel"/> at parse time (REQ-09).
    ///
    /// Row N is one step: publish that entity, then verify. That matches the agreed verification
    /// model - A sends one row, B waits or times out, record, move to the next row.
    ///
    /// Conventions
    ///   * `entityId` is the correlation key between the input and expectation files. It is NOT
    ///     sent as an attribute; it just pairs the rows up and names the entity in the report.
    ///   * An EMPTY cell means "not set" on input and "do not check" on output - which is how you
    ///     write a test that only cares about two of forty attributes.
    ///   * Lines starting with `#` are comments; `# key: value` lines are directives.
    /// </summary>
    public static class EntityCsvParser
    {
        public sealed class Sheet
        {
            /// <summary>Class named by the `# class:` directive.</summary>
            public string ClassName;
            /// <summary>`# kind:` directive - "object" (default) or "interaction".</summary>
            public string Kind = "object";
            public bool IsInteraction
            {
                get { return string.Equals(Kind, "interaction", StringComparison.OrdinalIgnoreCase); }
            }
            /// <summary>Attribute paths, in column order (excluding entityId).</summary>
            public readonly List<string> Columns = new List<string>();
            /// <summary>
            /// Optional per-column interpretation override, written `Attr:kind` in the header.
            /// Needed only when the FOM says a field is OPAQUE BYTES and the test wants to write
            /// it as text: `octet[0+]` is genuinely just bytes, and nothing in the FOM says
            /// otherwise, so the test author has to be the one to assert it is ASCII.
            /// </summary>
            public readonly Dictionary<string, string> Overrides =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Per-column tolerance from the `# tolerance:` directive.</summary>
            public readonly Dictionary<string, double> Tolerances =
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            public double? DefaultTolerance;
            public string SourceFile;
        }

        private const string IdColumn = "entityid";

        // ------------------------------------------------------------- reading ---

        public static List<InputStep> ReadInput(string path, FomModel fom, out Sheet sheet)
        {
            sheet = ReadSheet(path);
            var rows = ReadRows(path, sheet);
            var cls = sheet.ClassName;

            var steps = new List<InputStep>();
            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                var step = new InputStep
                {
                    Step = r + 1,
                    ObjClass = cls,
                    EntityId = row.Id,
                };
                for (int c = 0; c < sheet.Columns.Count; c++)
                {
                    var text = c < row.Cells.Count ? row.Cells[c] : "";
                    if (text.Length == 0) continue;                 // empty = not set

                    var pathName = sheet.Columns[c];
                    step.Attributes.Add(new AttributeCell
                    {
                        Name = pathName,
                        Value = text,
                        DataType = SpecFor(fom, cls, pathName, path, r + 2, sheet),
                    });
                }
                steps.Add(step);
            }
            return steps;
        }

        public static List<ExpectedStep> ReadExpected(string path, FomModel fom, out Sheet sheet)
        {
            sheet = ReadSheet(path);
            var rows = ReadRows(path, sheet);
            var cls = sheet.ClassName;

            var steps = new List<ExpectedStep>();
            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                var step = new ExpectedStep
                {
                    Step = r + 1,
                    ObjClass = cls,
                    EntityId = row.Id,
                };
                for (int c = 0; c < sheet.Columns.Count; c++)
                {
                    var text = c < row.Cells.Count ? row.Cells[c] : "";
                    if (text.Length == 0) continue;                 // empty = do not check

                    var pathName = sheet.Columns[c];
                    step.Expected.Add(new AttributeCell
                    {
                        Name = pathName,
                        Value = text,
                        DataType = SpecFor(fom, cls, pathName, path, r + 2, sheet),
                        Tolerance = ToleranceFor(sheet, pathName),
                    });
                }
                steps.Add(step);
            }
            return steps;
        }

        /// <summary>
        /// Ask the FOM where this field lives. A column the FOM does not define is a hard error
        /// naming the file and line: the alternative is comparing arbitrary bytes and calling it
        /// a broker defect.
        /// </summary>
        private static string SpecFor(FomModel fom, string cls, string attributePath,
                                      string file, int line, Sheet sheet)
        {
            if (fom == null)
                throw new InvalidOperationException(
                    "no FOM loaded - the entity CSV format needs the .omt (HLA 1.3) or FDD .xml " +
                    "to resolve datatypes");

            string error;
            var reference = fom.Resolve(cls, attributePath, out error);
            if (reference == null)
                throw new FormatException(Path.GetFileName(file) + ": column '" + attributePath +
                                          "' - " + error);

            // An override changes only HOW the bytes are read, never WHERE they are: the offset
            // still comes from the FOM, so a header typo cannot silently move the field.
            string kind;
            if (sheet != null && sheet.Overrides.TryGetValue(attributePath, out kind))
                return reference.Offset == 0
                    ? kind
                    : kind + "@" + reference.Offset.ToString(CultureInfo.InvariantCulture);

            return reference.CodecSpec;
        }

        private static double? ToleranceFor(Sheet sheet, string attributePath)
        {
            double t;
            if (sheet.Tolerances.TryGetValue(attributePath, out t)) return t;

            foreach (var kv in sheet.Tolerances)                    // wildcard: "WorldLocation.*"
            {
                var k = kv.Key;
                if (!k.EndsWith("*", StringComparison.Ordinal)) continue;
                if (attributePath.StartsWith(k.Substring(0, k.Length - 1),
                                             StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
            return sheet.DefaultTolerance;
        }

        // ------------------------------------------------------------ mechanics ---

        private sealed class Row
        {
            public string Id;
            public List<string> Cells = new List<string>();
        }

        /// <summary>Read directives + header. Does not read data rows.</summary>
        public static Sheet ReadSheet(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("CSV not found: " + path);

            var sheet = new Sheet { SourceFile = path };
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    ReadDirective(sheet, line.TrimStart('#').Trim());
                    continue;
                }

                var cells = SplitCsv(line);
                if (cells.Count == 0) continue;
                if (!string.Equals(cells[0].Trim(), IdColumn, StringComparison.OrdinalIgnoreCase))
                    throw new FormatException(Path.GetFileName(path) +
                        ": the first column must be 'entityId', found '" + cells[0].Trim() + "'");

                for (int i = 1; i < cells.Count; i++)
                {
                    var name = cells[i].Trim();
                    if (name.Length == 0) continue;

                    // "BtkCustomTag:string" - path before the first colon, override after.
                    var colonAt = name.IndexOf(':');
                    if (colonAt > 0)
                    {
                        var pathPart = name.Substring(0, colonAt).Trim();
                        var kindPart = name.Substring(colonAt + 1).Trim();
                        if (kindPart.Length > 0) sheet.Overrides[pathPart] = kindPart;
                        name = pathPart;
                    }
                    sheet.Columns.Add(name);
                }
                break;                                             // header found
            }

            if (string.IsNullOrEmpty(sheet.ClassName))
                throw new FormatException(Path.GetFileName(path) +
                    ": missing '# class: <ObjectClass>' directive");
            if (sheet.Columns.Count == 0)
                throw new FormatException(Path.GetFileName(path) + ": header declares no attributes");

            return sheet;
        }

        private static void ReadDirective(Sheet sheet, string body)
        {
            var colon = body.IndexOf(':');
            if (colon < 0) return;                                  // plain comment
            var key = body.Substring(0, colon).Trim().ToLowerInvariant();
            var value = body.Substring(colon + 1).Trim();

            if (key == "class") { sheet.ClassName = value; return; }
            if (key == "kind") { sheet.Kind = value; return; }
            if (key != "tolerance") return;

            foreach (var part in value.Split(','))
            {
                var p = part.Trim();
                if (p.Length == 0) continue;
                var eq = p.IndexOf('=');
                double d;
                if (eq < 0)
                {
                    if (double.TryParse(p, NumberStyles.Any, CultureInfo.InvariantCulture, out d))
                        sheet.DefaultTolerance = d;                 // "# tolerance: 0.001"
                    continue;
                }
                var name = p.Substring(0, eq).Trim();
                if (double.TryParse(p.Substring(eq + 1).Trim(), NumberStyles.Any,
                                    CultureInfo.InvariantCulture, out d))
                    sheet.Tolerances[name] = d;
            }
        }

        private static List<Row> ReadRows(string path, Sheet sheet)
        {
            var rows = new List<Row>();
            bool pastHeader = false;

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                var cells = SplitCsv(line);
                if (!pastHeader)
                {
                    if (string.Equals(cells[0].Trim(), IdColumn, StringComparison.OrdinalIgnoreCase))
                    { pastHeader = true; continue; }
                    continue;
                }

                var row = new Row { Id = cells[0].Trim() };
                for (int i = 1; i < cells.Count; i++) row.Cells.Add(cells[i].Trim());
                if (row.Id.Length == 0 && row.Cells.TrueForAll(string.IsNullOrEmpty)) continue;
                rows.Add(row);
            }
            return rows;
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
    }
}
