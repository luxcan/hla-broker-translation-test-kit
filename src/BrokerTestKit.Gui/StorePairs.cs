using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BrokerTestKit.Engine;
using Microsoft.Data.Sqlite;

namespace BrokerTestKit.Gui
{
    /// <summary>
    /// One broker crossing: what is sent on A, what must arrive on B. Each direction is its own pair.
    /// The two sides are ordinary <c>connection</c> rows, so the run path is unchanged.
    /// </summary>
    public sealed class PairRecord
    {
        public long Id;
        public string Name = "";
        public string ConnA = "";
        public string ConnB = "";
        /// <summary>Both agents on side A, identity mapping - proves the rig without the broker.</summary>
        public bool SelfTest;
        public DateTime CreatedUtc;

        // filled for lists
        public int TestCaseCount;
        public RunSummary LastRun;

        /// <summary>Connection names a pair owns outright. Older pairs point at shared ones.</summary>
        public static string OwnConnectionName(long pairId, string side)
        {
            return "pair-" + pairId.ToString(CultureInfo.InvariantCulture) + "-" + side;
        }

        public override string ToString() { return Name; }
    }

    /// <summary>One behaviour of the broker to check (REQ-13): N Send sheets on A, M Receive sheets on B.</summary>
    public sealed class TestCaseRecord
    {
        public long Id;
        public long PairId;
        public string Name = "";
        /// <summary>Null = the RunOptions default.</summary>
        public double? TimeoutSeconds;
        /// <summary>Set when a migration could not find the sheets, e.g. "sheets missing — re-upload".</summary>
        public string Note;
        public DateTime CreatedUtc, UpdatedUtc;
        public readonly List<TestCaseSheet> Sheets = new List<TestCaseSheet>();

        // filled for lists
        public RunSummary LastRun;

        public IEnumerable<TestCaseSheet> Send { get { foreach (var s in Sheets) if (s.IsSend) yield return s; } }
        public IEnumerable<TestCaseSheet> Receive { get { foreach (var s in Sheets) if (!s.IsSend) yield return s; } }

        public override string ToString() { return Name; }
    }

    /// <summary>One sheet of a test case. The CSV text is a content-addressed asset.</summary>
    public sealed class TestCaseSheet
    {
        public long Id;
        public string Side = "send";       // "send" | "receive"
        public string ClassName = "";
        public string AssetHash = "";
        public int Ord;
        public int Rows;                   // filled on load, for display

        public bool IsSend { get { return Side == "send"; } }
    }

    /// <summary>Filter for All results.</summary>
    public sealed class RunFilter
    {
        public long? PairId;
        /// <summary>null = any; "Pass", "Fail", "Timeout".</summary>
        public string Outcome;
        public string TestCaseText;
    }

    public sealed class RunTally
    {
        public int Total, Pass, Fail, TimedOut;
    }

    public sealed partial class Store
    {
        // ------------------------------------------------------------------ schema --

        private void MigratePairs()
        {
            // conn_a / conn_b are plain names, like connection.fom_name, NOT foreign keys: a
            // migrated pair may name a connection that was removed long ago, and that must not
            // stop the database opening. The pair then shows as broken until edited.
            Exec(@"
CREATE TABLE IF NOT EXISTS pair (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    name        TEXT NOT NULL UNIQUE,
    conn_a      TEXT NOT NULL,
    conn_b      TEXT NOT NULL,
    self_test   INTEGER NOT NULL DEFAULT 0,
    created_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS test_case (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    pair_id     INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
    name        TEXT NOT NULL,
    timeout_s   REAL,
    note        TEXT,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    UNIQUE(pair_id, name)
);

-- one row per sheet; REQ-13 allows several per side
CREATE TABLE IF NOT EXISTS test_case_sheet (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    test_case_id INTEGER NOT NULL REFERENCES test_case(id) ON DELETE CASCADE,
    side         TEXT NOT NULL CHECK (side IN ('send','receive')),
    class_name   TEXT NOT NULL,
    asset_hash   TEXT NOT NULL REFERENCES asset(hash),
    ord          INTEGER NOT NULL DEFAULT 0
);

-- REQ-13: one row per expected Receive row inside a step
CREATE TABLE IF NOT EXISTS run_part (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    step_id    INTEGER NOT NULL REFERENCES run_step(id) ON DELETE CASCADE,
    class_name TEXT NOT NULL,
    entity_id  TEXT,
    outcome    TEXT NOT NULL,
    message    TEXT
);
CREATE INDEX IF NOT EXISTS ix_part_step ON run_part(step_id);
CREATE INDEX IF NOT EXISTS ix_tc_pair ON test_case(pair_id);
");
            AddColumn("run", "pair_id", "INTEGER REFERENCES pair(id) ON DELETE CASCADE");
            AddColumn("run", "test_case_id", "INTEGER REFERENCES test_case(id) ON DELETE CASCADE");
            Exec("CREATE INDEX IF NOT EXISTS ix_run_pair ON run(pair_id);");
            Exec("CREATE INDEX IF NOT EXISTS ix_run_tc ON run(test_case_id);");
        }

        // ------------------------------------------------------------- text assets --

        /// <summary>
        /// Store a sheet as a content-addressed asset. Normalised to UTF-8 without a BOM (R4): an
        /// uploaded file written for Excel carries one, and the CSV parser would read it as part of
        /// the first directive.
        /// </summary>
        public string ImportText(string text, string fileName)
        {
            text = (text ?? "").TrimStart('﻿');
            var dir = Path.Combine(Path.GetTempPath(), "btk-import");
            Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, Guid.NewGuid().ToString("N") + "-" + SafeFile(fileName));
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            try
            {
                var a = ImportAsset(tmp);
                using (var cmd = _cn.CreateCommand())
                {
                    // the temp path is meaningless as provenance; keep the sheet's own name
                    cmd.CommandText = "UPDATE asset SET file_name=$n, original_path=NULL WHERE hash=$h";
                    cmd.Parameters.AddWithValue("$n", SafeFile(fileName));
                    cmd.Parameters.AddWithValue("$h", a.Hash);
                    cmd.ExecuteNonQuery();
                }
                return a.Hash;
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        public string ReadAssetText(string hash)
        {
            var a = GetAsset(hash);
            if (a == null || a.StoredPath == null || !File.Exists(a.StoredPath)) return null;
            return File.ReadAllText(a.StoredPath).TrimStart('﻿');
        }

        private static string SafeFile(string name)
        {
            var s = string.IsNullOrWhiteSpace(name) ? "sheet.csv" : name.Trim();
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
            if (!s.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) s += ".csv";
            return s;
        }

        // ------------------------------------------------------------------- pairs --

        public List<PairRecord> Pairs()
        {
            var list = new List<PairRecord>();
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT p.id,p.name,p.conn_a,p.conn_b,p.self_test,p.created_utc," +
                    "(SELECT COUNT(*) FROM test_case t WHERE t.pair_id=p.id) " +
                    "FROM pair p ORDER BY p.name COLLATE NOCASE";
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) list.Add(ReadPair(r));
            }
            foreach (var p in list) p.LastRun = LastRunOf(p.Id, null);
            return list;
        }

        public PairRecord Pair(long id)
        {
            PairRecord p = null;
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT p.id,p.name,p.conn_a,p.conn_b,p.self_test,p.created_utc," +
                    "(SELECT COUNT(*) FROM test_case t WHERE t.pair_id=p.id) FROM pair p WHERE p.id=$id";
                cmd.Parameters.AddWithValue("$id", id);
                using (var r = cmd.ExecuteReader()) if (r.Read()) p = ReadPair(r);
            }
            if (p != null) p.LastRun = LastRunOf(p.Id, null);
            return p;
        }

        private static PairRecord ReadPair(SqliteDataReader r)
        {
            return new PairRecord
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                ConnA = r.GetString(2),
                ConnB = r.GetString(3),
                SelfTest = r.GetInt32(4) != 0,
                CreatedUtc = ParseUtc(r.GetString(5)),
                TestCaseCount = r.GetInt32(6),
            };
        }

        /// <summary>Insert (Id 0) or update. Returns the id. Throws on a duplicate name.</summary>
        public long SavePair(PairRecord p)
        {
            using (var cmd = _cn.CreateCommand())
            {
                if (p.Id == 0)
                {
                    cmd.CommandText =
                        "INSERT INTO pair(name,conn_a,conn_b,self_test,created_utc) VALUES($n,$a,$b,$s,$t); " +
                        "SELECT last_insert_rowid();";
                    cmd.Parameters.AddWithValue("$t", Utc());
                }
                else
                {
                    cmd.CommandText = "UPDATE pair SET name=$n, conn_a=$a, conn_b=$b, self_test=$s WHERE id=$id; SELECT $id;";
                    cmd.Parameters.AddWithValue("$id", p.Id);
                }
                cmd.Parameters.AddWithValue("$n", p.Name);
                cmd.Parameters.AddWithValue("$a", p.ConnA ?? "");
                cmd.Parameters.AddWithValue("$b", p.ConnB ?? "");
                cmd.Parameters.AddWithValue("$s", p.SelfTest ? 1 : 0);
                p.Id = Convert.ToInt64(cmd.ExecuteScalar());
            }
            return p.Id;
        }

        /// <summary>
        /// Hard delete (D5): the pair, its test cases, and every run of them. Sheet assets stay in
        /// assets\ - content-addressed and possibly shared. Connections the pair owns go too;
        /// shared ones from before pairs existed are left alone.
        /// </summary>
        public void DeletePair(long id)
        {
            using (var tx = _cn.BeginTransaction())
            {
                Run(tx, "DELETE FROM run WHERE pair_id=$id OR test_case_id IN (SELECT id FROM test_case WHERE pair_id=$id)", id);
                Run(tx, "DELETE FROM test_case WHERE pair_id=$id", id);
                Run(tx, "DELETE FROM pair WHERE id=$id", id);
                using (var cmd = _cn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM connection WHERE name IN ($a,$b)";
                    cmd.Parameters.AddWithValue("$a", PairRecord.OwnConnectionName(id, "A"));
                    cmd.Parameters.AddWithValue("$b", PairRecord.OwnConnectionName(id, "B"));
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        private void Run(SqliteTransaction tx, string sql, long id)
        {
            using (var cmd = _cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }

        public int RunCountOfPair(long pairId)
        {
            return Scalar("SELECT COUNT(*) FROM run WHERE pair_id=$id OR test_case_id IN " +
                          "(SELECT id FROM test_case WHERE pair_id=$id)", pairId);
        }

        public int RunCountOfTestCase(long testCaseId)
        {
            return Scalar("SELECT COUNT(*) FROM run WHERE test_case_id=$id", testCaseId);
        }

        public int CountFoms() { return Scalar("SELECT COUNT(*) FROM fom", 0); }
        public int CountPairs() { return Scalar("SELECT COUNT(*) FROM pair", 0); }

        /// <summary>How many pairs use this FOM on either side - the registry's "Used by" and its remove guard.</summary>
        public int FomUsage(string fomName)
        {
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT COUNT(*) FROM pair p WHERE EXISTS (SELECT 1 FROM connection c " +
                    "WHERE c.fom_name=$f COLLATE NOCASE AND (c.name=p.conn_a OR c.name=p.conn_b))";
                cmd.Parameters.AddWithValue("$f", fomName ?? "");
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        private int Scalar(string sql, long id)
        {
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$id", id);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        // -------------------------------------------------------------- test cases --

        public List<TestCaseRecord> TestCases(long pairId)
        {
            var list = new List<TestCaseRecord>();
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "SELECT id,pair_id,name,timeout_s,note,created_utc,updated_utc " +
                                  "FROM test_case WHERE pair_id=$p ORDER BY name COLLATE NOCASE";
                cmd.Parameters.AddWithValue("$p", pairId);
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) list.Add(ReadTestCase(r));
            }
            foreach (var t in list) { LoadSheets(t); t.LastRun = LastRunOf(null, t.Id); }
            return list;
        }

        public TestCaseRecord TestCase(long id)
        {
            TestCaseRecord t = null;
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "SELECT id,pair_id,name,timeout_s,note,created_utc,updated_utc FROM test_case WHERE id=$id";
                cmd.Parameters.AddWithValue("$id", id);
                using (var r = cmd.ExecuteReader()) if (r.Read()) t = ReadTestCase(r);
            }
            if (t != null) { LoadSheets(t); t.LastRun = LastRunOf(null, t.Id); }
            return t;
        }

        private static TestCaseRecord ReadTestCase(SqliteDataReader r)
        {
            return new TestCaseRecord
            {
                Id = r.GetInt64(0),
                PairId = r.GetInt64(1),
                Name = r.GetString(2),
                TimeoutSeconds = r.IsDBNull(3) ? (double?)null : r.GetDouble(3),
                Note = r.IsDBNull(4) ? null : r.GetString(4),
                CreatedUtc = ParseUtc(r.GetString(5)),
                UpdatedUtc = ParseUtc(r.GetString(6)),
            };
        }

        private void LoadSheets(TestCaseRecord t)
        {
            t.Sheets.Clear();
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "SELECT id,side,class_name,asset_hash,ord FROM test_case_sheet " +
                                  "WHERE test_case_id=$t ORDER BY side DESC, ord";      // send first
                cmd.Parameters.AddWithValue("$t", t.Id);
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        t.Sheets.Add(new TestCaseSheet
                        {
                            Id = r.GetInt64(0), Side = r.GetString(1), ClassName = r.GetString(2),
                            AssetHash = r.GetString(3), Ord = r.GetInt32(4),
                        });
            }
            foreach (var s in t.Sheets) s.Rows = CountDataRows(ReadAssetText(s.AssetHash));
        }

        /// <summary>Data rows in a sheet's text: lines after the header that are not comments.</summary>
        public static int CountDataRows(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int n = 0; bool header = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (!header) { header = true; continue; }
                if (line.Replace(",", "").Trim().Length > 0) n++;
            }
            return n;
        }

        /// <summary>Insert or update a test case and replace its sheets, in one transaction.</summary>
        public long SaveTestCase(TestCaseRecord t)
        {
            using (var tx = _cn.BeginTransaction())
            {
                using (var cmd = _cn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    if (t.Id == 0)
                    {
                        cmd.CommandText =
                            "INSERT INTO test_case(pair_id,name,timeout_s,note,created_utc,updated_utc) " +
                            "VALUES($p,$n,$to,$note,$t,$t); SELECT last_insert_rowid();";
                        cmd.Parameters.AddWithValue("$p", t.PairId);
                    }
                    else
                    {
                        cmd.CommandText = "UPDATE test_case SET name=$n, timeout_s=$to, note=$note, updated_utc=$t WHERE id=$id; SELECT $id;";
                        cmd.Parameters.AddWithValue("$id", t.Id);
                    }
                    cmd.Parameters.AddWithValue("$n", t.Name);
                    cmd.Parameters.AddWithValue("$to", t.TimeoutSeconds.HasValue ? (object)t.TimeoutSeconds.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("$note", (object)t.Note ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$t", Utc());
                    t.Id = Convert.ToInt64(cmd.ExecuteScalar());
                }

                Run(tx, "DELETE FROM test_case_sheet WHERE test_case_id=$id", t.Id);
                int ord = 0;
                foreach (var s in t.Sheets)
                    using (var cmd = _cn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "INSERT INTO test_case_sheet(test_case_id,side,class_name,asset_hash,ord) " +
                                          "VALUES($t,$s,$c,$h,$o)";
                        cmd.Parameters.AddWithValue("$t", t.Id);
                        cmd.Parameters.AddWithValue("$s", s.Side);
                        cmd.Parameters.AddWithValue("$c", s.ClassName ?? "");
                        cmd.Parameters.AddWithValue("$h", s.AssetHash);
                        cmd.Parameters.AddWithValue("$o", s.Ord = ord++);
                        cmd.ExecuteNonQuery();
                    }
                tx.Commit();
            }
            return t.Id;
        }

        /// <summary>Hard delete (D5): the test case and its runs.</summary>
        public void DeleteTestCase(long id)
        {
            using (var tx = _cn.BeginTransaction())
            {
                Run(tx, "DELETE FROM run WHERE test_case_id=$id", id);
                Run(tx, "DELETE FROM test_case WHERE id=$id", id);
                tx.Commit();
            }
        }

        // -------------------------------------------------------------------- runs --

        public long BeginRun(PairRecord pair, TestCaseRecord tc, ConnectionRecord a, ConnectionRecord b)
        {
            var id = BeginRun(a.Fom == null ? a.FomName : a.Fom.Name, b.Fom == null ? b.FomName : b.Fom.Name,
                              a.ExecName, b.ExecName, pair.SelfTest, null, null, a.Name, b.Name, null, null);
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "UPDATE run SET pair_id=$p, test_case_id=$t WHERE id=$id";
                cmd.Parameters.AddWithValue("$p", pair.Id);
                cmd.Parameters.AddWithValue("$t", tc.Id);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            return id;
        }

        private const string RunColumns =
            "r.id,r.started_utc,r.fom_a,r.fom_b,r.exec_a,r.exec_b,r.self_test,r.passed,r.failed,r.timed_out,r.seconds," +
            "r.conn_a,r.conn_b,r.abort_reason,r.input_path,r.expected_path,r.finished_utc," +
            "r.pair_id,r.test_case_id,p.name,t.name";
        private const string RunFrom =
            " FROM run r LEFT JOIN pair p ON p.id=r.pair_id LEFT JOIN test_case t ON t.id=r.test_case_id ";

        private List<RunSummary> ReadRuns(SqliteCommand cmd)
        {
            var list = new List<RunSummary>();
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    list.Add(new RunSummary
                    {
                        Id = r.GetInt64(0),
                        StartedUtc = ParseUtc(r.GetString(1)),
                        FomA = Str(r, 2), FomB = Str(r, 3), ExecA = Str(r, 4), ExecB = Str(r, 5),
                        SelfTest = r.GetInt32(6) != 0,
                        Passed = r.GetInt32(7), Failed = r.GetInt32(8), TimedOut = r.GetInt32(9),
                        Seconds = r.GetDouble(10),
                        ConnA = Str(r, 11), ConnB = Str(r, 12), AbortReason = Str(r, 13),
                        InputPath = Str(r, 14), ExpectedPath = Str(r, 15),
                        Finished = !r.IsDBNull(16),
                        PairId = r.IsDBNull(17) ? (long?)null : r.GetInt64(17),
                        TestCaseId = r.IsDBNull(18) ? (long?)null : r.GetInt64(18),
                        PairName = Str(r, 19),
                        TestCaseName = Str(r, 20),
                    });
            foreach (var s in list) s.LatencyP50 = LatencyP50(s.Id);
            return list;
        }

        /// <summary>Median broker latency of one run, ms, or null when no row produced one.</summary>
        private double? LatencyP50(long runId)
        {
            var ms = new List<double>();
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "SELECT latency_ms FROM run_step WHERE run_id=$r AND latency_ms IS NOT NULL";
                cmd.Parameters.AddWithValue("$r", runId);
                using (var r = cmd.ExecuteReader()) while (r.Read()) ms.Add(r.GetDouble(0));
            }
            if (ms.Count == 0) return null;
            ms.Sort();
            var i = (int)Math.Ceiling(0.5 * ms.Count) - 1;
            return ms[Math.Max(0, i)];
        }

        public List<RunSummary> RecentRunsLinked(int limit)
        {
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "SELECT " + RunColumns + RunFrom + "ORDER BY r.id DESC LIMIT $n";
                cmd.Parameters.AddWithValue("$n", limit);
                return ReadRuns(cmd);
            }
        }

        public RunSummary RunById(long id)
        {
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "SELECT " + RunColumns + RunFrom + "WHERE r.id=$id";
                cmd.Parameters.AddWithValue("$id", id);
                var l = ReadRuns(cmd);
                return l.Count == 0 ? null : l[0];
            }
        }

        public List<RunSummary> RunsOfTestCase(long testCaseId, int limit)
        {
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "SELECT " + RunColumns + RunFrom + "WHERE r.test_case_id=$t ORDER BY r.id DESC LIMIT $n";
                cmd.Parameters.AddWithValue("$t", testCaseId);
                cmd.Parameters.AddWithValue("$n", limit);
                return ReadRuns(cmd);
            }
        }

        public List<RunSummary> RunsOfPair(long pairId, int limit)
        {
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "SELECT " + RunColumns + RunFrom + "WHERE r.pair_id=$p ORDER BY r.id DESC LIMIT $n";
                cmd.Parameters.AddWithValue("$p", pairId);
                cmd.Parameters.AddWithValue("$n", limit);
                return ReadRuns(cmd);
            }
        }

        private RunSummary LastRunOf(long? pairId, long? testCaseId)
        {
            var l = testCaseId.HasValue ? RunsOfTestCase(testCaseId.Value, 1) : RunsOfPair(pairId.Value, 1);
            return l.Count == 0 ? null : l[0];
        }

        /// <summary>
        /// Outcome as SQL, matching <see cref="RunSummary.Result"/>: Timeout only when nothing
        /// passed or failed and something timed out; Fail on any failure or abort; else Pass.
        /// </summary>
        private const string OutcomeSql =
            "(CASE WHEN r.finished_utc IS NULL OR (r.abort_reason IS NOT NULL AND r.abort_reason<>'') THEN 'Fail' " +
            "WHEN r.failed>0 THEN 'Fail' WHEN r.timed_out>0 THEN 'Timeout' WHEN r.passed>0 THEN 'Pass' ELSE 'Fail' END)";

        private static string Where(RunFilter f, SqliteCommand cmd)
        {
            var parts = new List<string>();
            if (f != null && f.PairId.HasValue) { parts.Add("r.pair_id=$fp"); cmd.Parameters.AddWithValue("$fp", f.PairId.Value); }
            if (f != null && !string.IsNullOrEmpty(f.Outcome)) { parts.Add(OutcomeSql + "=$fo"); cmd.Parameters.AddWithValue("$fo", f.Outcome); }
            if (f != null && !string.IsNullOrWhiteSpace(f.TestCaseText))
            {
                parts.Add("t.name LIKE $ft");
                cmd.Parameters.AddWithValue("$ft", "%" + f.TestCaseText.Trim() + "%");
            }
            return parts.Count == 0 ? "" : "WHERE " + string.Join(" AND ", parts.ToArray()) + " ";
        }

        public List<RunSummary> Runs(RunFilter f, int offset, int limit)
        {
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "SELECT " + RunColumns + RunFrom + Where(f, cmd) + "ORDER BY r.id DESC LIMIT $n OFFSET $o";
                cmd.Parameters.AddWithValue("$n", limit);
                cmd.Parameters.AddWithValue("$o", offset);
                return ReadRuns(cmd);
            }
        }

        public RunTally Tally(RunFilter f)
        {
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*), " +
                    "SUM(CASE WHEN " + OutcomeSql + "='Pass' THEN 1 ELSE 0 END), " +
                    "SUM(CASE WHEN " + OutcomeSql + "='Fail' THEN 1 ELSE 0 END), " +
                    "SUM(CASE WHEN " + OutcomeSql + "='Timeout' THEN 1 ELSE 0 END)" + RunFrom + Where(f, cmd);
                using (var r = cmd.ExecuteReader())
                {
                    r.Read();
                    return new RunTally
                    {
                        Total = r.GetInt32(0),
                        Pass = r.IsDBNull(1) ? 0 : r.GetInt32(1),
                        Fail = r.IsDBNull(2) ? 0 : r.GetInt32(2),
                        TimedOut = r.IsDBNull(3) ? 0 : r.GetInt32(3),
                    };
                }
            }
        }

        // ---------------------------------------------------------- per-class parts --

        private void InsertParts(long stepId, StepResult step)
        {
            foreach (var p in step.Parts)
                using (var cmd = _cn.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO run_part(step_id,class_name,entity_id,outcome,message) VALUES($s,$c,$e,$o,$m)";
                    cmd.Parameters.AddWithValue("$s", stepId);
                    cmd.Parameters.AddWithValue("$c", p.ClassName ?? "");
                    cmd.Parameters.AddWithValue("$e", (object)p.EntityId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$o", p.Outcome.ToString());
                    cmd.Parameters.AddWithValue("$m", (object)p.Message ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
        }

        public void LoadParts(long runId, List<StepResult> steps)
        {
            var byStep = new Dictionary<int, StepResult>();
            foreach (var s in steps) byStep[s.Step] = s;
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "SELECT s.step,p.class_name,p.entity_id,p.outcome,p.message FROM run_part p " +
                                  "JOIN run_step s ON s.id=p.step_id WHERE s.run_id=$r ORDER BY p.id";
                cmd.Parameters.AddWithValue("$r", runId);
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        StepResult st;
                        if (!byStep.TryGetValue(r.GetInt32(0), out st)) continue;
                        Outcome o;
                        if (!Enum.TryParse(r.GetString(3), out o)) o = Outcome.Skipped;
                        st.Parts.Add(new PartResult
                        {
                            ClassName = r.GetString(1), EntityId = Str(r, 2), Outcome = o, Message = Str(r, 4),
                        });
                    }
            }
        }

        // --------------------------------------------------------------- migration --

        /// <summary>
        /// T3, once: give every pre-pairs run a pair and a test case. Idempotent and guarded by a
        /// setting. Never deletes anything (R8). Runs whose sheets have since moved still list with
        /// their results; only re-running them needs the sheets (R5).
        /// </summary>
        public string MigrateRunsToPairs(Func<string, string> fomOfConnection)
        {
            if (Get("migrated.pairs") == "1") return null;

            var groups = new List<string[]>();     // conn_a, conn_b, self_test
            using (var cmd = _cn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT IFNULL(conn_a,''),IFNULL(conn_b,''),self_test FROM run WHERE pair_id IS NULL";
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) groups.Add(new[] { r.GetString(0), r.GetString(1), r.GetInt32(2).ToString(CultureInfo.InvariantCulture) });
            }

            int pairs = 0, cases = 0, runs = 0;
            foreach (var g in groups)
            {
                var connA = g[0]; var connB = g[1]; var self = g[2] == "1";
                if (connA.Length == 0) continue;           // pre-connection runs: nothing to name them by

                var name = UniquePairName((fomOfConnection(connA) ?? connA) + " → " +
                                          (self ? "self-test" : (fomOfConnection(connB) ?? connB)));
                var pair = new PairRecord { Name = name, ConnA = connA, ConnB = self ? connA : connB, SelfTest = self };
                SavePair(pair);
                pairs++;

                // One test case per distinct (input, expected) content; the latest paths win
                // because they are the likeliest to still exist.
                var sheetsOf = new Dictionary<string, string[]>();   // key -> input path, expected path
                using (var cmd = _cn.CreateCommand())
                {
                    cmd.CommandText = "SELECT IFNULL(input_hash,''),IFNULL(expected_hash,''),IFNULL(input_path,''),IFNULL(expected_path,'') " +
                                      "FROM run WHERE pair_id IS NULL AND IFNULL(conn_a,'')=$a AND IFNULL(conn_b,'')=$b AND self_test=$s ORDER BY id";
                    cmd.Parameters.AddWithValue("$a", connA);
                    cmd.Parameters.AddWithValue("$b", connB);
                    cmd.Parameters.AddWithValue("$s", self ? 1 : 0);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            sheetsOf[r.GetString(0) + "|" + r.GetString(1)] = new[] { r.GetString(2), r.GetString(3) };
                }

                var tcIdOfKey = new Dictionary<string, long>();
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in sheetsOf)
                {
                    var inPath = kv.Value[0]; var outPath = kv.Value[1];
                    var tc = new TestCaseRecord { PairId = pair.Id };
                    var have = File.Exists(inPath) && File.Exists(outPath);
                    var cls = have ? ClassOf(File.ReadAllText(inPath)) : null;
                    var baseName = string.IsNullOrEmpty(cls) ? "Imported" : cls;
                    var tcName = baseName; int n = 2;
                    while (!used.Add(tcName)) tcName = baseName + " (" + n++ + ")";
                    tc.Name = tcName;
                    if (have)
                    {
                        var inText = File.ReadAllText(inPath);
                        var outText = File.ReadAllText(outPath);
                        tc.Sheets.Add(new TestCaseSheet { Side = "send", ClassName = ClassOf(inText) ?? "", AssetHash = ImportText(inText, Path.GetFileName(inPath)) });
                        tc.Sheets.Add(new TestCaseSheet { Side = "receive", ClassName = ClassOf(outText) ?? "", AssetHash = ImportText(outText, Path.GetFileName(outPath)) });
                    }
                    else tc.Note = "sheets missing — re-upload";
                    SaveTestCase(tc);
                    tcIdOfKey[kv.Key] = tc.Id;
                    cases++;
                }

                using (var cmd = _cn.CreateCommand())
                {
                    cmd.CommandText = "SELECT id,IFNULL(input_hash,'')||'|'||IFNULL(expected_hash,'') FROM run " +
                                      "WHERE pair_id IS NULL AND IFNULL(conn_a,'')=$a AND IFNULL(conn_b,'')=$b AND self_test=$s";
                    cmd.Parameters.AddWithValue("$a", connA);
                    cmd.Parameters.AddWithValue("$b", connB);
                    cmd.Parameters.AddWithValue("$s", self ? 1 : 0);
                    var link = new List<KeyValuePair<long, long>>();
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) link.Add(new KeyValuePair<long, long>(r.GetInt64(0), tcIdOfKey[r.GetString(1)]));
                    foreach (var l in link)
                        using (var up = _cn.CreateCommand())
                        {
                            up.CommandText = "UPDATE run SET pair_id=$p, test_case_id=$t WHERE id=$id";
                            up.Parameters.AddWithValue("$p", pair.Id);
                            up.Parameters.AddWithValue("$t", l.Value);
                            up.Parameters.AddWithValue("$id", l.Key);
                            up.ExecuteNonQuery();
                            runs++;
                        }
                }
            }

            Set("migrated.pairs", "1");
            return pairs == 0 ? null
                : string.Format(CultureInfo.InvariantCulture,
                    "Grouped {0} earlier run(s) into {1} pair(s) and {2} test case(s).", runs, pairs, cases);
        }

        private string UniquePairName(string baseName)
        {
            var name = baseName; int n = 2;
            for (;;)
            {
                using (var cmd = _cn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM pair WHERE name=$n COLLATE NOCASE";
                    cmd.Parameters.AddWithValue("$n", name);
                    if (Convert.ToInt32(cmd.ExecuteScalar()) == 0) return name;
                }
                name = baseName + " (" + n++ + ")";
            }
        }

        /// <summary>The `# class:` directive of a sheet's text, or null.</summary>
        public static string ClassOf(string text)
        {
            if (text == null) return null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim().TrimStart('﻿');
                if (!line.StartsWith("#", StringComparison.Ordinal)) { if (line.Length > 0) break; continue; }
                var body = line.TrimStart('#').Trim();
                if (body.StartsWith("class:", StringComparison.OrdinalIgnoreCase)) return body.Substring(6).Trim();
            }
            return null;
        }

        private static DateTime ParseUtc(string s)
        {
            return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        }
    }
}
