using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using BrokerTestKit.Engine;

namespace BrokerTestKit.Gui
{
    /// <summary>One asset the workspace has imported.</summary>
    public sealed class AssetRef
    {
        public string Hash;
        public string OriginalPath;      // where it came from — provenance only
        public string FileName;
        public string StoredPath;        // the copy the app actually uses
        public long Size;

        public override string ToString() { return FileName ?? Hash; }
    }

    /// <summary>Header row for the run history list.</summary>
    public sealed class RunSummary
    {
        public long Id;
        public DateTime StartedUtc;
        public string FomA, FomB, ExecA, ExecB;
        public bool SelfTest;
        public int Passed, Failed, TimedOut;
        public double Seconds;
        public string ConnA, ConnB, AbortReason, InputPath, ExpectedPath;
        /// <summary>False when the app died mid-run: the rows that did complete are still saved.</summary>
        public bool Finished;

        public string Started { get { return StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); } }
        public string Outcome
        {
            get
            {
                if (!Finished) return "Incomplete";
                if (!string.IsNullOrEmpty(AbortReason)) return "Aborted";
                return Failed + TimedOut == 0 && Passed > 0 ? "Pass" : "Fail";
            }
        }
        public string Pair
        {
            get
            {
                var a = string.IsNullOrEmpty(ConnA) ? ExecA : ConnA;
                var b = string.IsNullOrEmpty(ConnB) ? ExecB : ConnB;
                return SelfTest ? a + "  (self-test)" : a + "  →  " + b;
            }
        }
        public string Duration { get { return Seconds.ToString("F1", CultureInfo.InvariantCulture) + " s"; } }
        public string Tally { get { return Passed + " passed · " + Failed + " failed · " + TimedOut + " timed out"; } }
        public string Mode { get { return SelfTest ? "self-test" : "A → broker → B"; } }

        // ---- pairs / test cases (PRP gui-redesign) ----
        public long? PairId, TestCaseId;
        public string PairName, TestCaseName;
        public double? LatencyP50;

        /// <summary>
        /// One word for the result column: Pass, Fail or Timeout. Timeout only when nothing passed
        /// or failed - a run where the broker went silent; any wrong value or an abort is a Fail.
        /// Kept in step with Store.OutcomeSql so filters and labels agree.
        /// </summary>
        public string Result
        {
            get
            {
                if (!Finished || !string.IsNullOrEmpty(AbortReason)) return "Fail";
                if (Failed > 0) return "Fail";
                if (TimedOut > 0) return "Timeout";
                return Passed > 0 ? "Pass" : "Fail";
            }
        }
        public string ResultText
        {
            get
            {
                var r = Result == "Timeout" ? "Timed out" : Result;
                var total = Passed + Failed + TimedOut;
                // A stopped run never learns how many rows it would have had, so "7 / 7" would read
                // as complete. Say how far it got instead.
                if (!Finished) return "Incomplete · " + total + (total == 1 ? " row run" : " rows run");
                if (!string.IsNullOrEmpty(AbortReason)) return "Aborted · " + total + (total == 1 ? " row run" : " rows run");
                return r + " · " + Passed + " / " + total;
            }
        }
        public string When { get { return Fmt.When(StartedUtc); } }
        public string LatencyText { get { return LatencyP50.HasValue ? LatencyP50.Value.ToString("F0", CultureInfo.InvariantCulture) + " ms" : "—"; } }
        public string PairDisplay { get { return string.IsNullOrEmpty(PairName) ? Pair : PairName; } }
        public string TestCaseDisplay { get { return string.IsNullOrEmpty(TestCaseName) ? "—" : TestCaseName; } }
    }

    /// <summary>The PRP's one timestamp format: dd MMM yy HH:mm, local, invariant month names.</summary>
    public static class Fmt
    {
        public static string When(DateTime utc)
        {
            return utc.ToLocalTime().ToString("dd MMM yy HH:mm", CultureInfo.InvariantCulture);
        }

        public static string When(RunSummary r) { return r == null ? "—" : When(r.StartedUtc); }
    }

    /// <summary>
    /// Everything the app remembers: imported assets, the FOM registry, settings, and run history.
    ///
    /// One SQLite file next to the imported assets, so the workspace is a single self-contained
    /// thing to back up or hand over. Run history is the reason this is a database rather than more
    /// flat files — "when did this attribute start failing?" is a relational question over
    /// runs × rows × field comparisons, and it grows without bound.
    /// </summary>
    public sealed partial class Store : IDisposable
    {
        private readonly SqliteConnection _cn;
        public readonly Workspace Workspace;

        public Store(Workspace ws)
        {
            Workspace = ws;
            _cn = new SqliteConnection("Data Source=" + ws.DatabasePath);
            _cn.Open();
            Exec("PRAGMA journal_mode=WAL;");
            Exec("PRAGMA foreign_keys=ON;");
            Migrate();
        }

        // ------------------------------------------------------------------ schema --

        private void Migrate()
        {
            Exec(@"
CREATE TABLE IF NOT EXISTS asset (
    hash          TEXT PRIMARY KEY,
    file_name     TEXT NOT NULL,
    original_path TEXT,
    size          INTEGER NOT NULL DEFAULT 0,
    imported_utc  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS fom (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    name           TEXT NOT NULL UNIQUE,
    is_evolved     INTEGER NOT NULL,
    structure_hash TEXT REFERENCES asset(hash),
    datatype_hash  TEXT REFERENCES asset(hash),
    mim_hash       TEXT REFERENCES asset(hash),
    module_hashes  TEXT NOT NULL DEFAULT '',
    added_utc      TEXT NOT NULL
);

-- What the parser worked out at import, kept so a restart does not redo it.
-- The key carries every content hash that fed the parse AND the parser version, so a changed
-- FOM or a changed parser misses rather than serving a stale answer.
CREATE TABLE IF NOT EXISTS fom_model (
    key            TEXT PRIMARY KEY,
    parser_version INTEGER NOT NULL,
    classes        INTEGER NOT NULL,
    interactions   INTEGER NOT NULL,
    datatypes      INTEGER NOT NULL,
    body           TEXT NOT NULL,
    built_utc      TEXT NOT NULL
);

-- A named way to reach a federation: which FOM, which RTI, which execution.
-- fom_name is a plain name rather than a foreign key: removing a FOM should leave a connection
-- visibly broken in the list, not silently delete it or block the removal.
CREATE TABLE IF NOT EXISTS connection (
    name          TEXT PRIMARY KEY,
    fom_name      TEXT NOT NULL,
    exec_name     TEXT NOT NULL,
    federate_name TEXT NOT NULL DEFAULT '',
    rti_bin       TEXT NOT NULL DEFAULT '',
    rid_file      TEXT NOT NULL DEFAULT '',
    added_utc     TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS setting (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS run (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    started_utc  TEXT NOT NULL,
    finished_utc TEXT,
    fom_a        TEXT,
    fom_b        TEXT,
    exec_a       TEXT,
    exec_b       TEXT,
    self_test    INTEGER NOT NULL DEFAULT 0,
    input_hash   TEXT,
    expected_hash TEXT,
    passed       INTEGER NOT NULL DEFAULT 0,
    failed       INTEGER NOT NULL DEFAULT 0,
    timed_out    INTEGER NOT NULL DEFAULT 0,
    seconds      REAL NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS run_step (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id     INTEGER NOT NULL REFERENCES run(id) ON DELETE CASCADE,
    step       INTEGER NOT NULL,
    entity_id  TEXT,
    outcome    TEXT NOT NULL,
    elapsed_ms INTEGER NOT NULL DEFAULT 0,
    message    TEXT
);

CREATE TABLE IF NOT EXISTS run_field (
    id       INTEGER PRIMARY KEY AUTOINCREMENT,
    step_id  INTEGER NOT NULL REFERENCES run_step(id) ON DELETE CASCADE,
    name     TEXT NOT NULL,
    expected TEXT,
    actual   TEXT,
    ok       INTEGER NOT NULL,
    note     TEXT
);

CREATE INDEX IF NOT EXISTS ix_step_run   ON run_step(run_id);
CREATE INDEX IF NOT EXISTS ix_field_step ON run_field(step_id);
-- the index that makes 'when did this attribute start failing' cheap
CREATE INDEX IF NOT EXISTS ix_field_name ON run_field(name, ok);
");
            // CREATE TABLE IF NOT EXISTS cannot add a column to a table that already exists, so
            // later columns are added one at a time, guarded.
            AddColumn("connection", "assistant_port", "TEXT NOT NULL DEFAULT ''");
            AddColumn("connection", "config_dir", "TEXT NOT NULL DEFAULT ''");
            // Run history was defined before anything wrote to it, and lacked what the Run tab
            // actually shows: the latency per row, why a run stopped, and which pair was tested.
            AddColumn("run_step", "latency_ms", "REAL");
            AddColumn("run", "abort_reason", "TEXT");
            AddColumn("run", "conn_a", "TEXT");
            AddColumn("run", "conn_b", "TEXT");
            AddColumn("run", "input_path", "TEXT");
            AddColumn("run", "expected_path", "TEXT");
            MigratePairs();
        }

        /// <summary>Add a column unless it is already there. SQLite has no ADD COLUMN IF NOT EXISTS.</summary>
        private void AddColumn(string table, string column, string decl)
        {
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(" + table + ")";
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
            }
            Exec("ALTER TABLE " + table + " ADD COLUMN " + column + " " + decl + ";");
        }

        // ------------------------------------------------------------------ assets --

        /// <summary>Import a file into the workspace and record where it came from.</summary>
        public AssetRef ImportAsset(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) return null;

            string stored;
            long size;
            var hash = Workspace.Import(sourcePath, out stored, out size);

            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO asset(hash,file_name,original_path,size,imported_utc) " +
                    "VALUES($h,$n,$p,$s,$t) " +
                    "ON CONFLICT(hash) DO UPDATE SET original_path=excluded.original_path";
                cmd.Parameters.AddWithValue("$h", hash);
                cmd.Parameters.AddWithValue("$n", Path.GetFileName(sourcePath));
                cmd.Parameters.AddWithValue("$p", sourcePath);
                cmd.Parameters.AddWithValue("$s", size);
                cmd.Parameters.AddWithValue("$t", Utc());
                cmd.ExecuteNonQuery();
            }

            return new AssetRef
            {
                Hash = hash,
                FileName = Path.GetFileName(sourcePath),
                OriginalPath = sourcePath,
                StoredPath = stored,
                Size = size,
            };
        }

        public AssetRef GetAsset(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return null;
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "SELECT hash,file_name,original_path,size FROM asset WHERE hash=$h";
                cmd.Parameters.AddWithValue("$h", hash);
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;
                    var a = new AssetRef
                    {
                        Hash = r.GetString(0),
                        FileName = r.GetString(1),
                        OriginalPath = r.IsDBNull(2) ? null : r.GetString(2),
                        Size = r.GetInt64(3),
                    };
                    a.StoredPath = Workspace.PathOf(a.Hash, Path.GetExtension(a.FileName));
                    return a;
                }
            }
        }

        // -------------------------------------------------------------------- FOMs --

        public void UpsertFom(FomEntryRecord rec)
        {
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO fom(name,is_evolved,structure_hash,datatype_hash,mim_hash,module_hashes,added_utc) " +
                    "VALUES($n,$e,$s,$d,$m,$mods,$t) " +
                    "ON CONFLICT(name) DO UPDATE SET is_evolved=excluded.is_evolved, " +
                    "structure_hash=excluded.structure_hash, datatype_hash=excluded.datatype_hash, " +
                    "mim_hash=excluded.mim_hash, module_hashes=excluded.module_hashes";
                cmd.Parameters.AddWithValue("$n", rec.Name);
                cmd.Parameters.AddWithValue("$e", rec.IsEvolved ? 1 : 0);
                cmd.Parameters.AddWithValue("$s", (object)rec.StructureHash ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$d", (object)rec.DatatypeHash ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$m", (object)rec.MimHash ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$mods", rec.ModuleHashes ?? "");
                cmd.Parameters.AddWithValue("$t", Utc());
                cmd.ExecuteNonQuery();
            }
        }

        public void DeleteFom(string name)
        {
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM fom WHERE name=$n";
                cmd.Parameters.AddWithValue("$n", name);
                cmd.ExecuteNonQuery();
            }
        }

        public List<FomEntryRecord> LoadFoms()
        {
            var list = new List<FomEntryRecord>();
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT name,is_evolved,structure_hash,datatype_hash,mim_hash,module_hashes " +
                    "FROM fom ORDER BY name";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new FomEntryRecord
                        {
                            Name = r.GetString(0),
                            IsEvolved = r.GetInt32(1) != 0,
                            StructureHash = r.IsDBNull(2) ? null : r.GetString(2),
                            DatatypeHash = r.IsDBNull(3) ? null : r.GetString(3),
                            MimHash = r.IsDBNull(4) ? null : r.GetString(4),
                            ModuleHashes = r.IsDBNull(5) ? "" : r.GetString(5),
                        });
                    }
                }
            }
            foreach (var rec in list) Rehydrate(rec);
            return list;
        }

        /// <summary>Turn stored hashes back into usable workspace paths.</summary>
        public void Rehydrate(FomEntryRecord rec)
        {
            rec.StructureFile = PathFor(rec.StructureHash);
            rec.DatatypeFile = PathFor(rec.DatatypeHash);
            rec.Mim = PathFor(rec.MimHash);

            var parts = new List<string>();
            foreach (var h in (rec.ModuleHashes ?? "").Split(','))
                if (!string.IsNullOrWhiteSpace(h))
                {
                    var p = PathFor(h.Trim());
                    if (p != null) parts.Add(p);
                }
            rec.Modules = string.Join(",", parts.ToArray());

            var a = GetAsset(rec.StructureHash);
            rec.OriginalPath = a == null ? null : a.OriginalPath;
        }

        private string PathFor(string hash)
        {
            var a = GetAsset(hash);
            return a == null ? null : a.StoredPath;
        }

        // ------------------------------------------------------------- connections --

        public void UpsertConnection(ConnectionRecord rec)
        {
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO connection(name,fom_name,exec_name,federate_name,rti_bin,rid_file,assistant_port,config_dir,added_utc) " +
                    "VALUES($n,$f,$e,$fd,$b,$r,$ap,$cd,$t) " +
                    "ON CONFLICT(name) DO UPDATE SET fom_name=excluded.fom_name, " +
                    "exec_name=excluded.exec_name, federate_name=excluded.federate_name, " +
                    "rti_bin=excluded.rti_bin, rid_file=excluded.rid_file, " +
                    "assistant_port=excluded.assistant_port, config_dir=excluded.config_dir";
                cmd.Parameters.AddWithValue("$n", rec.Name);
                cmd.Parameters.AddWithValue("$f", rec.FomName ?? "");
                cmd.Parameters.AddWithValue("$e", rec.ExecName ?? "");
                cmd.Parameters.AddWithValue("$fd", rec.FederateName ?? "");
                cmd.Parameters.AddWithValue("$b", rec.Rti.RtiBin ?? "");
                cmd.Parameters.AddWithValue("$r", rec.Rti.RidFile ?? "");
                cmd.Parameters.AddWithValue("$ap", rec.Rti.AssistantPort ?? "");
                cmd.Parameters.AddWithValue("$cd", rec.Rti.ConfigDir ?? "");
                cmd.Parameters.AddWithValue("$t", Utc());
                cmd.ExecuteNonQuery();
            }
        }

        public void DeleteConnection(string name)
        {
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM connection WHERE name=$n";
                cmd.Parameters.AddWithValue("$n", name);
                cmd.ExecuteNonQuery();
            }
        }

        public List<ConnectionRecord> LoadConnections()
        {
            var list = new List<ConnectionRecord>();
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT name,fom_name,exec_name,federate_name,rti_bin,rid_file,assistant_port,config_dir " +
                    "FROM connection ORDER BY name";
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        var rec = new ConnectionRecord
                        {
                            Name = r.GetString(0),
                            FomName = r.GetString(1),
                            ExecName = r.GetString(2),
                            FederateName = r.GetString(3),
                        };
                        rec.Rti.RtiBin = r.GetString(4);
                        rec.Rti.RidFile = r.GetString(5);
                        rec.Rti.AssistantPort = r.IsDBNull(6) ? "" : r.GetString(6);
                        rec.Rti.ConfigDir = r.IsDBNull(7) ? "" : r.GetString(7);
                        list.Add(rec);
                    }
            }
            return list;
        }

        /// <summary>
        /// Copy a RID file into the workspace unless it is already there. The rid decides WHICH RTI
        /// a federate joins, so a run that cannot name the exact rid it used is not reproducible.
        /// Kept under its own name in <c>rti\</c> rather than content-addressed: the RTI resolves
        /// FOM files relative to the rid's folder, and a readable name helps when reading a rid by
        /// hand.
        /// </summary>
        public string ImportRid(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return path;
            if (path.StartsWith(Workspace.Root, StringComparison.OrdinalIgnoreCase)) return path;
            try
            {
                var kept = Path.Combine(Workspace.Root, "rti");
                Directory.CreateDirectory(kept);
                var dest = Path.Combine(kept, Path.GetFileName(path));
                File.Copy(path, dest, true);
                ImportAsset(path);                 // provenance + hash for the run record
                return dest;
            }
            catch { return path; }
        }

        // -------------------------------------------------------------- FOM models --

        /// <summary>
        /// The stored model for this exact combination of files and parser, or null.
        ///
        /// A body that will not read back is treated as a miss and deleted: re-parsing costs
        /// milliseconds, whereas a half-read model would resolve byte offsets wrongly and blame
        /// the broker for it.
        /// </summary>
        public FomModel GetModel(string key)
        {
            string body = null;
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "SELECT body FROM fom_model WHERE key=$k";
                cmd.Parameters.AddWithValue("$k", key);
                var v = cmd.ExecuteScalar();
                if (v != null && v != DBNull.Value) body = Convert.ToString(v);
            }
            if (body == null) return null;

            try { return FomModelCodec.Read(body); }
            catch { DeleteModel(key); return null; }
        }

        public void PutModel(string key, FomModel model)
        {
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO fom_model(key,parser_version,classes,interactions,datatypes,body,built_utc) " +
                    "VALUES($k,$v,$c,$i,$d,$b,$t) " +
                    "ON CONFLICT(key) DO UPDATE SET body=excluded.body, built_utc=excluded.built_utc";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$v", FomModelCodec.Version);
                cmd.Parameters.AddWithValue("$c", model.Classes.Count);
                cmd.Parameters.AddWithValue("$i", model.Interactions.Count);
                cmd.Parameters.AddWithValue("$d", model.Types.Count);
                cmd.Parameters.AddWithValue("$b", FomModelCodec.Write(model));
                cmd.Parameters.AddWithValue("$t", Utc());
                cmd.ExecuteNonQuery();
            }
        }

        public void DeleteModel(string key)
        {
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM fom_model WHERE key=$k";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Drop every model this build can no longer read. Called at startup so a parser change
        /// cleans up after itself instead of leaving dead rows behind for good.
        /// </summary>
        public int PurgeStaleModels()
        {
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM fom_model WHERE parser_version <> $v";
                cmd.Parameters.AddWithValue("$v", FomModelCodec.Version);
                return cmd.ExecuteNonQuery();
            }
        }

        // ---------------------------------------------------------------- settings --

        public string Get(string key, string dflt = "")
        {
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "SELECT value FROM setting WHERE key=$k";
                cmd.Parameters.AddWithValue("$k", key);
                var v = cmd.ExecuteScalar();
                return v == null || v == DBNull.Value ? dflt : Convert.ToString(v);
            }
        }

        public void Set(string key, string value)
        {
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO setting(key,value) VALUES($k,$v) " +
                                  "ON CONFLICT(key) DO UPDATE SET value=excluded.value";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$v", value ?? "");
                cmd.ExecuteNonQuery();
            }
        }

        // -------------------------------------------------------------------- runs --

        public long BeginRun(string fomA, string fomB, string execA, string execB, bool selfTest,
                             string inputHash, string expectedHash,
                             string connA = null, string connB = null,
                             string inputPath = null, string expectedPath = null)
        {
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO run(started_utc,fom_a,fom_b,exec_a,exec_b,self_test,input_hash,expected_hash," +
                    "conn_a,conn_b,input_path,expected_path) " +
                    "VALUES($t,$fa,$fb,$ea,$eb,$st,$ih,$eh,$ca,$cb,$ip,$ep); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$ca", (object)connA ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$cb", (object)connB ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ip", (object)inputPath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ep", (object)expectedPath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$t", Utc());
                cmd.Parameters.AddWithValue("$fa", (object)fomA ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$fb", (object)fomB ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ea", (object)execA ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$eb", (object)execB ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$st", selfTest ? 1 : 0);
                cmd.Parameters.AddWithValue("$ih", (object)inputHash ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$eh", (object)expectedHash ?? DBNull.Value);
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
        }

        public void SaveStep(long runId, StepResult step)
        {
            long stepId;
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO run_step(run_id,step,entity_id,outcome,elapsed_ms,message,latency_ms) " +
                    "VALUES($r,$s,$e,$o,$ms,$m,$l); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$r", runId);
                cmd.Parameters.AddWithValue("$s", step.Step);
                cmd.Parameters.AddWithValue("$e", (object)step.EntityId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$l", step.Latency.HasValue
                    ? (object)step.Latency.Value.TotalMilliseconds : DBNull.Value);
                cmd.Parameters.AddWithValue("$o", step.Outcome.ToString());
                cmd.Parameters.AddWithValue("$ms", (long)step.Elapsed.TotalMilliseconds);
                cmd.Parameters.AddWithValue("$m", (object)step.Message ?? DBNull.Value);
                stepId = Convert.ToInt64(cmd.ExecuteScalar());
            }

            foreach (var f in step.Fields)
            {
                using (var cmd = _cn.CreateCommand())
                {
                    cmd.CommandText =
                        "INSERT INTO run_field(step_id,name,expected,actual,ok,note) VALUES($s,$n,$e,$a,$o,$t)";
                    cmd.Parameters.AddWithValue("$s", stepId);
                    cmd.Parameters.AddWithValue("$n", f.Name ?? "");
                    cmd.Parameters.AddWithValue("$e", (object)f.Expected ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$a", (object)f.Actual ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$o", f.Ok ? 1 : 0);
                    cmd.Parameters.AddWithValue("$t", (object)f.Note ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
            }
            InsertParts(stepId, step);
        }

        public void EndRun(long runId, RunReport report)
        {
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText =
                    "UPDATE run SET finished_utc=$t, passed=$p, failed=$f, timed_out=$x, seconds=$s, " +
                    "abort_reason=$ab WHERE id=$id";
                cmd.Parameters.AddWithValue("$ab", (object)report.AbortReason ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$t", Utc());
                cmd.Parameters.AddWithValue("$p", report.Passed);
                cmd.Parameters.AddWithValue("$f", report.Failed);
                cmd.Parameters.AddWithValue("$x", report.TimedOut);
                cmd.Parameters.AddWithValue("$s", report.Duration.TotalSeconds);
                cmd.Parameters.AddWithValue("$id", runId);
                cmd.ExecuteNonQuery();
            }
        }

        public List<RunSummary> RecentRuns(int limit = 100)
        {
            var list = new List<RunSummary>();
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT id,started_utc,fom_a,fom_b,exec_a,exec_b,self_test,passed,failed,timed_out,seconds," +
                    "conn_a,conn_b,abort_reason,input_path,expected_path,finished_utc " +
                    "FROM run ORDER BY id DESC LIMIT $n";
                cmd.Parameters.AddWithValue("$n", limit);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        list.Add(new RunSummary
                        {
                            Id = r.GetInt64(0),
                            StartedUtc = DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture,
                                                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                            FomA = Str(r, 2),
                            FomB = Str(r, 3),
                            ExecA = Str(r, 4),
                            ExecB = Str(r, 5),
                            SelfTest = r.GetInt32(6) != 0,
                            Passed = r.GetInt32(7),
                            Failed = r.GetInt32(8),
                            TimedOut = r.GetInt32(9),
                            Seconds = r.GetDouble(10),
                            ConnA = Str(r, 11),
                            ConnB = Str(r, 12),
                            AbortReason = Str(r, 13),
                            InputPath = Str(r, 14),
                            ExpectedPath = Str(r, 15),
                            Finished = !r.IsDBNull(16),
                        });
                }
            }
            return list;
        }

        /// <summary>
        /// The question this database exists to answer: for one field, how has it fared over time?
        /// </summary>
        public List<string> FieldHistory(string fieldName, int limit = 50)
        {
            var rows = new List<string>();
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT r.id, r.started_utc, f.ok, f.expected, f.actual, f.note " +
                    "FROM run_field f " +
                    "JOIN run_step s ON s.id = f.step_id " +
                    "JOIN run r ON r.id = s.run_id " +
                    "WHERE f.name = $n ORDER BY r.id DESC LIMIT $l";
                cmd.Parameters.AddWithValue("$n", fieldName);
                cmd.Parameters.AddWithValue("$l", limit);
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        rows.Add(string.Format(CultureInfo.InvariantCulture,
                            "run {0}  {1}  {2}  expected={3} actual={4} {5}",
                            r.GetInt64(0), r.GetString(1), r.GetInt32(2) != 0 ? "OK  " : "FAIL",
                            Str(r, 3), Str(r, 4), Str(r, 5)));
            }
            return rows;
        }

        public List<StepResult> StepsOf(long runId)
        {
            var steps = new List<StepResult>();
            var ids = new List<long>();
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "SELECT id,step,outcome,elapsed_ms,message,entity_id,latency_ms " +
                                  "FROM run_step WHERE run_id=$r ORDER BY step";
                cmd.Parameters.AddWithValue("$r", runId);
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        Outcome o;
                        if (!Enum.TryParse(r.GetString(2), out o)) o = Outcome.Skipped;
                        steps.Add(new StepResult
                        {
                            Step = r.GetInt32(1),
                            Outcome = o,
                            Elapsed = TimeSpan.FromMilliseconds(r.GetInt64(3)),
                            Message = Str(r, 4),
                            EntityId = Str(r, 5),
                            // FromTicks, not FromMilliseconds: on .NET Framework the latter rounds to a whole
                            // millisecond, so a saved 19.2 ms came back as 19.0.
                            Latency = r.IsDBNull(6) ? (TimeSpan?)null
                                    : TimeSpan.FromTicks((long)Math.Round(r.GetDouble(6) * TimeSpan.TicksPerMillisecond)),
                        });
                        ids.Add(r.GetInt64(0));
                    }
            }
            for (int i = 0; i < steps.Count; i++)
            {
                using (var cmd = _cn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name,expected,actual,ok,note FROM run_field WHERE step_id=$s ORDER BY id";
                    cmd.Parameters.AddWithValue("$s", ids[i]);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            steps[i].Fields.Add(new FieldResult
                            {
                                Name = r.GetString(0),
                                Expected = Str(r, 1),
                                Actual = Str(r, 2),
                                Ok = r.GetInt32(3) != 0,
                                Note = Str(r, 4),
                            });
                }
            }
            LoadParts(runId, steps);
            return steps;
        }

        // ------------------------------------------------------------------- utils --

        private static string Str(SqliteDataReader r, int i) { return r.IsDBNull(i) ? "" : r.GetString(i); }
        private static string Utc() { return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); }

        private void Exec(string sql)
        {
            using (var cmd = _cn.CreateCommand()) { cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        }

        public void Dispose()
        {
            try { _cn.Close(); _cn.Dispose(); } catch { }
        }
    }
}
