using System;

namespace BrokerTestKit.Engine
{
    /// <summary>
    /// Datatype codec for RPR FOM values crossing the Universal HLA layer as raw byte[].
    ///
    /// RPR/DIS records are BIG-ENDIAN (IEEE 1516.2 "BE" encodings: HLAfloat64BE,
    /// HLAinteger32BE, ...). .NET's BitConverter is little-endian on x86/x64, so every
    /// multi-byte value must be byte-swapped.
    ///
    /// This is the seed of the full datatype-driven codec; start with the primitives and
    /// the records the tests actually exercise.
    /// </summary>
    public static class RprCodec
    {
        // ---- primitives ------------------------------------------------------------

        public static byte[] Float64BE(double v)
        {
            var b = BitConverter.GetBytes(v);
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            return b;
        }

        public static double ToFloat64BE(byte[] b, int offset = 0)
        {
            if (b == null || b.Length < offset + 8)
                throw new ArgumentException("need 8 bytes for float64 at offset " + offset);
            var t = new byte[8];
            Array.Copy(b, offset, t, 0, 8);
            if (BitConverter.IsLittleEndian) Array.Reverse(t);
            return BitConverter.ToDouble(t, 0);
        }

        public static byte[] Float32BE(float v)
        {
            var b = BitConverter.GetBytes(v);
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            return b;
        }

        public static float ToFloat32BE(byte[] b, int offset = 0)
        {
            var t = new byte[4];
            Array.Copy(b, offset, t, 0, 4);
            if (BitConverter.IsLittleEndian) Array.Reverse(t);
            return BitConverter.ToSingle(t, 0);
        }

        public static byte[] Int32BE(int v)
        {
            var b = BitConverter.GetBytes(v);
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            return b;
        }

        public static int ToInt32BE(byte[] b, int offset = 0)
        {
            var t = new byte[4];
            Array.Copy(b, offset, t, 0, 4);
            if (BitConverter.IsLittleEndian) Array.Reverse(t);
            return BitConverter.ToInt32(t, 0);
        }

        public static byte[] Int16BE(short v)
        {
            var b = BitConverter.GetBytes(v);
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            return b;
        }

        public static short ToInt16BE(byte[] b, int offset = 0)
        {
            var t = new byte[2];
            Array.Copy(b, offset, t, 0, 2);
            if (BitConverter.IsLittleEndian) Array.Reverse(t);
            return BitConverter.ToInt16(t, 0);
        }

        // ---- records ---------------------------------------------------------------

        /// <summary>
        /// WorldLocation = HLAfixedRecord { X, Y, Z : HLAfloat64BE } — geocentric metres.
        /// 24 bytes.
        /// </summary>
        public static byte[] WorldLocation(double x, double y, double z)
        {
            var b = new byte[24];
            Buffer.BlockCopy(Float64BE(x), 0, b, 0, 8);
            Buffer.BlockCopy(Float64BE(y), 0, b, 8, 8);
            Buffer.BlockCopy(Float64BE(z), 0, b, 16, 8);
            return b;
        }

        public static void ToWorldLocation(byte[] b, out double x, out double y, out double z)
        {
            if (b == null || b.Length < 24)
                throw new ArgumentException("WorldLocation needs 24 bytes, got " +
                                            (b == null ? "null" : b.Length.ToString()));
            x = ToFloat64BE(b, 0);
            y = ToFloat64BE(b, 8);
            z = ToFloat64BE(b, 16);
        }

        /// <summary>
        /// EntityIdentifier = Site(uint16) + Application(uint16) + Entity(uint16) — 6 bytes.
        /// </summary>
        public static byte[] EntityIdentifier(ushort site, ushort app, ushort entity)
        {
            var b = new byte[6];
            Buffer.BlockCopy(Int16BE(unchecked((short)site)),   0, b, 0, 2);
            Buffer.BlockCopy(Int16BE(unchecked((short)app)),    0, b, 2, 2);
            Buffer.BlockCopy(Int16BE(unchecked((short)entity)), 0, b, 4, 2);
            return b;
        }

        // ---- helpers ---------------------------------------------------------------

        /// <summary>Hex dump for reports/diffs, e.g. "40 8F 40 00 ...".</summary>
        public static string Hex(byte[] b, int max = 32)
        {
            if (b == null) return "(null)";
            if (b.Length == 0) return "(empty)";
            var n = Math.Min(b.Length, max);
            var sb = new System.Text.StringBuilder(n * 3 + 8);
            for (int i = 0; i < n; i++) { if (i > 0) sb.Append(' '); sb.Append(b[i].ToString("X2")); }
            if (b.Length > n) sb.Append(" ... (").Append(b.Length).Append(" bytes)");
            return sb.ToString();
        }

        /// <summary>Exact byte equality (used before any tolerance-aware comparison).</summary>
        public static bool BytesEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
