// btk_wire.h - line protocol + byte helpers shared by the BTK native agents.
//
// The native agents speak the SAME stdin/stdout line protocol as the managed
// (C#) agent, so BrokerTestKit.Engine.AgentHost can spawn either one - it takes
// the exe path as a constructor argument and never assumes a language.
//
// Everything here is deliberately FOM-agnostic: values are raw bytes on the wire
// and hex on the pipe. Interpretation (float64 / string / struct) is the caller's
// job, driven by the OMT (HLA 1.3) or FDD (Evolved) - see REQ-09.
//
// Endianness: HLA/RPR payloads are network byte order (big-endian). The be*()
// helpers below byte-swap from the host, which is always little-endian here (x64).

#ifndef BTK_WIRE_H
#define BTK_WIRE_H

#include <string>
#include <vector>
#include <map>
#include <sstream>
#include <cstdio>
#include <cstring>
#include <cstdlib>

namespace btk {

typedef std::vector<unsigned char> Bytes;

// ---------------------------------------------------------------- hex codec --

inline std::string toHex(const unsigned char* p, size_t n)
{
    static const char* D = "0123456789abcdef";
    std::string s;
    s.reserve(n * 2);
    for (size_t i = 0; i < n; ++i) {
        s += D[(p[i] >> 4) & 0xF];
        s += D[p[i] & 0xF];
    }
    return s;
}

inline std::string toHex(const Bytes& b)
{
    return b.empty() ? std::string() : toHex(&b[0], b.size());
}

inline int hexVal(char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

inline bool fromHex(const std::string& s, Bytes& out)
{
    out.clear();
    if (s.size() % 2 != 0) return false;
    out.reserve(s.size() / 2);
    for (size_t i = 0; i < s.size(); i += 2) {
        int hi = hexVal(s[i]);
        int lo = hexVal(s[i + 1]);
        if (hi < 0 || lo < 0) return false;
        out.push_back((unsigned char)((hi << 4) | lo));
    }
    return true;
}

// ------------------------------------------------- big-endian primitive codec --

inline Bytes beFrom(const void* src, size_t n)
{
    const unsigned char* p = (const unsigned char*)src;
    Bytes b(n);
    for (size_t i = 0; i < n; ++i) b[i] = p[n - 1 - i];   // host LE -> network BE
    return b;
}

inline Bytes beF64(double v)  { return beFrom(&v, 8); }
inline Bytes beF32(float v)   { return beFrom(&v, 4); }
inline Bytes beI32(int v)     { return beFrom(&v, 4); }
inline Bytes beI16(short v)   { return beFrom(&v, 2); }
inline Bytes beU8(unsigned char v) { return Bytes(1, v); }

inline bool beToF64(const Bytes& b, double& out)
{
    if (b.size() != 8) return false;
    unsigned char t[8];
    for (int i = 0; i < 8; ++i) t[i] = b[7 - i];
    std::memcpy(&out, t, 8);
    return true;
}

inline bool beToF32(const Bytes& b, float& out)
{
    if (b.size() != 4) return false;
    unsigned char t[4];
    for (int i = 0; i < 4; ++i) t[i] = b[3 - i];
    std::memcpy(&out, t, 4);
    return true;
}

/// Fixed-width ASCII, zero padded / truncated (RPR marking is 11 chars + 1 code byte).
inline Bytes asciiFixed(const std::string& s, size_t width)
{
    Bytes b(width, 0);
    for (size_t i = 0; i < width && i < s.size(); ++i) b[i] = (unsigned char)s[i];
    return b;
}

inline Bytes asciiRaw(const std::string& s)
{
    Bytes b(s.size());
    for (size_t i = 0; i < s.size(); ++i) b[i] = (unsigned char)s[i];
    return b;
}

// ------------------------------------------------- narrow <-> wide (1516 only) --
//
// HLA Evolved is a wide-string API; HLA 1.3 and our pipe protocol are narrow. FOM class and
// attribute names are ASCII by construction (IEEE 1516.2 identifiers), so a byte-wise widen
// is correct here and avoids the deprecated <codecvt>.

inline std::wstring toW(const std::string& s)
{
    std::wstring w;
    w.reserve(s.size());
    for (size_t i = 0; i < s.size(); ++i) w += (wchar_t)(unsigned char)s[i];
    return w;
}

inline std::string toN(const std::wstring& w)
{
    std::string s;
    s.reserve(w.size());
    for (size_t i = 0; i < w.size(); ++i)
        s += (w[i] >= 0 && w[i] < 128) ? (char)w[i] : '?';
    return s;
}

// --------------------------------------------------------- line-protocol I/O --

inline void say(const std::string& s)
{
    std::fputs(s.c_str(), stdout);
    std::fputc('\n', stdout);
    std::fflush(stdout);
}

inline void errLine(const std::string& s)
{
    say("ERR " + s);
}

/// Diagnostics go to stderr so they never pollute the protocol stream.
inline void note(const std::string& s)
{
    std::fputs(s.c_str(), stderr);
    std::fputc('\n', stderr);
    std::fflush(stderr);
}

inline std::string lower(const std::string& s)
{
    std::string r(s);
    for (size_t i = 0; i < r.size(); ++i)
        if (r[i] >= 'A' && r[i] <= 'Z') r[i] = (char)(r[i] - 'A' + 'a');
    return r;
}

inline std::string upper(const std::string& s)
{
    std::string r(s);
    for (size_t i = 0; i < r.size(); ++i)
        if (r[i] >= 'a' && r[i] <= 'z') r[i] = (char)(r[i] - 'a' + 'A');
    return r;
}

inline std::string trim(const std::string& s)
{
    // A UTF-8 BOM counts as leading whitespace here. Two entirely ordinary things put one in
    // front of the first command: an editor saving a script file as "UTF-8 with signature",
    // and .NET's Process.StandardInput, which inherits the parent CONSOLE's encoding and
    // writes the preamble the moment the writer is created. Either way the agent used to
    // reject a command that reads as perfectly correct in the transcript - "unknown verb
    // 'JOIN'" with an invisible character in front of it - and only in some shells.
    size_t z = 0;
    if (s.size() >= 3 &&
        (unsigned char)s[0] == 0xEF && (unsigned char)s[1] == 0xBB && (unsigned char)s[2] == 0xBF)
        z = 3;

    size_t a = s.find_first_not_of(" \t\r\n", z);
    if (a == std::string::npos) return std::string();
    size_t b = s.find_last_not_of(" \t\r\n");
    return s.substr(a, b - a + 1);
}

typedef std::map<std::string, std::string> Kv;

/// "VERB k=v k=v" -> map of lower-cased key to raw value. Values may not contain spaces;
/// use hex= for anything binary or space-bearing.
inline Kv parseKv(const std::string& line)
{
    Kv kv;
    std::istringstream is(line);
    std::string tok;
    bool first = true;
    while (is >> tok) {
        if (first) { first = false; continue; }          // skip the verb
        size_t eq = tok.find('=');
        if (eq == std::string::npos) continue;
        kv[lower(tok.substr(0, eq))] = tok.substr(eq + 1);
    }
    return kv;
}

inline std::string verbOf(const std::string& line)
{
    std::istringstream is(line);
    std::string v;
    is >> v;
    return upper(v);
}

inline bool has(const Kv& kv, const std::string& k)
{
    return kv.find(k) != kv.end();
}

inline std::string get(const Kv& kv, const std::string& k, const std::string& dflt = std::string())
{
    Kv::const_iterator it = kv.find(k);
    return it == kv.end() ? dflt : it->second;
}

inline std::vector<std::string> splitList(const std::string& s, char sep = ',')
{
    std::vector<std::string> out;
    std::string cur;
    for (size_t i = 0; i < s.size(); ++i) {
        if (s[i] == sep) { cur = trim(cur); if (!cur.empty()) out.push_back(cur); cur.clear(); }
        else cur += s[i];
    }
    cur = trim(cur);
    if (!cur.empty()) out.push_back(cur);
    return out;
}

/// Decode the value-bearing keys of a SETATTR command into raw bytes.
/// hex= is canonical; the typed forms are conveniences so hand-written tests and
/// smoke scripts do not need a codec in front of them.
inline bool decodeValue(const Kv& kv, Bytes& out, std::string& why)
{
    if (has(kv, "hex")) {
        if (!fromHex(get(kv, "hex"), out)) { why = "bad hex"; return false; }
        return true;
    }
    if (has(kv, "f64")) { out = beF64(std::atof(get(kv, "f64").c_str())); return true; }
    if (has(kv, "f32")) { out = beF32((float)std::atof(get(kv, "f32").c_str())); return true; }
    if (has(kv, "i32")) { out = beI32(std::atoi(get(kv, "i32").c_str())); return true; }
    if (has(kv, "i16")) { out = beI16((short)std::atoi(get(kv, "i16").c_str())); return true; }
    if (has(kv, "u8"))  { out = beU8((unsigned char)std::atoi(get(kv, "u8").c_str())); return true; }
    if (has(kv, "str")) {
        std::string s = get(kv, "str");
        if (has(kv, "width")) out = asciiFixed(s, (size_t)std::atoi(get(kv, "width").c_str()));
        else                  out = asciiRaw(s);
        return true;
    }
    why = "no value: expected one of hex= f64= f32= i32= i16= u8= str=";
    return false;
}

} // namespace btk

#endif // BTK_WIRE_H
