using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BrokerTestKit.Engine
{
    public enum FomKind { Basic, Simple, Enumerated, Array, FixedRecord, Variant, Unknown }

    /// <summary>One field of a fixed record, with its resolved byte offset within that record.</summary>
    public sealed class FomField
    {
        public string Name;
        public string DataType;
        public int Offset = -1;
        public int Size = -1;

        public override string ToString()
        {
            return Name + ":" + DataType + "@" + Offset + "[" + Size + "]";
        }
    }

    /// <summary>A datatype as declared in an OMT (HLA 1.3) or FDD (Evolved).</summary>
    public sealed class FomType
    {
        public string Name;
        public FomKind Kind = FomKind.Unknown;
        /// <summary>simpleData / enumeratedData: the underlying basic type.</summary>
        public string Representation;
        /// <summary>arrayData: element type + cardinality ("Dynamic" or a number).</summary>
        public string ElementType;
        public string Cardinality;
        public readonly List<FomField> Fields = new List<FomField>();
        /// <summary>Total byte size, or -1 when variable length.</summary>
        public int Size = -1;

        public override string ToString() { return Name + " (" + Kind + ", " + Size + " bytes)"; }
    }

    /// <summary>
    /// Where one addressed field lives inside an attribute's bytes, and how to read it.
    /// This is what replaces the CSV's hand-written <c>datatype</c> column.
    /// </summary>
    public sealed class FieldRef
    {
        public string AttributePath;
        public string TypeName;
        /// <summary>A <see cref="ValueCodec"/> kind: float64, uint16, string, hex, …</summary>
        public string Kind;
        public int Offset;
        public int Size = -1;         // -1 = variable
        public bool IsVariable { get { return Size < 0; } }

        /// <summary>The datatype string the codec understands, e.g. "float64@8".</summary>
        public string CodecSpec
        {
            get
            {
                var s = Kind;
                if (Kind == "string" && Size > 0) s += ":" + Size.ToString(CultureInfo.InvariantCulture);
                if (Offset != 0) s += "@" + Offset.ToString(CultureInfo.InvariantCulture);
                return s;
            }
        }

        public override string ToString()
        {
            return AttributePath + " -> " + TypeName + " as " + CodecSpec;
        }
    }

    /// <summary>
    /// The FOM, read as DATA (REQ-09). Built by <see cref="OmtParser"/> for HLA 1.3 or
    /// <see cref="FddParser"/> for Evolved, and asked at runtime where a field lives.
    ///
    /// This is the piece that lets a test say <c>WorldLocation.x</c> and nothing else - no byte
    /// offset, no datatype. The FOM already states both, so the test author should never restate
    /// them; doing so was a stopgap that leaked a missing parser into the user's CSV.
    /// </summary>
    public sealed class FomModel
    {
        public string SourceFile;
        public string Flavour;      // "OMT (HLA 1.3)" or "FDD (1516-2010)"

        public readonly Dictionary<string, FomType> Types =
            new Dictionary<string, FomType>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Fully-qualified class name -&gt; attribute -&gt; datatype name (inherited included).</summary>
        public readonly Dictionary<string, Dictionary<string, string>> Classes =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Interaction class name -&gt; parameter -&gt; datatype name (inherited included).
        /// Interactions are addressed exactly like object classes everywhere downstream, so one
        /// CSV row can be one entity OR one interaction with no change to the format.
        /// </summary>
        public readonly Dictionary<string, Dictionary<string, string>> Interactions =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, FieldRef> _cache =
            new Dictionary<string, FieldRef>(StringComparer.OrdinalIgnoreCase);

        // -------------------------------------------------------------- lookup ----

        /// <summary>
        /// Find a class by short OR fully-qualified name. FDDs qualify
        /// (<c>HLAobjectRoot.BaseEntity.…</c>), OMTs do not, and test authors should not have to
        /// care which they are looking at.
        /// </summary>
        public Dictionary<string, string> FindClass(string className)
        {
            var hit = Lookup(Classes, className);
            return hit ?? Lookup(Interactions, className);
        }

        /// <summary>True when the name refers to an interaction rather than an object class.</summary>
        public bool IsInteraction(string name)
        {
            return Lookup(Classes, name) == null && Lookup(Interactions, name) != null;
        }

        /// <summary>
        /// The name the RTI knows this class by - which is not always the name the FOM file, or
        /// the test author, uses.
        ///
        /// An FDD stores classes already qualified (<c>HLAobjectRoot.BaseEntity.…</c>) and that is
        /// exactly what <c>getObjectClassHandle</c> wants. An OMT stores them WITHOUT the root
        /// (<c>BaseEntity.PhysicalEntity.Platform.GroundVehicle</c>) while the HLA 1.3 RTI still
        /// wants <c>ObjectRoot.</c> in front. So a CSV saying "GroundVehicle" has to become two
        /// different strings depending on which side it is going to, and getting that wrong shows
        /// up as NameNotFound - or worse, as a federate that joins and then sees nothing.
        ///
        /// Returns null when the FOM does not define the name at all.
        /// </summary>
        public string RtiClassName(string classOrInteraction)
        {
            if (string.IsNullOrEmpty(classOrInteraction)) return null;

            var interaction = Lookup(Classes, classOrInteraction) == null;
            var key = LookupKey(interaction ? Interactions : Classes, classOrInteraction);
            if (key == null) return null;

            var root = interaction ? "InteractionRoot." : "ObjectRoot.";
            if (key.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return key;
            if (key.StartsWith("HLA", StringComparison.OrdinalIgnoreCase)) return key;   // FDD: already rooted
            return root + key;
        }

        /// <summary>As <see cref="Lookup"/>, but returns the KEY rather than the attribute table.</summary>
        private static string LookupKey(
            Dictionary<string, Dictionary<string, string>> table, string className)
        {
            if (string.IsNullOrEmpty(className)) return null;
            if (table.ContainsKey(className))
            {
                foreach (var kv in table)                       // recover the stored spelling
                    if (string.Equals(kv.Key, className, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            }
            foreach (var kv in table)
            {
                var dot = kv.Key.LastIndexOf('.');
                var leaf = dot < 0 ? kv.Key : kv.Key.Substring(dot + 1);
                if (string.Equals(leaf, className, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            }
            foreach (var kv in table)
                if (kv.Key.EndsWith("." + className, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            return null;
        }

        private static Dictionary<string, string> Lookup(
            Dictionary<string, Dictionary<string, string>> table, string className)
        {
            if (string.IsNullOrEmpty(className)) return null;

            Dictionary<string, string> attrs;
            if (table.TryGetValue(className, out attrs)) return attrs;

            // match on the last path segment, then on any suffix
            foreach (var kv in table)
            {
                var name = kv.Key;
                var dot = name.LastIndexOf('.');
                var leaf = dot < 0 ? name : name.Substring(dot + 1);
                if (string.Equals(leaf, className, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
            foreach (var kv in table)
                if (kv.Key.EndsWith("." + className, StringComparison.OrdinalIgnoreCase)) return kv.Value;

            return null;
        }

        public string[] AttributesOf(string className)
        {
            var attrs = FindClass(className);
            if (attrs == null) return new string[0];
            var a = new string[attrs.Count];
            attrs.Keys.CopyTo(a, 0);
            Array.Sort(a, StringComparer.OrdinalIgnoreCase);
            return a;
        }

        /// <summary>
        /// Resolve "WorldLocation.x" on a class to a concrete (offset, size, kind).
        /// Returns null - with a reason - when the FOM does not define it, which is a far better
        /// outcome than silently comparing the wrong bytes.
        /// </summary>
        public FieldRef Resolve(string className, string attributePath, out string error)
        {
            error = null;
            var key = className + "|" + attributePath;
            FieldRef hit;
            if (_cache.TryGetValue(key, out hit)) return hit;

            var attrs = FindClass(className);
            if (attrs == null) { error = "class '" + className + "' is not in " + Describe(); return null; }

            var parts = (attributePath ?? "").Split('.');
            string typeName;
            if (!attrs.TryGetValue(parts[0], out typeName))
            {
                error = "attribute '" + parts[0] + "' is not defined on class '" + className +
                        "' in " + Describe();
                return null;
            }

            int offset = 0;
            var typeOfCurrent = typeName;

            for (int i = 1; i < parts.Length; i++)
            {
                FomType t;
                if (!Types.TryGetValue(typeOfCurrent, out t) || t.Kind != FomKind.FixedRecord)
                {
                    error = "'" + string.Join(".", parts, 0, i) + "' is of type " + typeOfCurrent +
                            ", which has no field '" + parts[i] + "'";
                    return null;
                }

                FomField field = null;
                foreach (var f in t.Fields)
                    if (string.Equals(f.Name, parts[i], StringComparison.OrdinalIgnoreCase)) { field = f; break; }

                if (field == null)
                {
                    var names = new List<string>();
                    foreach (var f in t.Fields) names.Add(f.Name);
                    error = "type " + typeOfCurrent + " has no field '" + parts[i] +
                            "'. Available: " + string.Join(", ", names.ToArray());
                    return null;
                }
                if (field.Offset < 0)
                {
                    error = "field '" + parts[i] + "' of " + typeOfCurrent +
                            " follows a variable-length field, so its offset is not fixed";
                    return null;
                }
                offset += field.Offset;
                typeOfCurrent = field.DataType;
            }

            var reference = new FieldRef
            {
                AttributePath = attributePath,
                TypeName = typeOfCurrent,
                Offset = offset,
                Size = SizeOf(typeOfCurrent),
                Kind = KindOf(typeOfCurrent),
            };
            _cache[key] = reference;
            return reference;
        }

        public FieldRef Resolve(string className, string attributePath)
        {
            string ignored;
            return Resolve(className, attributePath, out ignored);
        }

        public string Describe()
        {
            return (Flavour ?? "FOM") + " '" + (SourceFile ?? "?") + "'";
        }

        // ------------------------------------------------------ size / kind ------

        /// <summary>Byte size of a named type, or -1 if variable / unknown.</summary>
        public int SizeOf(string typeName)
        {
            int builtin = BuiltinSize(typeName);
            if (builtin != 0) return builtin;

            FomType t;
            if (!Types.TryGetValue(typeName, out t)) return -1;
            if (t.Size >= 0) return t.Size;

            switch (t.Kind)
            {
                case FomKind.Basic: return t.Size;
                case FomKind.Simple:
                case FomKind.Enumerated: return SizeOf(t.Representation);
                case FomKind.Array:
                    {
                        int n;
                        if (!int.TryParse(t.Cardinality, NumberStyles.Integer,
                                          CultureInfo.InvariantCulture, out n)) return -1;
                        var elem = SizeOf(t.ElementType);
                        return elem < 0 ? -1 : elem * n;
                    }
                case FomKind.FixedRecord:
                    {
                        // Fields do NOT sit end to end - each one starts on a boundary that suits
                        // its own type, and the gap counts towards the record's size.
                        int total = 0;
                        foreach (var f in t.Fields)
                        {
                            total = Pad(total, AlignmentOf(f.DataType));
                            var fs = SizeOf(f.DataType);
                            if (fs < 0) return -1;
                            total += fs;
                        }
                        return total;
                    }
                default: return -1;
            }
        }

        /// <summary>
        /// The byte boundary a value of this type has to start on.
        ///
        /// A basic type aligns to its own width; a record aligns to the widest thing inside it.
        /// This is what puts the three bytes of nothing between IsFrozen and Orientation inside
        /// RPR 2.0's SpatialFPStruct - a gap the FOM never states outright but every federate
        /// obeys, so a harness that lays fields end to end reads every field after it from the
        /// wrong place.
        /// </summary>
        public int AlignmentOf(string typeName) { return AlignmentOf(typeName, 0); }

        private int AlignmentOf(string typeName, int depth)
        {
            if (depth > 16) return 1;

            int builtin = BuiltinSize(typeName);
            if (builtin > 0) return builtin;

            FomType t;
            if (!Types.TryGetValue(typeName, out t)) return 1;

            switch (t.Kind)
            {
                case FomKind.Basic: return t.Size > 0 ? t.Size : 1;
                case FomKind.Simple:
                case FomKind.Enumerated: return AlignmentOf(t.Representation, depth + 1);
                case FomKind.Array: return AlignmentOf(t.ElementType, depth + 1);
                case FomKind.FixedRecord:
                    {
                        int a = 1;
                        foreach (var f in t.Fields)
                        {
                            var fa = AlignmentOf(f.DataType, depth + 1);
                            if (fa > a) a = fa;
                        }
                        return a;
                    }
                default: return 1;
            }
        }

        /// <summary>Round an offset up to the next multiple of <paramref name="align"/>.</summary>
        private static int Pad(int offset, int align)
        {
            if (align <= 1) return offset;
            var over = offset % align;
            return over == 0 ? offset : offset + (align - over);
        }

        /// <summary>Compute and store field offsets for every fixed record. Called after parsing.</summary>
        public void ComputeOffsets()
        {
            foreach (var t in Types.Values)
            {
                if (t.Kind != FomKind.FixedRecord) continue;
                int offset = 0;
                bool fixedSoFar = true;
                foreach (var f in t.Fields)
                {
                    f.Size = SizeOf(f.DataType);
                    if (fixedSoFar) offset = Pad(offset, AlignmentOf(f.DataType));
                    f.Offset = fixedSoFar ? offset : -1;
                    if (f.Size < 0) fixedSoFar = false;      // everything after is unaddressable
                    else if (fixedSoFar) offset += f.Size;
                }

                // NOTE: no padding is added after the LAST field. That is not an oversight - it is
                // what the wire showed. RPR 2.0's SpatialFPStruct aligns to 8 and its fields end at
                // 52; a trailing pad would make it 56 and the whole Spatial value 64 bytes. The
                // value we captured off federation B is 60 (8 for the discriminant + 52). Measured,
                // not assumed.
            }
            foreach (var t in Types.Values)
                if (t.Size < 0) t.Size = SizeOf(t.Name);
        }

        /// <summary>Map a FOM type onto a <see cref="ValueCodec"/> kind.</summary>
        public string KindOf(string typeName)
        {
            var basic = BasicNameOf(typeName);
            var k = KindOfBasic(basic);
            if (k != null) return k;

            FomType t;
            if (Types.TryGetValue(typeName, out t))
            {
                if (t.Kind == FomKind.Array)
                {
                    var elem = BasicNameOf(t.ElementType);
                    // Char arrays are text; anything else is opaque bytes.
                    if (elem != null && (elem.IndexOf("char", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         elem.IndexOf("ASCII", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         string.Equals(elem, "string", StringComparison.OrdinalIgnoreCase)))
                        return "string";
                    return "hex";
                }
            }
            // Structs and anything unresolved compare as raw bytes - never wrong, just strict.
            return "hex";
        }

        /// <summary>Follow simple/enumerated indirection down to the underlying basic type name.</summary>
        private string BasicNameOf(string typeName)
        {
            var seen = 0;
            var current = typeName;
            while (current != null && seen++ < 16)
            {
                if (BuiltinSize(current) != 0) return current;
                FomType t;
                if (!Types.TryGetValue(current, out t)) return current;
                if (t.Kind == FomKind.Basic) return t.Name;
                if (t.Kind == FomKind.Simple || t.Kind == FomKind.Enumerated) { current = t.Representation; continue; }
                return null;
            }
            return null;
        }

        // ------------------------------------------------------------ built-ins --

        /// <summary>
        /// Sizes for the standard HLA/OMT primitives, so a MIM is not required just to know that
        /// HLAfloat64BE is eight bytes. Returns 0 when the name is not a known primitive.
        /// </summary>
        public static int BuiltinSize(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            switch (name.Trim().ToLowerInvariant())
            {
                // ---- IEEE 1516 basic data representations (HLAstandardMIM)
                case "hlaoctet": case "hlabyte": case "hlaasciichar": return 1;
                case "hlaoctetpairbe": case "hlaoctetpairle":
                case "hlainteger16be": case "hlainteger16le":
                case "hlaunicodechar": return 2;
                case "hlainteger32be": case "hlainteger32le":
                case "hlafloat32be": case "hlafloat32le": return 4;
                case "hlainteger64be": case "hlainteger64le":
                case "hlafloat64be": case "hlafloat64le": return 8;

                // ---- RPR basic data representations
                case "rprunsignedinteger8": case "rprunsignedinteger8be": return 1;
                case "rprunsignedinteger16be": case "rprunsignedinteger16le": return 2;
                case "rprunsignedinteger32be": case "rprunsignedinteger32le": return 4;
                case "rprunsignedinteger64be": return 8;

                // ---- HLA 1.3 OMT primitives
                case "octet": case "char": case "boolean": return 1;
                case "short": case "unsigned short": return 2;
                case "long": case "unsigned long": case "float": case "hlacount": case "handle": return 4;
                case "double": case "unsigned long long": case "long long": case "timetype": return 8;

                default: return 0;
            }
        }

        private static string KindOfBasic(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            switch (name.Trim().ToLowerInvariant())
            {
                case "hlafloat64be": case "hlafloat64le": case "double": case "timetype": return "float64";
                case "hlafloat32be": case "hlafloat32le": case "float": return "float32";
                case "hlainteger64be": case "hlainteger64le": case "long long": return "int64";
                case "rprunsignedinteger64be": case "unsigned long long": return "uint64";
                case "hlainteger32be": case "hlainteger32le": case "long": return "int32";
                case "rprunsignedinteger32be": case "rprunsignedinteger32le":
                case "unsigned long": case "hlacount": case "handle": return "uint32";
                case "hlainteger16be": case "hlainteger16le": case "short": return "int16";
                case "rprunsignedinteger16be": case "rprunsignedinteger16le":
                case "unsigned short": case "hlaoctetpairbe": case "hlaoctetpairle": return "uint16";
                case "hlaoctet": case "hlabyte": case "octet": case "boolean":
                case "rprunsignedinteger8": case "rprunsignedinteger8be": return "uint8";
                // "string" is the HLA 1.3 OMT's own spelling for text, and it was missing
                // here - so RTIObjectIdStruct.ID, which is literally (DataType "string"),
                // fell through to "hex" and the CSV asked the author for raw bytes to
                // express an object INSTANCE NAME. The FDD side already got this right
                // via HLAASCIIchar, which is why only the 1.3 sheets were affected.
                case "hlaasciichar": case "char": case "string":
                case "hlaasciistring": case "hlaunicodestring": return "string";
                default: return null;
            }
        }

        // ------------------------------------------------------------- summary ---

        public string Summary()
        {
            var sb = new StringBuilder();
            sb.Append(Describe()).Append(": ")
              .Append(Classes.Count).Append(" classes, ")
              .Append(Interactions.Count).Append(" interactions, ")
              .Append(Types.Count).Append(" datatypes");
            return sb.ToString();
        }
    }
}
