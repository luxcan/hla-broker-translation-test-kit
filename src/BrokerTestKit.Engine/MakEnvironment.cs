using System;
using System.IO;
using System.Runtime.InteropServices;

namespace BrokerTestKit.Engine
{
    /// <summary>
    /// One-time process setup so the MAK native runtime can be found:
    ///   - native DLL search path (VR-Link bin64 + the RTI bin) for managedInterface*/libRTI
    ///   - RTI_CONFIG / RTI_RID_FILE so the RTI finds rid.mtl and the FOM files
    ///   - the FlexLM license server
    /// MUST be called before the first ExerciseConnection is constructed.
    ///
    /// NOTE: the RTI here must be a VC14/VC15 build, because VR-Link 5.10 is VC14.
    /// (RTI 5.0.1 = VC15, 4.6.1 = VC14. The installed 4.5.1 is a VC10 build and will NOT load.)
    /// </summary>
    public static class MakEnvironment
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        // The native CRT keeps its OWN cached copy of the environment. .NET's
        // Environment.SetEnvironmentVariable updates only the Win32 block, so native
        // getenv() (which VR-Link/the RTI use to read RTI_CONFIG, RTI_RID_FILE,
        // MAKLMGRD_LICENSE_FILE) would NOT see our values -- the RTI would silently fall
        // back to the install's own rid.mtl. _putenv_s updates the CRT copy as well.
        // VR-Link 5.10 is VC14.x, so its CRT is the UCRT (ucrtbase.dll).
        [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl,
                   CharSet = CharSet.Ansi, EntryPoint = "_putenv_s")]
        private static extern int CrtPutEnv(string name, string value);

        /// <summary>Set an environment variable so BOTH managed and native code observe it.</summary>
        private static void SetEnv(string name, string value)
        {
            Environment.SetEnvironmentVariable(name, value);
            try { CrtPutEnv(name, value); }
            catch (Exception ex) { Console.Error.WriteLine("WARN: native setenv failed for " + name + ": " + ex.Message); }
        }

        /// <summary>Folder holding vrLinkSharp.dll + the native managedInterface* DLLs.</summary>
        public static string VrLinkBin64 = @"C:\MAK\vrlink5.10\bin64";

        /// <summary>MAK RTI bin folder. Must be a VC14/VC15 build and match the broker's RTI.</summary>
        public static string RtiBin = @"C:\MAK\makRti5.0.1\bin";

        /// <summary>FlexLM license server; only applied if MAKLMGRD_LICENSE_FILE isn't already set.</summary>
        public static string LicenseFile = "27000@127.0.0.1";

        /// <summary>The resolved RTI_CONFIG folder (rid.mtl + FOM files). Set by Initialize().</summary>
        public static string RtiConfigDir { get; private set; }

        private static bool _initialized;

        /// <summary>
        /// Idempotent. Returns the resolved RTI_CONFIG directory.
        /// </summary>
        /// <param name="rtiConfigDir">
        /// Explicit config dir; if null, resolves BTK_RTI_CONFIG, else walks up from the
        /// executable looking for a "config" folder containing rid.mtl.
        /// </param>
        public static string Initialize(string rtiConfigDir = null)
        {
            if (_initialized) return RtiConfigDir;

            // 1) Native DLL search path. RTI first so we bind the intended RTI build.
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (Directory.Exists(RtiBin))
            {
                path = RtiBin + Path.PathSeparator + Path.Combine(RtiBin, "..", "lib") + Path.PathSeparator + path;
            }
            if (Directory.Exists(VrLinkBin64))
            {
                SetDllDirectory(VrLinkBin64);
                path = VrLinkBin64 + Path.PathSeparator + path;
            }
            SetEnv("PATH", path);

            // 2) RTI configuration (rid.mtl + .fed/.xml live here).
            RtiConfigDir = rtiConfigDir ?? ResolveRtiConfigDir();
            SetEnv("RTI_CONFIG", RtiConfigDir);
            var rid = Path.Combine(RtiConfigDir, "rid.mtl");
            if (File.Exists(rid)) SetEnv("RTI_RID_FILE", rid);

            // 3) License server (don't override an externally supplied value).
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MAKLMGRD_LICENSE_FILE")))
                SetEnv("MAKLMGRD_LICENSE_FILE", LicenseFile);

            _initialized = true;
            return RtiConfigDir;
        }

        private static string ResolveRtiConfigDir()
        {
            var env = Environment.GetEnvironmentVariable("BTK_RTI_CONFIG");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;

            var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (d != null)
            {
                var c = Path.Combine(d.FullName, "config");
                if (File.Exists(Path.Combine(c, "rid.mtl"))) return c;
                d = d.Parent;
            }
            return AppDomain.CurrentDomain.BaseDirectory;
        }
    }
}
