using System;
using System.Collections.Generic;
using BrokerTestKit.Engine;

namespace BrokerTestKit.Cli
{
    /// <summary>
    /// Headless runner / prototype driver for the HLA Broker Translation Test Kit.
    ///
    ///   prp1  join + resign Federation A (HLA 1.3, RPR FOM 1.0)          [PASSING]
    ///   prp2  join BOTH federations in one process (A=1.3, B=Evolved)
    ///
    /// Both federations may sit on the SAME MAK RTI (a single RTI serves 1.3 and Evolved
    /// simultaneously), which is what makes the single-process design possible.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string mode = args.Length > 0 && !args[0].StartsWith("--") ? args[0].ToLowerInvariant() : "prp1";

            // Must happen before any VR-Link type is used.
            var cfgDir = MakEnvironment.Initialize();
            Console.WriteLine("mode        = " + mode);
            Console.WriteLine("RTI_CONFIG  = " + cfgDir);
            Console.WriteLine("VR-Link     = " + MakEnvironment.VrLinkBin64);
            Console.WriteLine("RTI         = " + MakEnvironment.RtiBin);
            Console.WriteLine();

            try
            {
                switch (mode)
                {
                    case "prp1": return RunPrp1(args);
                    case "prp2": return RunPrp2(args);
                    case "prp3": return RunPrp3(args);
                    case "listen": return RunListen(args);
                    case "listen2": return RunListen2(args);
                    case "publish": return RunPublish(args);
                    case "prp4": return RunPrp4(args);
                    case "prp5": return RunPrp5(args);
                    case "prp6": return RunPrp6(args);
                    case "run": return RunCsv(args);
                    case "native": return RunNative(args);
                    case "fom": return RunFom(args);
                    case "entity": return RunEntity(args);
                    case "case": return RunCase(args);
                    case "template": return RunTemplate(args);
                    default:
                        Console.Error.WriteLine("Unknown mode '" + mode + "'. Use: prp1..prp6 | run | native | listen | publish");
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAILED: " + ex.GetType().Name + ": " + ex.Message);
                // REQ-07: a licence failure in words, ahead of the stack trace that would bury it.
                var licence = LicenceDiagnosis.Explain(ex.ToString());
                if (licence != null) { Console.Error.WriteLine(); Console.Error.WriteLine("LICENCE: " + licence); }
                Console.Error.WriteLine();
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        /// <summary>PRP-1: join Federation A and resign cleanly.</summary>
        private static int RunPrp1(string[] args)
        {
            var cfg = FederationConfig.DefaultFedA();
            ApplyArgs(args, cfg);

            Console.WriteLine("PRP-1  join + resign  " + cfg);
            using (var fed = new FederateConnection(cfg))
            {
                fed.Connect();
                Console.WriteLine("  JOINED " + cfg.FederationName);
                for (int i = 0; i < 5; i++) fed.Pump(0.1);
                Console.WriteLine("  pump x5 OK -- connection is live");
            }
            Console.WriteLine("  RESIGNED");
            return 0;
        }

        /// <summary>
        /// PRP-2: hold BOTH federations in ONE process — Federation A on HLA 1.3 (RPR 1.0)
        /// and Federation B on HLA Evolved (RPR 2.0) — and pump both. This proves the core
        /// premise of the test kit: one app can drive A and verify B.
        /// </summary>
        private static int RunPrp2(string[] args)
        {
            var a = FederationConfig.DefaultFedA();
            var b = FederationConfig.DefaultFedB();

            Console.WriteLine("PRP-2  dual join (one process, one RTI)");
            Console.WriteLine("  " + a);
            Console.WriteLine("  " + b);
            Console.WriteLine();

            using (var fedA = new FederateConnection(a))
            using (var fedB = new FederateConnection(b))
            {
                Console.WriteLine("Connecting Federation A (HLA 1.3) ...");
                fedA.Connect();
                Console.WriteLine("  JOINED " + a.FederationName);

                Console.WriteLine("Connecting Federation B (HLA Evolved) ...");
                fedB.Connect();
                Console.WriteLine("  JOINED " + b.FederationName);

                // Observe anything either side already sees (proves callbacks are wired).
                fedA.ObjectDiscovered += s => Console.WriteLine("  [A discover] " + s);
                fedA.ObjectUpdated    += s => Console.WriteLine("  [A update  ] " + s);
                fedB.ObjectDiscovered += s => Console.WriteLine("  [B discover] " + s);
                fedB.ObjectUpdated    += s => Console.WriteLine("  [B update  ] " + s);

                fedA.SubscribeObject("BaseEntity");
                fedB.SubscribeObject("BaseEntity");
                Console.WriteLine("Subscribed 'BaseEntity' on both sides.");

                Console.WriteLine("Pumping both connections ...");
                for (int i = 0; i < 20; i++) { fedA.Pump(0.05); fedB.Pump(0.05); }
                Console.WriteLine("  pump x20 OK -- BOTH connections live simultaneously");
            }

            Console.WriteLine("  RESIGNED both");
            return 0;
        }

        /// <summary>
        /// PRP-3: LOOPBACK SELF-TEST on the TYPED path (the Universal layer does not work —
        /// see vr-link-knowledge.md §3). Two federates in the SAME federation: one publishes
        /// an entity via EntityPublisher, the other observes it via ReflectedEntityList and
        /// compares decoded state with tolerance. Exercises the §9.1 match-or-timeout loop.
        /// If this passes, later failures point at the BROKER, not the harness.
        /// </summary>
        private static int RunPrp3(string[] args)
        {
            // Geocentric metres (roughly on the earth's surface, like MAK's own examples).
            const double X = -2696048.0, Y = -4429591.0, Z = 3701299.0;
            const double Tol = 0.5;                       // metres — DESIGN 9.1: tolerance, not equality
            var timeout = TimeSpan.FromSeconds(20);

            var pubCfg = FederationConfig.DefaultFedA(); pubCfg.FederateType = "HarnessPub";
            var subCfg = FederationConfig.DefaultFedA(); subCfg.FederateType = "HarnessSub";

            Console.WriteLine("PRP-3  loopback self-test (typed path) on " + pubCfg.FederationName);
            Console.WriteLine("  publishing worldPosition = ({0}, {1}, {2})  tol={3}m", X, Y, Z, Tol);
            Console.WriteLine();

            using (var sub = new FederateConnection(subCfg))
            using (var pub = new FederateConnection(pubCfg))
            {
                sub.Connect(); Console.WriteLine("  subscriber joined");
                pub.Connect(); Console.WriteLine("  publisher  joined");

                var list = sub.CreateEntityList();
                var publisher = pub.CreateEntityPublisher();
                try
                {
                    // Follow MAK's own talk.cs pattern exactly: a STABLE esr.id is required —
                    // without it VR-Link registers a brand-new object on every tick().
                    var esr = publisher.esr;
                    var type = new makVrl.EntityType(1, 2, 225, 1, 1, 0, 0);
                    esr.type  = type;
                    esr.guise = type;
                    esr.id = "1:2:3";                 // Site:Host:EntityNumber
                    esr.entityIdentifier = esr.id;    // for HLA RPR FOM
                    esr.markingText = "BTK-PRP3";     // stable correlation key (DESIGN §9)
                    esr.worldPosition = new makVrl.Vector3d(X, Y, Z);
                    Console.WriteLine("  publisher entity: id='{0}' marking='{1}'", esr.id, esr.markingText);

                    // quiesce before the stimulus (DESIGN 9.1 rule 4)
                    for (int i = 0; i < 5; i++) { sub.Pump(); pub.Pump(); }

                    Console.WriteLine("  published; waiting up to " + timeout.TotalSeconds + "s ...");

                    // DESIGN 9.1: match-or-timeout on CONTENT, not a fixed sleep.
                    var pubClock = pub.Raw.clock;
                    var subClock = sub.Raw.clock;
                    const double dt = 0.05;
                    double simTime = 0.0;

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    makVrl.Vector3d? got = null;
                    string gotId = null;
                    while (got == null && sw.Elapsed < timeout)
                    {
                        // VR-Link needs sim time advanced; reflected state is dead-reckoned to it.
                        pubClock.setSimTime(simTime);
                        subClock.setSimTime(simTime);

                        pub.Pump();      // -1.0: actually ticks the RTI (0.0 does NOT)
                        sub.Pump();

                        esr.worldPosition = new makVrl.Vector3d(X, Y, Z);   // hold it still
                        publisher.tick(dt);
                        simTime += dt;

                        foreach (var ent in list.entities)
                        {
                            var e = ent.esr;
                            if (e == null) continue;
                            var p = e.worldPosition;
                            if (Math.Abs(p.X - X) <= Tol && Math.Abs(p.Y - Y) <= Tol && Math.Abs(p.Z - Z) <= Tol)
                            {
                                got = p; gotId = e.id; break;
                            }
                        }
                        System.Threading.Thread.Sleep(10);
                    }
                    sw.Stop();

                    if (got == null)
                    {
                        Console.Error.WriteLine("  TIMEOUT after {0:F1}s -- expected position never observed",
                                                sw.Elapsed.TotalSeconds);
                        foreach (var ent in list.entities)
                            Console.Error.WriteLine("    saw entity " + ent.esr.id + " at " + ent.esr.worldPosition);
                        return 1;
                    }

                    var v = got.Value;
                    Console.WriteLine("  received after {0:F2}s  (entity {1})", sw.Elapsed.TotalSeconds, gotId);
                    Console.WriteLine("  expected: ({0}, {1}, {2})", X, Y, Z);
                    Console.WriteLine("  actual:   ({0}, {1}, {2})", v.X, v.Y, v.Z);
                    Console.WriteLine("  delta:    ({0:F6}, {1:F6}, {2:F6})",
                                      Math.Abs(v.X - X), Math.Abs(v.Y - Y), Math.Abs(v.Z - Z));
                    Console.WriteLine("  MATCH -- round trip through the RTI within tolerance");
                }
                finally
                {
                    // VR-Link can AV inside EntityPublisher.Dispose() (it sends the
                    // delete-object message); don't let teardown mask the test result.
                    try { publisher.Dispose(); } catch (Exception ex) { Console.Error.WriteLine("  (publisher dispose warning: " + ex.GetType().Name + ")"); }
                    try { list.Dispose(); }      catch (Exception ex) { Console.Error.WriteLine("  (list dispose warning: " + ex.GetType().Name + ")"); }
                }
            }

            Console.WriteLine("  RESIGNED both");
            return 0;
        }

        /// <summary>
        /// Diagnostic: subscribe catch-all on Federation A and print everything received for
        /// N seconds. Run MAK's own publisher (talkHLA13_64.exe) into the same federation to
        /// prove the Universal RECEIVE path independently of our publish path.
        /// </summary>
        private static int RunListen(string[] args)
        {
            var cfg = FederationConfig.DefaultFedA();
            cfg.FederateType = "HarnessListen";
            ApplyArgs(args, cfg);

            double secs = 15;
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--seconds") double.TryParse(args[i + 1], out secs);

            Console.WriteLine("LISTEN  " + cfg + "  for " + secs + "s (catch-all)");
            int count = 0;
            using (var fed = new FederateConnection(cfg))
            {
                fed.Connect();
                Console.WriteLine("  joined; listening ...");
                fed.ObjectDiscovered += s => { count++; Console.WriteLine("  [discover] " + s); };
                fed.ObjectUpdated += s =>
                {
                    count++;
                    Console.WriteLine("  [update  ] " + s + "  attrs=[" +
                                      string.Join(", ", new List<string>(s.Attributes.Keys).ToArray()) + "]");
                    foreach (var kv in s.Attributes)
                        Console.WriteLine("             " + kv.Key + " = " + RprCodec.Hex(kv.Value, 24));
                };
                fed.ObjectRemoved += s => { count++; Console.WriteLine("  [removed ] " + s); };
                fed.SubscribeAllObjects();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < secs) fed.Pump(0.05);
            }
            Console.WriteLine("  received " + count + " message(s)");
            return count > 0 ? 0 : 1;
        }

        /// <summary>
        /// Diagnostic 2: observe entities via the TYPED path (ReflectedEntityList) but read
        /// their attributes BY NAME via StateRepository.getAttribute(name) — the rename-proof
        /// access we need, on the code path that actually works.
        /// </summary>
        private static int RunListen2(string[] args)
        {
            var cfg = FederationConfig.DefaultFedA();
            cfg.FederateType = "HarnessListen2";
            ApplyArgs(args, cfg);

            double secs = 15;
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--seconds") double.TryParse(args[i + 1], out secs);

            // Attributes to probe by name (RPR 1.0 BaseEntity / PhysicalEntity).
            var probe = new[] { "WorldLocation", "VelocityVector", "Orientation",
                                "EntityType", "EntityIdentifier", "Marking", "DamageState" };

            Console.WriteLine("LISTEN2 (typed list + getAttribute-by-name)  " + cfg + "  " + secs + "s");
            int seen = 0;
            using (var fed = new FederateConnection(cfg))
            {
                fed.Connect();
                Console.WriteLine("  joined; building ReflectedEntityList ...");
                var list = fed.CreateEntityList();   // has Dispose() but doesn't implement IDisposable
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var reported = new HashSet<string>();
                    while (sw.Elapsed.TotalSeconds < secs)
                    {
                        fed.Pump(0.05);
                        foreach (var ent in list.entities)
                        {
                            var esr = ent.esr;
                            if (esr == null) continue;
                            var id = esr.id ?? "(no id)";
                            if (!reported.Add(id)) continue;
                            seen++;
                            Console.WriteLine("  [entity] id=" + id + "  pos=" + esr.worldPosition);
                            foreach (var name in probe)
                            {
                                try
                                {
                                    if (!esr.hasAttribute(name)) { Console.WriteLine("      " + name + ": (absent)"); continue; }
                                    var v = esr.getAttribute(name);
                                    Console.WriteLine("      " + name + " = " + (v ?? "(null)") +
                                                      "   [" + (v == null ? "null" : v.GetType().Name) + "]");
                                }
                                catch (Exception ex) { Console.WriteLine("      " + name + ": EX " + ex.GetType().Name + ": " + ex.Message); }
                            }
                        }
                    }
                }
                finally { list.Dispose(); }
            }
            Console.WriteLine("  saw " + seen + " entity(ies)");
            return seen > 0 ? 0 : 1;
        }

        /// <summary>
        /// Publish one entity with a known position/marking for N seconds. Verified against
        /// MAK's own listenHLA13_64.exe to prove OUR publish path works (the counterpart of
        /// `listen2`, which proved our receive path against MAK's talkHLA13_64.exe).
        /// </summary>
        private static int RunPublish(string[] args)
        {
            const double X = -2696048.0, Y = -4429591.0, Z = 3701299.0;
            var cfg = FederationConfig.DefaultFedA();
            cfg.FederateType = "HarnessPublish";
            ApplyArgs(args, cfg);

            double secs = 12;
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--seconds") double.TryParse(args[i + 1], out secs);

            Console.WriteLine("PUBLISH  " + cfg + "  for " + secs + "s");
            using (var fed = new FederateConnection(cfg))
            {
                fed.Connect();
                var pubr = fed.CreateEntityPublisher();
                try
                {
                    var esr = pubr.esr;
                    var type = new makVrl.EntityType(1, 2, 225, 1, 1, 0, 0);
                    esr.type = type; esr.guise = type;
                    esr.id = "7:7:7";
                    esr.entityIdentifier = esr.id;
                    esr.markingText = "BTK-PUB";
                    esr.worldPosition = new makVrl.Vector3d(X, Y, Z);
                    Console.WriteLine("  publishing id='{0}' marking='{1}' pos=({2}, {3}, {4})",
                                      esr.id, esr.markingText, X, Y, Z);

                    var clock = fed.Raw.clock;
                    const double dt = 0.05;
                    double simTime = 0;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < secs)
                    {
                        clock.setSimTime(simTime);
                        fed.Pump(-1.0);
                        esr.worldPosition = new makVrl.Vector3d(X, Y, Z);
                        pubr.tick(dt);
                        simTime += dt;
                        System.Threading.Thread.Sleep(50);
                    }
                    Console.WriteLine("  done publishing");
                }
                finally
                {
                    // VR-Link can AV inside EntityPublisher.Dispose() when it sends the
                    // delete-object message; don't let teardown mask a successful run.
                    try { pubr.Dispose(); }
                    catch (Exception ex) { Console.Error.WriteLine("  (publisher dispose warning: " + ex.GetType().Name + ")"); }
                }
            }
            return 0;
        }


        /// <summary>
        /// PRP-4: does a SINGLE process holding BOTH connections still move DATA?
        /// PRP-3 showed two same-federation connections in one process interfere (VR-Link's
        /// ManagedInterface is a per-process singleton). Here we hold Fed A (1.3) AND
        /// Fed B (Evolved) — different protocols — and publish an entity on A while both are
        /// open. An EXTERNAL federate (MAK listenHLA13_64.exe) verifies A's data reaches the
        /// wire. If this works, the single-process harness design is sound.
        /// </summary>
        private static int RunPrp4(string[] args)
        {
            const double X = -2696048.0, Y = -4429591.0, Z = 3701299.0;
            double secs = 16;
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--seconds") double.TryParse(args[i + 1], out secs);

            var a = FederationConfig.DefaultFedA(); a.FederateType = "HarnessDualA";
            var b = FederationConfig.DefaultFedB(); b.FederateType = "HarnessDualB"; b.FederateName = "HarnessDualB";

            Console.WriteLine("PRP-4  dual-connection DATA test (one process)");
            Console.WriteLine("  " + a);
            Console.WriteLine("  " + b);

            using (var fedA = new FederateConnection(a))
            using (var fedB = new FederateConnection(b))
            {
                // ORDERING VARIANT: build A's list/publisher BEFORE B connects, to test
                // whether the crash is ordering-related or fundamental to 2 connections.
                fedA.Connect(); Console.WriteLine("  JOINED A");
                var pubr = fedA.CreateEntityPublisher();
                var listA = fedA.CreateEntityList();
                Console.WriteLine("  A publisher + entity list created OK (before B connects)");

                fedB.Connect(); Console.WriteLine("  JOINED B");
                makVrl.ReflectedEntityList listB = null;
                try { listB = fedB.CreateEntityList(); Console.WriteLine("  B entity list created OK"); }
                catch (Exception ex) { Console.Error.WriteLine("  B entity list FAILED: " + ex.GetType().Name); }
                try
                {
                    var esr = pubr.esr;
                    var t = new makVrl.EntityType(1, 2, 225, 1, 1, 0, 0);
                    esr.type = t; esr.guise = t;
                    esr.id = "9:9:9";
                    esr.entityIdentifier = esr.id;
                    esr.markingText = "BTK-DUAL";
                    esr.worldPosition = new makVrl.Vector3d(X, Y, Z);
                    Console.WriteLine("  publishing on A: id='{0}' marking='{1}'", esr.id, esr.markingText);

                    var clkA = fedA.Raw.clock; var clkB = fedB.Raw.clock;
                    const double dt = 0.05; double simTime = 0;
                    var seenA = new HashSet<string>(); var seenB = new HashSet<string>();

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < secs)
                    {
                        clkA.setSimTime(simTime); clkB.setSimTime(simTime);
                        fedA.Pump(); fedB.Pump();          // -1.0 = actually ticks the RTI
                        esr.worldPosition = new makVrl.Vector3d(X, Y, Z);
                        pubr.tick(dt);
                        simTime += dt;

                        foreach (var e in listA.entities)
                            if (e.esr != null && seenA.Add(e.esr.id ?? "?"))
                                Console.WriteLine("    [A sees] " + e.esr.id + " @ " + e.esr.worldPosition);
                        if (listB != null)
                            foreach (var e in listB.entities)
                                if (e.esr != null && seenB.Add(e.esr.id ?? "?"))
                                    Console.WriteLine("    [B sees] " + e.esr.id + " @ " + e.esr.worldPosition);

                        System.Threading.Thread.Sleep(40);
                    }
                    Console.WriteLine("  done. A saw {0} entity(ies), B saw {1}", seenA.Count, seenB.Count);
                }
                finally
                {
                    try { pubr.Dispose(); } catch (Exception ex) { Console.Error.WriteLine("  (pub dispose: " + ex.GetType().Name + ")"); }
                    try { listA.Dispose(); } catch { }
                    try { if (listB != null) listB.Dispose(); } catch { }
                }
            }
            Console.WriteLine("  RESIGNED both");
            return 0;
        }


        /// <summary>Locate BrokerTestKit.Agent.exe next to this build.</summary>
        private static string FindAgentExe()
        {
            var d = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (d != null)
            {
                var cand = System.IO.Path.Combine(d.FullName,
                    @"src\BrokerTestKit.Agent\bin\x64\Debug\BrokerTestKit.Agent.exe");
                if (System.IO.File.Exists(cand)) return cand;
                d = d.Parent;
            }
            throw new System.IO.FileNotFoundException("BrokerTestKit.Agent.exe not found");
        }

        /// <summary>
        /// PRP-5: MULTI-PROCESS architecture test — the design PRP-4 proved is required.
        /// Spawns three agent processes, each owning ONE connection:
        ///   pub : publishes an entity
        ///   sub : observes -> must see pub's entity
        /// Proves data crosses between separate agent PROCESSES with no VR-Link singleton
        /// collisions. (Fed B / Evolved agents are proven to join separately; the licence
        /// only has 2 vrl_run seats, which is exactly the production shape: one agent per side.)
        /// </summary>
        private static int RunPrp5(string[] args)
        {
            const double X = -2696048.0, Y = -4429591.0, Z = 3701299.0;
            const double Tol = 0.5;
            var join = TimeSpan.FromSeconds(30);
            var settle = TimeSpan.FromSeconds(25);

            var agentExe = FindAgentExe();
            Console.WriteLine("PRP-5  multi-process agents");
            Console.WriteLine("  agent exe: " + agentExe);
            Console.WriteLine();

            // NOTE: each agent consumes ONE VR-Link 'vrl_run' seat and this site licence has
            // only TWO. So we run exactly two agents — which is also the production shape
            // (one agent for Fed A, one for Fed B). A 3rd agent fails with FlexNet -4,132
            // "Licensed number of users already reached".
            using (var pub  = new AgentHost("pub",  agentExe, "--label pub --version hla13 --exec BTK-FedA --federateType AgentPub"))
            using (var sub  = new AgentHost("sub",  agentExe, "--label sub --version hla13 --exec BTK-FedA --federateType AgentSub"))
            {
                foreach (var a in new[] { pub, sub })
                    if (a.WaitFor("OK READY", TimeSpan.FromSeconds(20)) == null)
                    { Console.Error.WriteLine("  " + a.Label + ": never became READY"); return 1; }
                Console.WriteLine("  both agents READY");

                if (!sub.Join(join))  { Console.Error.WriteLine("  sub failed to join");  return 1; }
                Console.WriteLine("  sub  JOINED Fed A");
                if (!pub.Join(join))  { Console.Error.WriteLine("  pub failed to join");  return 1; }
                Console.WriteLine("  pub  JOINED Fed A");

                pub.Publish("5:5:5", X, Y, Z, "BTK-PRP5");
                Console.WriteLine("  pub publishing 5:5:5 @ ({0}, {1}, {2})", X, Y, Z);

                // DESIGN 9.1: match-or-timeout on CONTENT
                var got = sub.WaitForEntity(e =>
                    Math.Abs(e.X - X) <= Tol && Math.Abs(e.Y - Y) <= Tol && Math.Abs(e.Z - Z) <= Tol, settle);

                if (got == null)
                {
                    Console.Error.WriteLine("  TIMEOUT - sub never observed the expected entity");
                    foreach (var l in sub.Transcript) Console.Error.WriteLine("    sub| " + l);
                    return 1;
                }

                Console.WriteLine("  sub OBSERVED: " + got);
                Console.WriteLine("  delta: ({0:F6}, {1:F6}, {2:F6})",
                                  Math.Abs(got.X - X), Math.Abs(got.Y - Y), Math.Abs(got.Z - Z));

                pub.Send("PING"); sub.Send("PING");
                var p1 = pub.WaitFor("OK PONG", TimeSpan.FromSeconds(10));
                var p2 = sub.WaitFor("OK PONG", TimeSpan.FromSeconds(10));
                Console.WriteLine("  health: pub=" + (p1 ?? "-") + "  sub=" + (p2 ?? "-"));
                if (p1 == null || p2 == null) { Console.Error.WriteLine("  an agent is unhealthy"); return 1; }

                Console.WriteLine();
                Console.WriteLine("  PASS -- data flowed between two SEPARATE agent processes");
                Console.WriteLine("  (multi-process design validated; 2 agents = the 2 licensed vrl_run seats)");
            }
            return 0;
        }


        /// <summary>
        /// PRP-6: CUSTOM-ATTRIBUTE channel — the capability the real requirement depends on.
        /// The broker translates CUSTOMISED RPR 1.0 -> CUSTOMISED RPR 2.0, and customised
        /// attributes are NOT modelled by VR-Link, so they are only reachable through the
        /// generic by-name channel (StateRepository.get/setAttribute + enableGenericAttributes).
        /// Uses config/BTK-CustomRPR1.fed (stock RPR 1.0 + BtkCustomValue/BtkCustomTag).
        /// </summary>
        private static int RunPrp6(string[] args)
        {
            const string Fed  = "BTK-CustomRPR1.fed";
            const string Exec = "BTK-CustomA";
            const string Attr = "BtkCustomValue";
            const string Hex  = "DEADBEEF01020304";       // arbitrary custom payload
            const double X = -2696048.0, Y = -4429591.0, Z = 3701299.0;
            var join = TimeSpan.FromSeconds(30);
            var settle = TimeSpan.FromSeconds(25);

            var agentExe = FindAgentExe();
            Console.WriteLine("PRP-6  custom (unmodelled) attribute round-trip");
            Console.WriteLine("  fed=" + Fed + "  attr=" + Attr + "  payload=" + Hex);
            Console.WriteLine();

            using (var pub = new AgentHost("pub", agentExe,
                       "--label pub --version hla13 --exec " + Exec + " --fed " + Fed + " --federateType CustPub"))
            using (var sub = new AgentHost("sub", agentExe,
                       "--label sub --version hla13 --exec " + Exec + " --fed " + Fed + " --federateType CustSub"))
            {
                foreach (var a in new[] { pub, sub })
                    if (a.WaitFor("OK READY", TimeSpan.FromSeconds(20)) == null)
                    { Console.Error.WriteLine("  " + a.Label + " never READY"); return 1; }

                if (!sub.Join(join)) { Console.Error.WriteLine("  sub join failed"); return 1; }
                if (!pub.Join(join)) { Console.Error.WriteLine("  pub join failed"); return 1; }
                Console.WriteLine("  both agents joined " + Exec);

                sub.Send("WATCH name=" + Attr);
                Console.WriteLine("  sub watching: " + (sub.WaitFor("OK WATCHING", TimeSpan.FromSeconds(10)) ?? "NO ACK"));

                pub.Publish("6:6:6", X, Y, Z, "BTK-PRP6");
                pub.WaitFor("OK PUBLISHED", TimeSpan.FromSeconds(10));

                pub.Send("SETATTR name=" + Attr + " value=" + Hex);
                var setAck = pub.WaitFor("OK SETATTR", TimeSpan.FromSeconds(10));
                var setErr = setAck == null ? pub.WaitFor("ERR SETATTR", TimeSpan.FromSeconds(1)) : null;
                Console.WriteLine("  pub setattr: " + (setAck ?? setErr ?? "NO RESPONSE"));

                // observe the entity, then ask what the generic channel reports
                var seen = sub.WaitForEntity(e => e.Id == "6:6:6", settle);
                Console.WriteLine("  sub observed entity: " + (seen == null ? "NONE" : seen.ToString()));

                sub.Send("PROBE");
                sub.WaitFor("OK PROBE", TimeSpan.FromSeconds(15));

                Console.WriteLine();
                Console.WriteLine("  --- sub transcript (PROBE / ENT lines) ---");
                bool sawAttr = false;
                foreach (var l in sub.Transcript)
                {
                    if (l.StartsWith("PROBE ") || (l.StartsWith("ENT ") && l.Contains(Attr)))
                    {
                        Console.WriteLine("    " + l);
                        if (l.IndexOf(Hex, StringComparison.OrdinalIgnoreCase) >= 0) sawAttr = true;
                    }
                }
                Console.WriteLine();
                if (sawAttr)
                {
                    Console.WriteLine("  PASS -- custom attribute round-tripped via the generic channel");
                    return 0;
                }
                Console.WriteLine("  INCONCLUSIVE/FAIL -- custom payload not observed on the receiving side");
                Console.WriteLine("  (see transcript above; pub setattr result is the key line)");
                return 1;
            }
        }


        /// <summary>
        /// run &lt;input.csv&gt; &lt;output.csv&gt; [--selftest]
        ///
        /// The product: drive Federation A from input.csv, verify Federation B against
        /// output.csv, one step at a time (DESIGN 9.1). Exits non-zero on any failure so it
        /// can gate CI.
        ///
        /// --selftest points BOTH agents at Federation A, so the expected mapping is the
        /// identity. That validates the rig itself without the broker; drop the flag to run
        /// A -> broker -> B for real.
        /// </summary>
        private static int RunCsv(string[] args)
        {
            string inPath = null, outPath = null;
            bool selfTest = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--selftest") { selfTest = true; continue; }
                if (args[i].StartsWith("--")) continue;
                if (inPath == null) inPath = args[i];
                else if (outPath == null) outPath = args[i];
            }
            if (inPath == null || outPath == null)
            {
                Console.Error.WriteLine("usage: run <input.csv> <output.csv> [--selftest]");
                return 2;
            }

            var input = CsvParser.ReadInput(inPath);
            var expected = CsvParser.ReadExpected(outPath);
            Console.WriteLine("RUN  input=" + inPath + " (" + input.Count + " steps)");
            Console.WriteLine("     expect=" + outPath + " (" + expected.Count + " steps)");
            Console.WriteLine("     mode=" + (selfTest ? "SELF-TEST (both agents on Fed A, identity mapping)"
                                                       : "A -> broker -> B"));
            Console.WriteLine();

            var agentExe = FindAgentExe();
            var argsA = "--label A --version hla13 --exec BTK-FedA --federateType RunnerA";
            var argsB = selfTest
                ? "--label B --version hla13 --exec BTK-FedA --federateType RunnerB"
                : "--label B --version hla1516e --exec BTK-FedB --federateType RunnerB --federateName RunnerB";

            using (var a = new AgentHost("A", agentExe, argsA))
            using (var b = new AgentHost("B", agentExe, argsB))
            {
                foreach (var ag in new[] { a, b })
                    if (ag.WaitFor("OK READY", TimeSpan.FromSeconds(20)) == null)
                    { Console.Error.WriteLine("agent " + ag.Label + " never READY"); return 1; }

                if (!b.Join(TimeSpan.FromSeconds(30))) { Console.Error.WriteLine("B failed to join"); return 1; }
                if (!a.Join(TimeSpan.FromSeconds(30))) { Console.Error.WriteLine("A failed to join"); return 1; }
                Console.WriteLine("both agents joined");
                Console.WriteLine();

                var runner = new TestRunner(a, b, new RunOptions());
                runner.Progress += (step, msg) => Console.WriteLine("  [step " + step + "] " + msg);

                var report = runner.Run(input, expected);

                Console.WriteLine();
                Console.WriteLine("================ RESULTS ================");
                foreach (var s in report.Steps)
                {
                    Console.WriteLine(s.ToString());
                    foreach (var f in s.Fields)
                        if (!f.Ok) Console.WriteLine("        " + f);
                }
                Console.WriteLine("=========================================");
                Console.WriteLine(report.Summary());
                return report.AllPassed ? 0 : 1;
            }
        }

        /// <summary>--execName / --federateType / --fedFile / --rprVersion overrides.</summary>
        private static void ApplyArgs(string[] args, FederationConfig cfg)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                var v = args[i + 1];
                switch (args[i])
                {
                    case "--execName":     cfg.FederationName = v; i++; break;
                    case "--federateType": cfg.FederateType   = v; i++; break;
                    case "--fedFile":      cfg.FedFile        = v; i++; break;
                    case "--rprVersion":
                        double d; if (double.TryParse(v, out d)) cfg.RprVersion = d; i++; break;
                }
            }
        }
        /// <summary>
        /// Golden-file run over the NATIVE agents - raw RTI, exact bytes, custom attributes.
        ///
        ///   native &lt;input.csv&gt; &lt;output.csv&gt; [--selftest]
        ///
        /// --selftest puts BOTH agents on the SAME HLA 1.3 federation, so the expected mapping is
        /// the identity. That exercises the whole chain - CSV, codec, agent, RTI, agent, compare -
        /// without needing the broker, and any failure is the harness's fault, not the broker's.
        /// Without it, A is HLA 1.3 and B is HLA Evolved with the broker in between.
        /// </summary>
        private static int RunNative(string[] args)
        {
            string inPath = null, outPath = null;
            bool selfTest = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--selftest") { selfTest = true; continue; }
                if (args[i].StartsWith("--")) continue;
                if (inPath == null) inPath = args[i];
                else if (outPath == null) outPath = args[i];
            }
            if (inPath == null || outPath == null)
            {
                Console.Error.WriteLine("usage: native <input.csv> <output.csv> [--selftest]");
                return 2;
            }

            var input = CsvParser.ReadInput(inPath);
            var expected = CsvParser.ReadExpected(outPath);

            var agent13 = FindNativeAgent("btk-agent13.exe");
            var agent1516e = selfTest ? agent13 : FindNativeAgent("btk-agent1516e.exe");

            var cfgA = new NativeSideConfig
            {
                Label = "A",
                AgentExe = agent13,
                FederationName = selfTest ? "BTK-Loopback" : "BTK-FedA",
                FederateName = "BtkRunnerA",
                FederateType = "Harness",
                FedFile = "BTK-CustomRPR1.fed",
                ObjectClass = "BaseEntity",
            };

            var cfgB = selfTest
                ? new NativeSideConfig
                {
                    Label = "B",
                    AgentExe = agent13,
                    FederationName = "BTK-Loopback",
                    FederateName = "BtkRunnerB",
                    FederateType = "Harness",
                    FedFile = "BTK-CustomRPR1.fed",
                    ObjectClass = "BaseEntity",
                }
                : new NativeSideConfig
                {
                    Label = "B",
                    AgentExe = agent1516e,
                    FederationName = "BTK-FedB",
                    FederateName = "BtkRunnerB",
                    FederateType = "Harness",
                    FedFile = "RPR_FOM_v2.0_1516-2010.xml",
                    JoinFomModules = "BTK-CustomRPR2-module.xml",
                    ObjectClass = "HLAobjectRoot.BaseEntity.BtkCustomEntity",
                    ClassAlias = "BtkEntity",
                };

            Console.WriteLine("NATIVE RUN  input=" + inPath + " (" + input.Count + " steps)");
            Console.WriteLine("            expect=" + outPath + " (" + expected.Count + " steps)");
            Console.WriteLine("            mode=" + (selfTest
                ? "SELF-TEST (both agents on one HLA 1.3 federation, identity mapping)"
                : "A [HLA 1.3] -> broker -> B [HLA Evolved]"));
            Console.WriteLine("            agentA=" + cfgA.AgentExe);
            Console.WriteLine("            agentB=" + cfgB.AgentExe);
            Console.WriteLine();

            using (var a = new NativeAgentHost("A", cfgA.AgentExe))
            using (var b = new NativeAgentHost("B", cfgB.AgentExe))
            {
                var runner = new NativeTestRunner(a, b, cfgA, cfgB, new RunOptions());
                runner.Progress += (step, msg) => Console.WriteLine("  [step " + step + "] " + msg);

                // B must be subscribed BEFORE A registers, or the discovery race decides the result.
                runner.Prepare(input, expected);
                Console.WriteLine();

                var report = runner.Run(input, expected);

                Console.WriteLine();
                Console.WriteLine("================ RESULTS ================");
                foreach (var st in report.Steps)
                {
                    Console.WriteLine(st.ToString());
                    foreach (var f in st.Fields)
                        Console.WriteLine("        " + f);
                }
                Console.WriteLine("=========================================");
                Console.WriteLine(report.Summary());
                return report.AllPassed ? 0 : 1;
            }
        }

        /// <summary>
        /// Collect repeated <c>--variant Attr=ENUMERATOR</c> flags. A variant record has no layout
        /// until one alternative is named, so this is how the caller says which.
        /// </summary>
        private static Dictionary<string, string> ParseVariants(string[] args)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i + 1 < args.Length; i++)
            {
                if (args[i] != "--variant") continue;
                var pair = args[i + 1];
                var eq = pair.IndexOf('=');
                if (eq <= 0)
                    throw new FormatException("--variant wants Attribute=ENUMERATOR, got '" + pair + "'");
                map[pair.Substring(0, eq).Trim()] = pair.Substring(eq + 1).Trim();
            }
            return map;
        }

        /// <summary>
        /// Collect repeated <c>--array Attr=N</c> flags. A dynamic array carries no length of its
        /// own - RPR encodes these "without array length, containing only the elements" - so the
        /// caller states how many the sheet should lay out.
        /// </summary>
        private static Dictionary<string, int> ParseArrays(string[] args)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i + 1 < args.Length; i++)
            {
                if (args[i] != "--array") continue;
                var pair = args[i + 1];
                var eq = pair.IndexOf('=');
                int n;
                if (eq <= 0 || !int.TryParse(pair.Substring(eq + 1).Trim(),
                                             System.Globalization.NumberStyles.Integer,
                                             System.Globalization.CultureInfo.InvariantCulture,
                                             out n) || n < 0)
                    throw new FormatException("--array wants Attribute=COUNT, got '" + pair + "'");
                map[pair.Substring(0, eq).Trim()] = n;
            }
            return map;
        }

        /// <summary>Locate a native agent binary, walking up from the CLI's own directory.</summary>
        private static string FindNativeAgent(string exeName)
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 6 && dir != null; i++)
            {
                var candidate = System.IO.Path.Combine(
                    System.IO.Path.Combine(System.IO.Path.Combine(dir, "build"), "native"), exeName);
                if (System.IO.File.Exists(candidate)) return candidate;
                var parent = System.IO.Directory.GetParent(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar));
                dir = parent == null ? null : parent.FullName;
            }
            throw new System.IO.FileNotFoundException(
                "native agent '" + exeName + "' not found under any build/native folder. " +
                "Run src/BrokerTestKit.AgentNative/build-native.bat first.", exeName);
        }

        /// <summary>
        /// fom &lt;file.omt|file.xml&gt; [class] [attr.path ...]
        /// Dump what the FOM parser understood. This is how you check that a customised FOM is
        /// readable BEFORE writing any tests against it - and it is the thing that removes the
        /// datatype column from the CSVs (REQ-09).
        /// </summary>
        private static int RunFom(string[] args)
        {
            var variants = ParseVariants(args);
            var rest = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--variant") { i++; continue; }        // flag AND its value
                if (!args[i].StartsWith("--")) rest.Add(args[i]);
            }
            if (rest.Count == 0)
            {
                Console.Error.WriteLine("usage: fom <file.omt|file.xml> [class] [attribute.path ...] " +
                                        "[--variant Attr=ENUMERATOR]");
                return 2;
            }

            var files = new List<string>();
            var probes = new List<string>();
            string cls = null;
            foreach (var r in rest)
            {
                var ext = System.IO.Path.GetExtension(r).ToLowerInvariant();
                if (ext == ".omt" || ext == ".xml" || ext == ".fed" || ext == ".dif") files.Add(Resolve(r));
                else if (cls == null) cls = r;
                else probes.Add(r);
            }

            var model = FomLoader.Load(files.ToArray());
            Console.WriteLine(model.Summary());
            Console.WriteLine();

            if (cls == null)
            {
                Console.WriteLine("classes (first 40):");
                int n = 0;
                foreach (var k in model.Classes.Keys)
                {
                    Console.WriteLine("  " + k);
                    if (++n >= 40) { Console.WriteLine("  ..."); break; }
                }
                return 0;
            }

            var attrs = model.FindClass(cls);
            if (attrs == null) { Console.Error.WriteLine("class not found: " + cls); return 1; }

            if (probes.Count == 0)
            {
                Console.WriteLine("attributes of " + cls + ":");
                foreach (var a in model.AttributesOf(cls))
                {
                    var r = model.Resolve(cls, a, variants);
                    Console.WriteLine(string.Format("  {0,-28} {1,-32} {2}",
                        a, attrs[a], r == null ? "?" : r.CodecSpec + "  size=" + r.Size));
                }
                return 0;
            }

            Console.WriteLine("resolving on " + cls + ":");
            int bad = 0;
            foreach (var probe in probes)
            {
                string err;
                var r = model.Resolve(cls, probe, variants, out err);
                if (r == null) { Console.WriteLine("  " + probe + "  -> ERROR: " + err); bad++; }
                else Console.WriteLine(string.Format("  {0,-28} type={1,-28} offset={2,-4} size={3,-4} codec={4}",
                    probe, r.TypeName, r.Offset, r.Size, r.CodecSpec));
            }
            return bad == 0 ? 0 : 1;
        }

        /// <summary>Resolve a bare filename against RTI_CONFIG so tests can name FOMs, not paths.</summary>
        private static string Resolve(string file)
        {
            if (System.IO.File.Exists(file)) return file;
            var cfg = MakEnvironment.RtiConfigDir;
            if (!string.IsNullOrEmpty(cfg))
            {
                var candidate = System.IO.Path.Combine(cfg, file);
                if (System.IO.File.Exists(candidate)) return candidate;
            }
            return file;
        }

        /// <summary>
        /// entity &lt;input.csv&gt; &lt;output.csv&gt; [--selftest]
        ///
        /// The entity-per-row format: one class per file, one column per attribute, one row per
        /// entity. Datatypes come from the FOM, not the CSV.
        /// </summary>
        private static int RunEntity(string[] args)
        {
            string inPath = null, outPath = null, profilePath = null;
            var reportPaths = new List<string>();
            bool selfTest = false, quiet = false;
            var opt = new RunOptions();
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (a == "--selftest") { selfTest = true; continue; }
                if (a == "--quiet")    { quiet = true; continue; }
                if (a == "--profile" && i + 1 < args.Length) { profilePath = args[++i]; continue; }
                if (a == "--report"  && i + 1 < args.Length) { reportPaths.Add(args[++i]); continue; }
                if (a == "--quiesce" && i + 1 < args.Length)
                { opt.Quiesce = TimeSpan.FromMilliseconds(double.Parse(args[++i], inv)); continue; }
                if (a == "--timeout" && i + 1 < args.Length)
                { opt.Timeout = TimeSpan.FromSeconds(double.Parse(args[++i], inv)); continue; }
                if (a == "--abort-after" && i + 1 < args.Length)
                { opt.AbortAfterConsecutiveTimeouts = int.Parse(args[++i], inv); continue; }
                if (a.StartsWith("--")) continue;
                if (inPath == null) inPath = a;
                else if (outPath == null) outPath = a;
            }
            if (inPath == null || outPath == null)
            {
                Console.Error.WriteLine("usage: entity <input.csv> <output.csv> [--profile <file>] " +
                                        "[--selftest] [--quiesce ms] [--timeout s] [--abort-after N] " +
                                        "[--report <file.csv|.xml|.html>]... [--quiet]");
                return 2;
            }

            // ---- the rig ---------------------------------------------------------
            // With --profile the whole rig comes from data: which RTI each side links, which rid
            // and Assistant port it uses, which federation and FOM modules it joins, which class
            // carries the data. Without it, the original compiled-in custom-FOM rig is kept so
            // existing invocations still work.
            BridgeProfile profile = profilePath == null ? null : BridgeProfile.Load(Resolve(profilePath));
            if (profile != null)
            {
                Console.WriteLine(profile.Describe());
                Console.WriteLine();
                if (selfTest)
                {
                    Console.Error.WriteLine("--selftest and --profile are mutually exclusive: a profile " +
                                            "already says which two federations to use.");
                    return 2;
                }
            }

            var agentA = FindNativeAgent(profile != null ? profile.A.Agent : "btk-agent13.exe");
            var agentB = selfTest ? agentA
                                  : FindNativeAgent(profile != null ? profile.B.Agent : "btk-agent1516e.exe");

            // A is HLA 1.3, so its datatypes come from the .omt (the .fed carries none).
            var fomA = FomLoader.Load(profile != null ? profile.A.Fom : Resolve("BTK-CustomRPR1.omt"));
            var fomB = selfTest ? fomA
                                : FomLoader.Load(profile != null ? profile.B.Fom : Resolve("BTK-CustomRPR2.xml"));
            Console.WriteLine("FOM A  " + fomA.Summary());
            Console.WriteLine("FOM B  " + fomB.Summary());

            EntityCsvParser.Sheet sheetIn, sheetOut;
            var input = EntityCsvParser.ReadInput(inPath, fomA, out sheetIn);
            var expected = EntityCsvParser.ReadExpected(outPath, fomB, out sheetOut);

            Console.WriteLine();
            Console.WriteLine("INPUT   " + inPath);
            Console.WriteLine("        class=" + sheetIn.ClassName + "  entities=" + input.Count +
                              "  columns=" + string.Join(", ", sheetIn.Columns.ToArray()));
            Console.WriteLine("EXPECT  " + outPath);
            Console.WriteLine("        class=" + sheetOut.ClassName + "  entities=" + expected.Count +
                              "  columns=" + string.Join(", ", sheetOut.Columns.ToArray()));
            Console.WriteLine();
            Console.WriteLine("resolved datatypes (from the FOM, not the CSV):");
            foreach (var col in sheetIn.Columns)
            {
                var r = fomA.Resolve(sheetIn.ClassName, col, sheetIn.Variants);
                Console.WriteLine(string.Format("  A  {0,-24} {1,-26} {2}",
                    col, r == null ? "?" : r.TypeName, r == null ? "?" : r.CodecSpec));
            }
            foreach (var col in sheetOut.Columns)
            {
                var r = fomB.Resolve(sheetOut.ClassName, col, sheetOut.Variants);
                Console.WriteLine(string.Format("  B  {0,-24} {1,-26} {2}",
                    col, r == null ? "?" : r.TypeName, r == null ? "?" : r.CodecSpec));
            }
            Console.WriteLine();
            Console.WriteLine(string.Format(inv, "run options: quiesce={0:F0}ms  timeout={1:F1}s  abort-after={2}",
                opt.Quiesce.TotalMilliseconds, opt.Timeout.TotalSeconds,
                opt.AbortAfterConsecutiveTimeouts > 0
                    ? opt.AbortAfterConsecutiveTimeouts + " consecutive timeouts" : "never"));
            Console.WriteLine();

            NativeSideConfig cfgA, cfgB;
            IDictionary<string, string> envA = null, envB = null;

            if (profile != null)
            {
                // The CSV names the class the way the FOM parser knows it (bare "GroundVehicle");
                // the RTI wants its own spelling, which differs per HLA version. Ask the FOM.
                cfgA = profile.A.ToSideConfig(agentA, fomA.RtiClassName(sheetIn.ClassName),
                                              sheetIn.ClassName, sheetIn.IsInteraction);
                cfgB = profile.B.ToSideConfig(agentB, fomB.RtiClassName(sheetOut.ClassName),
                                              sheetOut.ClassName, sheetOut.IsInteraction);
                envA = profile.A.Environment(profile.Licence);
                envB = profile.B.Environment(profile.Licence);
            }
            else
            {
                cfgA = new NativeSideConfig
                {
                    Label = "A", AgentExe = agentA,
                    FederationName = selfTest ? "BTK-Loopback" : "BTK-FedA",
                    FederateName = "BtkRunnerA", FederateType = "Harness",
                    FedFile = "BTK-CustomRPR1.fed", ObjectClass = sheetIn.ClassName,
                    IsInteraction = sheetIn.IsInteraction,
                };
                cfgB = selfTest
                    ? new NativeSideConfig
                    {
                        Label = "B", AgentExe = agentB,
                        FederationName = "BTK-Loopback",
                        FederateName = "BtkRunnerB", FederateType = "Harness",
                        FedFile = "BTK-CustomRPR1.fed", ObjectClass = sheetOut.ClassName,
                        IsInteraction = sheetOut.IsInteraction,
                    }
                    : new NativeSideConfig
                    {
                        Label = "B", AgentExe = agentB,
                        FederationName = "BTK-FedB",
                        FederateName = "BtkRunnerB", FederateType = "Harness",
                        FedFile = "BTK-CustomRPR2.xml", ObjectClass = sheetOut.ClassName,
                        ClassAlias = "TargetClass",
                        IsInteraction = sheetOut.IsInteraction,
                    };
            }

            using (var a = new NativeAgentHost("A", cfgA.AgentExe, "", envA))
            using (var b = new NativeAgentHost("B", cfgB.AgentExe, "", envB))
            {
                var runner = new NativeTestRunner(a, b, cfgA, cfgB, opt);

                // At 4000 rows a line per row is unreadable, so --quiet keeps only the failures
                // plus a heartbeat. Nothing is lost: --report writes every row regardless.
                int total = input.Count;
                int lastBeat = 0;                 // Progress fires twice per row; beat once
                var wall = System.Diagnostics.Stopwatch.StartNew();
                runner.Progress += (step, msg) =>
                {
                    var bad = msg.StartsWith("Fail") || msg.StartsWith("Timeout");
                    if (!quiet || bad || step == 0)
                    {
                        Console.WriteLine("  [row " + step + "] " + msg);
                    }
                    else if (step % 100 == 0 && step != lastBeat)
                    {
                        lastBeat = step;
                        var rate = step / Math.Max(wall.Elapsed.TotalSeconds, 0.001);
                        var eta = TimeSpan.FromSeconds((total - step) / Math.Max(rate, 0.001));
                        Console.WriteLine(string.Format(inv,
                            "  ... {0}/{1} rows   {2:F1} rows/s   eta {3:hh\\:mm\\:ss}",
                            step, total, rate, eta));
                    }
                };

                runner.Prepare(input, expected);
                Console.WriteLine();

                var report = runner.Run(input, expected);

                Console.WriteLine();
                Console.WriteLine("================ RESULTS ================");
                int shown = 0;
                foreach (var st in report.Steps)
                {
                    if (quiet && st.Outcome == Outcome.Pass) continue;
                    if (quiet && ++shown > 50)
                    {
                        Console.WriteLine("  ... further failures are in the report file only");
                        break;
                    }
                    Console.WriteLine(st.ToString());
                    foreach (var f in st.Fields) Console.WriteLine("        " + f);
                }
                Console.WriteLine("=========================================");
                Console.WriteLine(report.Summary());

                // --report may repeat; the extension picks the format (.xml JUnit, .html page, else CSV),
                // so CI can take the JUnit file and a person the HTML from the same run.
                var context = new[]
                {
                    "input    " + System.IO.Path.GetFullPath(inPath),
                    "expected " + System.IO.Path.GetFullPath(outPath),
                    "profile  " + (profilePath == null ? "(none)" : System.IO.Path.GetFullPath(profilePath)),
                };
                foreach (var rp in reportPaths)
                {
                    RunReportWriter.Write(rp, report, context, System.IO.Path.GetFileNameWithoutExtension(outPath));
                    Console.WriteLine("report  " + System.IO.Path.GetFullPath(rp) +
                                      "  (" + RunReportWriter.FormatFor(rp) + ")");
                }
                return report.AllPassed ? 0 : 1;
            }
        }

        /// <summary>
        /// case &lt;file.testcase&gt; [--profile &lt;file&gt; | --selftest] [--quiesce ms] [--timeout s]
        ///      [--abort-after N] [--report out.csv|.xml|.html]... [--quiet]
        ///
        /// A multi-sheet test case (REQ-13): N Send sheets on A, M Receive sheets on B, one step per
        /// entity group, and a verdict that waits for every expected class.
        /// </summary>
        private static int RunCase(string[] args)
        {
            string casePath = null, profilePath = null;
            var reportPaths = new List<string>();
            bool selfTest = false, quiet = false;
            var opt = new RunOptions();
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (a == "--selftest") { selfTest = true; continue; }
                if (a == "--quiet")    { quiet = true; continue; }
                if (a == "--profile" && i + 1 < args.Length) { profilePath = args[++i]; continue; }
                if (a == "--report"  && i + 1 < args.Length) { reportPaths.Add(args[++i]); continue; }
                if (a == "--quiesce" && i + 1 < args.Length)
                { opt.Quiesce = TimeSpan.FromMilliseconds(double.Parse(args[++i], inv)); continue; }
                if (a == "--timeout" && i + 1 < args.Length)
                { opt.Timeout = TimeSpan.FromSeconds(double.Parse(args[++i], inv)); continue; }
                if (a == "--abort-after" && i + 1 < args.Length)
                { opt.AbortAfterConsecutiveTimeouts = int.Parse(args[++i], inv); continue; }
                if (a.StartsWith("--")) continue;
                if (casePath == null) casePath = a;
            }
            if (casePath == null)
            {
                Console.Error.WriteLine("usage: case <file.testcase> [--profile <file> | --selftest] [--quiesce ms] " +
                                        "[--timeout s] [--abort-after N] [--report <file.csv|.xml|.html>]... [--quiet]");
                return 2;
            }
            if (selfTest && profilePath != null)
            {
                Console.Error.WriteLine("--selftest and --profile are mutually exclusive: a profile " +
                                        "already says which two federations to use.");
                return 2;
            }

            BridgeProfile profile = profilePath == null ? null : BridgeProfile.Load(Resolve(profilePath));
            if (profile != null)
            {
                Console.WriteLine(profile.Describe());
                Console.WriteLine();
                if (!string.IsNullOrEmpty(profile.A.Class) || !string.IsNullOrEmpty(profile.B.Class))
                    Console.WriteLine("note: the profile's class= override is ignored for a test case - " +
                                      "every sheet names its own class.");
            }

            var agentA = FindNativeAgent(profile != null ? profile.A.Agent : "btk-agent13.exe");
            var agentB = selfTest ? agentA
                                  : FindNativeAgent(profile != null ? profile.B.Agent : "btk-agent1516e.exe");
            var fomA = FomLoader.Load(profile != null ? profile.A.Fom : Resolve("BTK-CustomRPR1.omt"));
            var fomB = selfTest ? fomA
                                : FomLoader.Load(profile != null ? profile.B.Fom : Resolve("BTK-CustomRPR2.xml"));
            Console.WriteLine("FOM A  " + fomA.Summary());
            Console.WriteLine("FOM B  " + fomB.Summary());
            Console.WriteLine();

            var tc = TestCase.Load(casePath, fomA, fomB);
            Console.WriteLine(tc.Describe());
            Console.WriteLine();
            Console.WriteLine(string.Format(inv, "run options: quiesce={0:F0}ms  timeout={1:F1}s  abort-after={2}",
                opt.Quiesce.TotalMilliseconds, opt.Timeout.TotalSeconds,
                opt.AbortAfterConsecutiveTimeouts > 0
                    ? opt.AbortAfterConsecutiveTimeouts + " consecutive timeouts" : "never"));
            Console.WriteLine();

            NativeSideConfig cfgA, cfgB;
            IDictionary<string, string> envA = null, envB = null;
            if (profile != null)
            {
                // Class fields are per sheet in a test case; only the join parameters are used.
                cfgA = profile.A.ToSideConfig(agentA, null, null, false);
                cfgB = profile.B.ToSideConfig(agentB, null, null, false);
                envA = profile.A.Environment(profile.Licence);
                envB = profile.B.Environment(profile.Licence);
            }
            else
            {
                cfgA = new NativeSideConfig
                {
                    Label = "A", AgentExe = agentA,
                    FederationName = selfTest ? "BTK-Loopback" : "BTK-FedA",
                    FederateName = "BtkRunnerA", FederateType = "Harness", FedFile = "BTK-CustomRPR1.fed",
                };
                cfgB = new NativeSideConfig
                {
                    Label = "B", AgentExe = agentB,
                    FederationName = selfTest ? "BTK-Loopback" : "BTK-FedB",
                    FederateName = "BtkRunnerB", FederateType = "Harness",
                    FedFile = selfTest ? "BTK-CustomRPR1.fed" : "BTK-CustomRPR2.xml",
                };
            }

            using (var a = new NativeAgentHost("A", cfgA.AgentExe, "", envA))
            using (var b = new NativeAgentHost("B", cfgB.AgentExe, "", envB))
            {
                var runner = new TestCaseRunner(a, b, cfgA, cfgB, opt);
                runner.Progress += (step, msg) =>
                {
                    var bad = msg.StartsWith("Fail") || msg.StartsWith("Timeout") || msg.StartsWith("ABORT");
                    if (!quiet || bad || step == 0) Console.WriteLine("  [step " + step + "] " + msg);
                };

                runner.Prepare(tc);
                Console.WriteLine();
                var report = runner.Run(tc);

                Console.WriteLine();
                Console.WriteLine("================ RESULTS ================");
                foreach (var st in report.Steps)
                {
                    if (quiet && st.Outcome == Outcome.Pass) continue;
                    Console.WriteLine(st.ToString());
                    foreach (var p in st.Parts) Console.WriteLine("      " + p);
                    foreach (var f in st.Fields) if (!quiet || !f.Ok) Console.WriteLine("        " + f);
                }
                Console.WriteLine("=========================================");
                Console.WriteLine(report.Summary());

                var context = new List<string>
                {
                    "testcase " + tc.ManifestPath,
                    "profile  " + (profilePath == null ? (selfTest ? "(self-test loopback)" : "(none)")
                                                       : System.IO.Path.GetFullPath(profilePath)),
                };
                foreach (var s in tc.AllSheets)
                    context.Add((s.IsSend ? "send     " : "receive  ") + s.Side + " " + s.ClassName + "  " +
                                System.IO.Path.GetFullPath(s.Sheet.SourceFile));

                foreach (var rp in reportPaths)
                {
                    RunReportWriter.Write(rp, report, context, tc.Name);
                    Console.WriteLine("report  " + System.IO.Path.GetFullPath(rp) +
                                      "  (" + RunReportWriter.FormatFor(rp) + ")");
                }
                return report.AllPassed ? 0 : 1;
            }
        }

        /// <summary>
        /// template &lt;fom.omt|fom.xml&gt; &lt;ClassOrInteraction&gt; [out.csv] [--expected] [--rows N] [--only a,b,c]
        ///
        /// The headless form of the GUI's "upload a FOM, generate a CSV template" step. The FOM
        /// decides the columns; the author only fills in values. Structs are expanded to their
        /// addressable leaf fields, since that is the level people reason about.
        /// </summary>
        private static int RunTemplate(string[] args)
        {
            var rest = new List<string>();
            var opt = new CsvTemplateGenerator.Options();
            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (a == "--expected") { opt.ForExpectations = true; continue; }
                if (a == "--skip-variable") { opt.SkipVariable = true; continue; }
                if (a == "--rows" && i + 1 < args.Length) { opt.SampleRows = int.Parse(args[++i]); continue; }
                if (a == "--variant" && i + 1 < args.Length) { i++; continue; }   // read by ParseVariants
                if (a == "--array" && i + 1 < args.Length) { i++; continue; }     // read by ParseArrays
                if (a == "--max" && i + 1 < args.Length) { opt.MaxColumns = int.Parse(args[++i]); continue; }
                if (a == "--only" && i + 1 < args.Length)
                {
                    opt.Include = new List<string>(args[++i].Split(','));
                    for (int k = 0; k < opt.Include.Count; k++) opt.Include[k] = opt.Include[k].Trim();
                    continue;
                }
                if (a.StartsWith("--")) continue;
                rest.Add(a);
            }
            if (rest.Count < 2)
            {
                Console.Error.WriteLine(
                    "usage: template <fom.omt|fom.xml> <ClassOrInteraction> [out.csv] " +
                    "[--expected] [--rows N] [--only a,b,c] [--skip-variable] [--max N] " +
                    "[--variant Attr=ENUMERATOR] [--array Attr=COUNT]");
                return 2;
            }

            opt.Variants = ParseVariants(args);
            opt.Arrays = ParseArrays(args);

            var fom = FomLoader.Load(Resolve(rest[0]));
            var target = rest[1];
            var outPath = rest.Count > 2 ? rest[2] : null;

            var text = CsvTemplateGenerator.Generate(fom, target, opt);

            var sheet = CsvTemplateGenerator.Build(fom, target, opt);

            if (outPath == null) Console.Write(text);
            else
            {
                System.IO.File.WriteAllText(outPath, text);
                Console.WriteLine("wrote " + outPath);
                Console.WriteLine("  source  " + fom.Describe());
                Console.WriteLine("  target  " + target +
                                  (fom.IsInteraction(target) ? "  (interaction)" : "  (object class)"));
                Console.WriteLine("  columns " + sheet.Columns.Count);
            }

            // Anything left out is said out loud, on stderr, whether the sheet went to a file or
            // to stdout. A template that quietly omits attributes reads as "the FOM lacks them".
            if (sheet.Unaddressable.Count > 0)
                Console.Error.WriteLine("  note    " + sheet.Unaddressable.Count +
                    " attribute(s) have no addressable fixed layout and were left out: " +
                    string.Join(", ", sheet.Unaddressable.ToArray()));
            if (sheet.Dropped.Count > 0)
            {
                Console.Error.WriteLine("  TRUNCATED: column cap " + opt.MaxColumns + " reached - " +
                    sheet.Dropped.Count + " attribute(s) MISSING: " +
                    string.Join(", ", sheet.Dropped.ToArray()));
                Console.Error.WriteLine("             re-run with --max <n> or --only <a,b,c>.");
                return 3;
            }
            return 0;
        }

    }
}
