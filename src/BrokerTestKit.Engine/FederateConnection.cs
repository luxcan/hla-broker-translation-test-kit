using System;
using System.Collections.Generic;
using makVrl;

namespace BrokerTestKit.Engine
{
    /// <summary>A generic HLA object/interaction sample: class name + attributes as raw bytes.</summary>
    public sealed class GenericSample
    {
        public string ObjClass;
        public string ObjName;
        public readonly Dictionary<string, byte[]> Attributes =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);

        public override string ToString()
        {
            return string.Format("{0} '{1}' ({2} attrs)", ObjClass, ObjName, Attributes.Count);
        }
    }

    /// <summary>
    /// One federate joined to one federation, using VR-Link's **Universal HLA layer** so all
    /// access is FOM-agnostic: classes and attributes are addressed BY NAME and values cross
    /// as raw byte[]. This keeps the harness independent of the broker's internal translation
    /// path and immune to FOM attribute renames.
    ///
    /// Call MakEnvironment.Initialize() once before constructing any of these.
    /// </summary>
    public sealed class FederateConnection : IDisposable
    {
        private ExerciseConnection _conn;
        private readonly List<Tuple<string, MessageDelegate, string>> _handlers =
            new List<Tuple<string, MessageDelegate, string>>();

        public FederationConfig Config { get; private set; }
        public bool IsConnected { get { return _conn != null; } }

        /// <summary>
        /// The underlying VR-Link connection, for the TYPED path (ReflectedEntityList etc.).
        /// NOTE: the Universal HLA message layer is NOT fed in a normal federate — verified
        /// empirically — so entity traffic must be observed via ReflectedEntityList, using
        /// StateRepository.getAttribute(name) for by-name (rename-proof) access.
        /// </summary>
        public ExerciseConnection Raw { get { RequireConnected(); return _conn; } }

        /// <summary>
        /// Typed reflected-entity list — the working way to observe remote entities.
        /// NOTE: has Dispose() but does NOT implement IDisposable, so no `using`.
        /// </summary>
        public ReflectedEntityList CreateEntityList()
        {
            RequireConnected();
            return new ReflectedEntityList(_conn);
        }

        /// <summary>
        /// Typed entity publisher — the working way to put an entity on the wire.
        /// Set fields on <c>.esr</c>, then call <c>.tick(dt)</c> to send.
        /// NOTE: has Dispose() but does NOT implement IDisposable, so no `using`.
        /// </summary>
        public EntityPublisher CreateEntityPublisher()
        {
            RequireConnected();
            return new EntityPublisher(_conn);
        }

        /// <summary>Raised when an object is discovered (identity only — state arrives later).</summary>
        public event Action<GenericSample> ObjectDiscovered;
        /// <summary>Raised on an attribute update (this is where state is valid).</summary>
        public event Action<GenericSample> ObjectUpdated;
        /// <summary>Raised when an object is removed.</summary>
        public event Action<GenericSample> ObjectRemoved;
        /// <summary>Raised when an interaction is received.</summary>
        public event Action<GenericSample> InteractionReceived;

        public FederateConnection(FederationConfig config)
        {
            if (config == null) throw new ArgumentNullException("config");
            Config = config;
        }

        /// <summary>Create/join the federation execution.</summary>
        public void Connect()
        {
            if (_conn != null) throw new InvalidOperationException("Already connected: " + Config);

            ExerciseConnectionInitializer init;
            if (Config.Version == HlaVersion.Hla13)
            {
                // NOTE: the HLA 1.3 initializer has NO federateName property — only federateType.
                var i = new Hla13ExerciseConnectionInitializer
                {
                    federationName          = Config.FederationName,
                    federateType            = Config.FederateType,
                    fedFilename             = Config.FedFile,
                    rprVersion              = Config.RprVersion,
                    enableGenericAttributes = Config.EnableGenericAttributes,
                    networkBigEndian        = Config.NetworkBigEndian,
                };
                init = i;
            }
            else
            {
                var i = new Hla1516eExerciseConnectionInitializer
                {
                    federationName          = Config.FederationName,
                    federateType            = Config.FederateType,
                    fedFilename             = Config.FedFile,
                    rprVersion              = Config.RprVersion,
                    enableGenericAttributes = Config.EnableGenericAttributes,
                    networkBigEndian        = Config.NetworkBigEndian,
                };
                if (!string.IsNullOrEmpty(Config.FederateName)) i.federateName = Config.FederateName;
                if (!string.IsNullOrEmpty(Config.Mim)) i.mim = Config.Mim;
                if (Config.FomModules != null && Config.FomModules.Count > 0) i.fomModules = Config.FomModules;
                init = i;
            }

            _conn = new ExerciseConnection(init);   // connects + creates-if-absent + joins
        }

        /// <summary>
        /// Subscribe to a FOM object class by name. Handlers fire during Pump().
        /// </summary>
        public void SubscribeObject(string fomObjectClass)
        {
            RequireConnected();
            Add(UniversalHlaObjDiscoveryMessage.theName, fomObjectClass,
                m => Raise(ObjectDiscovered, ToSample(m)));
            Add(UniversalHlaObjStateMessage.theName, fomObjectClass,
                m => Raise(ObjectUpdated, ToSample(m)));
            Add(UniversalHlaObjRemovedMessage.theName, fomObjectClass,
                m => Raise(ObjectRemoved, ToSample(m)));
        }

        /// <summary>
        /// Subscribe to ALL Universal object messages regardless of FOM class (empty class
        /// filter). Useful for diagnostics and for "what actually arrived" discovery.
        /// </summary>
        public void SubscribeAllObjects()
        {
            RequireConnected();
            Add(UniversalHlaObjDiscoveryMessage.theName, "", m => Raise(ObjectDiscovered, ToSample(m)));
            Add(UniversalHlaObjStateMessage.theName,     "", m => Raise(ObjectUpdated,    ToSample(m)));
            Add(UniversalHlaObjRemovedMessage.theName,   "", m => Raise(ObjectRemoved,    ToSample(m)));
        }

        /// <summary>Subscribe to a FOM interaction class by name.</summary>
        public void SubscribeInteraction(string fomInteractionClass)
        {
            RequireConnected();
            Add(UniversalHlaInteractionMessage.theName, fomInteractionClass,
                m => Raise(InteractionReceived, ToSample(m)));
        }

        /// <summary>Publish/update a generic object (attributes are raw, FOM-encoded bytes).</summary>
        public void PublishObject(GenericSample sample)
        {
            RequireConnected();
            var msg = new UniversalHlaObjStateMessage
            {
                objClass = sample.ObjClass,
                objName  = sample.ObjName,
            };
            foreach (var kv in sample.Attributes) msg.setAttribute(kv.Key, kv.Value);
            _conn.sendMessage(msg);
        }

        /// <summary>Send a generic interaction (parameters are raw, FOM-encoded bytes).</summary>
        public void SendInteraction(GenericSample sample)
        {
            RequireConnected();
            var msg = new UniversalHlaInteractionMessage { objClass = sample.ObjClass };
            foreach (var kv in sample.Attributes) msg.setAttribute(kv.Key, kv.Value);
            _conn.sendMessage(msg);
        }

        /// <summary>
        /// Tick the RTI: process inbound traffic (firing handlers) AND flush outbound
        /// updates. Must be called regularly — nothing is sent or received without it.
        ///
        /// ⚠️ Use a NEGATIVE value (the default). Empirically, <c>drainInput(0.0)</c> does
        /// NOT tick the RTI: objects register but attribute updates never reach subscribers,
        /// and nothing is received. MAK's own examples call <c>drainInput(-1.0)</c>, which
        /// processes everything currently available and returns.
        /// </summary>
        public void Pump(double seconds = -1.0)
        {
            RequireConnected();
            _conn.drainInput(seconds);
        }

        public void Dispose()
        {
            if (_conn == null) return;
            foreach (var h in _handlers)
            {
                try { _conn.removeMessageHandler(h.Item1, h.Item2, h.Item3); } catch { /* best effort */ }
            }
            _handlers.Clear();
            _conn.Dispose();   // resign (+ destroy fed exec if last out)
            _conn = null;
        }

        // ---- internals -------------------------------------------------------------

        private void Add(string messageType, string fomClass, Action<Message> body)
        {
            MessageDelegate del = m => body(m);
            _conn.addMessageHandler(messageType, del, fomClass);
            _handlers.Add(Tuple.Create(messageType, del, fomClass));
        }

        private static GenericSample ToSample(Message m)
        {
            var s = new GenericSample();

            var st = m as UniversalHlaObjStateMessage;
            if (st != null)
            {
                s.ObjClass = st.objClass; s.ObjName = st.objName;
                UniversalAttributeReader.TryReadAll(st, s.Attributes);
                return s;
            }

            var disc = m as UniversalHlaObjDiscoveryMessage;
            if (disc != null) { s.ObjClass = disc.objClass; s.ObjName = disc.objName; return s; }

            var rem = m as UniversalHlaObjRemovedMessage;
            if (rem != null) { s.ObjClass = rem.objClass; s.ObjName = rem.objName; return s; }

            var inter = m as UniversalHlaInteractionMessage;
            if (inter != null)
            {
                s.ObjClass = inter.objClass;
                UniversalAttributeReader.TryReadAll(inter, s.Attributes);
                return s;
            }

            s.ObjClass = m == null ? "(null)" : m.name;
            return s;
        }

        private static void Raise(Action<GenericSample> evt, GenericSample s)
        {
            var h = evt;
            if (h != null) h(s);
        }

        private void RequireConnected()
        {
            if (_conn == null) throw new InvalidOperationException("Not connected: " + Config);
        }
    }
}
