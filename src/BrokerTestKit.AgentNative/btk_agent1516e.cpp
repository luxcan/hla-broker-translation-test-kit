// btk_agent1516e.cpp - BrokerTestKit native agent for HLA Evolved / IEEE 1516-2010.
//
// The Federation B counterpart of btk_agent13.cpp. Same job, same line protocol, same
// FOM-agnostic contract - only the RTI API differs. Written as a separate binary rather
// than an #ifdef because the two APIs share almost no vocabulary:
//
//   HLA 1.3                              HLA Evolved (1516-2010)
//   -------------------------------------------------------------------------------
//   RTI::RTIambassador (concrete)        RTIambassadorFactory().createRTIambassador()
//   (join connects implicitly)           connect() is a SEPARATE, REQUIRED first step
//   char* names                          std::wstring names
//   integer handles                      handle CLASSES with toString()
//   AttributeHandleValuePairSet          AttributeHandleValueMap (std::map)
//   AttributeHandleSet (factory)         std::set<AttributeHandle>
//   tick(min, max)                       evokeMultipleCallbacks(min, max)
//   one .fed file                        FDD + 0..n FOM modules + optional MIM  <- REQ-01
//
// The last row is the interesting one: this agent takes fom= and mim= because Evolved
// genuinely supports them, which is exactly the asymmetry REQ-01 records.

#include <string>
#include <vector>
#include <map>
#include <set>
#include <deque>
#include <mutex>
#include <thread>
#include <chrono>
#include <iostream>
#include <sstream>
#include <cstring>
#include <cstdlib>

#include "btk_wire.h"

#include <RTI/RTIambassadorFactory.h>
#include <RTI/RTIambassador.h>
#include <RTI/NullFederateAmbassador.h>
#include <RTI/Handle.h>
#include <RTI/Enums.h>

using btk::Bytes;
using btk::Kv;
using btk::toW;
using btk::toN;

namespace e = rti1516e;

// ------------------------------------------------------------------ state ----

// Created lazily on JOIN. Same reasoning as the 1.3 agent: nothing should dial the RTI
// before main() has had a chance to apply --rid / --config.
static std::auto_ptr<e::RTIambassador> g_rti;
static bool          g_connected = false;
static bool          g_joined    = false;
static volatile bool g_running   = true;
static std::string   g_exec;

static std::map<std::string, e::ObjectClassHandle> g_classH;
static std::map<e::ObjectClassHandle, std::string> g_classN;
static std::map<std::string, e::AttributeHandle>   g_attrH;   // "Class::Attr" -> handle
static std::map<e::AttributeHandle, std::string>   g_attrN;

struct Owned {
    e::ObjectInstanceHandle handle;
    std::string             className;
    std::string             instName;
    std::map<e::AttributeHandle, Bytes> pending;
};

struct Seen {
    e::ObjectInstanceHandle handle;
    std::string             className;
    std::string             instName;
    std::map<e::AttributeHandle, Bytes> attrs;
};

static std::map<std::string, Owned>                  g_owned;
static std::map<e::ObjectInstanceHandle, Seen>       g_seen;

// ---- interactions (REQ-12) -------------------------------------------------
// Same shape as the 1.3 agent: no instance identity, parameters stage then flush on SENDINT.
static std::map<std::string, e::InteractionClassHandle> g_intH;
static std::map<e::InteractionClassHandle, std::string> g_intN;
static std::map<std::string, e::ParameterHandle>        g_paramH;
static std::map<e::ParameterHandle, std::string>        g_paramN;
static std::map<std::string, std::map<e::ParameterHandle, Bytes> > g_pendingInt;

static std::deque<std::string> g_cmds;
static std::mutex              g_cmdLock;

// Instance-name reservation state. HLA 1.3 lets you name an object at registration time;
// 1516-2010 makes you RESERVE the name first and wait for an asynchronous callback, or
// registerObjectInstance fails with ObjectInstanceNameNotReserved.
static int g_reserveState = 0;   // 0 = pending, 1 = succeeded, -1 = failed

// ---------------------------------------------------------------- helpers ----

static std::string U(unsigned long long v)
{
    std::ostringstream os;
    os << v;
    return os.str();
}

/// Handles are opaque classes in 1516. toString() is the only portable identity we can put
/// on the pipe - it is stable for the life of the federation, which is all the parent needs.
static std::string H(const e::ObjectInstanceHandle& h) { return toN(h.toString()); }

static std::string classNameOf(const e::ObjectClassHandle& h)
{
    std::map<e::ObjectClassHandle, std::string>::const_iterator it = g_classN.find(h);
    return it != g_classN.end() ? it->second : ("#" + toN(h.toString()));
}

static std::string attrNameOf(const e::AttributeHandle& h)
{
    std::map<e::AttributeHandle, std::string>::const_iterator it = g_attrN.find(h);
    return it != g_attrN.end() ? it->second : ("#" + toN(h.toString()));
}

static void requireJoined()
{
    if (!g_joined) throw std::runtime_error("not joined - send JOIN first");
}

static e::AttributeHandle attrHandle(const std::string& className, const std::string& attr)
{
    std::string key = className + "::" + attr;
    std::map<std::string, e::AttributeHandle>::const_iterator it = g_attrH.find(key);
    if (it != g_attrH.end()) return it->second;

    std::map<std::string, e::ObjectClassHandle>::const_iterator c = g_classH.find(className);
    if (c == g_classH.end()) throw std::runtime_error("unknown class '" + className + "' - send CLASS first");

    e::AttributeHandle ah = g_rti->getAttributeHandle(c->second, toW(attr));
    g_attrH[key] = ah;
    g_attrN[ah]  = attr;
    return ah;
}

/// The RTI resolves FDD/module paths itself, but it does not know about RTI_CONFIG for
/// 1516 the way it does for .fed files. Resolve relative names against it ourselves so a
/// test can say fom=BTK-CustomRPR2-module.xml and not care where it lives.
static std::string resolveFom(const std::string& file)
{
    if (file.empty()) return file;
    if (file.find(':') != std::string::npos || file[0] == '\\' || file[0] == '/') return file;

    const char* cfg = std::getenv("RTI_CONFIG");
    if (!cfg) return file;

    std::string candidate = std::string(cfg) + "\\" + file;
    FILE* f = std::fopen(candidate.c_str(), "rb");
    if (f) { std::fclose(f); return candidate; }
    return file;
}

static std::string intNameOf(const e::InteractionClassHandle& h)
{
    std::map<e::InteractionClassHandle, std::string>::const_iterator it = g_intN.find(h);
    return it != g_intN.end() ? it->second : ("#" + toN(h.toString()));
}

static std::string paramNameOf(const e::ParameterHandle& h)
{
    std::map<e::ParameterHandle, std::string>::const_iterator it = g_paramN.find(h);
    return it != g_paramN.end() ? it->second : ("#" + toN(h.toString()));
}

// -------------------------------------------------------- federate ambassador --

static void storeReflect(const e::ObjectInstanceHandle& obj, const e::AttributeHandleValueMap& av)
{
    Seen& s = g_seen[obj];
    if (s.className.empty()) { s.handle = obj; s.className = "?"; }

    for (e::AttributeHandleValueMap::const_iterator it = av.begin(); it != av.end(); ++it) {
        const e::VariableLengthData& v = it->second;
        size_t n = v.size();

        Bytes b(n);
        if (n && v.data()) std::memcpy(&b[0], v.data(), n);
        s.attrs[it->first] = b;

        btk::say("ATTR obj=" + H(obj) +
                 " class=" + s.className +
                 " name="  + attrNameOf(it->first) +
                 " len="   + U(n) +
                 " hex="   + btk::toHex(b));
    }
}

static void storeInteraction(const e::InteractionClassHandle& cls,
                             const e::ParameterHandleValueMap& pv)
{
    const std::string name = intNameOf(cls);
    btk::say("INT class=" + name + " params=" + U(pv.size()));

    for (e::ParameterHandleValueMap::const_iterator it = pv.begin(); it != pv.end(); ++it) {
        const e::VariableLengthData& v = it->second;
        size_t n = v.size();

        Bytes b(n);
        if (n && v.data()) std::memcpy(&b[0], v.data(), n);

        btk::say("PARAM class=" + name +
                 " name=" + paramNameOf(it->first) +
                 " len="  + U(n) +
                 " hex="  + btk::toHex(b));
    }
}

static void gone(const e::ObjectInstanceHandle& obj)
{
    g_seen.erase(obj);
    btk::say("GONE obj=" + H(obj));
}

class BtkAmbassador : public e::NullFederateAmbassador
{
public:
    void objectInstanceNameReservationSucceeded(std::wstring const& /*name*/)
        throw (e::FederateInternalError) { g_reserveState = 1; }

    void objectInstanceNameReservationFailed(std::wstring const& /*name*/)
        throw (e::FederateInternalError) { g_reserveState = -1; }

    void discoverObjectInstance(e::ObjectInstanceHandle theObject,
                                e::ObjectClassHandle    theObjectClass,
                                std::wstring const&     theObjectInstanceName)
        throw (e::FederateInternalError)
    {
        Seen s;
        s.handle    = theObject;
        s.className = classNameOf(theObjectClass);
        s.instName  = toN(theObjectInstanceName);
        g_seen[theObject] = s;

        btk::say("DISC obj=" + H(theObject) + " class=" + s.className + " name=" + s.instName);
    }

    void discoverObjectInstance(e::ObjectInstanceHandle theObject,
                                e::ObjectClassHandle    theObjectClass,
                                std::wstring const&     theObjectInstanceName,
                                e::FederateHandle       /*producingFederate*/)
        throw (e::FederateInternalError)
    {
        discoverObjectInstance(theObject, theObjectClass, theObjectInstanceName);
    }

    // Receive-order variant - the one that fires with no time management.
    void reflectAttributeValues(e::ObjectInstanceHandle          theObject,
                                e::AttributeHandleValueMap const& theAttributeValues,
                                e::VariableLengthData const&      /*tag*/,
                                e::OrderType                      /*sentOrder*/,
                                e::TransportationType             /*theType*/,
                                e::SupplementalReflectInfo        /*info*/)
        throw (e::FederateInternalError)
    {
        storeReflect(theObject, theAttributeValues);
    }

    void reflectAttributeValues(e::ObjectInstanceHandle          theObject,
                                e::AttributeHandleValueMap const& theAttributeValues,
                                e::VariableLengthData const&      /*tag*/,
                                e::OrderType                      /*sentOrder*/,
                                e::TransportationType             /*theType*/,
                                e::LogicalTime const&             /*theTime*/,
                                e::OrderType                      /*receivedOrder*/,
                                e::SupplementalReflectInfo        /*info*/)
        throw (e::FederateInternalError)
    {
        storeReflect(theObject, theAttributeValues);
    }

    void reflectAttributeValues(e::ObjectInstanceHandle          theObject,
                                e::AttributeHandleValueMap const& theAttributeValues,
                                e::VariableLengthData const&      /*tag*/,
                                e::OrderType                      /*sentOrder*/,
                                e::TransportationType             /*theType*/,
                                e::LogicalTime const&             /*theTime*/,
                                e::OrderType                      /*receivedOrder*/,
                                e::MessageRetractionHandle        /*theHandle*/,
                                e::SupplementalReflectInfo        /*info*/)
        throw (e::FederateInternalError)
    {
        storeReflect(theObject, theAttributeValues);
    }

    void receiveInteraction(e::InteractionClassHandle        theInteraction,
                            e::ParameterHandleValueMap const& theParameterValues,
                            e::VariableLengthData const&      /*tag*/,
                            e::OrderType                      /*sentOrder*/,
                            e::TransportationType             /*theType*/,
                            e::SupplementalReceiveInfo        /*info*/)
        throw (e::FederateInternalError)
    {
        storeInteraction(theInteraction, theParameterValues);
    }

    void receiveInteraction(e::InteractionClassHandle        theInteraction,
                            e::ParameterHandleValueMap const& theParameterValues,
                            e::VariableLengthData const&      /*tag*/,
                            e::OrderType                      /*sentOrder*/,
                            e::TransportationType             /*theType*/,
                            e::LogicalTime const&             /*theTime*/,
                            e::OrderType                      /*receivedOrder*/,
                            e::SupplementalReceiveInfo        /*info*/)
        throw (e::FederateInternalError)
    {
        storeInteraction(theInteraction, theParameterValues);
    }

    void receiveInteraction(e::InteractionClassHandle        theInteraction,
                            e::ParameterHandleValueMap const& theParameterValues,
                            e::VariableLengthData const&      /*tag*/,
                            e::OrderType                      /*sentOrder*/,
                            e::TransportationType             /*theType*/,
                            e::LogicalTime const&             /*theTime*/,
                            e::OrderType                      /*receivedOrder*/,
                            e::MessageRetractionHandle        /*theHandle*/,
                            e::SupplementalReceiveInfo        /*info*/)
        throw (e::FederateInternalError)
    {
        storeInteraction(theInteraction, theParameterValues);
    }

    void removeObjectInstance(e::ObjectInstanceHandle      theObject,
                              e::VariableLengthData const& /*tag*/,
                              e::OrderType                 /*sentOrder*/,
                              e::SupplementalRemoveInfo    /*info*/)
        throw (e::FederateInternalError)
    {
        gone(theObject);
    }

    void removeObjectInstance(e::ObjectInstanceHandle      theObject,
                              e::VariableLengthData const& /*tag*/,
                              e::OrderType                 /*sentOrder*/,
                              e::LogicalTime const&        /*theTime*/,
                              e::OrderType                 /*receivedOrder*/,
                              e::SupplementalRemoveInfo    /*info*/)
        throw (e::FederateInternalError)
    {
        gone(theObject);
    }
};

static BtkAmbassador g_amb;

// --------------------------------------------------------------- commands ----

static void pump();

/// JOIN exec=<name> fed=<FDD.xml> [fom=<a.xml,b.xml>] [mim=<mim.xml>]
///      federate=<name> [type=<federateType>] [create=1]
static void cmdJoin(const Kv& kv)
{
    g_exec              = btk::get(kv, "exec", "BTK-FedB");
    std::string fed     = btk::get(kv, "fed");
    std::string mim     = btk::get(kv, "mim");
    std::string fedName = btk::get(kv, "federate", "BTK-Agent1516e");
    std::string fedType = btk::get(kv, "type", fedName);
    bool create         = btk::get(kv, "create", "1") == "1";

    if (!g_rti.get()) {
        e::RTIambassadorFactory factory;
        g_rti = factory.createRTIambassador();
    }

    // connect() has no HLA 1.3 equivalent - it is a distinct, mandatory step in Evolved.
    if (!g_connected) {
        g_rti->connect(g_amb, e::HLA_EVOKED);
        g_connected = true;
        btk::say("OK CONNECT model=HLA_EVOKED");
    }

    // REQ-01: Evolved takes an FDD plus 0..n additional FOM modules (and optionally a MIM).
    std::vector<std::wstring> modules;
    if (!fed.empty()) modules.push_back(toW(resolveFom(fed)));

    std::vector<std::string> extra = btk::splitList(btk::get(kv, "fom"));
    for (size_t i = 0; i < extra.size(); ++i) modules.push_back(toW(resolveFom(extra[i])));

    if (create) {
        if (modules.empty()) { btk::errLine("JOIN create=1 needs fed=<FDD .xml>"); return; }
        try {
            if (mim.empty())
                g_rti->createFederationExecution(toW(g_exec), modules);
            else
                g_rti->createFederationExecutionWithMIM(toW(g_exec), modules, toW(resolveFom(mim)));
            btk::say("OK CREATE exec=" + g_exec + " modules=" + U(modules.size()) +
                     (mim.empty() ? "" : " mim=" + mim));
        }
        catch (e::FederationExecutionAlreadyExists&) {
            btk::say("OK CREATE exec=" + g_exec + " existing=1");
        }
    }

    // 1516-2010 also allows a federate to bring FOM modules at JOIN time, independently of
    // whoever created the federation. Exposed separately from fom= because the two are
    // genuinely different services with different merge behaviour.
    std::vector<std::wstring> joinModules;
    std::vector<std::string> jm = btk::splitList(btk::get(kv, "joinfom"));
    for (size_t i = 0; i < jm.size(); ++i) joinModules.push_back(toW(resolveFom(jm[i])));

    e::FederateHandle fh = g_rti->joinFederationExecution(toW(fedName), toW(fedType),
                                                          toW(g_exec), joinModules);
    g_joined = true;
    btk::say("OK JOIN exec=" + g_exec + " federate=" + fedName +
             " handle=" + toN(fh.toString()));
}

/// CLASS name=<QualifiedName> attrs=<A,B,C> mode=pub|sub|pubsub
/// In 1516 the class name is normally fully qualified, e.g.
///   HLAobjectRoot.BaseEntity.PhysicalEntity.Platform.GroundVehicle
static void cmdClass(const Kv& kv)
{
    requireJoined();
    std::string name  = btk::get(kv, "name");
    std::string attrs = btk::get(kv, "attrs");
    std::string mode  = btk::lower(btk::get(kv, "mode", "pubsub"));
    std::string label = btk::get(kv, "as", name);      // short alias used by REGISTER/SETATTR
    if (name.empty()) { btk::errLine("CLASS needs name="); return; }

    e::ObjectClassHandle ch = g_rti->getObjectClassHandle(toW(name));
    g_classH[label] = ch;
    g_classN[ch]    = label;

    std::vector<std::string> list = btk::splitList(attrs);
    e::AttributeHandleSet set;

    for (size_t i = 0; i < list.size(); ++i) {
        e::AttributeHandle ah;
        try {
            ah = g_rti->getAttributeHandle(ch, toW(list[i]));
        }
        catch (e::NameNotFound&) {
            btk::errLine("attribute '" + list[i] + "' is not defined on class '" + name +
                         "' in this FDD/module set - check the customised FOM");
            return;
        }
        g_attrH[label + "::" + list[i]] = ah;
        g_attrN[ah] = list[i];
        set.insert(ah);
    }

    bool pub = (mode == "pub" || mode == "pubsub");
    bool sub = (mode == "sub" || mode == "pubsub");
    if (pub && !set.empty()) g_rti->publishObjectClassAttributes(ch, set);
    if (sub && !set.empty()) g_rti->subscribeObjectClassAttributes(ch, set);

    btk::say("OK CLASS name=" + label + " handle=" + toN(ch.toString()) +
             " attrs=" + U(list.size()) + " mode=" + mode);
}

static void cmdRegister(const Kv& kv)
{
    requireJoined();
    std::string id   = btk::get(kv, "id");
    std::string cls  = btk::get(kv, "class");
    std::string inst = btk::get(kv, "name", id);
    if (id.empty() || cls.empty()) { btk::errLine("REGISTER needs id= and class="); return; }

    std::map<std::string, e::ObjectClassHandle>::const_iterator c = g_classH.find(cls);
    if (c == g_classH.end()) { btk::errLine("unknown class '" + cls + "' - send CLASS first"); return; }

    Owned o;
    o.className = cls;
    o.instName  = inst;

    if (inst.empty()) {
        o.handle = g_rti->registerObjectInstance(c->second);
    }
    else {
        // Reserve the name, then pump callbacks until the RTI answers. Bounded so a lost
        // reservation cannot wedge the agent; on timeout we fall back to an RTI-assigned name
        // rather than failing the whole run, and say so.
        g_reserveState = 0;
        g_rti->reserveObjectInstanceName(toW(inst));

        std::chrono::steady_clock::time_point end =
            std::chrono::steady_clock::now() + std::chrono::seconds(5);
        while (g_reserveState == 0 && std::chrono::steady_clock::now() < end) pump();

        if (g_reserveState == 1) {
            o.handle = g_rti->registerObjectInstance(c->second, toW(inst));
        }
        else {
            btk::note("name reservation " +
                      std::string(g_reserveState < 0 ? "REFUSED" : "timed out") +
                      " for '" + inst + "' - falling back to an RTI-assigned name");
            o.handle   = g_rti->registerObjectInstance(c->second);
            o.instName = "";
        }
    }
    g_owned[id] = o;

    btk::say("OK REGISTER id=" + id + " handle=" + H(o.handle) + " name=" + inst + " class=" + cls);
}

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

    it->second.pending[attrHandle(it->second.className, attr)] = v;
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

    e::AttributeHandleValueMap values;
    for (std::map<e::AttributeHandle, Bytes>::const_iterator a = o.pending.begin();
         a != o.pending.end(); ++a)
    {
        const Bytes& b = a->second;
        values[a->first] = e::VariableLengthData(b.empty() ? "" : (const void*)&b[0], b.size());
    }

    g_rti->updateAttributeValues(o.handle, values,
                                 e::VariableLengthData(tag.c_str(), tag.size()));

    size_t n = o.pending.size();
    o.pending.clear();
    btk::say("OK UPDATE id=" + id + " attrs=" + U(n));
}

static void cmdDelete(const Kv& kv)
{
    requireJoined();
    std::string id = btk::get(kv, "id");
    std::map<std::string, Owned>::iterator it = g_owned.find(id);
    if (it == g_owned.end()) { btk::errLine("unknown id '" + id + "'"); return; }

    static const char* tag = "btk";
    g_rti->deleteObjectInstance(it->second.handle, e::VariableLengthData(tag, 3));
    g_owned.erase(it);
    btk::say("OK DELETE id=" + id);
}

/// INTCLASS name=<Qualified.Interaction> params=<A,B,C> mode=pub|sub|pubsub [as=<alias>]
static void cmdIntClass(const Kv& kv)
{
    requireJoined();
    std::string name   = btk::get(kv, "name");
    std::string params = btk::get(kv, "params");
    std::string mode   = btk::lower(btk::get(kv, "mode", "pubsub"));
    std::string label  = btk::get(kv, "as", name);
    if (name.empty()) { btk::errLine("INTCLASS needs name="); return; }

    e::InteractionClassHandle ch = g_rti->getInteractionClassHandle(toW(name));
    g_intH[label] = ch;
    g_intN[ch]    = label;

    std::vector<std::string> list = btk::splitList(params);
    for (size_t i = 0; i < list.size(); ++i) {
        e::ParameterHandle ph;
        try {
            ph = g_rti->getParameterHandle(ch, toW(list[i]));
        }
        catch (e::NameNotFound&) {
            btk::errLine("parameter '" + list[i] + "' is not defined on interaction '" + name +
                         "' in this FDD - check the customised FOM");
            return;
        }
        g_paramH[label + "::" + list[i]] = ph;
        g_paramN[ph] = list[i];
    }

    if (mode == "pub" || mode == "pubsub") g_rti->publishInteractionClass(ch);
    if (mode == "sub" || mode == "pubsub") g_rti->subscribeInteractionClass(ch);

    btk::say("OK INTCLASS name=" + label + " handle=" + toN(ch.toString()) +
             " params=" + U(list.size()) + " mode=" + mode);
}

static e::ParameterHandle paramHandle(const std::string& label, const std::string& param)
{
    std::string key = label + "::" + param;
    std::map<std::string, e::ParameterHandle>::const_iterator it = g_paramH.find(key);
    if (it != g_paramH.end()) return it->second;

    std::map<std::string, e::InteractionClassHandle>::const_iterator c = g_intH.find(label);
    if (c == g_intH.end()) throw std::runtime_error("unknown interaction '" + label + "' - send INTCLASS first");

    e::ParameterHandle ph = g_rti->getParameterHandle(c->second, toW(param));
    g_paramH[key] = ph;
    g_paramN[ph]  = param;
    return ph;
}

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

static void cmdSendInt(const Kv& kv)
{
    requireJoined();
    std::string label = btk::get(kv, "class");
    std::string tag   = btk::get(kv, "tag", "btk");

    std::map<std::string, e::InteractionClassHandle>::const_iterator c = g_intH.find(label);
    if (c == g_intH.end()) { btk::errLine("unknown interaction '" + label + "' - send INTCLASS first"); return; }

    std::map<e::ParameterHandle, Bytes>& pending = g_pendingInt[label];
    e::ParameterHandleValueMap values;
    for (std::map<e::ParameterHandle, Bytes>::const_iterator a = pending.begin();
         a != pending.end(); ++a)
    {
        const Bytes& b = a->second;
        values[a->first] = e::VariableLengthData(b.empty() ? "" : (const void*)&b[0], b.size());
    }

    g_rti->sendInteraction(c->second, values, e::VariableLengthData(tag.c_str(), tag.size()));

    size_t n = pending.size();
    pending.clear();
    btk::say("OK SENDINT class=" + label + " params=" + U(n));
}

static void cmdSnapshot()
{
    for (std::map<e::ObjectInstanceHandle, Seen>::const_iterator s = g_seen.begin();
         s != g_seen.end(); ++s)
    {
        btk::say("DISC obj=" + H(s->first) + " class=" + s->second.className +
                 " name=" + s->second.instName);
        for (std::map<e::AttributeHandle, Bytes>::const_iterator a = s->second.attrs.begin();
             a != s->second.attrs.end(); ++a)
        {
            btk::say("ATTR obj=" + H(s->first) +
                     " class=" + s->second.className +
                     " name="  + attrNameOf(a->first) +
                     " len="   + U(a->second.size()) +
                     " hex="   + btk::toHex(a->second));
        }
    }
    btk::say("OK SNAPSHOT objects=" + U(g_seen.size()));
}

static void pump()
{
    if (!g_connected) { std::this_thread::sleep_for(std::chrono::milliseconds(20)); return; }
    try { g_rti->evokeMultipleCallbacks(0.01, 0.05); }
    catch (e::Exception& ex) { btk::note("evoke: " + toN(ex.what())); }
}

static void cmdWait(const Kv& kv)
{
    long ms = std::atol(btk::get(kv, "ms", "1000").c_str());
    std::chrono::steady_clock::time_point end =
        std::chrono::steady_clock::now() + std::chrono::milliseconds(ms);
    while (std::chrono::steady_clock::now() < end) pump();
    btk::say("OK WAIT ms=" + U((unsigned long long)ms));
}

static void cmdResign()
{
    if (g_joined) {
        try { g_rti->resignFederationExecution(e::CANCEL_THEN_DELETE_THEN_DIVEST); }
        catch (e::Exception& ex) { btk::note("resign: " + toN(ex.what())); }
        g_joined = false;
    }
    if (g_connected) {
        try { g_rti->disconnect(); }
        catch (e::Exception& ex) { btk::note("disconnect: " + toN(ex.what())); }
        g_connected = false;
    }
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
    catch (e::Exception& ex)  { btk::errLine(toN(ex.what())); }
    catch (std::exception& ex){ btk::errLine(ex.what()); }
    catch (...)               { btk::errLine("unknown native exception"); }
}

// ------------------------------------------------------------------- main ----

static void readStdin()
{
    std::string line;
    while (std::getline(std::cin, line)) {
        std::string t = btk::trim(line);
        if (t.empty() || t[0] == '#' || t[0] == ';') continue;
        {
            std::lock_guard<std::mutex> lk(g_cmdLock);
            g_cmds.push_back(t);
        }
        if (btk::upper(t) == "QUIT") return;
    }
    std::lock_guard<std::mutex> lk(g_cmdLock);
    g_cmds.push_back("QUIT");
}

static bool applyArgs(int argc, char** argv)
{
    for (int i = 1; i < argc; ++i) {
        std::string a = argv[i];
        std::string val;
        size_t eq = a.find('=');
        if (eq != std::string::npos) { val = a.substr(eq + 1); a = a.substr(0, eq); }
        else if (i + 1 < argc && a.substr(0, 2) == "--") { val = argv[++i]; }

        if      (a == "--version") { btk::say("OK VERSION agent=btk-agent1516e hla=1516-2010 rti=MAK-5.0.1"); return false; }
        else if (a == "--rid")     { _putenv_s("RTI_RID_FILE", val.c_str()); btk::note("RTI_RID_FILE=" + val); }
        else if (a == "--config")  { _putenv_s("RTI_CONFIG",   val.c_str()); btk::note("RTI_CONFIG=" + val); }
    }
    return true;
}

int main(int argc, char** argv)
{
    if (!applyArgs(argc, argv)) return 0;

    btk::say("OK READY agent=btk-agent1516e hla=1516-2010");
    std::thread reader(readStdin);

    while (g_running) {
        pump();

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

    if (g_joined || g_connected) cmdResign();
    if (reader.joinable()) reader.detach();
    return 0;
}
