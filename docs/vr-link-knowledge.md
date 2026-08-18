# VR-Link 5.9 Knowledge Base

> Distilled from the MAK VR-Link 5.9 docs under `C:\MAK\vrlink5.9\doc` (scanned
> 2026-06-29), focused on what's needed to build the C# HLA test harness in this repo.
> See [DESIGN.md](DESIGN.md) for how this is applied. Cross-referenced from `CLAUDE.md`.
>
> **Confidence tags:** ✅ confirmed in docs · ⚠️ caveat / gap · ❓ to verify at runtime.

---

## 0. Where the knowledge lives (doc map)

| Source | What it actually is | Use it for |
|---|---|---|
| `doc/VRLinkGettingStartedGuide.pdf` | **Install/concepts only** — no code | RID/`rid.mtl`, `RTI_CONFIG`, defaults, FED/FDD lookup order |
| `doc/reference/csharp/` (Doxygen HTML) | **The C# API reference** | All C# class/method/property signatures |
| `doc/reference/hla1516e/` (HTML) | **The real C++ dev manual** (chapters) | Concepts, patterns, time mgmt, encoders — maps 1:1 to C# |
| `doc/MAKInteroperabilityGuide.pdf` | Product-compat guide (not a standards ref) | 1.3↔1516 root-class rename, FED/FDD naming, VR-Exchange notes |
| `doc/VRL5.9ReleaseNotes.pdf` | Release notes | 5.9 default changes (NETN, federation name), C# codegen fixes |
| `doc/AddingContent.pdf` | ❌ **3D art/terrain/GUI manual** — irrelevant | nothing for us; do not re-read (64 MB) |

There is **no** separate "VR-Link User's Guide" or "FOM Mapping" PDF in this install — the
developer detail is in the HTML reference, not a PDF.

---

## 1. Protocol targeting — C# is RUNTIME-selectable (key enabler) ✅

- **C++**: HLA version (1.3 / 1516 / 1516e) is a **compile-time** choice via preprocessor
  (`DtHLA`, `DtHLA_1516`, `DtHLA_1516_EVOLVED`). One C++ binary = one HLA version.
- **C#**: version is selected at **runtime** via `ExerciseConnection.ConnectionType`
  `{ DIS, HLA13, HLA1516, HLA1516E, WEBLVC }`, by choosing the initializer subclass.
  **→ A single C# process can open both a 1.3 connection and a 1516e connection at once.**
  This is exactly what the harness (Federate A on 1.3 + Federate B on Evolved) needs.

C# binding internals: `ManagedInterfaceCS` (C#) talks to `ManagedInterface` (C++) over a
serialized **message-passing** layer (not P/Invoke/SWIG). `ManagedInterface` is a singleton
loading one plug-in "strategy" set per protocol. DLL to reference: `vrLinkSharp.dll` from
`<install>/bin64`. Namespace: `using makVrl;` (the `Dt` prefix is dropped; methods stay
camelCase, state is exposed as camelCase **properties**).

⚠️ **Deployment gotcha (confirmed 2026-06-29):** `vrLinkSharp.dll` is a **mixed-mode managed**
assembly. The CLR resolves *managed* assemblies via app-base/probing — **NOT via `PATH`**
(PATH is native-only). So putting `bin64` on PATH does NOT let the exe find `vrLinkSharp.dll`
→ `FileNotFoundException: Could not load file or assembly 'vrLinkSharp'`. Fixes: (a) hook
`AppDomain.AssemblyResolve` to `Assembly.LoadFrom(bin64\vrLinkSharp.dll)` — register it
before any `makVrl` type is touched (keep makVrl code in a separate method so it JITs after);
or (b) Copy-Local the dll next to the exe; or (c) build into `bin64` like the MAK examples do.
The harness uses (a). Its **native** deps (`managedInterface*.dll`, RTI DLLs) still load via
PATH + `SetDllDirectory(bin64)`.

---

## 2. Joining a federation (C#) ✅

```csharp
using makVrl;

// Federation B — HLA Evolved (1516-2010)
var initE = new Hla1516eExerciseConnectionInitializer {
    federationName          = "MAK-One-2024",          // 5.9 default; set explicitly
    federateName            = "HarnessB",
    federateType            = "Tester",
    fedFilename             = "MAK-One-2024.xml",       // FDD .xml
    fomModules              = new List<string>{ /* NETN/RPR module xml paths */ },
    rprVersion              = 2.0,
    enableGenericAttributes = true,                     // REQUIRED for by-name raw access
    networkBigEndian        = true,
};
var connB = new ExerciseConnection(initE);             // ctor connects + joins

// Federation A — HLA 1.3 (same process)
var initA = new Hla13ExerciseConnectionInitializer {
    federationName          = "MAK-One-2024",
    federateName            = "HarnessA",
    fedFilename             = "MAK-One-2024.fed",       // .fed
    enableGenericAttributes = true,
};
var connA = new ExerciseConnection(initA);
// ... use ...
connA.Dispose();  connB.Dispose();                     // resign/destroy
```

**Initializer properties** (all `*ExerciseConnectionInitializer` subclasses):
`federationName, federateName, federateType, fedFilename, fomMapper, rprVersion,
enableGenericAttributes, deviceAddress, networkBigEndian, useAbsoluteTimestamps,
notifyLevel`. **`Hla1516eExerciseConnectionInitializer` adds** `fomModules` (List<string>)
and `mim`.

**5.9 defaults that will bite if unset** (release notes):
- Federation name default is now **`MAK-One-2024`** (was `MAK-RPR-2.0`).
- HLA Evolved federates default to the **NETN FOM + NETN FOM Mapper** (extends RPR 2.0).
  The old RPR 2.0 draft "will not work." Set `fomModules`/`fomMapper` to match your peers.
- RID file is `rid.mtl`; set env `RTI_CONFIG` to the dir holding the `.fed`/`.fdd`/`.xml`
  and `rid.mtl`. Lightweight mode needs no `rtiexec`; **Time Management / DDM / MOM /
  sync points / save-restore require `rtiexec`** (`useRtiExec 1`, one per federation).

---

## 3. ⚠️ CORRECTION (verified empirically 2026-08-13): the Universal layer does NOT receive

**Tested on VR-Link 5.10 + MAK RTI 5.0.1 and it does not work as the docs imply.**

- Registering `ExerciseConnection.addMessageHandler(UniversalHlaObjStateMessage.theName, del, "BaseEntity")`
  — and also with an **empty** class filter (catch-all) — yields **zero messages**.
- Publishing via `sendMessage(new UniversalHlaObjStateMessage{...})` puts **nothing on the wire**.
- Control test proving it isn't the rig: MAK's own `talkHLA13_64.exe` published
  (`Registered object ... of class Aircraft(23)`) and MAK's own `listenHLA13_64.exe`
  received it fine — while our Universal subscriber in the same federation saw 0.
- There are only 4 `Universal*` types and **no registration/enable API** on
  `ExerciseConnection`. Conclusion: the Universal layer is **not fed in an ordinary VR-Link
  federate** (it appears to be internal plumbing / for WebLVC-style hosts).

**➡ Use the TYPED path instead — it is proven working (see §3a).**

## 3y. 🚨 ARCHITECTURE-DECIDING: ONE ExerciseConnection PER PROCESS

**VR-Link 5.10's C# binding cannot run two `ExerciseConnection`s in one process for DATA.**
Joining works; anything real does not. Proven by A/B test (identical publish workload, the
only variable being a second connection):

| Publisher | External observer result |
|---|---|
| **single** connection | `[entity] id=7:7:7 pos=(-2696048,-4429591,3701299)` — saw 1 ✅ |
| **dual** connection (A=1.3 + B=Evolved) | **saw 0** ❌ + Segmentation fault |

Failure modes seen with two connections open:
- `AccessViolationException` in `ExerciseConnection.sendSubscribe` when creating the 2nd
  `ReflectedEntityList` (partly avoidable by creating A's list/publisher *before* B connects);
- `AccessViolationException` in `EntityPublisher.Dispose()`;
- published data never reaches the wire at all.

Root cause: `ManagedInterface` is a **per-process singleton** (see §1) — it cannot route for
two connections. Same reason two same-federation connections fail (§3z rule 4).

⚠️ `AccessViolationException` is a **Corrupted State Exception** — a plain `try/catch` will
NOT catch it; the process dies. Don't plan on trapping it.

**➡ CONSEQUENCE: the harness MUST be multi-process — one agent process per federation**
(Fed A agent, Fed B agent), coordinated over IPC by the WPF GUI, exactly as VR-Exchange
itself does (Portal + one broker process per connection). The GUI/Engine hold no RTI, so
they stay VC-agnostic and can also span different RTI versions/VCs later.

## 3w. Where HLA 1.3 datatypes live: the .omt, NOT the .fed ✅ (verified 2026-08-13)

**The `.fed` contains no datatype information at all** — `grep -ci datatype MAK-RPR1-1-1.fed` = **0**.
An attribute row is only: `(attribute WorldLocation best_effort receive)` = name + transport + order.

**The `.omt` (Object Model Template, DIF text format) carries the datatypes** and is
machine-readable (MAK ships e.g. `bin64/MAK-RPR2-1-10.omt`, ~950 KB, `(DIF HLA-OMT v1.3 ...)`):

```lisp
(Attribute (Name "WorldLocation")
    (DataType "WorldLocationStruct") ...)

(ComplexDataType (Name "WorldLocationStruct")
    (ComplexComponent (FieldName "X") (DataType "double") (Cardinality "1") (Units "meter (m)") ...)
    (ComplexComponent (FieldName "Y") (DataType "double") ...)
    (ComplexComponent (FieldName "Z") (DataType "double") ...))
```

So a **data-driven codec is possible on BOTH sides** — nothing FOM-specific need be compiled in:

| Side | Datatype source | Machine-readable |
|---|---|---|
| Fed B — HLA Evolved | FDD `.xml` (`<dataType>...</dataType>`) | ✅ |
| Fed A — HLA 1.3 | **`.omt`** (the `.fed` has none) | ✅ |

This is what makes dotted addressing like `<ClassPath>.WorldLocation.x` work generically:
the OMT supplies the field name (`X`) and its type (`double`).

⚠️ The OMT gives the **logical** type only; the **big-endian** wire rule comes from the
RPR/DIS convention (one fixed rule for all RPR FOMs, not per-FOM knowledge).

**➡ ACTION: for the CUSTOMISED RPR 1.0, obtain the `.omt` (or the FOM tool's OMT export)
alongside the `.fed`.** Without it, custom datatypes are not machine-discoverable and would
have to be reverse-engineered from the broker's C++.

## 3x. Licence seats gate the multi-process design ⚠️

Each **agent process = one VR-Link `vrl_run` seat**. This site licence has **2**:

```
INCREMENT vrl_run maklmgrd 2027.031 31-mar-2027 2     <- two seats
PACKAGE   vrl_run ... COMPONENTS="hvl1 hvl2 ..."      <- hvl1 = the HLA feature
```

A 3rd concurrent VR-Link process fails at connect with:
`Licensed number of users already reached. Feature: hvl1 ... FlexNet Licensing error:-4,132`

**Consequences**
- The harness fits exactly: **one agent for Fed A + one for Fed B = 2 seats.**
- **No headroom**: you cannot run MAK's `talkHLA13`/`listenHLA13` for debugging *while* both
  agents are up. Stop an agent first, or ask MAK for more seats.
- The **VR-Exchange broker does not** consume `vrl_run` — it uses `mak_portal` (1) and
  `portal_adp` (999), so the broker can run alongside both agents.

## 3v. 🚪 THE ESCAPE HATCH: raw MAK RTI in C++ (verified 2026-08-18)

The C# binding's missing FOM Mapper / encoder (§3, §7) is **not** a property of HLA — it is a
property of the C# binding. Dropping to the raw RTI C++ API removes the limitation entirely.

**Verified working** — `src/BrokerTestKit.AgentNative/btk_agent13.cpp`, proof in
`tests/native/run-smoke.bat`: two federates, four attributes (two of them **custom**, absent from
any RPR mapper), every byte round-tripped identical.

| fact | detail | status |
|---|---|---|
| Raw bytes in | `AttributeHandleValuePairSet::add(handle, const char* buf, ULong len)` — no encoder involved | ✅ |
| Raw bytes out | `reflectAttributeValues` hands back the same triple; `getValuePointer(i, len)` | ✅ |
| Fully by-name | `getObjectClassHandle(name)` / `getAttributeHandle(name, class)` — zero FOM compiled in | ✅ |
| Licence pool | links the RTI, so `makrtiLib` (**14** seats), NOT `vrl_run` (2) — lifts the §3x ceiling | ✅ |
| Fidelity | no dead reckoning, no thresholding, no smoothing between the test and the wire | ✅ |
| Headers / libs | `C:\MAK\makRti5.0.1\include\HLA13` + `libRTI-NG_64.lib`, `libfedtime_64.lib` | ✅ |

### Build constraints (each one is fatal if missed) ⚠️

- **`/std:c++14`** — HLA 1.3 headers use dynamic exception specs `throw(A, B)`, *removed* in C++17.
- **`/DRTI_USES_STD_FSTREAM`** — otherwise `RTI.hh` includes the pre-standard `<fstream.h>`.
- **VS2022 (VC17) links against the VC15 RTI libs.** VC14–VC17 are one ABI island (universal CRT).
  The RTI announces it at startup: *"HLA 1.3 for Microsoft Visual C++ version 14.1 Release Mode."*

### 🚨 `RTIambassador` connects inside its CONSTRUCTOR ⚠️

A global `RTI::RTIambassador` dials the RTI during **static init — before `main()`**. It therefore
picks up MAK's default `rid.mtl`, not ours, and fails with *"Failed to create a reliable connection
to the RTI Forwarder ... RTI_internalMsgReliableWhenUsingRtiexec is enabled"*. **Construct it
lazily**, after `RTI_RID_FILE` is set. Same class of bug as the managed side's RID problem (§3z).

### HLA Evolved (1516-2010) — same result, different API (verified 2026-08-18)

`btk_agent1516e.cpp` does the same job on the B side. Custom attributes round-trip identically.
The API shares almost no vocabulary with 1.3, which is why it is a separate binary:

| HLA 1.3 | HLA Evolved |
|---|---|
| `RTI::RTIambassador` (concrete class) | `RTIambassadorFactory().createRTIambassador()` |
| join connects implicitly | **`connect()` is a separate, required first step** |
| `char*` names | `std::wstring` names |
| integer handles | handle **classes** with `toString()` |
| `AttributeHandleValuePairSet` (+ factory) | `AttributeHandleValueMap` (a `std::map`) |
| `tick(min, max)` | `evokeMultipleCallbacks(min, max)` |
| name an object at register time | **must `reserveObjectInstanceName()` and await a callback** |
| one `.fed` | FDD + 0..n modules + optional MIM |

**🚨 Instance names must be reserved.** `registerObjectInstance(class, name)` fails with
`ObjectInstanceNameNotReserved` unless `reserveObjectInstanceName()` ran first *and* its
asynchronous `objectInstanceNameReservationSucceeded` callback arrived. The agent reserves,
pumps until the RTI answers (bounded at 5 s), then registers — falling back to an RTI-assigned
name rather than failing the run.

### 🚨 FOM module merge rules (MAK RTI 5.0.1) ⚠️

Two behaviours that cost real time. Both now recorded in REQ-01 because they shape the UI.

1. **Modules passed to `createFederationExecution` are SILENTLY DROPPED.** Only the first file is
   used — no exception, no warning, the attributes are simply absent. Proven by reversing the
   order of `{fdd, module}`: whichever came first won. Pass modules to
   `joinFederationExecution(..., additionalFomModules)` instead, where merging really happens.
2. **A module may add NEW SUBCLASSES only**, never attributes to a class another module defines.
   Re-declaring `BaseEntity` with extra attributes gives *"Failed merging object classes. Object
   class's info for HLAobjectRoot.BaseEntity does not match other module"*. The existing class
   must appear as pure **scaffolding** (name only), with new attributes on a new subclass.

Consequence: a customised RPR 2.0 that adds attributes to *existing* RPR classes **cannot** be
shipped as a module — it has to be a single merged FDD.

### Where this leaves the two agents

| side | HLA | agent | status |
|---|---|---|---|
| A | 1.3 | `btk-agent13.exe` | ✅ built, custom attributes proven |
| B | Evolved | `btk-agent1516e.exe` | ✅ built, custom attributes proven |

Both prove out via `tests/native/run-smoke.bat` (exit 0). VR-Link remains useful for *modelled*
RPR state, but it is no longer required by the harness — which also removes the runtime VR-Link
licence dependency from the finished tool.

---

## 3z. Hard-won runtime rules (all verified empirically 2026-08-13)

**1. `drainInput(0.0)` does NOT tick the RTI. Always pass a NEGATIVE value.**
With `0.0`, objects register but attribute updates never reach subscribers and nothing is
received — a silent, total failure. MAK's own `talk.cs` uses `exConn.drainInput(-1.0)`
(process everything available, then return). Our `FederateConnection.Pump()` now defaults
to `-1.0`. *This single line was the difference between "nothing works" and "works".*

**2. A publisher needs a STABLE `esr.id` BEFORE the first `tick()`.**
Otherwise VR-Link registers a **brand-new object on every tick** (we saw IDs 265, 266,
267 … climbing). Per `talk.cs`:
```csharp
esr.type = t; esr.guise = t;
esr.id = "1:2:3";                 // Site:Host:EntityNumber
esr.entityIdentifier = esr.id;    // for HLA RPR FOM
esr.markingText = "BTK-PUB";      // handy stable correlation key
```
With it: `Registered object 1:2:3(...) of class Aircraft(23)` — exactly one object.

**3. Advance sim time every iteration:** `exConn.clock.setSimTime(t)` then `pub.tick(dt)`.
Reflected state is dead-reckoned to sim time.

**4. ⚠️ Two ExerciseConnections to the SAME federation in ONE process do NOT work.**
A same-process loopback (publisher + subscriber, both HLA 1.3, one federation) never
delivers — and a spurious extra object appears. VR-Link's `ManagedInterface` is a
**per-process singleton**, so two same-protocol connections interfere.
*The identical publish/subscribe code works perfectly across two PROCESSES.*
→ Do loopback self-tests **cross-process**. Two connections of **different** protocols
(1.3 + Evolved) do co-exist for join/pump (PRP-2), but their data flow is **not yet
verified** — if it also collides, the harness must use the multi-process agent design.

**5. `EntityPublisher.Dispose()` can throw `AccessViolationException`** (inside
`dll_sendMessage`, sending the delete-object). Wrap teardown in try/catch so it can't mask
a successful run.

**6. `ReflectedEntityList` and `EntityPublisher` have `Dispose()` but do NOT implement
`IDisposable`** — no `using`; call `Dispose()` in a `finally`.

## 3a. The working path: ReflectedEntityList + typed SR (+ generic attrs) ✅

```csharp
var list = new ReflectedEntityList(exConn);      // NOTE: has Dispose() but does NOT
...                                              // implement IDisposable -> no `using`
exConn.drainInput(0.05);
foreach (var ent in list.entities) {
    EntityStateRepository esr = ent.esr;
    var id  = esr.id;                 // "58720257"
    var pos = esr.worldPosition;      // Vector3d, already DECODED
}
list.Dispose();
```

Verified output: `Discovered Entity: 58720257 of class Aircraft(23)`,
`pos=(-2696048.93, -4429591.55, 3701299.64)`.

- **Mapped RPR attributes → typed, decoded properties** on `EntityStateRepository`:
  `id, entityIdentifier, type, guise, worldPosition, worldVelocity, worldAcceleration,
  worldOrientation, rotationalVelocity, lastSet*, algorithm, frozenBit, damageState,
  mobility, firePower, smoke, trailingEffects, hatchState, flaming, PaintScheme`, plus
  `getArticulatedPart/getAttachedPart/lightState`.
  **VR-Link does the encode/decode — no byte codec needed for these.**
- **Unmapped attributes → by-name generic channel** on `StateRepository`:
  `Object getAttribute(string)`, `void setAttribute(string, Object)`, `bool hasAttribute(string)`
  (requires `enableGenericAttributes`). ⚠️ These are for attributes the FOM mapper does **not**
  model — probing mapped FOM names (`WorldLocation`, `EntityType`, `Marking`…) returns
  `hasAttribute == false`.
- **Rename-proofing still holds, and is actually better:** a FOM attribute rename is absorbed
  by the **FOM Mapper config**, so harness code comparing SR properties is unaffected.

## 3b. Universal HLA layer (documented API — NOT usable for receive, see §3)

FOM-agnostic, by-name, **raw `byte[]`** publish + receive in pure C#. All extend `Message`;
the `Universal*` classes are in the **global** namespace (connection/publisher are `makVrl`).

| Class | Direction | Key members |
|---|---|---|
| `UniversalHlaObjStateMessage` | publish + receive | `objClass`, `objName` (get/set); `byte[] getAttribute(string)`, `void setAttribute(string, byte[])` |
| `UniversalHlaObjDiscoveryMessage` | receive | `objClass`, `objName` (class+instance name only, no payload) |
| `UniversalHlaObjRemovedMessage` | receive | `objClass`, `objName` |
| `UniversalHlaInteractionMessage` | publish + receive | `objClass` (= interaction class); `getAttribute`/`setAttribute(byte[])`; no `objName` |
| `Message` (base) | — | `virtual Byte[] encode()`, `static Message decode(ref Byte[])`, `name`, `timeStamp` |

Internally the payload is `Dictionary<string, byte[]> myAttributes` keyed by FOM field name —
confirming attributes/parameters are raw bytes by name.

**Routing on `ExerciseConnection`:**
```csharp
// register a handler, scoped to a FOM class via the 3rd arg (uniClsName)
void addMessageHandler(string messageType, MessageDelegate del, string uniClsName = "");
void removeMessageHandler(string messageType, MessageDelegate del, string uniClsName = "");
void drainInput(double maxTime);   // pump; fires handlers. negative maxTime = listen continuously
void sendMessage(Message m);       // generic send
int  nextEventId();
// delegate void MessageDelegate(Message m);
```

**End-to-end generic flow:**
```csharp
// RECEIVE (Federate B)
connB.addMessageHandler("UniversalHlaObjDiscoveryMessage", OnDiscover, "BaseEntity");
connB.addMessageHandler("UniversalHlaObjStateMessage",     OnUpdate,   "BaseEntity");
connB.addMessageHandler("UniversalHlaObjRemovedMessage",   OnRemove,   "BaseEntity");
connB.addMessageHandler("UniversalHlaInteractionMessage",  OnFire,     "WeaponFire");
void OnUpdate(Message m) {
    var s = (UniversalHlaObjStateMessage)m;
    byte[] loc = s.getAttribute("WorldLocation");   // raw bytes — decode per FOM datatype
}
// pump every frame:
connB.drainInput(0.0);

// PUBLISH (Federate A)
var msg = new UniversalHlaObjStateMessage { objClass = "BaseEntity.PhysicalEntity.Platform.Aircraft", objName = "AC1" };
msg.setAttribute("WorldLocation", encodedBytes);    // YOU encode the bytes
connA.sendMessage(msg);
```

This is the path-agnostic, **rename-proof** access: `objClass`/attribute names are plain
strings; values are `byte[]` and never change when names change.

---

## 4. Typed path (alternative — NOT used by the harness) ℹ️

For strongly-typed RPR work: `EntityPublisher` + `EntityStateRepository` (`.esr` property,
`.tick(dt)`), `ReflectedEntityList` (`.entities`), typed interactions as `*Message`
(`FireMessage`, etc.). State exposed as decoded properties (`worldPosition`, `worldVelocity`).
Generated per object type via `C-Sharp/bin/messageCodeGen-cs.exe`. We **don't** need this —
it's tied to known FOM concepts and would require codegen per type. Recorded for context only.

C++ equivalents (the HTML manual): `DtExerciseConn`, `DtEntityPublisher`,
`DtEntityStateRepository` (`esr()`), `DtReflectedEntityList`, `DtFireInteraction`. Naming map:
`Dt*`→drop prefix, `send/sendStamped`→`sendMessage`, `addCallback`→`addMessageHandler`.

---

## 5. The pump loop, callbacks & HLA gotchas ⚠️

- **`drainInput()` must be called every frame** — it drives `RTI::tick()` and fires all
  registered handlers. Nothing is received without it.
- **HLA discovery vs state ordering:** when an object is discovered, the state is **not yet
  populated** — only the identity (`objClass`/`objName`) is valid. State arrives in a later
  update callback / after a subsequent `drainInput()`. **Bake this into the harness:** assert
  on attribute values in the update handler (or after a settle pass), never at discovery.
- **Do not make RTI calls inside receive callbacks** (they run inside `RTI::tick()`); defer
  if needed (C++ has `addPostDrainCallback`).
- **Interactions are transient** — copy out any data you need in the handler.
- Sim time should be set each frame (`clock`); typed reflected state is **dead-reckoned to
  current sim time** (not relevant for raw-byte generic access, but relevant if you ever read
  typed `location()`).

---

## 6. FOM mapping (DtFomMapper / NETN) ✅

Three layers: FOM-independent API ⇄ **FOM mapping layer** (`DtFomMapper`) ⇄ RTI. Switching
FOMs that share VR-Link's concepts touches only the mapper, not app code. The
`ExerciseConnection` initializer's `fomMapper` selects it. Built-ins: `DtRprFomMapper`
(full RPR), `DtSimpleRprFomMapper` (subset), `DtEmptyFomMapper` (clean slate), and in 5.9 a
**NETN FOM Mapper** (`DtNetnFomMapper.setUseMakNetnExtensions(bool)`, default true).
For the **generic** path the mapper is mostly bypassed for unmapped attributes — that's the
point. Enable unmapped passthrough via `enableGenericAttributes` (C#) /
`setGenericAttributes(true)` / per-class `enableUnknownParameters()` (C++).

---

## 7. Encoding / decoding — the harness's responsibility ⚠️ (this is the real work)

The Universal layer hands you **raw `byte[]`** by name; it does **not** decode to typed
values. You must build a datatype-driven codec (CSV string ⇄ `byte[]`).

**Confirmed wire facts (from MAK docs / RPR records):**
- `WorldLocation` = 3 × **IEEE float64** geocentric metres (x,y,z).
- `EntityIdentifier` = Site(16) + Application(16) + Entity(16) = 48 bits.
- `UUID` = 128 bits. Variable-length records are length-prefixed and **padded to 64-bit**.
- Discriminated unions use a 32-bit "Variant Type" discriminant (≈ 1516 `HLAvariantRecord`).
- VR-Link "Net" types (`DtNetU16`, `DtNet64Vector`…) are network byte order; casting Net↔native
  byte-swaps automatically. RPR/DIS records are **big-endian**.
- ⚠️ 5.9 C# codegen had a **padding bug** for array/fixedRecord (`octetBoundary`) affecting
  `VariableDatumSet` in Data interactions — relevant if hand-encoding parameter bytes.

**Coordinates (C++ classes; C# has `GeodeticCoord`/`CoordinateTransform`/`UtmCoordinate`):**
state is geocentric `DtVector` metres; `DtGeodeticCoord` ⇄ geocentric; topographic via
`DtCoordTransform` + `DtGeocToTopoTransform`. Orientation = `DtTaitBryan` (psi/theta/phi Euler
radians, **geocentric-referenced**, not heading/pitch/roll unless transformed). Datum WGS84.

⚠️ **Gap:** the 1516 standard-encoding tables (`HLAfixedRecord`, `HLAvariableArray`,
`HLAinteger32BE`, `HLAfloat64BE`, `HLAunicodeString`, `HLAvariantRecord`, `HLAopaqueData`) are
**not** in the MAK docs. Get them from **IEEE 1516.2-2010 §4** and the **SISO RPR-FOM enc
tables**. For Fed B (Evolved) the datatypes are also in the **FDD `.xml`** (self-describing);
for Fed A (1.3) the `.fed` has none → use OMT tables / RPR-FOM / a hand-authored map.

---

## 8. Time management ⚠️ C++ only — C# gap (constrains Q2)

C++ (via the RTI ambassador, not VR-Link wrappers):
```cpp
exConn.rtiAmb()->enableTimeRegulation(suggestedFedTime, lookAhead);
exConn.rtiAmb()->enableTimeConstrained();
exConn.rtiAmb()->timeAdvanceRequest(requestedFedTime);
exConn.setSendFedTime(true);  // true => all msgs TSO; default false => RO
// grant/enabled callbacks on exConn.fedAmb(): addTimeAdvanceGrantCb,
// addTimeConstrainedEnabledCb, addTimeRegulationEnabledCb
```
FedTime types: 1516e `HLAfloat64Time`, 1516 `LogicalTimeImpl`, 1.3 `RTIfedTime` (`.getTime()`).
Rule: keep `simTime ≥ FedTime + lookahead`; don't request an advance until the
constrained-enabled callback fires.

⚠️ **The C# narrative docs document NO time-management API.** `ExerciseConnection` doesn't
obviously expose `enableTimeConstrained`/`timeAdvanceRequest`/`setSendFedTime`.
**Implication for the harness:** deterministic time-managed lockstep verification may be
**C++-only**. The pragmatic pure-C# approach is **RO (receive-order) + settle-window + timeout**
verification. ❓ Verify against the full C# class reference before committing to lockstep.

---

## 9. HLA 1.3 vs Evolved — the testable differences ✅

| Topic | HLA 1.3 | HLA Evolved (1516-2010) |
|---|---|---|
| FOM file | `.fed` | `.xml` (FDD), `_evolved.xml`; supports **FOM modules** + MIM |
| **Object root class** | `ObjectRoot` | `HLAobjectRoot` |
| **Interaction root class** | `InteractionRoot` | `HLAinteractionRoot` |
| Datatypes in file | **No** | **Yes** (FDD carries datatype tables) |
| FedTime | `RTIfedTime` | `HLAfloat64Time` |

**The #1 bridging fact (and a built-in test assertion):** the RTI keys on the literal class
path — `ObjectRoot.X` ≠ `HLAobjectRoot.X`. A correct broker **rewrites the root prefix** in
both directions. The harness should assert that an object published as
`ObjectRoot.BaseEntity.…Aircraft` on the 1.3 side appears as
`HLAobjectRoot.BaseEntity.…Aircraft` on the Evolved side (and vice versa).

Other interop notes: MAK allows mixing a 1.3-style FED with a 1516 RTI, but **FOM
representation must stay consistent across the bridge**. Ownership/DDM/MOM do **not** cross the
version boundary cleanly — a broker forwarding only object/interaction updates may drop those
semantics.

---

## 10. FOM file naming (matters for matching files across the bridge) ✅

- **New scheme (VR-Link ≥5.2):** `MAK-<FOMname><revision><version>.fed|.xml|_evolved.xml`
  (e.g. `MAK-RPR20017-1-1.fed` = RPR FOM 2 draft 17). Products on ≥5.2 default to these.
- **Old scheme (deprecated, still shipped):** `VR-Link<RPRversion>.fed` (e.g.
  `VR-Link20017-1.fed`). To interop with older products, align FED names + federation names.
- Evolved standard RPR FOM ships as `RPR_FOM_v2.0_1516-2010.xml` with MAK additions as separate
  modules (e.g. `NETN-BASE.xml`, `MAK-Physical-2_evolved.xml`, `MAK-VRFExt-9_evolved.xml`).
  Module version integer increments on **incompatible** change.

---

## 11. RPR / NETN FOM (orientation, not a full catalog) ℹ️

Lineage: MAK object model = NETN FOM 3.0 → RPR FOM 2.0 → DIS. RPR = Real-time Platform
Reference FOM. Standard classes (`BaseEntity` → `PhysicalEntity` → `Platform` → `GroundVehicle`/
`Aircraft`/…), `EntityType` (7-field), `WorldLocation`, markings, enumerations. NETN adds
`NETN_Aircraft`/`NETN_GroundVehicle`/… ; MAK adds `MAK_Aircraft`/`MAK_Radio`/`MAK_Sensor`/….
⚠️ The full RPR class/datatype catalog is **not** in the MAK docs — use the **SISO RPR-FOM**
(SISO-STD-001/001.1) and the **NETN FOM**.

---

## 12. VR-Exchange (the broker under test) ✅

- Single executable; **broker per protocol**, many-to-many translation via a canonical hub
  (matches our HLA 1.3 ↔ Evolved case).
- A FOM Mapper used with VR-Exchange **must be built with the same VR-Link version** as the
  VR-Exchange it runs in. (Our broker = VR-Exchange 2.5.1; harness = independent, VR-Link 5.9.)
- Version coupling: VR-Exchange ≥2.0.1 ↔ MAK RTI ≥4.0.4. WebLVC Server is built on VR-Exchange.
- ⚠️ VR-Exchange broker config / FOM-Mapper rule syntax / internal identity-handle mapping are
  **not** in these docs — see the VR-Exchange User's Guide and the examples under
  `C:\MAK\vrexchange2.5.1\examples` (already surveyed in [DESIGN.md](DESIGN.md) §6/§11).

---

## 13. External references to pull when needed

- **IEEE 1516.2-2010 §4** — standard datatype encodings (the codec source of truth).
- **SISO-STD-001 / 001.1 (RPR-FOM)** — class + datatype + enumeration catalog, enc tables.
- **NETN FOM** — the 5.9 Evolved default extension set.
- VR-Exchange User's Guide — broker mapping behaviour the harness verifies.

---

## 14. Net implications for the harness (summary)

1. Single C# app, two `ExerciseConnection`s (Hla13 + Hla1516e) — runtime-selected. ✅
2. Use the **Universal HLA layer** for generic by-name `byte[]` publish/receive. ✅
3. Set `enableGenericAttributes = true` and explicit `federationName`/`fomModules`. ✅
4. Build a **datatype-driven codec** (the main effort): FDD `.xml` for Fed B; OMT/RPR for
   Fed A; IEEE 1516.2 + RPR enc tables as the spec. ⚠️
5. Verification cadence: **RO + settle-window + timeout** in pure C# (time-managed lockstep
   appears C++-only — verify). ⚠️ → resolves DESIGN.md Q2 toward RO unless proven otherwise.
6. Free built-in assertion: **root-class rename** `ObjectRoot`↔`HLAobjectRoot`. ✅
7. Handle the **discover-then-update** ordering; assert state in update handlers. ⚠️
