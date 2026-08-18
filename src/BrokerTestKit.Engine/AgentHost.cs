using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace BrokerTestKit.Engine
{
    /// <summary>One entity observed by an agent (already decoded by VR-Link).</summary>
    public sealed class ObservedEntity
    {
        public string Id;
        public double X, Y, Z;
        public string Marking;
        public DateTime SeenUtc;

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0} @ ({1}, {2}, {3}) marking={4}", Id, X, Y, Z, Marking);
        }
    }

    /// <summary>
    /// Parent-side handle to one <c>BrokerTestKit.Agent</c> child process.
    ///
    /// Each agent owns exactly ONE federation connection, because VR-Link's ManagedInterface
    /// is a per-process singleton (docs/vr-link-knowledge.md 3y). The GUI/Engine hold no RTI
    /// at all, so they are VC-agnostic and can even drive agents on different RTI versions.
    /// </summary>
    public sealed class AgentHost : IDisposable
    {
        private readonly Process _proc;
        private readonly ConcurrentQueue<string> _lines = new ConcurrentQueue<string>();
        private readonly List<string> _transcript = new List<string>();
        private readonly object _gate = new object();

        /// <summary>Latest state per entity id, updated as ENT lines stream in.</summary>
        public readonly Dictionary<string, ObservedEntity> Entities =
            new Dictionary<string, ObservedEntity>(StringComparer.Ordinal);

        public string Label { get; private set; }

        /// <summary>Raised for every line the agent emits (useful for GUI logs).</summary>
        public event Action<string, string> LineReceived;   // (label, line)

        public AgentHost(string label, string agentExePath, string agentArgs)
        {
            Label = label;
            var psi = new ProcessStartInfo(agentExePath, agentArgs)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(agentExePath) ?? ".",
            };
            _proc = new Process { StartInfo = psi };
            _proc.OutputDataReceived += (s, e) => { if (e.Data != null) OnLine(e.Data); };
            _proc.ErrorDataReceived += (s, e) => { if (e.Data != null) OnLine("STDERR " + e.Data); };
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
        }

        public bool HasExited { get { try { return _proc.HasExited; } catch { return true; } } }

        public void Send(string command)
        {
            _proc.StandardInput.WriteLine(command);
            _proc.StandardInput.Flush();
        }

        /// <summary>Wait until a line starting with <paramref name="prefix"/> arrives, or time out.</summary>
        public string WaitFor(string prefix, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                string line;
                while (_lines.TryDequeue(out line))
                    if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return line;
                if (HasExited) return null;
                System.Threading.Thread.Sleep(15);
            }
            return null;
        }

        /// <summary>Join the federation; returns true on OK JOINED.</summary>
        public bool Join(TimeSpan timeout)
        {
            Send("JOIN");
            return WaitFor("OK JOINED", timeout) != null;
        }

        /// <summary>Publish/update an entity by stable id.</summary>
        public void Publish(string id, double x, double y, double z, string marking)
        {
            Send(string.Format(CultureInfo.InvariantCulture,
                "PUBLISH id={0} x={1} y={2} z={3} marking={4}", id, x, y, z, marking));
        }

        /// <summary>
        /// Wait until an entity matching a predicate is observed (DESIGN 9.1: match-or-timeout
        /// on CONTENT, never a fixed sleep).
        /// </summary>
        public ObservedEntity WaitForEntity(Func<ObservedEntity, bool> match, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                lock (_gate)
                {
                    foreach (var e in Entities.Values) if (match(e)) return e;
                }
                if (HasExited) return null;
                System.Threading.Thread.Sleep(25);
            }
            return null;
        }

        public string[] Transcript { get { lock (_gate) return _transcript.ToArray(); } }

        private void OnLine(string line)
        {
            lock (_gate) _transcript.Add(line);
            _lines.Enqueue(line);

            if (line.StartsWith("ENT ", StringComparison.Ordinal))
            {
                var e = ParseEnt(line);
                if (e != null) lock (_gate) Entities[e.Id] = e;
            }

            var h = LineReceived;
            if (h != null) h(Label, line);
        }

        private static ObservedEntity ParseEnt(string line)
        {
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tok in line.Substring(4).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var i = tok.IndexOf('=');
                if (i > 0) kv[tok.Substring(0, i)] = tok.Substring(i + 1);
            }
            string id;
            if (!kv.TryGetValue("id", out id)) return null;
            return new ObservedEntity
            {
                Id = id,
                X = D(kv, "x"),
                Y = D(kv, "y"),
                Z = D(kv, "z"),
                Marking = kv.ContainsKey("marking") ? kv["marking"] : "",
                SeenUtc = DateTime.UtcNow,
            };
        }

        private static double D(Dictionary<string, string> kv, string k)
        {
            string v; double d;
            if (kv.TryGetValue(k, out v) &&
                double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out d)) return d;
            return 0;
        }

        public void Dispose()
        {
            try { if (!HasExited) { Send("QUIT"); _proc.WaitForExit(4000); } } catch { }
            try { if (!HasExited) _proc.Kill(); } catch { }
            try { _proc.Dispose(); } catch { }
        }
    }
}
