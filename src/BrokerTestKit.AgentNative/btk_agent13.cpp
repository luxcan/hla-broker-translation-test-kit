// btk_agent13.cpp - BrokerTestKit native agent for HLA 1.3 (raw MAK RTI, no VR-Link).
//
// WHY THIS EXISTS (REQ-08)
//   The C# / VR-Link agent cannot publish CUSTOM (unmodelled) attributes: the C# binding
//   exposes no FOM Mapper or encoder, so SETATTR succeeded locally but nothing ever reached
//   the wire. Raw HLA 1.3 has no such gap - AttributeHandleValuePairSet::add() takes a plain
//   (handle, buffer, length) triple, so we place EXACT BYTES on the wire and read EXACT BYTES
//   back. That is precisely what a broker regression test needs.
//
// WHY IT IS FOM-AGNOSTIC (REQ-09)
//   Nothing about any FOM is compiled in. Classes and attributes arrive as STRINGS over the
//   pipe and are resolved at runtime with getObjectClassHandle / getAttributeHandle. Renaming
//   an attribute in the .fed needs a new CSV, never a rebuild.
//
// SIDE BENEFITS
//   * Uses the makrtiLib licence pool (14 seats) instead of vrl_run (2). See REQ-07.
//   * No dead reckoning / thresholding between us and the wire, so a byte we send is the byte
//     the broker sees. VR-Link would have been free to smooth, extrapolate or suppress it.
//
// IPC - identical shape to the managed agent, so AgentHost can spawn either.
//   in : JOIN | CLASS | REGISTER | SETATTR | UPDATE | DELETE | SNAPSHOT | WAIT | PING | RESIGN | QUIT
//        INTCLASS | SETPARAM | SENDINT                                   (interactions)
//   out: OK <verb> k=v ... | DISC ... | ATTR ... | GONE ... | ERR <message>
//        INT ... | PARAM ...                                             (interactions)
//
// THREADING
//   All RTI calls stay on the main thread. stdin is read by a worker into a queue; RTI
//   callbacks fire inside tick() on the main thread and only touch our own maps + stdout.

#include <string>
#include <vector>
#include <map>
#include <deque>
#include <mutex>
#include <thread>
#include <chrono>
#include <iostream>
#include <sstream>
#include <cstring>
#include <cstdlib>

#include "btk_wire.h"

// RTI.hh must follow the standard headers: it defines NULL and pulls in <fstream>.
#include <NullFederateAmbassador.hh>

using btk::Bytes;
using btk::Kv;

// ------------------------------------------------------------------ state ----

// Constructed LAZILY, never as a global. MAK's RTIambassador connects to the RTI inside its
// CONSTRUCTOR, so a global instance would dial out during static init - before main() has had a
// chance to point RTI_RID_FILE at our rid.mtl, and before --version could answer without a live
// RTI. Same class of bug as the managed side's RID problem (docs/vr-link-knowledge.md).
static RTI::RTIambassador*  g_rti = 0;

static RTI::RTIambassador& rti()
{
    if (!g_rti) g_rti = new RTI::RTIambassador();
    return *g_rti;
}
static bool                 g_joined  = false;
static volatile bool        g_running = true;
static std::string          g_exec;

// Runtime name<->handle caches. This is the whole of our "FOM knowledge": nothing else.
static std::map<std::string, RTI::ObjectClassHandle> g_classH;   // "BaseEntity"      -> handle
static std::map<RTI::ObjectClassHandle, std::string> g_classN;   // handle            -> "BaseEntity"
static std::map<std::string, RTI::AttributeHandle>   g_attrH;    // "BaseEntity::Foo" -> handle
static std::map<RTI::AttributeHandle, std::string>   g_attrN;    // handle            -> "Foo"

/// An object instance we registered and own the attributes of.
struct Owned {
    RTI::ObjectHandle handle;
    std::string       className;
    std::string       instName;
    std::map<RTI::AttributeHandle, Bytes> pending;   // staged by SETATTR, flushed by UPDATE
    // Everything ever sent for this object, MERGED across updates. A real CGF sends the
    // static attributes once and then only what moves, so `pending` holds just the last
    // delta - it is not the object's state. Answering a provide request from `pending`
    // would return WorldLocation alone and silently omit EntityType, which is exactly the
    // bug a late-join test exists to detect.
    std::map<RTI::AttributeHandle, Bytes> lastSent;
    Owned() : handle(0) {}
};

/// An object instance somebody else registered that we discovered.
struct Seen {
    RTI::ObjectHandle handle;
    std::string       className;
    std::string       instName;
    std::map<RTI::AttributeHandle, Bytes> attrs;
    Seen() : handle(0) {}
};

static std::map<std::string, Owned>            g_owned;   // local id -> owned instance
static std::map<RTI::ObjectHandle, Seen>       g_seen;    // discovered instances

/// provideAttributeValueUpdate: the RTI asking us, the owner, to re-send current values.
/// This is how a late joiner recovers attributes it was not present for, and how the
/// federation-wide HLAautoProvide switch does its work.
///
/// The callback CANNOT answer in place: it runs inside tick(), and calling
/// updateAttributeValues from there is re-entrant - HLA 1.3 declares
/// ConcurrentAccessAttempted on exactly that path (RTIambServices.hh:346). So the request
/// is queued here and serviced after tick() returns.
///
/// No mutex: the callback fires on whichever thread called tick(), and both tick sites
/// (main and cmdWait) are the main thread. Only g_cmds is touched by the reader thread.
static bool g_provide = true;                  // --no-provide models a CGF that ignores requests

struct ProvideReq {
    RTI::ObjectHandle                  obj;
    std::vector<RTI::AttributeHandle>  attrs;
};
static std::deque<ProvideReq> g_provideQ;

// ---- interactions (REQ-12) -------------------------------------------------
// Interactions carry no instance identity: they are fire-and-forget events, so there is nothing
// to register and nothing to discover. Parameters stage exactly like attributes, and SENDINT is
// the moment they hit the wire - keeping the same stage-then-flush shape as SETATTR/UPDATE.
static std::map<std::string, RTI::InteractionClassHandle> g_intH;     // label -> handle
static std::map<RTI::InteractionClassHandle, std::string> g_intN;     // handle -> label
static std::map<std::string, RTI::ParameterHandle>        g_paramH;   // "Label::Param" -> handle
static std::map<RTI::ParameterHandle, std::string>        g_paramN;   // handle -> param name

/// Staged parameter values, per interaction label, flushed by SENDINT.
static std::map<std::string, std::map<RTI::ParameterHandle, Bytes> > g_pendingInt;

static std::deque<std::string> g_cmds;
static std::mutex              g_cmdLock;

// ---------------------------------------------------------------- helpers ----

static std::string U(unsigned long long v)
{
    std::ostringstream os;
    os << v;
    return os.str();
}

static std::string exText(RTI::Exception& e)
{
    std::string n = e._name   ? e._name   : "RTIexception";
    std::string r = e._reason ? e._reason : "";
    return r.empty() ? n : (n + ": " + r);
}

static std::string classNameOf(RTI::ObjectClassHandle h)
{
    std::map<RTI::ObjectClassHandle, std::string>::const_iterator it = g_classN.find(h);
    return it != g_classN.end() ? it->second : ("#" + U(h));
}

/// Attribute names come from what we resolved at CLASS time. An unknown handle means the RTI
/// sent us an attribute we never subscribed to - report the raw handle rather than guessing.
static std::string attrNameOf(RTI::AttributeHandle h)
{
    std::map<RTI::AttributeHandle, std::string>::const_iterator it = g_attrN.find(h);
    return it != g_attrN.end() ? it->second : ("#" + U(h));
}

static void requireJoined()
{
    if (!g_joined) throw std::runtime_error("not joined - send JOIN first");
}

/// Resolve (and cache) an attribute handle by name against a class. Throws NameNotFound if the
/// FED has no such attribute, which is exactly the signal a mis-specified custom FOM should give.
static RTI::AttributeHandle attrHandle(const std::string& className, const std::string& attr)
{
    std::string key = className + "::" + attr;
    std::map<std::string, RTI::AttributeHandle>::const_iterator it = g_attrH.find(key);
    if (it != g_attrH.end()) return it->second;

    std::map<std::string, RTI::ObjectClassHandle>::const_iterator c = g_classH.find(className);
    if (c == g_classH.end()) throw std::runtime_error("unknown class '" + className + "' - send CLASS first");

    RTI::AttributeHandle ah = rti().getAttributeHandle(attr.c_str(), c->second);
    g_attrH[key] = ah;
    g_attrN[ah]  = attr;
    return ah;
}

static std::string intNameOf(RTI::InteractionClassHandle h)
{
    std::map<RTI::InteractionClassHandle, std::string>::const_iterator it = g_intN.find(h);
    return it != g_intN.end() ? it->second : ("#" + U(h));
}

static std::string paramNameOf(RTI::ParameterHandle h)
{
    std::map<RTI::ParameterHandle, std::string>::const_iterator it = g_paramN.find(h);
    return it != g_paramN.end() ? it->second : ("#" + U(h));
}

// -------------------------------------------------------- federate ambassador --

static void storeReflect(RTI::ObjectHandle obj, const RTI::AttributeHandleValuePairSet& av)
{
    Seen& s = g_seen[obj];
    if (s.handle == 0) { s.handle = obj; s.className = "?"; }   // reflect before discover

    for (RTI::ULong i = 0; i < av.size(); ++i) {
        RTI::AttributeHandle h = av.getHandle(i);
        RTI::ULong len = 0;
        char* p = av.getValuePointer(i, len);

        Bytes b(len);
        if (len && p) std::memcpy(&b[0], p, len);
        s.attrs[h] = b;

        // Raw bytes straight out to the parent. No interpretation, no FOM.
        btk::say("ATTR obj=" + U(obj) +
                 " class=" + s.className +
                 " name="  + attrNameOf(h) +
                 " len="   + U(len) +
                 " hex="   + btk::toHex(b));
    }
}

static void storeInteraction(RTI::InteractionClassHandle cls,
                             const RTI::ParameterHandleValuePairSet& ps)
{
    const std::string name = intNameOf(cls);
    btk::say("INT class=" + name + " params=" + U(ps.size()));

    for (RTI::ULong i = 0; i < ps.size(); ++i) {
        RTI::ULong len = 0;
        char* p = ps.getValuePointer(i, len);

        Bytes b(len);
        if (len && p) std::memcpy(&b[0], p, len);

        btk::say("PARAM class=" + name +
                 " name=" + paramNameOf(ps.getHandle(i)) +
                 " len="  + U(len) +
                 " hex="  + btk::toHex(b));
    }
}

static void gone(RTI::ObjectHandle obj)
{
    g_seen.erase(obj);
    btk::say("GONE obj=" + U(obj));
}

class BtkAmbassador : public NullFederateAmbassador
{
public:
    void discoverObjectInstance(RTI::ObjectHandle      theObject,
                                RTI::ObjectClassHandle theObjectClass,
                                const char*            theObjectName)
        throw (RTI::CouldNotDiscover, RTI::ObjectClassNotKnown, RTI::FederateInternalError)
    {
        Seen s;
        s.handle    = theObject;
        s.className = classNameOf(theObjectClass);
        s.instName  = theObjectName ? theObjectName : "";
        g_seen[theObject] = s;

        btk::say("DISC obj=" + U(theObject) + " class=" + s.className + " name=" + s.instName);
    }

    /// Queue only - see the note on g_provideQ for why this must not answer in place.
    void provideAttributeValueUpdate(RTI::ObjectHandle              theObject,
                                     const RTI::AttributeHandleSet& theAttributes)
        throw (RTI::ObjectNotKnown, RTI::AttributeNotKnown,
               RTI::AttributeNotOwned, RTI::FederateInternalError)
    {
        ProvideReq r;
        r.obj = theObject;
        for (RTI::ULong i = 0; i < theAttributes.size(); ++i)
            r.attrs.push_back(theAttributes.getHandle(i));

        btk::say("PROVIDEREQ obj=" + U(theObject) + " attrs=" + U((unsigned long long)r.attrs.size()) +
                 (g_provide ? "" : " (ignored: --no-provide)"));

        if (g_provide) g_provideQ.push_back(r);
    }

    void reflectAttributeValues(RTI::ObjectHandle                 theObject,
                                const RTI::AttributeHandleValuePairSet& theAttributes,
                                const char*                       /*theTag*/)
        throw (RTI::ObjectNotKnown, RTI::AttributeNotKnown,
               RTI::FederateOwnsAttributes, RTI::FederateInternalError)
    {
        storeReflect(theObject, theAttributes);
    }

    void reflectAttributeValues(RTI::ObjectHandle                 theObject,
                                const RTI::AttributeHandleValuePairSet& theAttributes,
                                const RTI::FedTime&               /*theTime*/,
                                const char*                       /*theTag*/,
                                RTI::EventRetractionHandle        /*theHandle*/)
        throw (RTI::ObjectNotKnown, RTI::AttributeNotKnown, RTI::FederateOwnsAttributes,
               RTI::InvalidFederationTime, RTI::FederateInternalError)
    {
        storeReflect(theObject, theAttributes);
    }

    void receiveInteraction(RTI::InteractionClassHandle             theInteraction,
                            const RTI::ParameterHandleValuePairSet&  theParameters,
                            const char*                              /*theTag*/)
        throw (RTI::InteractionClassNotKnown, RTI::InteractionParameterNotKnown,
               RTI::FederateInternalError)
    {
        storeInteraction(theInteraction, theParameters);
    }

    void receiveInteraction(RTI::InteractionClassHandle             theInteraction,
                            const RTI::ParameterHandleValuePairSet&  theParameters,
                            const RTI::FedTime&                      /*theTime*/,
                            const char*                              /*theTag*/,
                            RTI::EventRetractionHandle               /*theHandle*/)
        throw (RTI::InteractionClassNotKnown, RTI::InteractionParameterNotKnown,
               RTI::InvalidFederationTime, RTI::FederateInternalError)
    {
        storeInteraction(theInteraction, theParameters);
    }

    void removeObjectInstance(RTI::ObjectHandle theObject, const char* /*theTag*/)
        throw (RTI::ObjectNotKnown, RTI::FederateInternalError)
    {
        gone(theObject);
    }

    void removeObjectInstance(RTI::ObjectHandle          theObject,
                              const RTI::FedTime&        /*theTime*/,
                              const char*                /*theTag*/,
                              RTI::EventRetractionHandle /*theHandle*/)
        throw (RTI::ObjectNotKnown, RTI::InvalidFederationTime, RTI::FederateInternalError)
    {
        gone(theObject);
    }
};

static BtkAmbassador g_amb;

// --------------------------------------------------------------- commands ----

static void cmdJoin(const Kv& kv)
{
    g_exec              = btk::get(kv, "exec", "BTK-FedA");
    std::string fed     = btk::get(kv, "fed");
    std::string fedName = btk::get(kv, "federate", "BTK-Agent13");
    bool create         = btk::get(kv, "create", "1") == "1";

    if (create) {
        if (fed.empty()) { btk::errLine("JOIN create=1 needs fed=<.fed file>"); return; }
        try {
            rti().createFederationExecution(g_exec.c_str(), fed.c_str());
            btk::say("OK CREATE exec=" + g_exec + " fed=" + fed);
        }
        catch (RTI::FederationExecutionAlreadyExists&) {
            btk::say("OK CREATE exec=" + g_exec + " existing=1");
        }
    }

    RTI::FederateHandle fh = rti().joinFederationExecution(fedName.c_str(), g_exec.c_str(), &g_amb);
    g_joined = true;
    btk::say("OK JOIN exec=" + g_exec + " federate=" + fedName + " handle=" + U(fh));
}

/// CLASS name=BaseEntity attrs=WorldLocation,BtkCustomValue mode=pubsub
/// The single FOM-agnostic entry point: names in, handles cached, publish/subscribe done.
static void cmdClass(const Kv& kv)
{
    requireJoined();
    std::string name  = btk::get(kv, "name");
    std::string attrs = btk::get(kv, "attrs");
    std::string mode  = btk::lower(btk::get(kv, "mode", "pubsub"));
    // Short alias used by REGISTER/SETATTR, so a script need not repeat the whole
    // ObjectRoot.BaseEntity.PhysicalEntity.Platform.GroundVehicle path every line.
    // INTCLASS below and BOTH commands in the Evolved agent already did this; object
    // CLASS here did not, which made "as=" silently do nothing on HLA 1.3.
    std::string label = btk::get(kv, "as", name);
    if (name.empty()) { btk::errLine("CLASS needs name="); return; }

    RTI::ObjectClassHandle ch = rti().getObjectClassHandle(name.c_str());
    g_classH[label] = ch;
    g_classN[ch]    = label;

    std::vector<std::string> list = btk::splitList(attrs);
    RTI::AttributeHandleSet* set = RTI::AttributeHandleSetFactory::create((RTI::ULong)list.size());

    try {
        for (size_t i = 0; i < list.size(); ++i) {
            RTI::AttributeHandle ah;
            try {
                ah = rti().getAttributeHandle(list[i].c_str(), ch);
            }
            catch (RTI::NameNotFound&) {
                // The most useful diagnostic this tool can give: the FED does not define it.
                delete set;
                btk::errLine("attribute '" + list[i] + "' is not defined on class '" + name +
                             "' in this FED - check the FOM");
                return;
            }
            g_attrH[label + "::" + list[i]] = ah;
            g_attrN[ah] = list[i];
            set->add(ah);
        }

        bool pub = (mode == "pub" || mode == "pubsub");
        bool sub = (mode == "sub" || mode == "pubsub");
        if (pub && !list.empty()) rti().publishObjectClass(ch, *set);
        if (sub && !list.empty()) rti().subscribeObjectClassAttributes(ch, *set);
    }
    catch (...) { delete set; throw; }

    delete set;
    btk::say("OK CLASS name=" + label + " handle=" + U(ch) +
             " attrs=" + U(list.size()) + " mode=" + mode);
}

static void cmdRegister(const Kv& kv)
{
    requireJoined();
    std::string id   = btk::get(kv, "id");
    std::string cls  = btk::get(kv, "class");
    std::string inst = btk::get(kv, "name", id);
    if (id.empty() || cls.empty()) { btk::errLine("REGISTER needs id= and class="); return; }

    std::map<std::string, RTI::ObjectClassHandle>::const_iterator c = g_classH.find(cls);
    if (c == g_classH.end()) { btk::errLine("unknown class '" + cls + "' - send CLASS first"); return; }

    Owned o;
    o.className = cls;
    o.instName  = inst;
    o.handle    = inst.empty() ? rti().registerObjectInstance(c->second)
                               : rti().registerObjectInstance(c->second, inst.c_str());
    g_owned[id] = o;

    btk::say("OK REGISTER id=" + id + " handle=" + U(o.handle) + " name=" + inst + " class=" + cls);
}

/// SETATTR id=t1 name=BtkCustomValue hex=41424344
/// Values are STAGED, not sent. UPDATE flushes them as one updateAttributeValues call so a CSV
/// step lands on the far side as a single reflect - which is what the step model assumes.
static void cmdSetAttr(const Kv& kv)
{
    requireJoined();
    std::string id   = btk::get(kv, "id");
    std::string attr = btk::get(kv, "name");
    if (id.empty() || attr.empty()) { btk::errLine("SETATTR needs id= and name="); return; }

    std::map<std::string, Owned>::iterator it = g_owned.find(id);
    if (it == g_owned.end()) { btk::errLine("unknown id '" + id + "' - send REGISTER first"); return; }

    Bytes v;
    std::string why;
    if (!btk::decodeValue(kv, v, why)) { btk::errLine("SETATTR " + why); return; }

    RTI::AttributeHandle ah = attrHandle(it->second.className, attr);
    it->second.pending[ah] = v;

    btk::say("OK SETATTR id=" + id + " name=" + attr + " len=" + U(v.size()) +
             " hex=" + btk::toHex(v));
}

static void cmdUpdate(const Kv& kv)
{
    requireJoined();
    std::string id  = btk::get(kv, "id");
    std::string tag = btk::get(kv, "tag", "btk");

    std::map<std::string, Owned>::iterator it = g_owned.find(id);
    if (it == g_owned.end()) { btk::errLine("unknown id '" + id + "'"); return; }

    Owned& o = it->second;
    if (o.pending.empty()) { btk::say("OK UPDATE id=" + id + " attrs=0"); return; }

    RTI::AttributeHandleValuePairSet* set =
        RTI::AttributeSetFactory::create((RTI::ULong)o.pending.size());

    try {
        for (std::map<RTI::AttributeHandle, Bytes>::const_iterator a = o.pending.begin();
             a != o.pending.end(); ++a)
        {
            const Bytes& b = a->second;
            // Empty payloads are legal HLA but &b[0] would be UB - pass a valid pointer.
            static const char kEmpty = 0;
            set->add(a->first, b.empty() ? &kEmpty : (const char*)&b[0], (RTI::ULong)b.size());
        }
        rti().updateAttributeValues(o.handle, *set, tag.c_str());
    }
    catch (...) { delete set; throw; }

    size_t n = o.pending.size();
    delete set;

    // MERGE, never assign. `pending` is only the latest delta; assigning would leave
    // lastSent holding WorldLocation alone after the first position update, and a provide
    // response would then omit EntityType entirely.
    for (std::map<RTI::AttributeHandle, Bytes>::const_iterator a = o.pending.begin();
         a != o.pending.end(); ++a)
        o.lastSent[a->first] = a->second;

    o.pending.clear();
    btk::say("OK UPDATE id=" + id + " attrs=" + U(n));
}

static void cmdDelete(const Kv& kv)
{
    requireJoined();
    std::string id = btk::get(kv, "id");
    std::map<std::string, Owned>::iterator it = g_owned.find(id);
    if (it == g_owned.end()) { btk::errLine("unknown id '" + id + "'"); return; }

    rti().deleteObjectInstance(it->second.handle, "btk");
    g_owned.erase(it);
    btk::say("OK DELETE id=" + id);
}

/// INTCLASS name=<Interaction> params=<A,B,C> mode=pub|sub|pubsub [as=<alias>]
/// HLA 1.3 publishes an interaction CLASS as a whole - unlike objects, there is no per-parameter
/// publish - so the params list here exists only to resolve and cache handles.
static void cmdIntClass(const Kv& kv)
{
    requireJoined();
    std::string name   = btk::get(kv, "name");
    std::string params = btk::get(kv, "params");
    std::string mode   = btk::lower(btk::get(kv, "mode", "pubsub"));
    std::string label  = btk::get(kv, "as", name);
    if (name.empty()) { btk::errLine("INTCLASS needs name="); return; }

    RTI::InteractionClassHandle ch = rti().getInteractionClassHandle(name.c_str());
    g_intH[label] = ch;
    g_intN[ch]    = label;

    std::vector<std::string> list = btk::splitList(params);
    for (size_t i = 0; i < list.size(); ++i) {
        RTI::ParameterHandle ph;
        try {
            ph = rti().getParameterHandle(list[i].c_str(), ch);
        }
        catch (RTI::NameNotFound&) {
            btk::errLine("parameter '" + list[i] + "' is not defined on interaction '" + name +
                         "' in this FED - check the customised FOM");
            return;
        }
        g_paramH[label + "::" + list[i]] = ph;
        g_paramN[ph] = list[i];
    }

    if (mode == "pub" || mode == "pubsub") rti().publishInteractionClass(ch);
    if (mode == "sub" || mode == "pubsub") rti().subscribeInteractionClass(ch, RTI::RTI_TRUE);

    btk::say("OK INTCLASS name=" + label + " handle=" + U(ch) +
             " params=" + U(list.size()) + " mode=" + mode);
}

static RTI::ParameterHandle paramHandle(const std::string& label, const std::string& param)
{
    std::string key = label + "::" + param;
    std::map<std::string, RTI::ParameterHandle>::const_iterator it = g_paramH.find(key);
    if (it != g_paramH.end()) return it->second;

    std::map<std::string, RTI::InteractionClassHandle>::const_iterator c = g_intH.find(label);
    if (c == g_intH.end()) throw std::runtime_error("unknown interaction '" + label + "' - send INTCLASS first");

    RTI::ParameterHandle ph = rti().getParameterHandle(param.c_str(), c->second);
    g_paramH[key] = ph;
    g_paramN[ph]  = param;
    return ph;
}

/// SETPARAM class=<Interaction> name=<Param> hex=... | f64= | str= ...
static void cmdSetParam(const Kv& kv)
{
    requireJoined();
    std::string label = btk::get(kv, "class");
    std::string param = btk::get(kv, "name");
    if (label.empty() || param.empty()) { btk::errLine("SETPARAM needs class= and name="); return; }

    Bytes v;
    std::string why;
    if (!btk::decodeValue(kv, v, why)) { btk::errLine("SETPARAM " + why); return; }

    g_pendingInt[label][paramHandle(label, param)] = v;
    btk::say("OK SETPARAM class=" + label + " name=" + param + " len=" + U(v.size()) +
             " hex=" + btk::toHex(v));
}

/// SENDINT class=<Interaction> [tag=<t>] - flush the staged parameters as ONE interaction.
static void cmdSendInt(const Kv& kv)
{
    requireJoined();
    std::string label = btk::get(kv, "class");
    std::string tag   = btk::get(kv, "tag", "btk");

    std::map<std::string, RTI::InteractionClassHandle>::const_iterator c = g_intH.find(label);
    if (c == g_intH.end()) { btk::errLine("unknown interaction '" + label + "' - send INTCLASS first"); return; }

    std::map<RTI::ParameterHandle, Bytes>& pending = g_pendingInt[label];
    RTI::ParameterHandleValuePairSet* set =
        RTI::ParameterSetFactory::create((RTI::ULong)pending.size());

    try {
        for (std::map<RTI::ParameterHandle, Bytes>::const_iterator a = pending.begin();
             a != pending.end(); ++a)
        {
            const Bytes& b = a->second;
            static const char kEmpty = 0;
            set->add(a->first, b.empty() ? &kEmpty : (const char*)&b[0], (RTI::ULong)b.size());
        }
        rti().sendInteraction(c->second, *set, tag.c_str());
    }
    catch (...) { delete set; throw; }

    size_t n = pending.size();
    delete set;
    pending.clear();
    btk::say("OK SENDINT class=" + label + " params=" + U(n));
}

static void cmdSnapshot()
{
    for (std::map<RTI::ObjectHandle, Seen>::const_iterator s = g_seen.begin(); s != g_seen.end(); ++s) {
        btk::say("DISC obj=" + U(s->first) + " class=" + s->second.className + " name=" + s->second.instName);
        for (std::map<RTI::AttributeHandle, Bytes>::const_iterator a = s->second.attrs.begin();
             a != s->second.attrs.end(); ++a)
        {
            btk::say("ATTR obj=" + U(s->first) +
                     " class=" + s->second.className +
                     " name="  + attrNameOf(a->first) +
                     " len="   + U(a->second.size()) +
                     " hex="   + btk::toHex(a->second));
        }
    }
    btk::say("OK SNAPSHOT objects=" + U(g_seen.size()));
}

/// Answer any queued provideAttributeValueUpdate requests. Safe to call only OUTSIDE
/// tick(). Re-sends the merged lastSent state for the attributes asked for.
static void servicePendingProvides()
{
    while (!g_provideQ.empty()) {
        ProvideReq r = g_provideQ.front();
        g_provideQ.pop_front();

        Owned* o = 0;
        for (std::map<std::string, Owned>::iterator it = g_owned.begin(); it != g_owned.end(); ++it)
            if (it->second.handle == r.obj) { o = &it->second; break; }
        if (!o) { btk::note("provide: we do not own obj " + U(r.obj)); continue; }

        std::map<RTI::AttributeHandle, Bytes> send;
        for (size_t i = 0; i < r.attrs.size(); ++i) {
            std::map<RTI::AttributeHandle, Bytes>::const_iterator f = o->lastSent.find(r.attrs[i]);
            if (f != o->lastSent.end()) send[f->first] = f->second;
        }
        if (send.empty()) { btk::note("provide: nothing sent yet for obj " + U(r.obj)); continue; }

        RTI::AttributeHandleValuePairSet* set =
            RTI::AttributeSetFactory::create((RTI::ULong)send.size());
        try {
            for (std::map<RTI::AttributeHandle, Bytes>::const_iterator a = send.begin();
                 a != send.end(); ++a)
            {
                static const char kEmpty = 0;
                set->add(a->first, a->second.empty() ? &kEmpty : (const char*)&a->second[0],
                         (RTI::ULong)a->second.size());
            }
            rti().updateAttributeValues(o->handle, *set, "btk-provide");
        }
        catch (RTI::Exception& e) { delete set; btk::errLine("provide: " + exText(e)); continue; }
        delete set;

        btk::say("OK PROVIDE obj=" + U(r.obj) + " attrs=" + U((unsigned long long)send.size()));
    }
}

/// WAIT ms=2000 - tick for a while. Makes scripted smoke tests possible without a parent
/// process: `type script.txt | btk-agent13.exe`.
static void cmdWait(const Kv& kv)
{
    long ms = std::atol(btk::get(kv, "ms", "1000").c_str());
    std::chrono::steady_clock::time_point end =
        std::chrono::steady_clock::now() + std::chrono::milliseconds(ms);

    while (std::chrono::steady_clock::now() < end) {
        // Servicing here is NOT optional. A script spends nearly all its wall time inside
        // WAIT, and main()'s service point is not reached until the WAIT returns - so a
        // request arriving mid-wait would sit unanswered for the whole interval, or for
        // ever if it lands in the last one before RESIGN.
        if (g_joined) {
            try { rti().tick(0.01, 0.05); } catch (RTI::Exception&) {}
            servicePendingProvides();
        }
        else std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    btk::say("OK WAIT ms=" + U((unsigned long long)ms));
}

static void cmdResign()
{
    if (!g_joined) { btk::say("OK RESIGN joined=0"); return; }
    try {
        rti().resignFederationExecution(RTI::DELETE_OBJECTS_AND_RELEASE_ATTRIBUTES);
    }
    catch (RTI::Exception& e) { btk::note("resign: " + exText(e)); }
    g_joined = false;
    btk::say("OK RESIGN exec=" + g_exec);
}

static void handle(const std::string& line)
{
    std::string verb = btk::verbOf(line);
    if (verb.empty()) return;
    Kv kv = btk::parseKv(line);

    try {
        if      (verb == "JOIN")     cmdJoin(kv);
        else if (verb == "CLASS")    cmdClass(kv);
        else if (verb == "REGISTER") cmdRegister(kv);
        else if (verb == "SETATTR")  cmdSetAttr(kv);
        else if (verb == "UPDATE")   cmdUpdate(kv);
        else if (verb == "DELETE")   cmdDelete(kv);
        else if (verb == "INTCLASS") cmdIntClass(kv);
        else if (verb == "SETPARAM") cmdSetParam(kv);
        else if (verb == "SENDINT")  cmdSendInt(kv);
        else if (verb == "SNAPSHOT") cmdSnapshot();
        else if (verb == "WAIT")     cmdWait(kv);
        else if (verb == "PING")     btk::say("OK PING joined=" + std::string(g_joined ? "1" : "0"));
        else if (verb == "RESIGN")   cmdResign();
        else if (verb == "QUIT")     { g_running = false; btk::say("OK QUIT"); }
        else btk::errLine("unknown verb '" + verb + "'");
    }
    catch (RTI::Exception& e)      { btk::errLine(exText(e)); }
    catch (std::exception& e)      { btk::errLine(e.what()); }
    catch (...)                    { btk::errLine("unknown native exception"); }
}

// ------------------------------------------------------------------- main ----

static void readStdin()
{
    std::string line;
    while (std::getline(std::cin, line)) {
        std::string t = btk::trim(line);
        if (t.empty() || t[0] == '#' || t[0] == ';') continue;   // comments in script files
        {
            std::lock_guard<std::mutex> lk(g_cmdLock);
            g_cmds.push_back(t);
        }
        // Do NOT stop the loop here: the main thread must still drain commands queued
        // before QUIT. Same race the managed agent hit (see BrokerTestKit.Agent/Program.cs).
        if (btk::upper(t) == "QUIT") return;
    }
    std::lock_guard<std::mutex> lk(g_cmdLock);
    g_cmds.push_back("QUIT");   // stdin closed -> shut down cleanly
}

/// --rid / --config must be applied BEFORE the ambassador is constructed, because MAK reads
/// them once when it dials the RTI. Unlike the managed side we can just call _putenv_s: the
/// C++ runtime and the RTI share one CRT, so there is no .NET-vs-native env split to bridge.
static bool applyArgs(int argc, char** argv)
{
    for (int i = 1; i < argc; ++i) {
        std::string a = argv[i];
        // Boolean flags must be matched BEFORE the value-consuming branch below, which
        // would otherwise swallow the next argument as this flag's value.
        if (a == "--no-provide") {
            g_provide = false;
            btk::note("provideAttributeValueUpdate disabled - modelling a CGF that ignores requests");
            continue;
        }
        std::string val;
        size_t eq = a.find('=');
        if (eq != std::string::npos) { val = a.substr(eq + 1); a = a.substr(0, eq); }
        else if (i + 1 < argc && a.substr(0, 2) == "--") { val = argv[++i]; }

        if      (a == "--version") { btk::say("OK VERSION agent=btk-agent13 hla=1.3 rti=MAK-5.0.1"); return false; }
        else if (a == "--rid")     { _putenv_s("RTI_RID_FILE", val.c_str()); btk::note("RTI_RID_FILE=" + val); }
        else if (a == "--config")  { _putenv_s("RTI_CONFIG",   val.c_str()); btk::note("RTI_CONFIG=" + val); }
    }
    return true;
}

int main(int argc, char** argv)
{
    if (!applyArgs(argc, argv)) return 0;

    btk::say("OK READY agent=btk-agent13 hla=1.3");
    std::thread reader(readStdin);

    while (g_running) {
        if (g_joined) {
            try { rti().tick(0.01, 0.05); }
            catch (RTI::Exception& e) { btk::note("tick: " + exText(e)); }
            servicePendingProvides();
        }
        else {
            std::this_thread::sleep_for(std::chrono::milliseconds(20));
        }

        for (int n = 0; n < 32 && g_running; ++n) {
            std::string cmd;
            {
                std::lock_guard<std::mutex> lk(g_cmdLock);
                if (g_cmds.empty()) break;
                cmd = g_cmds.front();
                g_cmds.pop_front();
            }
            handle(cmd);
        }
    }

    if (g_joined) cmdResign();
    if (reader.joinable()) reader.detach();   // it may be parked in getline on a live pipe
    return 0;
}
