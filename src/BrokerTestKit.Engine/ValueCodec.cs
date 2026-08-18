using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BrokerTestKit.Engine
{
    /// <summary>
    /// Turns a CSV cell into wire bytes and back. This is the whole of the harness's
    /// datatype knowledge, and it is deliberately DATA-DRIVEN (REQ-09): the caller supplies
    /// a datatype string from the CSV, nothing about any FOM is compiled in.
    ///
    /// All multi-byte forms are BIG-ENDIAN, because that is what HLA/RPR puts on the wire.
    ///
    /// Datatype grammar:  <c>&lt;kind&gt;[:&lt;width&gt;][@&lt;offset&gt;]</c>
    ///   kind    float64 float32 int64 int32 int16 int8 uint64 uint32 uint16 uint8
    ///           string hex raw bool
    ///   :width  fixed byte width for <c>string</c> (zero padded / truncated), e.g. string:11
    ///   @offset byte offset INTO the attribute's value, for addressing one field of a struct
    ///
    /// Examples
    ///   <c>float64</c>        the whole attribute is one double
    ///   <c>float64@8</c>      the double starting at byte 8 (WorldLocation.y in RPR)
    ///   <c>uint16@4</c>       EntityIdentifier.entityNumber
    ///   <c>string:11</c>      RPR marking text
    ///   <c>hex</c>            compare the raw bytes verbatim - always available, never wrong
    /// </summary>
    public static class ValueCodec
    {
        // ---------------------------------------------------------------- spec ----

        public sealed class Spec
        {
            public string Kind = "hex";
            public int Offset = 0;        // -1 = none given
            public int Width = -1;        // for string / raw
            public bool HasOffset;

            public override string ToString()
            {
                return Kind + (Width >= 0 ? ":" + Width : "") + (HasOffset ? "@" + Offset : "");
            }
        }

        public static Spec ParseSpec(string datatype)
        {
            var s = new Spec();
            var t = (datatype ?? "").Trim();
            if (t.Length == 0) { s.Kind = "hex"; return s; }

            var at = t.IndexOf('@');
            if (at >= 0)
            {
                int off;
                if (int.TryParse(t.Substring(at + 1), NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out off))
                {
                    s.Offset = off;
                    s.HasOffset = true;
                }
                t = t.Substring(0, at);
            }

            var colon = t.IndexOf(':');
            if (colon >= 0)
            {
                int w;
                if (int.TryParse(t.Substring(colon + 1), NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out w)) s.Width = w;
                t = t.Substring(0, colon);
            }

            s.Kind = t.Trim().ToLowerInvariant();
            if (s.Kind.Length == 0) s.Kind = "hex";
            return s;
        }

        /// <summary>Byte width of a fixed-size kind, or -1 if variable.</summary>
        public static int SizeOf(string kind)
        {
            switch (kind)
            {
                case "float64":
                case "int64":
                case "uint64": return 8;
                case "float32":
                case "int32":
                case "uint32": return 4;
                case "int16":
                case "uint16": return 2;
                case "int8":
                case "uint8":
                case "bool": return 1;
                default: return -1;      // string / hex / raw
            }
        }

        // -------------------------------------------------------------- encode ----

        /// <summary>CSV text -&gt; wire bytes. Throws <see cref="FormatException"/> on bad input.</summary>
        public static byte[] Encode(string text, string datatype)
        {
            var spec = ParseSpec(datatype);
            var t = (text ?? "").Trim();

            switch (spec.Kind)
            {
                case "float64": return Be(BitConverter.GetBytes(Dbl(t)));
                case "float32": return Be(BitConverter.GetBytes((float)Dbl(t)));
                case "int64": return Be(BitConverter.GetBytes(I64(t)));
                case "uint64": return Be(BitConverter.GetBytes((ulong)I64(t)));
                case "int32": return Be(BitConverter.GetBytes((int)I64(t)));
                case "uint32": return Be(BitConverter.GetBytes((uint)I64(t)));
                case "int16": return Be(BitConverter.GetBytes((short)I64(t)));
                case "uint16": return Be(BitConverter.GetBytes((ushort)I64(t)));
                case "int8": return new[] { (byte)(sbyte)I64(t) };
                case "uint8": return new[] { (byte)I64(t) };
                case "bool": return new[] { (byte)(IsTrue(t) ? 1 : 0) };

                case "string":
                    {
                        var raw = Encoding.ASCII.GetBytes(t);
                        if (spec.Width < 0) return raw;
                        var padded = new byte[spec.Width];
                        Array.Copy(raw, padded, Math.Min(raw.Length, spec.Width));
                        return padded;
                    }

                case "hex":
                case "raw":
                    return FromHex(t);

                default:
                    throw new FormatException("unknown datatype '" + datatype + "'");
            }
        }

        // -------------------------------------------------------------- decode ----

        /// <summary>
        /// Wire bytes -&gt; readable text, honouring any <c>@offset</c> in the spec.
        /// Returns null when the bytes are too short for the requested field, which is itself
        /// a reportable result (the far side sent a shorter struct than the test expects).
        /// </summary>
        public static string Decode(byte[] value, string datatype)
        {
            var spec = ParseSpec(datatype);
            byte[] slice = Slice(value, spec);
            if (slice == null) return null;

            switch (spec.Kind)
            {
                case "float64": return BitConverter.ToDouble(Be(slice), 0).ToString("R", CultureInfo.InvariantCulture);
                case "float32": return BitConverter.ToSingle(Be(slice), 0).ToString("R", CultureInfo.InvariantCulture);
                case "int64": return BitConverter.ToInt64(Be(slice), 0).ToString(CultureInfo.InvariantCulture);
                case "uint64": return BitConverter.ToUInt64(Be(slice), 0).ToString(CultureInfo.InvariantCulture);
                case "int32": return BitConverter.ToInt32(Be(slice), 0).ToString(CultureInfo.InvariantCulture);
                case "uint32": return BitConverter.ToUInt32(Be(slice), 0).ToString(CultureInfo.InvariantCulture);
                case "int16": return BitConverter.ToInt16(Be(slice), 0).ToString(CultureInfo.InvariantCulture);
                case "uint16": return BitConverter.ToUInt16(Be(slice), 0).ToString(CultureInfo.InvariantCulture);
                case "int8": return ((sbyte)slice[0]).ToString(CultureInfo.InvariantCulture);
                case "uint8": return slice[0].ToString(CultureInfo.InvariantCulture);
                case "bool": return slice[0] != 0 ? "true" : "false";
                case "string": return Encoding.ASCII.GetString(slice).TrimEnd('\0');
                default: return ToHex(slice);
            }
        }

        /// <summary>Extract the bytes this spec addresses. Null if the value is too short.</summary>
        public static byte[] Slice(byte[] value, Spec spec)
        {
            if (value == null) return null;

            int offset = spec.HasOffset ? spec.Offset : 0;
            int size = SizeOf(spec.Kind);
            if (size < 0) size = spec.Width >= 0 ? spec.Width : value.Length - offset;

            if (offset < 0 || size < 0 || offset + size > value.Length) return null;
            if (offset == 0 && size == value.Length) return value;

            var slice = new byte[size];
            Array.Copy(value, offset, slice, 0, size);
            return slice;
        }

        // ------------------------------------------------------------- compare ----

        /// <summary>
        /// Compare an observed attribute value against an expected CSV cell.
        ///
        /// Numeric kinds honour <paramref name="tolerance"/>; everything else is exact. Comparing
        /// as HEX is always available and is the strictest possible check - use it when the point
        /// of the test is that the broker must not alter the bytes at all.
        /// </summary>
        public static bool Compare(byte[] actual, string expectedText, string datatype,
                                   double? tolerance, out string actualText, out string note)
        {
            note = null;
            actualText = null;

            if (actual == null) { note = "not received"; return false; }

            var spec = ParseSpec(datatype);
            var slice = Slice(actual, spec);
            if (slice == null)
            {
                actualText = ToHex(actual);
                note = "value is " + actual.Length + " bytes - too short for " + spec;
                return false;
            }

            actualText = Decode(actual, datatype);

            if (IsNumeric(spec.Kind))
            {
                double a, e;
                if (!double.TryParse(actualText, NumberStyles.Any, CultureInfo.InvariantCulture, out a) ||
                    !double.TryParse((expectedText ?? "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out e))
                {
                    note = "not numeric";
                    return false;
                }
                var tol = tolerance ?? 0.0;
                var delta = Math.Abs(a - e);
                if (delta <= tol) return true;

                note = "delta=" + delta.ToString("G6", CultureInfo.InvariantCulture) +
                       " tol=" + tol.ToString("G6", CultureInfo.InvariantCulture);
                return false;
            }

            // Byte-exact kinds: compare the ENCODED expectation, not the decoded text, so that
            // padding and trailing nulls are held to account too.
            byte[] want;
            try { want = Encode(expectedText, datatype); }
            catch (FormatException ex) { note = ex.Message; return false; }

            if (SameBytes(slice, want)) return true;

            note = "expected bytes " + ToHex(want) + ", got " + ToHex(slice);
            return false;
        }

        public static bool IsNumeric(string kind)
        {
            switch (kind)
            {
                case "float64":
                case "float32":
                case "int64":
                case "uint64":
                case "int32":
                case "uint32":
                case "int16":
                case "uint16":
                case "int8":
                case "uint8": return true;
                default: return false;
            }
        }

        // --------------------------------------------------------------- paths ----

        /// <summary>
        /// Split "WorldLocation.x" into the HLA attribute name and its sub-field path.
        /// The RTI only knows the attribute; the sub-field is ours to resolve.
        /// </summary>
        public static void SplitPath(string path, out string attribute, out string subField)
        {
            var p = (path ?? "").Trim();
            var dot = p.IndexOf('.');
            if (dot < 0) { attribute = p; subField = null; return; }
            attribute = p.Substring(0, dot);
            subField = p.Substring(dot + 1);
        }

        /// <summary>
        /// STOPGAP for common RPR structs, used only when a dotted path has no explicit
        /// <c>@offset</c> in the CSV. Real coverage comes from parsing the OMT (1.3) or FDD
        /// (Evolved) - REQ-09. Keeping this small and named makes it obvious what to delete
        /// once the parser lands.
        /// </summary>
        private static readonly Dictionary<string, string> KnownSubFields =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // WorldLocationStruct { float64 x, y, z }
                { "WorldLocation.x", "float64@0" },
                { "WorldLocation.y", "float64@8" },
                { "WorldLocation.z", "float64@16" },
                // EntityIdentifierStruct { uint16 site, application, entity }
                { "EntityIdentifier.site", "uint16@0" },
                { "EntityIdentifier.application", "uint16@2" },
                { "EntityIdentifier.entity", "uint16@4" },
                // VelocityVectorStruct / AccelerationVector { float32 x, y, z }
                { "VelocityVector.x", "float32@0" },
                { "VelocityVector.y", "float32@4" },
                { "VelocityVector.z", "float32@8" },
                { "Orientation.psi", "float32@0" },
                { "Orientation.theta", "float32@4" },
                { "Orientation.phi", "float32@8" },
            };

        /// <summary>
        /// Full byte size of a struct we know about, or -1. Used when a test writes only SOME
        /// sub-fields (say WorldLocation.x alone): without this the attribute would go on the wire
        /// 8 bytes long instead of 24 and the far side would reject or mis-parse it.
        /// Superseded by the OMT/FDD parser - REQ-09.
        /// </summary>
        public static int KnownStructSize(string attribute)
        {
            int max = -1;
            var prefix = (attribute ?? "").Trim() + ".";
            foreach (var kv in KnownSubFields)
            {
                if (!kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var s = ParseSpec(kv.Value);
                var size = SizeOf(s.Kind);
                if (size < 0) continue;
                var extent = s.Offset + size;
                if (extent > max) max = extent;
            }
            return max;
        }

        /// <summary>
        /// Work out the effective datatype for a CSV row. An explicit <c>@offset</c> always wins;
        /// otherwise a dotted path is looked up in the stopgap table above.
        /// Returns null when a sub-field is addressed that we cannot resolve - the caller should
        /// report that clearly rather than silently comparing the wrong bytes.
        /// </summary>
        public static string ResolveDatatype(string attributePath, string datatype)
        {
            var spec = ParseSpec(datatype);
            if (spec.HasOffset) return datatype;

            string attr, sub;
            SplitPath(attributePath, out attr, out sub);
            if (sub == null) return datatype;

            string mapped;
            if (KnownSubFields.TryGetValue(attr + "." + sub, out mapped))
            {
                // Let the CSV override the KIND while we supply the offset, so
                // "WorldLocation.x" + "float64" still works and stays explicit.
                var known = ParseSpec(mapped);
                var wanted = ParseSpec(datatype);
                var kind = string.IsNullOrEmpty(datatype) ? known.Kind : wanted.Kind;
                return kind + "@" + known.Offset;
            }
            return null;
        }

        // --------------------------------------------------------------- utils ----

        /// <summary>Reverse in place-safe: host (little-endian x64) &lt;-&gt; network (big-endian).</summary>
        private static byte[] Be(byte[] b)
        {
            var r = (byte[])b.Clone();
            Array.Reverse(r);
            return r;
        }

        public static string ToHex(byte[] b)
        {
            if (b == null) return "";
            var sb = new StringBuilder(b.Length * 2);
            for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public static byte[] FromHex(string s)
        {
            var t = (s ?? "").Trim().Replace(" ", "");
            if (t.Length % 2 != 0) throw new FormatException("hex must have an even number of digits: '" + s + "'");
            var b = new byte[t.Length / 2];
            for (int i = 0; i < b.Length; i++)
            {
                if (!byte.TryParse(t.Substring(i * 2, 2), NumberStyles.HexNumber,
                                   CultureInfo.InvariantCulture, out b[i]))
                    throw new FormatException("bad hex digits in '" + s + "'");
            }
            return b;
        }

        public static bool SameBytes(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static double Dbl(string s)
        {
            double d;
            if (!double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d))
                throw new FormatException("'" + s + "' is not a number");
            return d;
        }

        private static long I64(string s)
        {
            long v;
            var t = (s ?? "").Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                ulong u;
                if (ulong.TryParse(t.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out u))
                    return (long)u;
            }
            if (!long.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                throw new FormatException("'" + s + "' is not an integer");
            return v;
        }

        private static bool IsTrue(string s)
        {
            var t = (s ?? "").Trim().ToLowerInvariant();
            return t == "1" || t == "true" || t == "yes" || t == "y";
        }
    }
}
