using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BrokerTestKit.Engine
{
    /// <summary>
    /// Reads an IEEE 1516-2010 FDD / FOM module (<c>.xml</c>) into a <see cref="FomModel"/>.
    ///
    /// The FDD carries structure AND datatypes, so one file is enough for Evolved - unlike
    /// HLA 1.3, which needs the .fed for structure and the .omt for datatypes (REQ-01).
    /// </summary>
    public static class FddParser
    {
        public static FomModel Parse(params string[] paths)
        {
            var model = new FomModel { Flavour = "FDD (1516-2010)" };
            var names = new List<string>();

            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (!File.Exists(path)) throw new FileNotFoundException("FDD not found: " + path, path);
                names.Add(Path.GetFileName(path));

                var doc = XDocument.Load(path);
                var ns = doc.Root.Name.Namespace;

                ReadDataTypes(model, doc.Root, ns);

                var objects = doc.Root.Element(ns + "objects");
                if (objects != null)
                    foreach (var oc in objects.Elements(ns + "objectClass"))
                        ReadClass(model, oc, ns, "", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

                var interactions = doc.Root.Element(ns + "interactions");
                if (interactions != null)
                    foreach (var ic in interactions.Elements(ns + "interactionClass"))
                        ReadInteraction(model, ic, ns, "", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            }

            // Merging alone is not enough: a class can be DEFINED in a later module than the one
            // that defines its parent (MAK_GroundVehicle lives in MAK-Physical-2_evolved.xml, its
            // ancestors in the base RPR FDD). Whatever order the files arrive in, settle
            // inheritance once at the end.
            Inherit(model.Classes);
            Inherit(model.Interactions);

            model.SourceFile = string.Join(" + ", names.ToArray());
            model.ComputeOffsets();
            return model;
        }

        /// <summary>
        /// Push every ancestor's attributes down into its descendants, shallowest first so a
        /// parent is complete before its children read it. Names are dotted paths, so the class
        /// tree is already in the keys - no second structure to keep in step.
        /// </summary>
        private static void Inherit(Dictionary<string, Dictionary<string, string>> table)
        {
            var keys = new List<string>(table.Keys);
            keys.Sort(delegate(string x, string y)
            {
                var d = Depth(x).CompareTo(Depth(y));
                return d != 0 ? d : string.CompareOrdinal(x, y);
            });

            foreach (var key in keys)
            {
                var dot = key.LastIndexOf('.');
                if (dot < 0) continue;

                Dictionary<string, string> parent;
                if (!table.TryGetValue(key.Substring(0, dot), out parent)) continue;

                var mine = table[key];
                foreach (var kv in parent)
                    if (!mine.ContainsKey(kv.Key)) mine[kv.Key] = kv.Value;
            }
        }

        private static int Depth(string dotted)
        {
            int n = 0;
            for (int i = 0; i < dotted.Length; i++) if (dotted[i] == '.') n++;
            return n;
        }

        private static void ReadDataTypes(FomModel model, XElement root, XNamespace ns)
        {
            var dts = root.Element(ns + "dataTypes");
            if (dts == null) return;

            Each(dts, ns, "basicDataRepresentations", "basicData", e =>
            {
                var t = new FomType { Name = Val(e, ns, "name"), Kind = FomKind.Basic };
                int bits;
                if (int.TryParse(Val(e, ns, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out bits))
                    t.Size = bits / 8;                       // FDD states size in BITS
                Add(model, t);
            });

            Each(dts, ns, "simpleDataTypes", "simpleData", e => Add(model, new FomType
            {
                Name = Val(e, ns, "name"),
                Kind = FomKind.Simple,
                Representation = Val(e, ns, "representation"),
            }));

            Each(dts, ns, "enumeratedDataTypes", "enumeratedData", e => Add(model, new FomType
            {
                Name = Val(e, ns, "name"),
                Kind = FomKind.Enumerated,
                Representation = Val(e, ns, "representation"),
            }));

            Each(dts, ns, "arrayDataTypes", "arrayData", e => Add(model, new FomType
            {
                Name = Val(e, ns, "name"),
                Kind = FomKind.Array,
                ElementType = Val(e, ns, "dataType"),
                Cardinality = Val(e, ns, "cardinality"),
            }));

            Each(dts, ns, "fixedRecordDataTypes", "fixedRecordData", e =>
            {
                var t = new FomType { Name = Val(e, ns, "name"), Kind = FomKind.FixedRecord };
                foreach (var f in e.Elements(ns + "field"))
                    t.Fields.Add(new FomField { Name = Val(f, ns, "name"), DataType = Val(f, ns, "dataType") });
                Add(model, t);
            });

            // A variant has no size of its own - its layout depends on the discriminant - but
            // the FDD states the whole menu, and throwing that away is what forced test authors to
            // paste raw bytes for Spatial. Keep the discriminant and every enumerator -> record
            // mapping; a sheet then names one alternative and the layout is ordinary again.
            Each(dts, ns, "variantRecordDataTypes", "variantRecordData", e =>
            {
                var v = new FomType
                {
                    Name = Val(e, ns, "name"),
                    Kind = FomKind.Variant,
                    Discriminant = Val(e, ns, "discriminant"),
                    DiscriminantType = Val(e, ns, "dataType"),
                };
                foreach (var a in e.Elements(ns + "alternative"))
                {
                    var enumerator = Val(a, ns, "enumerator");
                    var carries = Val(a, ns, "dataType");
                    if (!string.IsNullOrEmpty(enumerator) && !string.IsNullOrEmpty(carries))
                        v.Alternatives[enumerator] = carries;
                }
                Add(model, v);
            });
        }

        private static void ReadClass(FomModel model, XElement oc, XNamespace ns,
                                      string prefix, Dictionary<string, string> inherited)
        {
            var name = Val(oc, ns, "name");
            if (string.IsNullOrEmpty(name)) return;

            var full = prefix.Length == 0 ? name : prefix + "." + name;

            // Attributes accumulate down the tree: a subclass has its own plus every ancestor's.
            var attrs = new Dictionary<string, string>(inherited, StringComparer.OrdinalIgnoreCase);

            // …and ACROSS MODULES they accumulate too. A FOM module that adds a subclass has to
            // re-declare the whole ancestor chain to reach it, and those re-declarations are
            // SCAFFOLDING: they carry no attributes. Assigning here instead of merging let a
            // module wipe the base FOM's class - join RPR 2.0 plus the 16 MAK/NETN modules and
            // GroundVehicle came back with no EntityIdentifier at all.
            Dictionary<string, string> already;
            if (model.Classes.TryGetValue(full, out already))
                foreach (var kv in already) attrs[kv.Key] = kv.Value;

            foreach (var a in oc.Elements(ns + "attribute"))
            {
                var an = Val(a, ns, "name");
                if (!string.IsNullOrEmpty(an)) attrs[an] = Val(a, ns, "dataType");
            }
            model.Classes[full] = attrs;

            foreach (var child in oc.Elements(ns + "objectClass"))
                ReadClass(model, child, ns, full, attrs);
        }

        /// <summary>Interactions nest and inherit parameters exactly as object classes do.</summary>
        private static void ReadInteraction(FomModel model, XElement ic, XNamespace ns,
                                            string prefix, Dictionary<string, string> inherited)
        {
            var name = Val(ic, ns, "name");
            if (string.IsNullOrEmpty(name)) return;

            var full = prefix.Length == 0 ? name : prefix + "." + name;
            var pars = new Dictionary<string, string>(inherited, StringComparer.OrdinalIgnoreCase);

            Dictionary<string, string> already;
            if (model.Interactions.TryGetValue(full, out already))
                foreach (var kv in already) pars[kv.Key] = kv.Value;

            foreach (var pm in ic.Elements(ns + "parameter"))
            {
                var pn = Val(pm, ns, "name");
                if (!string.IsNullOrEmpty(pn)) pars[pn] = Val(pm, ns, "dataType");
            }
            model.Interactions[full] = pars;

            foreach (var child in ic.Elements(ns + "interactionClass"))
                ReadInteraction(model, child, ns, full, pars);
        }

        private static void Each(XElement parent, XNamespace ns, string group, string item, Action<XElement> fn)
        {
            var g = parent.Element(ns + group);
            if (g == null) return;
            foreach (var e in g.Elements(ns + item)) fn(e);
        }

        private static string Val(XElement e, XNamespace ns, string child)
        {
            var c = e.Element(ns + child);
            return c == null ? "" : c.Value.Trim();
        }

        private static void Add(FomModel m, FomType t)
        {
            if (!string.IsNullOrEmpty(t.Name)) m.Types[t.Name] = t;
        }
    }

    /// <summary>
    /// Reads an HLA 1.3 OMT / DIF file (<c>.omt</c>) into a <see cref="FomModel"/>.
    ///
    /// This is the file that makes HLA 1.3 testable at all: the <c>.fed</c> lists which attributes
    /// exist and states NO datatypes whatsoever, so without the OMT there is no way to know that
    /// WorldLocation is three big-endian doubles (REQ-01).
    ///
    /// The format is a LISP-ish s-expression text, not XML:
    ///   (Class (ID 1) (Name "BaseEntity" [26]) (Attribute (Name "WorldLocation") (DataType "…")))
    ///   (ComplexDataType (Name "WorldLocationStruct") (ComplexComponent (FieldName "X") …))
    /// </summary>
    public static class OmtParser
    {
        // (Name "Thing")  /  (Name "Thing" [26])  - the bracketed note is a DIF cross-reference
        private static readonly Regex RxName = new Regex(@"\(Name\s+""([^""]*)""", RegexOptions.Compiled);
        private static readonly Regex RxDataType = new Regex(@"\(DataType\s+""([^""]*)""", RegexOptions.Compiled);
        private static readonly Regex RxFieldName = new Regex(@"\(FieldName\s+""([^""]*)""", RegexOptions.Compiled);
        private static readonly Regex RxCardinality = new Regex(@"\(Cardinality\s+""([^""]*)""", RegexOptions.Compiled);
        /// <summary>
        /// ⚠ Case-insensitive on purpose: this FOM writes <c>(SuperClass 10)</c> with a capital C,
        /// and a case-sensitive match silently found NOTHING — every class then inherited nothing,
        /// so leaf classes like Aircraft came out with zero attributes.
        /// </summary>
        private static readonly Regex RxSuperclass = new Regex(@"\(Superclass\s+""?([^""\)\s]+)""?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>A class's or interaction's DIF ID — what a SuperClass reference points at.</summary>
        private static readonly Regex RxId = new Regex(@"\(ID\s+(\d+)\s*\)", RegexOptions.Compiled);
        private static readonly Regex RxRepresentation = new Regex(@"\(Representation\s+""?([^""\)\s]+)""?", RegexOptions.Compiled);

        public static FomModel Parse(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("OMT not found: " + path, path);

            // DIF files are 8-bit text with the odd stray high byte; latin-1 never throws.
            var text = File.ReadAllText(path, Encoding.GetEncoding("iso-8859-1"));
            var model = new FomModel
            {
                Flavour = "OMT (HLA 1.3)",
                SourceFile = Path.GetFileName(path),
            };

            foreach (var block in Blocks(text, "ComplexDataType"))
            {
                var t = new FomType { Name = FirstName(block), Kind = FomKind.FixedRecord };
                foreach (var comp in Blocks(block, "ComplexComponent"))
                {
                    var f = new FomField
                    {
                        Name = First(RxFieldName, comp),
                        DataType = First(RxDataType, comp),
                    };
                    // A component may repeat: (Cardinality "3") means three of them inline.
                    var card = First(RxCardinality, comp);
                    int n;
                    if (int.TryParse(card, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 1)
                    {
                        for (int i = 0; i < n; i++)
                            t.Fields.Add(new FomField
                            {
                                Name = f.Name + "[" + i.ToString(CultureInfo.InvariantCulture) + "]",
                                DataType = f.DataType,
                            });
                        continue;
                    }
                    if (!string.IsNullOrEmpty(f.Name)) t.Fields.Add(f);
                }
                if (!string.IsNullOrEmpty(t.Name)) model.Types[t.Name] = t;
            }

            foreach (var block in Blocks(text, "EnumeratedDataType"))
            {
                var name = FirstName(block);
                if (string.IsNullOrEmpty(name)) continue;
                // DIF enums do not state a representation; infer width from the Enum<n> suffix,
                // which RPR uses consistently (ForceIdentifierEnum8, ActionEnum32, ...).
                var rep = "unsigned long";
                var m = Regex.Match(name, @"Enum(\d+)$");
                if (m.Success)
                {
                    switch (m.Groups[1].Value)
                    {
                        case "8": rep = "octet"; break;
                        case "16": rep = "unsigned short"; break;
                        case "32": rep = "unsigned long"; break;
                        case "64": rep = "unsigned long long"; break;
                    }
                }
                model.Types[name] = new FomType { Name = name, Kind = FomKind.Enumerated, Representation = rep };
            }

            // Classes. DIF nests by (Superclass "X"); flatten to fully-qualified paths so the
            // model looks the same as an FDD's to everything downstream.
            var ownAttrs = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var parentOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var classIds = new Dictionary<string, string>();     // DIF numeric ID -> class name
            var classSuper = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var block in Blocks(text, "Class"))
            {
                var name = FirstName(block);
                if (string.IsNullOrEmpty(name)) continue;

                var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ab in Blocks(block, "Attribute"))
                {
                    var an = FirstName(ab);
                    if (string.IsNullOrEmpty(an)) continue;
                    var dt = First(RxDataType, ab);
                    var card = First(RxCardinality, ab);
                    // "0+" / "1+" mark a variable-length array of the stated type.
                    if (card.EndsWith("+", StringComparison.Ordinal)) dt = ArrayTypeFor(model, dt);
                    attrs[an] = dt;
                }
                ownAttrs[name] = attrs;

                var id = First(RxId, block);
                if (!string.IsNullOrEmpty(id)) classIds[id] = name;
                var sup = First(RxSuperclass, block);
                if (!string.IsNullOrEmpty(sup)) classSuper[name] = sup;
            }
            ResolveParents(classSuper, classIds, parentOf);

            foreach (var cls in ownAttrs.Keys)
            {
                var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var chain = new List<string>();
                var cur = cls;
                var guard = 0;
                while (!string.IsNullOrEmpty(cur) && guard++ < 32)
                {
                    chain.Insert(0, cur);
                    string parent;
                    cur = parentOf.TryGetValue(cur, out parent) ? parent : null;
                }
                foreach (var step in chain)
                {
                    Dictionary<string, string> a;
                    if (!ownAttrs.TryGetValue(step, out a)) continue;
                    foreach (var kv in a) merged[kv.Key] = kv.Value;
                }
                model.Classes[string.Join(".", chain.ToArray())] = merged;
            }

            // Interactions: identical shape to classes, parameters instead of attributes.
            var ownParams = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var iParentOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var intIds = new Dictionary<string, string>();        // separate ID space from classes
            var intSuper = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var block in Blocks(text, "Interaction"))
            {
                var name = FirstName(block);
                if (string.IsNullOrEmpty(name)) continue;

                var pars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pb in Blocks(block, "Parameter"))
                {
                    var pn = FirstName(pb);
                    if (string.IsNullOrEmpty(pn)) continue;
                    var dt = First(RxDataType, pb);
                    var card = First(RxCardinality, pb);
                    if (card.EndsWith("+", StringComparison.Ordinal)) dt = ArrayTypeFor(model, dt);
                    pars[pn] = dt;
                }
                ownParams[name] = pars;

                var id = First(RxId, block);
                if (!string.IsNullOrEmpty(id)) intIds[id] = name;
                var sup = First(RxSuperclass, block);
                if (!string.IsNullOrEmpty(sup)) intSuper[name] = sup;
            }
            ResolveParents(intSuper, intIds, iParentOf);

            foreach (var ic in ownParams.Keys)
            {
                var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var chain = new List<string>();
                var cur = ic;
                var guard = 0;
                while (!string.IsNullOrEmpty(cur) && guard++ < 32)
                {
                    chain.Insert(0, cur);
                    string parent;
                    cur = iParentOf.TryGetValue(cur, out parent) ? parent : null;
                }
                foreach (var step in chain)
                {
                    Dictionary<string, string> a;
                    if (!ownParams.TryGetValue(step, out a)) continue;
                    foreach (var kv in a) merged[kv.Key] = kv.Value;
                }
                model.Interactions[string.Join(".", chain.ToArray())] = merged;
            }

            model.ComputeOffsets();
            return model;
        }

        /// <summary>
        /// Turn superclass REFERENCES into names. DIF points at the parent by its numeric ID —
        /// <c>(SuperClass 10)</c> — so the raw token is useless until it is looked up. A quoted
        /// name is accepted too, because other DIF writers emit that form.
        ///
        /// Object classes and interactions have separate ID spaces, so each gets its own map.
        /// </summary>
        private static void ResolveParents(Dictionary<string, string> superOf,
                                           Dictionary<string, string> idToName,
                                           Dictionary<string, string> parentOf)
        {
            foreach (var kv in superOf)
            {
                string byId;
                var parent = idToName.TryGetValue(kv.Value, out byId) ? byId : kv.Value;
                if (string.IsNullOrEmpty(parent)) continue;
                if (string.Equals(parent, kv.Key, StringComparison.OrdinalIgnoreCase)) continue;  // self-cycle
                parentOf[kv.Key] = parent;
            }
        }

        /// <summary>Synthesise an array type for a "0+" cardinality attribute.</summary>
        private static string ArrayTypeFor(FomModel model, string elementType)
        {
            var name = elementType + "[]";
            if (!model.Types.ContainsKey(name))
                model.Types[name] = new FomType
                {
                    Name = name,
                    Kind = FomKind.Array,
                    ElementType = elementType,
                    Cardinality = "Dynamic",
                };
            return name;
        }

        /// <summary>
        /// Yield the text of each top-level <c>(Keyword …)</c> block, bracket-matched so a nested
        /// block never terminates its parent. Quoted strings are skipped, since descriptions in
        /// this FOM contain unbalanced parentheses.
        /// </summary>
        private static IEnumerable<string> Blocks(string text, string keyword)
        {
            var needle = "(" + keyword;
            int i = 0;
            while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
            {
                var after = i + needle.Length;
                if (after < text.Length && !char.IsWhiteSpace(text[after]) && text[after] != '(')
                { i = after; continue; }                 // e.g. "(ClassX" when looking for "(Class"

                int depth = 0;
                bool inString = false;
                int j = i;
                for (; j < text.Length; j++)
                {
                    var c = text[j];
                    if (inString)
                    {
                        if (c == '\\') { j++; continue; }
                        if (c == '"') inString = false;
                        continue;
                    }
                    if (c == '"') { inString = true; continue; }
                    if (c == '(') depth++;
                    else if (c == ')') { depth--; if (depth == 0) { j++; break; } }
                }
                yield return text.Substring(i, Math.Min(j, text.Length) - i);
                i = after;
            }
        }

        private static string FirstName(string block)
        {
            return First(RxName, block);
        }

        private static string First(Regex rx, string s)
        {
            var m = rx.Match(s);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }
    }

    /// <summary>Picks the right parser from the file extension.</summary>
    public static class FomLoader
    {
        public static FomModel Load(params string[] paths)
        {
            var xml = new List<string>();
            string omt = null;

            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                var ext = Path.GetExtension(p).ToLowerInvariant();
                if (ext == ".omt" || ext == ".dif") omt = p;
                else if (ext == ".xml") xml.Add(p);
                else if (ext == ".fed")
                    throw new NotSupportedException(
                        "a .fed carries NO datatypes - supply the matching .omt as well (REQ-01): " + p);
            }

            if (omt != null && xml.Count > 0)
                throw new NotSupportedException("supply either an .omt (HLA 1.3) or FDD .xml files, not both");
            if (omt != null) return OmtParser.Parse(omt);
            if (xml.Count > 0) return FddParser.Parse(xml.ToArray());

            throw new ArgumentException("no FOM file supplied");
        }
    }
}
