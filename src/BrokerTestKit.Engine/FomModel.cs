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
        /// <summary>
        /// The FDD's array encoding, e.g. RPRlengthlessArray, HLAvariableArray, HLAfixedArray.
        ///
        /// It decides where element 0 begins. RPR's "lengthless" arrays carry only the elements -
        /// the FOM says so outright: "The array is encoded without array length". A standard
        /// HLAvariableArray puts a 4-byte count first, so treating the two alike would shift every
        /// element by four bytes and read each one straddling its neighbours.
        /// </summary>
        public string Encoding;
        public readonly List<FomField> Fields = new List<FomField>();
        /// <summary>Total byte size, or -1 when variable length.</summary>
        public int Size = -1;

        // ---- variantRecordData only -------------------------------------------------
        /// <summary>Field name of the discriminant, e.g. "DeadReckoningAlgorithm".</summary>
        public string Discriminant;
        /// <summary>Datatype of the discriminant, e.g. "DeadReckoningAlgorithmEnum8".</summary>
        public string DiscriminantType;
        /// <summary>
        /// enumerator -&gt; the fixed record that alternative carries, e.g.
        /// DRM_FPW -&gt; SpatialFPStruct. A variant has no layout until one is chosen, which is
        /// why a sheet has to declare which.
        /// </summary>
        public readonly Dictionary<string, string> Alternatives =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
            return Resolve(className, attributePath, null, out error);
        }

        /// <param name="variants">
        /// Attribute path -&gt; chosen alternative, e.g. {"Spatial": "DRM_FPW"}. A variant has no
        /// fixed layout until one is named, so without this a path that descends into one cannot
        /// be resolved at all.
        /// </param>
        public FieldRef Resolve(string className, string attributePath,
                                IDictionary<string, string> variants, out string error)
        {
            error = null;
            var key = className + "|" + attributePath + "|" + ChoiceKey(variants);
            FieldRef hit;
            if (_cache.TryGetValue(key, out hit)) return hit;

            var attrs = FindClass(className);
            if (attrs == null) { error = "class '" + className + "' is not in " + Describe(); return null; }

            var parts = (attributePath ?? "").Split('.');

            // A segment may carry an array subscript - "ArticulatedParametersArray[2]". Split the
            // name from the index once, here, so the walk below never has to think about it again.
            var names = new string[parts.Length];
            var index = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++) SplitIndex(parts[i], out names[i], out index[i]);

            string typeName;
            if (!attrs.TryGetValue(names[0], out typeName))
            {
                error = "attribute '" + names[0] + "' is not defined on class '" + className +
                        "' in " + Describe();
                return null;
            }

            int offset = 0;
            var typeOfCurrent = typeName;

            if (index[0] >= 0 && !StepIntoElement(ref typeOfCurrent, ref offset, index[0],
                                                  names[0], out error))
                return null;

            for (int i = 1; i < parts.Length; i++)
            {
                FomType t;
                Types.TryGetValue(typeOfCurrent, out t);

                if (t != null && t.Kind == FomKind.Variant)
                {
                    var reached = string.Join(".", parts, 0, i);

                    // The discriminant itself is an ordinary field at offset 0.
                    if (string.Equals(names[i], t.Discriminant, StringComparison.OrdinalIgnoreCase))
                    {
                        typeOfCurrent = t.DiscriminantType;
                        continue;
                    }

                    var chosen = Chosen(variants, reached);
                    if (string.IsNullOrEmpty(chosen))
                    {
                        var menu = new List<string>(t.Alternatives.Keys);
                        menu.Sort(StringComparer.OrdinalIgnoreCase);
                        error = "'" + reached + "' is a variant record (" + typeOfCurrent +
                                "); its layout depends on " + t.Discriminant +
                                ", so the sheet has to say which alternative it means - add " +
                                "\"# variant: " + reached + "=<one of " +
                                string.Join(", ", menu.ToArray()) + ">\"";
                        return null;
                    }

                    string alternative;
                    if (!t.Alternatives.TryGetValue(chosen, out alternative))
                    {
                        var menu = new List<string>(t.Alternatives.Keys);
                        menu.Sort(StringComparer.OrdinalIgnoreCase);
                        error = "'" + chosen + "' is not an alternative of " + typeOfCurrent +
                                ". Available: " + string.Join(", ", menu.ToArray());
                        return null;
                    }

                    offset += VariantPayloadOffset(t);
                    typeOfCurrent = alternative;

                    // "<path>.<enumerator>" names the alternative's payload as a whole. When the
                    // alternative carries a scalar rather than a record - RPR 2.0's
                    // RFModulationType=Amplitude is an enum - that is the only way to address it.
                    if (string.Equals(names[i], chosen, StringComparison.OrdinalIgnoreCase)) continue;

                    Types.TryGetValue(typeOfCurrent, out t);
                }

                if (t == null || t.Kind != FomKind.FixedRecord)
                {
                    error = "'" + string.Join(".", parts, 0, i) + "' is of type " + typeOfCurrent +
                            ", which has no field '" + parts[i] + "'";
                    return null;
                }

                FomField field = null;
                foreach (var f in t.Fields)
                    if (string.Equals(f.Name, names[i], StringComparison.OrdinalIgnoreCase)) { field = f; break; }

                if (field == null)
                {
                    var available = new List<string>();
                    foreach (var f in t.Fields) available.Add(f.Name);
                    error = "type " + typeOfCurrent + " has no field '" + parts[i] +
                            "'. Available: " + string.Join(", ", available.ToArray());
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

                if (index[i] >= 0 && !StepIntoElement(ref typeOfCurrent, ref offset, index[i],
                                                      string.Join(".", parts, 0, i + 1), out error))
                    return null;
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
            return Resolve(className, attributePath, null, out ignored);
        }

        public FieldRef Resolve(string className, string attributePath,
                                IDictionary<string, string> variants)
        {
            string ignored;
            return Resolve(className, attributePath, variants, out ignored);
        }

        /// <summary>
        /// Byte size of a WHOLE attribute as it goes on the wire, or -1 when the FOM cannot fix
        /// one (a variable-length array, or a variant with no alternative named).
        ///
        /// <see cref="Resolve"/> answers for a FIELD; this answers for the attribute that contains
        /// it, which is what a publisher needs when a sheet fills only some of its sub-fields. A
        /// variant record has no size until an alternative is chosen, so the sheet's choices are
        /// part of the question - which is why this takes them.
        /// </summary>
        public int AttributeWireSize(string className, string attribute,
                                     IDictionary<string, string> variants)
        {
            var members = FindClass(className);
            if (members == null || string.IsNullOrEmpty(attribute)) return -1;

            string typeName;
            if (!members.TryGetValue(attribute, out typeName)) return -1;

            FomType t;
            if (Types.TryGetValue(typeName, out t) && t != null && t.Kind == FomKind.Variant)
            {
                var chosen = Chosen(variants, attribute);
                string alternative;
                if (string.IsNullOrEmpty(chosen) ||
                    !t.Alternatives.TryGetValue(chosen, out alternative)) return -1;

                var payload = SizeOf(alternative);
                return payload < 0 ? -1 : VariantPayloadOffset(t) + payload;
            }

            return SizeOf(typeName);
        }

        /// <summary>
        /// Split "Name[3]" into its name and index. Index is -1 when the segment carries none,
        /// which is every ordinary field.
        /// </summary>
        private static void SplitIndex(string segment, out string name, out int index)
        {
            name = segment ?? "";
            index = -1;

            var open = name.IndexOf('[');
            if (open <= 0 || !name.EndsWith("]", StringComparison.Ordinal)) return;

            int n;
            var inner = name.Substring(open + 1, name.Length - open - 2);
            if (!int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < 0)
                return;                                    // "[x]" is not an index; leave it alone

            name = name.Substring(0, open);
            index = n;
        }

        /// <summary>
        /// Move the walk from an array to one of its elements, advancing the offset past the
        /// element count (where the encoding has one) and the elements before it.
        /// </summary>
        private bool StepIntoElement(ref string typeOfCurrent, ref int offset, int index,
                                     string reached, out string error)
        {
            error = null;

            FomType array;
            if (!Types.TryGetValue(typeOfCurrent, out array) || array.Kind != FomKind.Array)
            {
                error = "'" + reached + "' is of type " + typeOfCurrent +
                        ", which is not an array, so it cannot be indexed";
                return false;
            }

            int elementSize;
            string why;
            if (!ArrayIndexable(array, out elementSize, out why)) { error = why; return false; }

            // A fixed-cardinality array states how many it holds; a dynamic one does not, and the
            // sheet's own columns decide - so only the fixed case can be range-checked here.
            int cardinality;
            if (int.TryParse(array.Cardinality, NumberStyles.Integer, CultureInfo.InvariantCulture,
                             out cardinality) && index >= cardinality)
            {
                error = "'" + reached + "' holds " + cardinality + " element(s), so [" + index +
                        "] is out of range";
                return false;
            }

            offset += ArrayCountPrefix(array) + index * elementSize;
            typeOfCurrent = array.ElementType;
            return true;
        }

        /// <summary>The alternative chosen for this path, or null.</summary>
        private static string Chosen(IDictionary<string, string> variants, string path)
        {
            if (variants == null || string.IsNullOrEmpty(path)) return null;
            foreach (var kv in variants)
                if (string.Equals(kv.Key, path, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        /// <summary>
        /// Choices fold into the resolution cache key. Without that, one sheet's "Spatial is an
        /// opaque blob" answer would be served to the next sheet, which declared an alternative.
        /// </summary>
        private static string ChoiceKey(IDictionary<string, string> variants)
        {
            if (variants == null || variants.Count == 0) return "";
            var parts = new List<string>();
            foreach (var kv in variants) parts.Add(kv.Key + "=" + kv.Value);
            parts.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(";", parts.ToArray());
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
                case FomKind.Variant:
                    {
                        // The whole record aligns to the widest thing it could hold, so the gap
                        // after the discriminant is the same whichever alternative is in play.
                        int a = AlignmentOf(t.DiscriminantType, depth + 1);
                        foreach (var kv in t.Alternatives)
                        {
                            var aa = AlignmentOf(kv.Value, depth + 1);
                            if (aa > a) a = aa;
                        }
                        return a;
                    }
                default: return 1;
            }
        }

        /// <summary>
        /// Byte at which a variant's chosen alternative begins: the discriminant, then padding up
        /// to the record's alignment.
        ///
        /// Measured, for the case we have on the wire: RPR 2.0's Spatial carries a 1-byte
        /// DeadReckoningAlgorithm and its payload starts at 8, because SpatialFPStruct opens with
        /// three float64. The other variants in RPR 2.0 have 2-byte discriminants and are
        /// COMPUTED by this rule rather than measured.
        /// </summary>
        public int VariantPayloadOffset(FomType variant)
        {
            if (variant == null) return 0;
            return Pad(SizeOf(variant.DiscriminantType), AlignmentOf(variant.Name));
        }

        /// <summary>
        /// Bytes that sit BEFORE element 0 of an array - the element count, where the encoding
        /// carries one.
        ///
        /// RPR's lengthless arrays carry no count at all, which is what makes them indexable from
        /// a sheet: element i is simply at i * elementSize. A standard HLAvariableArray prefixes a
        /// 4-byte count, and <see cref="ArrayIndexable"/> refuses those rather than quietly
        /// writing elements the far side would mis-read for want of that count.
        /// </summary>
        public int ArrayCountPrefix(FomType array)
        {
            if (array == null) return 0;
            return string.Equals(array.Encoding, "HLAvariableArray",
                                 StringComparison.OrdinalIgnoreCase) ? 4 : 0;
        }

        /// <summary>
        /// Whether a sheet may address this array element by element, and why not when it may not.
        ///
        /// Indexing needs two things: a fixed element size, so element i has a computable offset,
        /// and an encoding this kit can actually PUBLISH. HLAvariableArray fails the second -
        /// writing its elements without synthesising the 4-byte count would put a value on the
        /// wire that decodes to nothing sensible, so it is refused out loud instead.
        /// </summary>
        public bool ArrayIndexable(FomType array, out int elementSize, out string why)
        {
            elementSize = -1;
            why = null;

            if (array == null || array.Kind != FomKind.Array) { why = "not an array"; return false; }

            if (string.Equals(array.Encoding, "HLAvariableArray", StringComparison.OrdinalIgnoreCase))
            {
                why = "'" + array.Name + "' is an HLAvariableArray - its 4-byte element count is " +
                      "part of the value and this kit does not synthesise it yet, so it stays hex";
                return false;
            }

            elementSize = SizeOf(array.ElementType);
            if (elementSize <= 0)
            {
                why = "element type '" + array.ElementType + "' of '" + array.Name +
                      "' has no fixed size, so element offsets cannot be computed";
                elementSize = -1;
                return false;
            }
            return true;
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

        /// <summary>
        /// The shape of a type on the wire, without its names: "uint8", "{float64,float64,float64}",
        /// "[uint8 x 11]". Two FOMs that spell a type differently (OMT <c>boolean</c>, RPR 2.0
        /// <c>RPRboolean</c>) but lay out the same bytes get the same signature, which is what
        /// lets <see cref="FomDiff"/> tell a rename from a real change. Endianness is ignored:
        /// HLA 1.3 has no way to state it.
        /// </summary>
        public string WireSignature(string typeName)
        {
            var tokens = new List<string>();
            Flatten(typeName, 0, tokens);
            // run-length for reading: "uint8*12" rather than twelve uint8s
            var sb = new StringBuilder();
            for (int i = 0; i < tokens.Count; )
            {
                int j = i;
                while (j < tokens.Count && tokens[j] == tokens[i]) j++;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(tokens[i]);
                if (j - i > 1) sb.Append('*').Append((j - i).ToString(CultureInfo.InvariantCulture));
                i = j;
            }
            return sb.ToString();
        }

        /// <summary>
        /// The type as a flat list of wire tokens. Records are inlined and fixed arrays expanded,
        /// so <c>{octet, octet[11]}</c> and twelve octet fields meet; a one-field record holding
        /// text and a dynamic char array both come out as "text" (OMT <c>RTIObjectIdStruct</c>
        /// against RPR 2.0 <c>RTIobjectId</c>).
        /// </summary>
        private void Flatten(string typeName, int depth, List<string> outp)
        {
            if (string.IsNullOrEmpty(typeName)) { outp.Add("?"); return; }
            if (depth > 12 || outp.Count > 4096) { outp.Add("..."); return; }
            var basic = BasicNameOf(typeName);
            var k = KindOfBasic(basic);
            if (k != null)
            {
                outp.Add(k != "string" ? k : BuiltinSize(basic) > 0 ? "char" : "text");
                return;
            }
            FomType t;
            if (!Types.TryGetValue(typeName, out t) || t == null) { outp.Add("?" + typeName); return; }
            switch (t.Kind)
            {
                case FomKind.FixedRecord:
                    foreach (var f in t.Fields) Flatten(f.DataType, depth + 1, outp);
                    return;
                case FomKind.Array:
                    int n;
                    var fixedCount = int.TryParse(t.Cardinality, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0;
                    var elem = new List<string>();
                    Flatten(t.ElementType, depth + 1, elem);
                    if (fixedCount && n * elem.Count <= 4096) { for (int i = 0; i < n; i++) outp.AddRange(elem); return; }
                    var e = string.Join(",", elem.ToArray());
                    outp.Add(e == "char" ? "text" : "[" + e + "]*");
                    return;
                case FomKind.Variant:
                    var d = new List<string>();
                    Flatten(t.DiscriminantType, depth + 1, d);
                    outp.Add("variant<" + string.Join(",", d.ToArray()) + "|" + t.Alternatives.Count + ">");
                    return;
                default:
                    outp.Add("?" + typeName);
                    return;
            }
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
