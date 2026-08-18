using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using BrokerTestKit.Engine;

namespace BrokerTestKit.Agent
{
    /// <summary>
    /// ONE federation, ONE ExerciseConnection, ONE process.
    ///
    /// VR-Link's ManagedInterface is a per-process singleton, so a process can host exactly
    /// one connection (proven: two connections join but cannot move data and crash — see
    /// docs/vr-link-knowledge.md 3y). The harness therefore runs one Agent per federation
    /// and the GUI/Engine coordinate them over IPC — mirroring VR-Exchange's own
    /// Portal + one-broker-process-per-connection design.
    ///
    /// IPC: line protocol on stdin/stdout (no ports/firewall; trivially debuggable).
    ///   in : JOIN | PUBLISH k=v ... | SNAPSHOT | PING | QUIT
    ///   out: OK verb k=v ... | ENT k=v ... | ERR message
    ///
    /// All VR-Link calls stay on the main thread; stdin is read on a worker into a queue.
    /// </summary>
    internal static class Program
    {
        private static readonly ConcurrentQueue<string> Commands = new ConcurrentQueue<string>();
        private static volatile bool _running = true;

        private const double Dt = 0.05;

        /// <summary>Attribute names to probe generically (VR-Link does not model these).</summary>
        private static readonly List<string> Watch = new List<string>();

        private static int Main(string[] args)
        {
            var cfg = ParseConfig(args);
            MakEnvironment.Initialize();

            var reader = new Thread(ReadStdin) { IsBackground = true };
            reader.Start();

            FederateConnection fed = null;
            makVrl.ReflectedEntityList list = null;
            makVrl.EntityPublisher pub = null;
            double simTime = 0;
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);

            Say("OK READY label=" + cfg.Label + " exec=" + cfg.FederationName + " version=" + cfg.Version);

            try
            {
                while (_running)
                {
                    // 1) drive VR-Link. Pump() uses a NEGATIVE value: 0.0 does NOT tick the RTI.
                    if (fed != null)
                    {
                        fed.Raw.clock.setSimTime(simTime);
                        fed.Pump();
                        if (pub != null) pub.tick(Dt);
                        simTime += Dt;
                        if (list != null) StreamEntities(list, seen);
                    }

                    // 2) handle a few commands per tick
                    string cmd;
                    int budget = 8;
                    while (budget-- > 0 && Commands.TryDequeue(out cmd))
                    {
                        try { Handle(cmd, cfg, ref fed, ref list, ref pub, seen); }
                        catch (Exception ex) { Say("ERR " + ex.GetType().Name + ": " + OneLine(ex.Message)); }
                    }

                    Thread.Sleep(20);
                }
            }
            finally
            {
                // AccessViolationException here is a Corrupted State Exception and is NOT
                // catchable; keep teardown minimal, parent treats exit code as advisory.
                try { if (list != null) list.Dispose(); } catch { }
                try { if (fed != null) fed.Dispose(); } catch { }
                Say("OK BYE");
            }
            return 0;
        }

        private static void ReadStdin()
        {
            // Enqueue everything and let the MAIN loop act on QUIT, so commands queued
            // before it are still processed (FIFO). Setting _running here would race and
            // drop earlier commands.
            string line;
            while ((line = Console.In.ReadLine()) != null)
            {
                Commands.Enqueue(line);
                if (line.Trim().Equals("QUIT", StringComparison.OrdinalIgnoreCase)) return;
            }
            Commands.Enqueue("QUIT");   // stdin closed -> shut down cleanly
        }

        private static void Handle(string cmd, FederationConfig cfg,
            ref FederateConnection fed, ref makVrl.ReflectedEntityList list,
            ref makVrl.EntityPublisher pub, Dictionary<string, string> seen)
        {
            switch (Verb(cmd))
            {
                case "JOIN":
                    if (fed != null) { Say("ERR already joined"); return; }
                    fed = new FederateConnection(cfg);
                    fed.Connect();
                    list = fed.CreateEntityList();
                    Say("OK JOINED exec=" + cfg.FederationName);
                    return;

                case "PUBLISH":
                {
                    if (fed == null) { Say("ERR not joined"); return; }
                    var kv = Parse(cmd);
                    if (pub == null) pub = fed.CreateEntityPublisher();
                    var esr = pub.esr;

                    // Stable identity BEFORE the first tick, else VR-Link registers a NEW
                    // object on every tick.
                    var id = Get(kv, "id");
                    if (id.Length > 0) { esr.id = id; esr.entityIdentifier = id; }

                    var t = new makVrl.EntityType(
                        Int(kv, "kind", 1), Int(kv, "domain", 2), Int(kv, "country", 225),
                        Int(kv, "cat", 1), Int(kv, "subcat", 1), Int(kv, "spec", 0), Int(kv, "extra", 0));
                    esr.type = t;
                    esr.guise = t;

                    var marking = Get(kv, "marking");
                    if (marking.Length > 0) esr.markingText = marking;

                    esr.worldPosition = new makVrl.Vector3d(
                        Dbl(kv, "x", 0), Dbl(kv, "y", 0), Dbl(kv, "z", 0));
                    pub.tick(0.0);
                    Say("OK PUBLISHED id=" + esr.id);
                    return;
                }

                case "WATCH":
                {
                    var kv = Parse(cmd);
                    var n = Get(kv, "name");
                    if (n.Length == 0) { Say("ERR WATCH needs name="); return; }
                    if (!Watch.Contains(n)) Watch.Add(n);
                    Say("OK WATCHING name=" + n);
                    return;
                }

                case "SETATTR":
                {
                    if (fed == null) { Say("ERR not joined"); return; }
                    var kv = Parse(cmd);
                    var n = Get(kv, "name");
                    var hex = Get(kv, "value");
                    if (n.Length == 0) { Say("ERR SETATTR needs name="); return; }
                    if (pub == null) pub = fed.CreateEntityPublisher();
                    var sr = pub.esr;
                    var bytes = FromHex(hex);
                    try
                    {
                        sr.setAttribute(n, bytes);
                        Say("OK SETATTR name=" + n + " bytes=" + bytes.Length);
                    }
                    catch (Exception ex) { Say("ERR SETATTR " + n + " " + ex.GetType().Name + ": " + OneLine(ex.Message)); }
                    return;
                }

                case "PROBE":
                {
                    // report what the generic channel says about each watched attribute
                    if (fed == null) { Say("ERR not joined"); return; }
                    foreach (var e in ListOf(list))
                    {
                        var esr = e.esr; if (esr == null) continue;
                        foreach (var n in Watch)
                        {
                            string res;
                            try
                            {
                                var has = esr.hasAttribute(n);
                                var v = has ? esr.getAttribute(n) : null;
                                res = "has=" + has + " type=" + (v == null ? "null" : v.GetType().Name)
                                    + " value=" + Describe(v);
                            }
                            catch (Exception ex) { res = "EX " + ex.GetType().Name + ": " + OneLine(ex.Message); }
                            Say("PROBE id=" + (esr.id ?? "?") + " attr=" + n + " " + res);
                        }
                    }
                    Say("OK PROBE watched=" + Watch.Count);
                    return;
                }

                case "SNAPSHOT":
                    foreach (var kvp in seen) Say("ENT " + kvp.Value);
                    Say("OK SNAPSHOT count=" + seen.Count);
                    return;

                case "PING":
                    Say("OK PONG joined=" + (fed != null));
                    return;

                case "QUIT":
                    _running = false;
                    return;

                default:
                    Say("ERR unknown command");
                    return;
            }
        }

        /// <summary>Emit an ENT line whenever an entity appears or its state changes.</summary>
        private static void StreamEntities(makVrl.ReflectedEntityList list, Dictionary<string, string> seen)
        {
            foreach (var e in list.entities)
            {
                var esr = e.esr;
                if (esr == null) continue;
                var id = esr.id;
                if (string.IsNullOrEmpty(id)) id = "?";
                var p = esr.worldPosition;
                var rec = string.Format(CultureInfo.InvariantCulture,
                    "id={0} x={1} y={2} z={3} marking={4}", id, p.X, p.Y, p.Z, Safe(esr.markingText));
                foreach (var n in Watch)
                {
                    try
                    {
                        if (esr.hasAttribute(n))
                            rec += " " + n + "=" + Describe(esr.getAttribute(n));
                    }
                    catch { /* generic channel unavailable for this attr */ }
                }
                string prev;
                if (!seen.TryGetValue(id, out prev) || prev != rec)
                {
                    seen[id] = rec;
                    Say("ENT " + rec);
                }
            }
        }

        private static FederationConfig ParseConfig(string[] args)
        {
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i + 1 < args.Length; i += 2)
                kv[args[i].TrimStart('-')] = args[i + 1];

            var evolved = Get(kv, "version").Equals("hla1516e", StringComparison.OrdinalIgnoreCase);
            var cfg = evolved ? FederationConfig.DefaultFedB() : FederationConfig.DefaultFedA();

            if (Get(kv, "label").Length > 0) cfg.Label = Get(kv, "label");
            if (Get(kv, "exec").Length > 0) cfg.FederationName = Get(kv, "exec");
            if (Get(kv, "federateType").Length > 0) cfg.FederateType = Get(kv, "federateType");
            if (Get(kv, "federateName").Length > 0) cfg.FederateName = Get(kv, "federateName");
            if (Get(kv, "fed").Length > 0) cfg.FedFile = Get(kv, "fed");
            if (Get(kv, "rpr").Length > 0) cfg.RprVersion = Dbl(kv, "rpr", cfg.RprVersion);
            return cfg;
        }

        private static IEnumerable<makVrl.ReflectedEntity> ListOf(makVrl.ReflectedEntityList l)
        {
            if (l == null) yield break;
            foreach (var e in l.entities) yield return e;
        }

        /// <summary>Render a generic attribute value for the wire log.</summary>
        private static string Describe(object v)
        {
            if (v == null) return "-";
            var b = v as byte[];
            if (b != null) return ToHex(b);
            return (v.ToString() ?? "").Replace(' ', '_');
        }

        private static string ToHex(byte[] b)
        {
            if (b == null || b.Length == 0) return "-";
            var sb = new System.Text.StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("X2"));
            return sb.ToString();
        }

        private static byte[] FromHex(string h)
        {
            if (string.IsNullOrEmpty(h) || h == "-") return new byte[0];
            if ((h.Length & 1) != 0) h = "0" + h;
            var b = new byte[h.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16);
            return b;
        }

        // ---- tiny line-protocol helpers -------------------------------------------
        private static void Say(string s) { Console.Out.WriteLine(s); Console.Out.Flush(); }

        private static string OneLine(string s)
        {
            if (s == null) return "";
            return s.Replace('\r', ' ').Replace('\n', ' ');
        }

        private static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "-";
            return s.Replace(' ', '_');
        }

        private static string Verb(string cmd)
        {
            var c = cmd == null ? "" : cmd.Trim();
            var i = c.IndexOf(' ');
            var v = i < 0 ? c : c.Substring(0, i);
            return v.ToUpperInvariant();
        }

        private static Dictionary<string, string> Parse(string cmd)
        {
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var parts = (cmd == null ? "" : cmd).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var tok in parts)
            {
                var i = tok.IndexOf('=');
                if (i > 0) kv[tok.Substring(0, i)] = tok.Substring(i + 1);
            }
            return kv;
        }

        private static string Get(Dictionary<string, string> kv, string k)
        {
            string v;
            return kv.TryGetValue(k, out v) ? v : "";
        }

        private static double Dbl(Dictionary<string, string> kv, string k, double dflt)
        {
            double d;
            return double.TryParse(Get(kv, k), NumberStyles.Any, CultureInfo.InvariantCulture, out d) ? d : dflt;
        }

        private static int Int(Dictionary<string, string> kv, string k, int dflt)
        {
            int d;
            return int.TryParse(Get(kv, k), out d) ? d : dflt;
        }
    }
}
