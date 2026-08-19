using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BrokerTestKit.Engine
{
    /// <summary>
    /// Generates a CSV template from a FOM. This is the GUI's "upload FOM, get a template"
    /// step: the user picks an object class or an interaction, and the app emits a sheet whose
    /// columns are that class's attributes (or that interaction's parameters), already expanded
    /// to addressable sub-fields.
    ///
    /// Each ROW is then one entity or one interaction - the test author fills in values and
    /// never states a datatype, because the FOM already did.
    ///
    /// Column selection rules, and why:
    ///   * fixed records are EXPANDED to leaf sub-fields (WorldLocation -&gt; .X/.Y/.Z), because
    ///     that is the level a person reasons about and the level tolerances apply at;
    ///   * variable-length and variant fields are emitted as ONE whole-attribute column, since
    ///     their internals have no fixed offset - the harness compares them as raw bytes;
    ///   * opaque byte arrays get a `:string` hint, because a FOM cannot say "these bytes are
    ///     text" and the author usually means text.
    /// </summary>
    public static class CsvTemplateGenerator
    {
        public sealed class Options
        {
            /// <summary>Emit the `expected` sheet (adds a tolerance directive) rather than input.</summary>
            public bool ForExpectations;
            /// <summary>How many blank example rows to leave under the header.</summary>
            public int SampleRows = 3;
            /// <summary>Only these attributes (whole-attribute names). Null/empty = all.</summary>
            public List<string> Include;
            /// <summary>Skip attributes with no fixed size (arrays, variant records).</summary>
            public bool SkipVariable;
            /// <summary>Default tolerance written into the directive for float columns.</summary>
            public double FloatTolerance = 0.001;
            /// <summary>
            /// Backstop against a pathological FOM, NOT a usability limit.
            ///
            /// It used to be 60, which a normal RPR class blows straight through: Aircraft has 46
            /// attributes and expands past 70 columns, so the template stopped inside Marking and
            /// silently lost Orientation, VelocityVector and WorldLocation - the entire kinematic
            /// state, from a sheet that looked complete. Narrow the sheet with --only, which says
            /// what you meant; do not rely on a cap to do it.
            /// </summary>
            public int MaxColumns = 512;
        }

        /// <summary>
        /// What <see cref="Columns"/> produced, plus what it could not fit. Anything dropped is
        /// named: a template that quietly omits attributes reads as "the FOM does not have them".
        /// </summary>
        public sealed class Sheet
        {
            public readonly List<Column> Columns = new List<Column>();
            /// <summary>Whole attributes left out because the column cap was reached.</summary>
            public readonly List<string> Dropped = new List<string>();
            /// <summary>Attributes the FOM defines but cannot address (no fixed offset, skipped).</summary>
            public readonly List<string> Unaddressable = new List<string>();
        }

        public sealed class Column
        {
            public string Path;             // "WorldLocation.X"
            public string TypeName;         // "double"
            public string Kind;             // "float64"
            public int Offset;
            public int Size = -1;
            public string Override;         // ":string" hint, or null

            /// <summary>What actually goes in the header cell.</summary>
            public string Header
            {
                get { return string.IsNullOrEmpty(Override) ? Path : Path + ":" + Override; }
            }
        }

        /// <summary>Work out the columns for a class or interaction without writing anything.</summary>
        public static List<Column> Columns(FomModel fom, string className, Options options = null)
        {
            return Build(fom, className, options).Columns;
        }

        /// <summary>
        /// As <see cref="Columns"/>, but also reports what did not make it.
        ///
        /// Each attribute is expanded into its own list and only committed if the WHOLE of it
        /// fits. Cutting mid-attribute is worse than leaving it out: a half-expanded struct
        /// (Marking.MarkingData[0] … [9] and no [10]) reads as a complete field and encodes to a
        /// short value.
        /// </summary>
        public static Sheet Build(FomModel fom, string className, Options options = null)
        {
            var opt = options ?? new Options();
            var members = fom.FindClass(className);
            if (members == null)
                throw new ArgumentException("'" + className + "' is not a class or interaction in " +
                                            fom.Describe());

            var names = new List<string>(members.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);

            var sheet = new Sheet();
            foreach (var attr in names)
            {
                if (!Wanted(opt.Include, attr)) continue;

                var one = new List<Column>();
                Expand(fom, className, attr, one, opt, 0);

                if (one.Count == 0) { sheet.Unaddressable.Add(attr); continue; }

                if (sheet.Columns.Count + one.Count > opt.MaxColumns) { sheet.Dropped.Add(attr); continue; }
                sheet.Columns.AddRange(one);
            }
            return sheet;
        }

        private static bool Wanted(List<string> include, string attribute)
        {
            if (include == null || include.Count == 0) return true;
            foreach (var i in include)
                if (string.Equals(i, attribute, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Expand ONE attribute into its addressable columns. Deliberately unbounded - the caller
        /// applies the cap whole-attribute-at-a-time, so this never returns half a struct.
        /// </summary>
        private static void Expand(FomModel fom, string className, string path,
                                   List<Column> into, Options opt, int depth)
        {
            string error;
            var reference = fom.Resolve(className, path, out error);
            if (reference == null) return;                 // unaddressable - leave it out entirely

            // A fixed record with known field offsets is worth expanding; anything else is a leaf.
            FomType type;
            if (depth < 3 && fom.Types.TryGetValue(reference.TypeName, out type) &&
                type.Kind == FomKind.FixedRecord && type.Fields.Count > 0)
            {
                bool expanded = false;
                foreach (var f in type.Fields)
                {
                    if (f.Offset < 0) continue;            // after a variable field - not addressable
                    Expand(fom, className, path + "." + f.Name, into, opt, depth + 1);
                    expanded = true;
                }
                if (expanded) return;
            }

            if (reference.Size < 0 && opt.SkipVariable) return;

            var col = new Column
            {
                Path = path,
                TypeName = reference.TypeName,
                Kind = reference.Kind,
                Offset = reference.Offset,
                Size = reference.Size,
            };

            // The FOM cannot say "these opaque bytes are text", but the author almost always
            // means text when they type a marking or a tag. Offer the hint; it is easy to delete.
            if (reference.Kind == "hex" && IsByteArray(fom, reference.TypeName)) col.Override = "string";

            into.Add(col);
        }

        private static bool IsByteArray(FomModel fom, string typeName)
        {
            FomType t;
            if (!fom.Types.TryGetValue(typeName, out t)) return false;
            if (t.Kind != FomKind.Array) return false;
            var elem = fom.SizeOf(t.ElementType);
            return elem == 1;
        }

        // ---------------------------------------------------------------- write --

        /// <summary>Render the template as CSV text.</summary>
        public static string Generate(FomModel fom, string className, Options options = null)
        {
            var opt = options ?? new Options();
            var sheet = Build(fom, className, opt);
            var columns = sheet.Columns;
            var isInteraction = fom.IsInteraction(className);
            var noun = isInteraction ? "interaction" : "entity";

            var sb = new StringBuilder();
            sb.Append("# class: ").Append(className).Append('\n');
            sb.Append("# kind: ").Append(isInteraction ? "interaction" : "object").Append('\n');

            if (opt.ForExpectations)
            {
                var tol = new List<string>();
                foreach (var c in columns)
                    if (c.Kind == "float64" || c.Kind == "float32")
                        tol.Add(c.Path + "=" + opt.FloatTolerance.ToString("R", CultureInfo.InvariantCulture));
                if (tol.Count > 0)
                {
                    sb.Append("# tolerance: ");
                    sb.Append(string.Join(", ", tol.ToArray()));
                    sb.Append('\n');
                }
            }

            sb.Append("#\n");
            sb.Append("# Generated from ").Append(fom.Describe()).Append('\n');
            sb.Append("# One row = one ").Append(noun).Append(". ");
            sb.Append(opt.ForExpectations
                ? "Empty cell = do not check that field.\n"
                : "Empty cell = do not set that field.\n");
            sb.Append("#\n");
            sb.Append("# No datatype column: the FOM already states every type and offset.\n");

            if (sheet.Unaddressable.Count > 0)
            {
                sb.Append("#\n# NOT INCLUDED - the FOM defines these but they have no addressable\n");
                sb.Append("# fixed layout at this level (variable-length or variant):\n#   ");
                sb.Append(string.Join(", ", sheet.Unaddressable.ToArray())).Append('\n');
            }
            if (sheet.Dropped.Count > 0)
            {
                sb.Append("#\n# *** TRUNCATED *** the column cap (").Append(opt.MaxColumns);
                sb.Append(") was reached, so ").Append(sheet.Dropped.Count);
                sb.Append(" attribute(s) are MISSING from this sheet:\n#   ");
                sb.Append(string.Join(", ", sheet.Dropped.ToArray())).Append('\n');
                sb.Append("# Re-run with --max <n>, or --only <a,b,c> to choose deliberately.\n");
            }

            sb.Append("# Resolved for reference (do not edit these lines):\n");
            foreach (var c in columns)
                sb.Append("#   ").Append(Pad(c.Path, 34)).Append(Pad(c.TypeName, 30))
                  .Append(c.Kind).Append(c.Size >= 0 ? "  " + c.Size + " bytes" : "  variable")
                  .Append(c.Offset != 0 ? "  @" + c.Offset : "").Append('\n');
            sb.Append('\n');

            sb.Append("entityId");
            foreach (var c in columns) sb.Append(',').Append(c.Header);
            sb.Append('\n');

            for (int i = 0; i < opt.SampleRows; i++)
            {
                sb.Append(SampleId(isInteraction, i));
                for (int c = 0; c < columns.Count; c++) sb.Append(',');
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static string SampleId(bool isInteraction, int i)
        {
            var n = (101 + i).ToString(CultureInfo.InvariantCulture);
            return isInteraction ? "int-" + n : "1:1:" + n;
        }

        private static string Pad(string s, int width)
        {
            if (s == null) s = "";
            return s.Length >= width ? s + " " : s.PadRight(width);
        }
    }
}
