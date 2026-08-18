using System;
using System.Collections.Generic;
using System.Reflection;

namespace BrokerTestKit.Engine
{
    /// <summary>
    /// The Universal HLA message API exposes <c>getAttribute(name)</c> but no way to ENUMERATE
    /// which attributes actually arrived — internally they live in a protected
    /// <c>Dictionary&lt;string, byte[]&gt; myAttributes</c>.
    ///
    /// For golden-file comparison we only need lookup-by-name (the expected CSV supplies the
    /// names). But for the GUI's "what actually arrived" diff view, enumeration is very useful,
    /// so we read that field reflectively, degrading gracefully if the field ever moves.
    /// </summary>
    internal static class UniversalAttributeReader
    {
        private static readonly Dictionary<Type, FieldInfo> Cache = new Dictionary<Type, FieldInfo>();
        private static readonly object Gate = new object();

        /// <summary>
        /// Copy all attributes out of a Universal message. Returns false (and leaves
        /// <paramref name="into"/> untouched) if enumeration isn't possible on this build.
        /// </summary>
        public static bool TryReadAll(object universalMessage, IDictionary<string, byte[]> into)
        {
            if (universalMessage == null || into == null) return false;

            var field = FieldFor(universalMessage.GetType());
            if (field == null) return false;

            var dict = field.GetValue(universalMessage) as IDictionary<string, byte[]>;
            if (dict == null) return false;

            foreach (var kv in dict) into[kv.Key] = kv.Value;
            return true;
        }

        private static FieldInfo FieldFor(Type t)
        {
            lock (Gate)
            {
                FieldInfo cached;
                if (Cache.TryGetValue(t, out cached)) return cached;

                FieldInfo found = null;
                for (var cur = t; cur != null && found == null; cur = cur.BaseType)
                {
                    foreach (var f in cur.GetFields(BindingFlags.Instance |
                                                    BindingFlags.NonPublic |
                                                    BindingFlags.Public))
                    {
                        if (typeof(IDictionary<string, byte[]>).IsAssignableFrom(f.FieldType))
                        {
                            found = f;
                            break;
                        }
                    }
                }

                Cache[t] = found;
                return found;
            }
        }
    }
}
