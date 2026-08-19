using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace BrokerTestKit.Engine
{
    /// <summary>One object instance observed by a native agent, as RAW BYTES per attribute.</summary>
    public sealed class NativeObject
    {
        /// <summary>RTI object handle as reported by the agent (stable for the federation's life).</summary>
        public string Handle;
        public string ClassName;
        public string InstanceName;
        public DateTime LastSeenUtc;

        /// <summary>Attribute name -&gt; exactly the bytes that came off the wire.</summary>
        public readonly Dictionary<string, byte[]> Attributes =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public byte[] Get(string attribute)
        {
            byte[] v;
            return Attributes.TryGetValue(attribute, out v) ? v : null;
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} [{1}] '{2}' attrs={3}",
                Handle, ClassName, InstanceName, Attributes.Count);
        }
    }

    /// <summary>
    /// One interaction received by a native agent, as RAW BYTES per parameter.
    /// Interactions carry no instance identity - they are fire-and-forget events - so unlike
    /// <see cref="NativeObject"/> there is nothing to correlate by except the values themselves.
    /// </summary>
    public sealed class NativeInteraction
    {
        public string ClassName;
        public DateTime ReceivedUtc;

        public readonly Dictionary<string, byte[]> Parameters =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public byte[] Get(string parameter)
        {
            byte[] v;
            return Parameters.TryGetValue(parameter, out v) ? v : null;
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} params={1}",
                ClassName, Parameters.Count);
        }
    }

    /// <summary>
    /// Parent-side handle to one NATIVE agent (<c>btk-agent13.exe</c> / <c>btk-agent1516e.exe</c>).
    ///
    /// The difference from <see cref="AgentHost"/> is what comes back: the managed agent emits
    /// pre-decoded <c>ENT</c> lines (VR-Link having already interpreted the RPR FOM), whereas the
    /// native agent emits <c>ATTR … hex=…</c> - the untouched bytes. That is what lets the harness
    /// verify CUSTOM attributes and hold the broker to byte-exact translation (REQ-08 / REQ-09).
    ///
    /// One agent process per federation, exactly as before (REQ-06).
    /// </summary>
    public sealed class NativeAgentHost : IDisposable
    {
        private readonly Process _proc;
        /// <summary>
        /// The agent's stdin, wrapped so the encoding is OURS and not the caller's.
        ///
        /// Process.StandardInput inherits the console's encoding on .NET Framework. Launch the
        /// CLI from a UTF-8 console and the very first write emits a BYTE-ORDER MARK, which the
        /// agent reads as part of the first word: "unknown verb '﻿JOIN'". The rig then fails
        /// on a command that is plainly correct in the transcript, and only when run from certain
        /// shells. Pin it to UTF-8 without a BOM.
        /// </summary>
        private readonly StreamWriter _stdin;
        private readonly ConcurrentQueue<string> _lines = new ConcurrentQueue<string>();
        private readonly List<string> _transcript = new List<string>();
        private readonly object _gate = new object();
        private readonly Dictionary<string, NativeObject> _objects =
            new Dictionary<string, NativeObject>(StringComparer.Ordinal);
        private readonly List<NativeInteraction> _interactions = new List<NativeInteraction>();

        public string Label { get; private set; }
        public string ExePath { get; private set; }

        /// <summary>Raised for every line the agent emits (GUI logs, transcripts).</summary>
        public event Action<string, string> LineReceived;   // (label, line)

        public NativeAgentHost(string label, string agentExePath, string agentArgs = "",
                               IDictionary<string, string> environment = null)
        {
            if (!File.Exists(agentExePath))
                throw new FileNotFoundException(
                    "native agent not built: " + agentExePath +
                    "  (run src\\BrokerTestKit.AgentNative\\build-native.bat)", agentExePath);

            Label = label;
            ExePath = agentExePath;

            var psi = new ProcessStartInfo(agentExePath, agentArgs)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(agentExePath)) ?? ".",
            };

            // The agent resolves FOM files against RTI_CONFIG and reads RTI_RID_FILE; both must be
            // in place before it constructs its ambassador. By default it inherits whatever
            // MakEnvironment set, but each agent can be given its OWN environment - that is what
            // lets Federation A and Federation B run against different RTI installs at once, which
            // the one-process-per-federation design always allowed but nothing previously used.
            if (environment != null)
                foreach (var kv in environment) psi.EnvironmentVariables[kv.Key] = kv.Value;

            _proc = new Process { StartInfo = psi };
            _proc.OutputDataReceived += (s, e) => { if (e.Data != null) OnLine(e.Data); };
            _proc.ErrorDataReceived += (s, e) => { if (e.Data != null) OnStdErr(e.Data); };
            _proc.Start();
            _stdin = new StreamWriter(_proc.StandardInput.BaseStream,
                                      new UTF8Encoding(false)) { AutoFlush = true };
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
        }

        public bool HasExited { get { try { return _proc.HasExited; } catch { return true; } } }

        public string[] Transcript { get { lock (_gate) return _transcript.ToArray(); } }

        /// <summary>Snapshot of everything currently discovered, newest state per object.</summary>
        public NativeObject[] Objects
        {
            get
            {
                lock (_gate)
                {
                    var a = new NativeObject[_objects.Count];
                    _objects.Values.CopyTo(a, 0);
                    return a;
                }
            }
        }

        /// <summary>Interactions received since the last <see cref="ClearObserved"/>, in order.</summary>
        public NativeInteraction[] Interactions
        {
            get { lock (_gate) return _interactions.ToArray(); }
        }

        public void Send(string command)
        {
            if (HasExited) throw new InvalidOperationException(Label + ": agent has exited");
            _stdin.WriteLine(command);
            _stdin.Flush();
        }

        /// <summary>
        /// Send a command and wait for its <c>OK &lt;verb&gt;</c>. Throws on <c>ERR</c> so a
        /// mis-specified FOM fails the run loudly instead of silently producing no data - the
        /// exact failure mode that made the C# agent's custom-attribute gap so hard to see.
        /// </summary>
        public string Call(string command, TimeSpan timeout)
        {
            var verb = command.Split(' ')[0].ToUpperInvariant();
            Send(command);

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                string line;
                while (_lines.TryDequeue(out line))
                {
                    if (line.StartsWith("ERR ", StringComparison.Ordinal))
                        throw new InvalidOperationException(Label + ": " + line.Substring(4).Trim() +
                                                            "  (command: " + command + ")");
                    if (line.StartsWith("OK " + verb, StringComparison.Ordinal)) return line;
                }
                if (HasExited) throw new InvalidOperationException(Label + ": agent exited during " + verb);
                System.Threading.Thread.Sleep(10);
            }
            throw new TimeoutException(Label + ": no reply to '" + command + "' within " +
                                       timeout.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s");
        }

        // ------------------------------------------------------------ commands ----

        public void Join(string exec, string fedFile, string federateName,
                         string federateType = null, string joinFomModules = null,
                         string mim = null, bool create = true, TimeSpan? timeout = null)
        {
            var sb = new StringBuilder("JOIN");
            sb.Append(" exec=").Append(exec);
            if (!string.IsNullOrEmpty(fedFile)) sb.Append(" fed=").Append(fedFile);
            sb.Append(" federate=").Append(federateName);
            if (!string.IsNullOrEmpty(federateType)) sb.Append(" type=").Append(federateType);
            if (!string.IsNullOrEmpty(joinFomModules)) sb.Append(" joinfom=").Append(joinFomModules);
            if (!string.IsNullOrEmpty(mim)) sb.Append(" mim=").Append(mim);
            sb.Append(" create=").Append(create ? "1" : "0");

            Call(sb.ToString(), timeout ?? TimeSpan.FromSeconds(30));
        }

        /// <summary>
        /// Resolve a class and its attributes BY NAME and publish and/or subscribe them.
        /// <paramref name="alias"/> gives a short handle for fully-qualified 1516 class names.
        /// </summary>
        public void DeclareClass(string className, IEnumerable<string> attributes, string mode,
                                 string alias = null, TimeSpan? timeout = null)
        {
            var list = new List<string>();
            foreach (var a in attributes) if (!string.IsNullOrEmpty(a) && !list.Contains(a)) list.Add(a);

            var cmd = "CLASS name=" + className +
                      (string.IsNullOrEmpty(alias) ? "" : " as=" + alias) +
                      " attrs=" + string.Join(",", list.ToArray()) +
                      " mode=" + mode;
            Call(cmd, timeout ?? TimeSpan.FromSeconds(15));
        }

        public void Register(string localId, string classOrAlias, string instanceName = null,
                             TimeSpan? timeout = null)
        {
            var cmd = "REGISTER id=" + localId + " class=" + classOrAlias +
                      (string.IsNullOrEmpty(instanceName) ? "" : " name=" + instanceName);
            Call(cmd, timeout ?? TimeSpan.FromSeconds(15));
        }

        /// <summary>Stage one attribute's raw bytes. Nothing goes on the wire until <see cref="Update"/>.</summary>
        public void SetAttr(string localId, string attribute, byte[] value, TimeSpan? timeout = null)
        {
            Call("SETATTR id=" + localId + " name=" + attribute + " hex=" + ValueCodec.ToHex(value),
                 timeout ?? TimeSpan.FromSeconds(10));
        }

        /// <summary>Flush all staged attributes as ONE update, so a CSV step is one reflect.</summary>
        public void Update(string localId, string tag = null, TimeSpan? timeout = null)
        {
            Call("UPDATE id=" + localId + (string.IsNullOrEmpty(tag) ? "" : " tag=" + tag),
                 timeout ?? TimeSpan.FromSeconds(10));
        }

        /// <summary>
        /// Resolve an interaction and its parameters BY NAME and publish and/or subscribe.
        /// HLA publishes an interaction CLASS as a whole - there is no per-parameter publish -
        /// so the parameter list only resolves handles.
        /// </summary>
        public void DeclareInteraction(string className, IEnumerable<string> parameters, string mode,
                                       string alias = null, TimeSpan? timeout = null)
        {
            var list = new List<string>();
            foreach (var pn in parameters) if (!string.IsNullOrEmpty(pn) && !list.Contains(pn)) list.Add(pn);

            var cmd = "INTCLASS name=" + className +
                      (string.IsNullOrEmpty(alias) ? "" : " as=" + alias) +
                      " params=" + string.Join(",", list.ToArray()) +
                      " mode=" + mode;
            Call(cmd, timeout ?? TimeSpan.FromSeconds(15));
        }

        /// <summary>Stage one parameter. Nothing is sent until <see cref="SendInteraction"/>.</summary>
        public void SetParam(string classOrAlias, string parameter, byte[] value, TimeSpan? timeout = null)
        {
            Call("SETPARAM class=" + classOrAlias + " name=" + parameter +
                 " hex=" + ValueCodec.ToHex(value), timeout ?? TimeSpan.FromSeconds(10));
        }

        /// <summary>Flush the staged parameters as ONE interaction.</summary>
        public void SendInteraction(string classOrAlias, string tag = null, TimeSpan? timeout = null)
        {
            Call("SENDINT class=" + classOrAlias + (string.IsNullOrEmpty(tag) ? "" : " tag=" + tag),
                 timeout ?? TimeSpan.FromSeconds(10));
        }

        public void Ping(TimeSpan? timeout = null) { Call("PING", timeout ?? TimeSpan.FromSeconds(5)); }

        /// <summary>Let the agent tick for a while (it cannot pump while blocked on us).</summary>
        public void Wait(int milliseconds, TimeSpan? timeout = null)
        {
            Call("WAIT ms=" + milliseconds.ToString(CultureInfo.InvariantCulture),
                 timeout ?? TimeSpan.FromMilliseconds(milliseconds + 10000));
        }

        /// <summary>Forget everything discovered so far (used to quiesce B between steps).</summary>
        public void ClearObserved()
        {
            lock (_gate) { _objects.Clear(); _interactions.Clear(); }
        }

        // --------------------------------------------------------------- lines ----

        private void OnStdErr(string data)
        {
            lock (_gate) _transcript.Add("[stderr] " + data);
            var h = LineReceived;
            if (h != null) h(Label, "[stderr] " + data);
        }

        private void OnLine(string line)
        {
            lock (_gate) _transcript.Add(line);

            if (line.StartsWith("ATTR ", StringComparison.Ordinal)) OnAttr(line);
            else if (line.StartsWith("DISC ", StringComparison.Ordinal)) OnDisc(line);
            else if (line.StartsWith("GONE ", StringComparison.Ordinal)) OnGone(line);
            else if (line.StartsWith("PARAM ", StringComparison.Ordinal)) OnParam(line);
            else if (line.StartsWith("INT ", StringComparison.Ordinal)) OnInt(line);
            else _lines.Enqueue(line);      // OK / ERR / banner - for Call()

            var h = LineReceived;
            if (h != null) h(Label, line);
        }

        private void OnDisc(string line)
        {
            var kv = Kv(line, 5);
            string obj;
            if (!kv.TryGetValue("obj", out obj)) return;

            lock (_gate)
            {
                NativeObject o;
                if (!_objects.TryGetValue(obj, out o))
                {
                    o = new NativeObject { Handle = obj };
                    _objects[obj] = o;
                }
                o.ClassName = Val(kv, "class");
                o.InstanceName = Val(kv, "name");
                o.LastSeenUtc = DateTime.UtcNow;
            }
        }

        private void OnAttr(string line)
        {
            var kv = Kv(line, 5);
            string obj, name;
            if (!kv.TryGetValue("obj", out obj) || !kv.TryGetValue("name", out name)) return;

            byte[] value;
            try { value = ValueCodec.FromHex(Val(kv, "hex")); }
            catch (FormatException) { return; }

            lock (_gate)
            {
                NativeObject o;
                if (!_objects.TryGetValue(obj, out o))
                {
                    o = new NativeObject { Handle = obj, ClassName = Val(kv, "class") };
                    _objects[obj] = o;
                }
                o.Attributes[name] = value;
                o.LastSeenUtc = DateTime.UtcNow;
            }
        }

        private void OnGone(string line)
        {
            var kv = Kv(line, 5);
            string obj;
            if (!kv.TryGetValue("obj", out obj)) return;
            lock (_gate) _objects.Remove(obj);
        }

        /// <summary>An INT line opens a new interaction; the PARAM lines that follow fill it.</summary>
        private void OnInt(string line)
        {
            var kv = Kv(line, 4);
            lock (_gate)
                _interactions.Add(new NativeInteraction
                {
                    ClassName = Val(kv, "class"),
                    ReceivedUtc = DateTime.UtcNow,
                });
        }

        private void OnParam(string line)
        {
            var kv = Kv(line, 6);
            string name;
            if (!kv.TryGetValue("name", out name)) return;

            byte[] value;
            try { value = ValueCodec.FromHex(Val(kv, "hex")); }
            catch (FormatException) { return; }

            lock (_gate)
            {
                // The agent is single-threaded and emits INT then its PARAMs contiguously, so the
                // last interaction is always the one being filled. Tolerate a stray PARAM anyway.
                if (_interactions.Count == 0)
                    _interactions.Add(new NativeInteraction
                    {
                        ClassName = Val(kv, "class"),
                        ReceivedUtc = DateTime.UtcNow,
                    });
                _interactions[_interactions.Count - 1].Parameters[name] = value;
            }
        }

        private static Dictionary<string, string> Kv(string line, int skip)
        {
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var body = line.Length > skip ? line.Substring(skip) : "";
            foreach (var tok in body.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var i = tok.IndexOf('=');
                if (i > 0) kv[tok.Substring(0, i)] = tok.Substring(i + 1);
            }
            return kv;
        }

        private static string Val(Dictionary<string, string> kv, string k)
        {
            string v;
            return kv.TryGetValue(k, out v) ? v : "";
        }

        public void Dispose()
        {
            try
            {
                if (!HasExited)
                {
                    _stdin.WriteLine("QUIT");
                    _stdin.Flush();
                    _proc.WaitForExit(5000);
                }
            }
            catch { }
            try { if (!HasExited) _proc.Kill(); } catch { }
            try { _proc.Dispose(); } catch { }
        }
    }
}
