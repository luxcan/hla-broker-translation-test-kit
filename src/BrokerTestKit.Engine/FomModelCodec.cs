using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BrokerTestKit.Engine
{
    /// <summary>
    /// Writes a parsed <see cref="FomModel"/> out as text and reads it back, so the app can keep
    /// what it worked out at import instead of re-deriving it on every start.
    ///
    /// Line-based and tab-separated rather than JSON: no dependency, it stays readable when
    /// something looks wrong, and reading it back is a single pass. The line prefixes are the
    /// format — see <see cref="Version"/> before changing any of them.
    /// </summary>
    public static class FomModelCodec
    {
        /// <summary>
        /// 🚨 BUMP THIS whenever the parsers or this format change.
        ///
        /// A stored model is keyed on the FOM's content hash. That catches an edited FOM, but it
        /// cannot catch an edited PARSER: fix an offset bug in <see cref="OmtParser"/> and every
        /// stored model is quietly wrong while its hash still matches — the runner would then
        /// compare the wrong bytes and report a broker fault that does not exist. This version is
        /// what makes those entries miss instead.
        /// </summary>
        /// v2: OmtParser now resolves (SuperClass &lt;id&gt;), so every OMT class inherits. Models
        ///     stored by v1 have leaf classes with zero attributes and must not be served.
        public const int Version = 5;

        private const string Magic = "BTKFOM";
        private const char Tab = '\t';

        // ------------------------------------------------------------------ write --

        public static string Write(FomModel m)
        {
            if (m == null) throw new ArgumentNullException("m");
            var sb = new StringBuilder(64 * 1024);

            Line(sb, Magic, Version.ToString(CultureInfo.InvariantCulture));
            Line(sb, "S", m.SourceFile);
            Line(sb, "V", m.Flavour);

            foreach (var kv in m.Types)
            {
                var t = kv.Value;
                Line(sb, "T", t.Name, t.Kind.ToString(), t.Representation, t.ElementType,
                     t.Cardinality, Num(t.Size));
                foreach (var f in t.Fields)
                    Line(sb, "f", f.Name, f.DataType, Num(f.Offset), Num(f.Size));
            }

            WriteTable(sb, m.Classes, "C", "a");
            WriteTable(sb, m.Interactions, "I", "p");
            return sb.ToString();
        }

        private static void WriteTable(StringBuilder sb,
            Dictionary<string, Dictionary<string, string>> table, string head, string row)
        {
            foreach (var kv in table)
            {
                Line(sb, head, kv.Key);
                foreach (var a in kv.Value) Line(sb, row, a.Key, a.Value);
            }
        }

        // ------------------------------------------------------------------- read --

        /// <summary>
        /// Rebuild a model. Throws on anything it does not recognise — a stored model that cannot
        /// be read back is a cache miss, never a half-populated model, because a model missing
        /// half its datatypes would resolve offsets wrongly rather than fail.
        /// </summary>
        public static FomModel Read(string text)
        {
            if (string.IsNullOrEmpty(text)) throw new FormatException("empty stored model");

            var m = new FomModel();
            FomType type = null;                       // the T the following f lines belong to
            Dictionary<string, string> members = null; // the C/I the following a/p lines belong to
            bool header = false;

            using (var sr = new StringReader(text))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    var p = line.Split(Tab);
                    for (int i = 0; i < p.Length; i++) p[i] = Unescape(p[i]);

                    switch (p[0])
                    {
                        case Magic:
                            var v = int.Parse(p[1], CultureInfo.InvariantCulture);
                            if (v != Version)
                                throw new FormatException("stored model is format " + v +
                                                          ", this build reads " + Version);
                            header = true;
                            break;

                        case "S": m.SourceFile = p[1]; break;
                        case "V": m.Flavour = p[1]; break;

                        case "T":
                            type = new FomType
                            {
                                Name = p[1],
                                Kind = (FomKind)Enum.Parse(typeof(FomKind), p[2]),
                                Representation = p[3],
                                ElementType = p[4],
                                Cardinality = p[5],
                                Size = int.Parse(p[6], CultureInfo.InvariantCulture),
                            };
                            m.Types[type.Name] = type;
                            members = null;
                            break;

                        case "f":
                            if (type == null) throw new FormatException("field line before its datatype");
                            type.Fields.Add(new FomField
                            {
                                Name = p[1],
                                DataType = p[2],
                                Offset = int.Parse(p[3], CultureInfo.InvariantCulture),
                                Size = int.Parse(p[4], CultureInfo.InvariantCulture),
                            });
                            break;

                        case "C":
                            members = NewMembers();
                            m.Classes[p[1]] = members;
                            type = null;
                            break;

                        case "I":
                            members = NewMembers();
                            m.Interactions[p[1]] = members;
                            type = null;
                            break;

                        case "a":
                        case "p":
                            if (members == null) throw new FormatException("member line before its class");
                            members[p[1]] = p[2];
                            break;

                        default:
                            throw new FormatException("unknown record '" + p[0] + "'");
                    }
                }
            }

            if (!header) throw new FormatException("not a stored FOM model");
            return m;
        }

        private static Dictionary<string, string> NewMembers()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ utils --

        private static void Line(StringBuilder sb, params string[] parts)
        {
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append(Tab);
                sb.Append(Escape(parts[i]));
            }
            sb.Append('\n');
        }

        private static string Num(int n) { return n.ToString(CultureInfo.InvariantCulture); }

        /// <summary>
        /// A tab or newline inside a FOM name would otherwise silently shift every field after it,
        /// and null has to survive the round trip distinctly from "".
        /// </summary>
        private static string Escape(string s)
        {
            if (s == null) return "\\0";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '\t') sb.Append("\\t");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Unescape(string s)
        {
            if (s == "\\0") return null;
            if (s.IndexOf('\\') < 0) return s;

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                var c = s[++i];
                if (c == '\\') sb.Append('\\');
                else if (c == 't') sb.Append('\t');
                else if (c == 'n') sb.Append('\n');
                else if (c == 'r') sb.Append('\r');
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
