using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BrokerTestKit.Gui
{
    /// <summary>
    /// The app's own folder, and the rule that anything the app depends on gets COPIED into it.
    ///
    /// The workspace sits BESIDE THE EXE — database, imported FOMs and rid files all under the
    /// folder the app runs from — so the whole tool is one directory you can zip, copy to a
    /// stick, or hand to a colleague and have it work unchanged.
    ///
    /// ⚠ Two consequences of that choice, accepted deliberately:
    ///   * a clean rebuild that wipes <c>bin\</c> takes the database with it — back the folder up
    ///     before rebuilding if a run history matters;
    ///   * installed under <c>Program Files</c> the folder is not writable by a normal user, and
    ///     the app will say so on startup rather than silently writing somewhere else.
    ///
    /// Why import rather than reference the user's path:
    ///   * a registered FOM cannot break because somebody moved, renamed or deleted the original;
    ///   * a recorded run stays reproducible — it points at the exact bytes it ran against, not at
    ///     a path whose contents may since have changed;
    ///   * the workspace is self-contained, so backing it up or handing it to a colleague takes the
    ///     database AND every file it refers to.
    ///
    /// Files are stored CONTENT-ADDRESSED, under their SHA-256. Registering the same FOM twice
    /// costs one copy, and re-importing an edited file produces a new hash rather than silently
    /// overwriting the bytes an old run was verified against.
    /// </summary>
    public sealed class Workspace
    {
        public string Root { get; private set; }

        /// <summary>The folder the exe runs from — the workspace root unless one is passed in.</summary>
        public static string DefaultRoot
        {
            get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/'); }
        }

        /// <summary>Where the workspace lived before it moved beside the exe.</summary>
        public static string LegacyRoot
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrokerTestKit");
            }
        }

        public Workspace(string root = null)
        {
            Root = root ?? DefaultRoot;
            Directory.CreateDirectory(Root);
            EnsureWritable();
            Directory.CreateDirectory(AssetDir);
        }

        /// <summary>
        /// Fail loudly and early. SQLite's "unable to open database file" tells the user nothing;
        /// this says which folder and why.
        /// </summary>
        private void EnsureWritable()
        {
            var probe = Path.Combine(Root, ".btk-write-test");
            try
            {
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                throw new IOException(
                    "The workspace folder is not writable:" + Environment.NewLine + "    " + Root +
                    Environment.NewLine + Environment.NewLine +
                    "BrokerTestKit keeps its database and imported FOMs beside the exe. Move the " +
                    "app somewhere writable (not Program Files), or run it from a copy." +
                    Environment.NewLine + Environment.NewLine + ex.Message, ex);
            }
        }

        /// <summary>
        /// One-time adoption of a workspace left in AppData by an earlier build, so moving the
        /// storage beside the exe does not look like the app lost the registered FOMs.
        ///
        /// Copies rather than moves: if anything here goes wrong the old folder is still intact.
        /// Does nothing once this workspace has a database of its own.
        /// </summary>
        public string AdoptLegacyWorkspace()
        {
            var old = LegacyRoot;
            if (string.Equals(old, Root, StringComparison.OrdinalIgnoreCase)) return null;
            if (!File.Exists(Path.Combine(old, "btk.db"))) return null;
            if (File.Exists(DatabasePath)) return null;          // already in use — leave it alone

            int copied = 0;
            foreach (var src in Directory.GetFiles(old, "*", SearchOption.AllDirectories))
            {
                var rel = src.Substring(old.Length).TrimStart('\\', '/');
                var dst = Path.Combine(Root, rel);
                if (File.Exists(dst)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(src, dst);
                try { File.SetAttributes(dst, File.GetAttributes(src)); } catch { }
                copied++;
            }
            return copied == 0 ? null : "adopted " + copied + " file(s) from " + old;
        }

        public string AssetDir { get { return Path.Combine(Root, "assets"); } }
        public string DatabasePath { get { return Path.Combine(Root, "btk.db"); } }

        /// <summary>Where a hash lives on disk. Sharded by the first byte to keep folders small.</summary>
        public string PathOf(string hash, string extension)
        {
            if (string.IsNullOrEmpty(hash)) return null;
            var dir = Path.Combine(AssetDir, hash.Substring(0, 2));
            return Path.Combine(dir, hash + (extension ?? ""));
        }

        /// <summary>
        /// Copy a file in and return its hash. Idempotent: an identical file already present is
        /// not copied again. Returns null for a blank path so optional files stay optional.
        /// </summary>
        public string Import(string sourcePath, out string storedPath, out long size)
        {
            storedPath = null;
            size = 0;
            if (string.IsNullOrWhiteSpace(sourcePath)) return null;
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("file not found: " + sourcePath, sourcePath);

            var hash = HashOf(sourcePath);
            var ext = Path.GetExtension(sourcePath);
            var target = PathOf(hash, ext);

            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (!File.Exists(target))
            {
                File.Copy(sourcePath, target, false);
                // Imported assets are a record of what a run used - make them read-only so an
                // accidental edit cannot rewrite history.
                try { File.SetAttributes(target, FileAttributes.ReadOnly); } catch { }
            }

            storedPath = target;
            size = new FileInfo(target).Length;
            return hash;
        }

        public static string HashOf(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
            {
                var bytes = sha.ComputeHash(fs);
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>True when the original file still matches what was imported.</summary>
        public static bool StillMatches(string originalPath, string hash)
        {
            try
            {
                return !string.IsNullOrEmpty(originalPath) && File.Exists(originalPath) &&
                       string.Equals(HashOf(originalPath), hash, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>Copy an imported asset back out, e.g. to hand a FOM to someone.</summary>
        public void Export(string hash, string extension, string destination)
        {
            var src = PathOf(hash, extension);
            if (src == null || !File.Exists(src)) throw new FileNotFoundException("asset not in workspace", hash);
            File.Copy(src, destination, true);
            try { File.SetAttributes(destination, FileAttributes.Normal); } catch { }
        }
    }
}
