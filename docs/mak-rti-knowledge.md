# MAK RTI — distilled knowledge

> Everything here was **verified on this machine**, not read off a datasheet. Where a claim came
> from an experiment, the experiment is shown. Companion docs:
> [vr-link-knowledge.md](vr-link-knowledge.md) (VR-Link API),
> [APP-REQUIREMENTS.md](APP-REQUIREMENTS.md), [DESIGN.md](DESIGN.md).
>
> Status key: ✅ proven here · ⚠️ caveat · ❓ to verify

---

## 1. What is installed

| Install | Client libs | CRT | ABI island |
|---|---|---|---|
| `makRti3.3.2_linkup` | 32-bit only | — | legacy |
| `makRti4.3` | `libRTI-NG*`, `librti1516e*` (32 + 64) | `MSVCR100` / `MSVCP100` | **VC10** |
| `makRti4.4.2` | 32 + 64 | `MSVCR100` | **VC10** |
| `makRti4.5.1` | 32 + 64 | `MSVCR100` | **VC10** |
| `makRti4.5.1(vc14)` | 64 only | `VCRUNTIME140` + ucrt | **VC14+** |
| `makRti4.6.1` | 64 only | `VCRUNTIME140` + ucrt | **VC14+** |
| `makRti5.0.1` | 64 only | `VCRUNTIME140` + ucrt | **VC14+** (reports VC 14.1) |

Determined with `dumpbin /dependents` on each `bin/libRTI-NG_64.dll`. ✅

That MAK ships `makRti4.5.1` **and** `makRti4.5.1(vc14)` side by side is the clearest possible
statement that the VC build is part of the product identity, not an implementation detail.

---

## 2. 🚨 One agent binary serves every RTI in the same VC island ✅

The harness's native agent links the RTI **import library** at build time but binds
`libRTI-NG_64.dll` at **load** time from `PATH`. Both 4.6.1 and 5.0.1 ship that same DLL name on
the same ABI island, so the *same executable* joins either — only `PATH` differs:

```
PATH=…\makRti4.6.1\bin   →  "RTI version 4.6.1 HLA 1.3 for Microsoft Visual C++ version 14.0"   OK JOIN
PATH=…\makRti5.0.1\bin   →  "RTI version 5.0.1 HLA 1.3 for Microsoft Visual C++ version 14.1"   OK JOIN
```

Same `btk-agent13.exe`, no rebuild, custom attributes resolved on both. ✅

### 🚨 The rule that matters: VC is per-FEDERATE, not per-FEDERATION ✅

**Federates linking different VC builds of the SAME RTI version share a federation normally.**
The VC build decides which local library a process can link; the *wire protocol* belongs to the
RTI version. Proven — one federation on RTI 4.5.1, two federates on different VC builds:

```
PUBLISHER  (linked vc14)  RTI version 4.5.1 HLA 1.3 … Visual C++ version 14.0
SUBSCRIBER (linked VC10)  RTI version 4.5.1 HLA 1.3 … Visual C++ version 10.0
  DISC obj=3674210305 class=BaseEntity name=BTK-TANK-01
  ATTR BtkCustomTag len=13 hex=435553544f4d2d5441472d3031   … all four byte-identical
```

**Consequence.** A VC-mismatch problem is only ever about *one process and the library it loads*.
It is never a reason two applications cannot interoperate. So VR-Exchange 2.9 links
`makRti4.5.1(vc14)`, a legacy VC10 application links `makRti4.5.1`, and they meet in the same
federation. No VC10 build of anything else is required.

Contrast with **different RTI versions** (§3): those genuinely do collide, on the Assistant port,
and need separate `RTI_ASSISTANT_PORT` values.

### Loading a mismatched library into ONE process

Two different things were conflated here, so state them apart:

| | VC10 RTI from a VC14+ process |
|---|---|
| **Technically possible?** | **Yes** for HLA 1.3 (proven below). **No** for HLA Evolved — access violation, because the 1516e API passes `std::wstring` / `std::vector` across the DLL boundary. |
| **Allowed by VR-Exchange?** | **No.** It checks the VC of the library it loads and refuses outright, whatever the API. |

VR-Exchange's refusal is a **policy check**, measured directly — VR-Exchange 2.9 with a broker
pointed at 4.5.1 VC10:

```
Fatal Error: Mismatching RTI compiler version. Looking for: libRTI-NG_64.dll.
Found library in: C:\MAK\makRti4.5.1\bin\libRTI-NG_64.dll. Expected VC14 but found VC10
```

The sibling broker on 5.0.1 (VC15) started normally in the same run, so the per-connection
environment was applied correctly — VR-Exchange simply will not use a mismatched library.

Our agents perform no such check, which is why they get further. The same
`btk-agent13.exe` — built with **VS2022 (linker 14.44)** and linked against **5.0.1's** import
library — ran the full custom-attribute round-trip against **4.3**:

```
Using MAK Technologies' RTI version 4.3 HLA 1.3 for Microsoft Visual C++ version 10.0 Release Mode.
OK JOIN exec=BTK-NativeTest federate=NativeSub handle=5879
ATTR BtkCustomTag     len=13  hex=435553544f4d2d5441472d3031
ATTR BtkCustomValue   len=8   hex=40934a0000000000
ATTR EntityIdentifier len=6   hex=000100010065
ATTR WorldLocation    len=24  hex=c14491b8…4154fb1800000000
```

Byte-identical to what was published. So **one binary serves every RTI installed here** — 4.3
through 5.0.1, VC10 and VC14+ alike — with only `PATH` differing.

⚠️ **It works; it is not *supported*.** Mixing CRTs is undefined behaviour in general, and this
only holds because of what actually crosses the DLL boundary here: integer handles and byte
buffers, with every RTI-owned object allocated *and* freed by the RTI. MSVC also matches `catch`
types by type-name string, which is why exceptions thrown by a VC10 DLL are still caught in a VC17
binary. Code that passed an STL object by value, or freed an RTI allocation with our `delete`,
could still corrupt the heap — silently, with no version error to point at it. Treat VC10 as
"empirically fine for this agent", not as a guarantee for new code.

**Consequence for the app:** to bridge any two of these RTIs you need **both installed** (DLLs,
licence, rid) but only **one agent binary**. Per-federation `RTI bin` + `RID file` settings are
enough.

⚠️ **`MAKLMGRD_LICENSE_FILE` must be set explicitly** for the older RTIs. Left unset, 4.3 defaults
to `27000@localhost`, reports *"MAK RTI license not found"*, and then **hangs** rather than failing
— two agents both stalled after loading the rid, with no error. Setting `27000@127.0.0.1` fixed it
outright. A hang, not a message, is the symptom to recognise.

---

## 3. 🚨 The RTI Assistant is the real cross-version blocker ✅

Two RTI versions running at once fail with:

> *Assistant causing connection to shutdown because: Invalid message version from Assistant.*

**Cause, measured.** The Assistant listens on a **fixed TCP port 6003**:

```
netstat -ano  →  TCP 127.0.0.1:6003  LISTENING  <rtiAssistant.exe pid>
```

Every LRC does *"Attempt to create and connect to Assistant"* on start-up. Whichever version's
Assistant claimed 6003 first serves everyone, so the other version's federate talks to a foreign
Assistant and is disconnected on a version check.

### ✅ SOLVED — `RTI_ASSISTANT_PORT` moves it *(corrects the advice below)*

6003 is only the **default**. The Assistant's port is set by an **environment variable**,
`RTI_ASSISTANT_PORT` — documented in the *VR-Exchange Users Guide* §5.3, "Connecting Two
Different RTIs", not in any rid. That is why searching the rid files and the DLL's strings for
`RTI_*assistant*` found nothing and led to the wrong conclusion that the Assistant had to be
killed.

Proven here — set it, and the Assistant follows:

```
RTI_ASSISTANT_PORT=6017  →  TCP 127.0.0.1:6017 LISTENING
                            "Attempt to create and connect to Assistant / Connected to RTI Assistant."
                            OK JOIN exec=BTK-PortProbe handle=6654
```

And the test that previously failed now passes — **both RTI versions live at once**:

```
TCP 127.0.0.1:6046 LISTENING      TCP 127.0.0.1:6050 LISTENING
4.6.1 (assistant 6046)  RTI version 4.6.1 … VC 14.0   OK JOIN handle=291
5.0.1 (assistant 6050)  RTI version 5.0.1 … VC 14.1   OK JOIN handle=3166
```

**Rule:** give every RTI version its own `RTI_ASSISTANT_PORT`, its own `RTI_RID_FILE`, and its own
`PATH`. MAK's guide adds one more: at install time, do **not** set either RTI as the default, and
make sure no RTI appears in the global `PATH` — the per-process environment must be the only thing
that decides.

### Why the rid alone is not enough

**Port 6003 is not settable from the rid.** The rid's `RTI_udpPort` / `RTI_tcpPort` /
`RTI_destAddrString` separate *federation data* traffic — and MAK's stock rids already separate
those per version:

| RTI | udp/tcp | destAddr | distributedForwarderPort |
|---|---|---|---|
| 4.6.1 | 4000 | 229.7.7.7 | 5000 |
| 5.0.1 | 5000 | 229.7.7.10 | 5004 |

Separating those is necessary but **not sufficient** — verified: with distinct rids on distinct
ports, 4.6.1 joined and 5.0.1 still failed, purely on the Assistant. Killing the Assistants and
running 5.0.1 alone with the identical rid worked immediately. ✅

~~**Practical rule.** Kill `rtiAssistant.exe` before a cross-version run.~~ **Superseded** — set
`RTI_ASSISTANT_PORT` per RTI instead, as above. Killing the Assistant was a workaround for a
problem that has a supported setting.

> This is the same failure seen earlier in the project with a stale **elevated** Assistant left
> running. It is not a one-off; it is structural.

---

## 4. Configuration parameters (`rid.mtl`)

The rid is the RTI's configuration file, pointed to by `RTI_RID_FILE`. It is where "which RTI do I
join" actually lives: ports, multicast group, forwarder address, whether an `rtiexec` is expected.

**The shipped rid is the authoritative parameter reference** — every setting appears with its
comment, which is more reliable and far faster to consult than the PDFs.

### Parameter count by version ✅

| Version | Parameters |
|---|---|
| 4.3 | 123 |
| 4.4.2 | 127 |
| 4.5.1 | 129 |
| 4.6.1 | 130 |
| 5.0.1 | 131 |

Yes — newer versions have more, but the growth is small and targeted, not a redesign.

### What changed, version by version ✅

| Step | Added | Removed |
|---|---|---|
| 4.3 → 4.4.2 | `RTI_automaticNameReserveEnabled`, `RTI_maxObjectsPerFederate`, `RTI_momVerboseLevel`, `RTI_nextEventRequestAndRetractServiceEnabled`, `RTI_strictHLA1516eAttributeOwnershipUnavailableCallback`, `RTI_waitIOPeriodForBundling`, `RTI_variableLengthDataUsesNull` | `RTI_IOLockQueue` |
| 4.4.2 → 4.5.1 | `RTI_fomModuleSorting`, `RTI_tcpNetworkInterfaceAddr` | — |
| 4.5.1 → 4.6.1 | `RTI_tcpPacketBundlingSize`, `RTI_udpPacketBundlingSize` | `RTI_packetBundlingSize` (split in two) |
| 4.6.1 → 5.0.1 | `RTI_enableFomComponentSorting`, `RTI_federationShadowEnabled` | `RTI_enableFomBackwardsCompatibility` |

### ⚠️ The one that matters for this project

`RTI_enableFomBackwardsCompatibility` **exists in 4.3–4.6.1 and is GONE in 5.0.1**, replaced by
`RTI_enableFomComponentSorting`. A rid copied forward from a 4.x install to 5.0.1 therefore carries
a setting the RTI no longer knows, and lacks the one it now wants. Anything that relies on FOM
ordering across the bridge should be checked on both sides. ❓

| Setting | 4.3 | 4.6.1 | 5.0.1 |
|---|---|---|---|
| `RTI_extend13and1516interop` | ✅ | ✅ | ✅ |
| `RTI_enableFomBackwardsCompatibility` | ✅ | ✅ | ❌ removed |
| `RTI_enableFomComponentSorting` | ❌ | ❌ | ✅ new |
| `RTI_useRtiExec` | ✅ | ✅ | ✅ |

### Settings this project relies on

```lisp
(setqb RTI_extend13and1516interop 1)      ;; one RTI serves HLA 1.3 AND 1516 at once - the
                                          ;; whole reason a single RTI can host both federations
(setqb RTI_enableFomComponentSorting 1)   ;; 5.0.1 only; needed when running mixed HLA versions
(setqb RTI_useRtiExec 0)                  ;; no rtiexec process required (peer-to-peer)
(setqb RTI_udpPort 4000) (setqb RTI_tcpPort 4000)
(setqb RTI_destAddrString "229.7.7.7")    ;; multicast group == which federation traffic you see
(setqb RTI_tcpForwarderAddr "127.0.0.1")
```

⚠️ Using MAK's **default** rid instead of the harness's fails with *"Failed to create a reliable
connection to the RTI Forwarder … RTI_internalMsgReliableWhenUsingRtiexec is enabled"*, because the
stock rid expects an `rtiexec`. Always pass `--rid`.

---

## 5. Environment ✅

| Variable | Purpose |
|---|---|
| `RTI_RID_FILE` | the rid to load — **decides which RTI you join** |
| `RTI_CONFIG` | folder the RTI resolves FOM files against |
| `MAKLMGRD_LICENSE_FILE` | FlexLM server, e.g. `27000@127.0.0.1` |
| `PATH` | must contain the chosen RTI's `bin` — this selects the *install* |

⚠️ `Environment.SetEnvironmentVariable` in .NET is **invisible to native `getenv()`**. The managed
side must also call `_putenv_s` (see `MakEnvironment`), or the RTI silently loads the wrong rid.
The native agents avoid the problem entirely: same CRT, so `_putenv_s` alone is enough.

⚠️ `RTI::RTIambassador` **connects inside its constructor**. A global instance dials the RTI during
static init, before `main()` can set `RTI_RID_FILE`. Construct it lazily.

---

## 6. Licensing ⚠️

- Dongle-locked, `FLEXID=9-4106D644`, expires **31-Mar-2027**.
- Pools: `vrl_run` **2 seats**, `makrtiLib` **14**, `mak_portal` 1, `portal_adp` 999.
- The native agents link the RTI directly, so they draw on `makrtiLib` (14) — **not** the 2-seat
  VR-Link pool. That is what lifted the two-agent ceiling.
- Exhaustion appears as FlexNet `-4,132 "Licensed number of users already reached"`.
- A vendor-daemon failure appears as `-97`. Seen twice; once fixed by `lmutil lmreread`, once by
  **plugging the USB dongle back in** (`lmutil lmhostid -flexid` returned empty).

---

## 7. Cross-RTI checklist

To bridge two RTIs with this harness:

1. Both RTIs installed, each with its **own rid** on distinct `RTI_udpPort` / `RTI_tcpPort` /
   `RTI_destAddrString`. MAK's stock rids already differ — start from those, not from one shared file.
2. Each RTI **version** gets its own `RTI_ASSISTANT_PORT` (§3). Not the same value, and not both
   left unset — unset means 6003 for both.
3. Per-federation `PATH` (the app sets this per agent process — each agent is its own process).
4. Licence server reachable; enough `makrtiLib` seats. Set `MAKLMGRD_LICENSE_FILE` **explicitly** —
   unset, older RTIs hang rather than fail (§2).
5. A VC mismatch only ever constrains ONE PROCESS and the library it loads. It never stops two
   applications interoperating (§2).

---

## 8. VR-Exchange ✅

### Which install ⚠️

| Install | Arch | Built | Can load |
|---|---|---|---|
| `vrexchange2.9` | **x64**, `bin64` only | **VC14** (linker 14.00) | VC14 / VC15 RTIs — 4.5.1(vc14), 4.6.1, 5.0.1 |
| `vrexchange2.5.1` | x86, `bin` only | VC10 | VC10 RTIs only — 4.3, 4.4.2, 4.5.1 |

Release notes 2.9: *"The VC 14 build is binary compatible with all MAK VC 15 products."* Users Guide
§5.3: *"The RTIs must be built with the same compiler as VR-Exchange."*

**The VC check is enforced and fatal.** A 2.9 broker pointed at a VC10 RTI:

```
Fatal Error: Mismatching RTI compiler version. Looking for: libRTI-NG_64.dll.
Found library in: C:\MAK\makRti4.5.1\bin\libRTI-NG_64.dll. Expected VC14 but found VC10
```

A broker cannot be rebuilt for a different VC either: `libRprHla13Broker.vcxproj` is
`PlatformToolset v140` and links `libPortal.lib`, `libBroker.lib`, `Qt5*.lib` and `$(MAK_VRLDIR)` —
VR-Exchange's own VC14 libraries. So **one VR-Exchange cannot span two VC islands**, and two
VR-Exchanges cannot help either: each owns its portal and shared-memory queues.

### Where the configuration lives ‼️

| File | Holds |
|---|---|
| `bin64\VR-Exchange.xml` | the **connections**: name, `.bcf`, broker exe, `startOnLaunch`, and the **per-connection environment** |
| `bin64\<name>.bcf` | one broker's settings: `execName`, `fedFileName`, `rprFomVersion`, `fomModules`, … |
| `%APPDATA%\MAK\RTI\<ver>\<host>\connections.xml` | the **RTI Assistant's** named RTI connections — tcp/udp/forwarder ports, and which RID configuration is `chosen` |

`vrx.exe -l <file>` selects an alternate connections file, `-G` runs headless, and `startOnLaunch`
is per connection — so the whole thing scripts without the GUI.

Per-connection environment is what selects the RTI (Users Guide §5.3.2): `RTI_RID_FILE`, `PATH`,
`RTI_ASSISTANT_PORT`. Confirm it actually applied by setting `brokersShouldDumpEnvironmnt` — the
broker then writes its entire environment into its log.

### 🚨 Config errors reach STDOUT ONLY

A malformed connections file is reported with file, line, reason and a caret:

```
BTK-Bridge.xml:17: parser error : Comment not terminated
DtCorruptedState: Error reading: Comment not terminated
```

…and **nowhere else**. Not in `-L <logfile>`, and not in `<logFilename>` — that setting lives inside
the file that failed to parse. Worse, the log then records a **normal-looking startup**; the only
tell is the queue names falling back to the defaults `CQ` / `IQ` / `OQ` instead of the configured
ones. **Always capture stdout when launching `vrx.exe`.** A clean log is not evidence the config
was read.

Related traps:

- **XML comments must not contain `--`.** Using it as a dash is what produced the error above.
- **Broker logs APPEND across runs.** Truncate first, or a `Joined federation` line from an earlier
  run reads as success this time. `tests/native/run-bridge.ps1` does this.
- Broker logs never print the RTI version. To know which library a broker really loaded, read the
  process's loaded modules — `(Get-Process rprHla1516eBroker).Modules`.

### Starting a broker by hand

The portal prints the command line it uses, so a broker can be run manually against a running
portal with any environment:

```
rprHla13Broker.exe -l "<name>.bcf" -t 5111 --controlQueueName BTKCQ ^
    --interactionQueueName BTKIQ --objectQueueName BTKOQ -E -G --brokerName "<name>"
```

Started without the `-t` / queue arguments it exits with *"Broker TCP Socket didn't connect in
time"* — that is the broker failing to reach the **portal**, nothing to do with the RTI.

### Translators and the FOM ⚠️

- The built-in RPR translators are written for **stock RPR**, so a stock-to-stock bridge needs no
  FOM Mapper. Custom attributes are another matter: **generic translators cannot cross HLA
  specifications** (Appendix A.1.2 — *"Generic messages do not work between the different HLA
  specifications"*, and the class must be identical in both FOMs). An HLA 1.3 → Evolved bridge is
  exactly the case that rules out, so unmodelled attributes need a **FOM Mapper**
  (`fomMapperLibName`) or they do not cross at all.
- VR-Link's RPR 2.0 mapping publishes **MAK-EXTENDED classes**. Observed:
  `Registered object BTK-STOCK-01 of class MAK_GroundVehicle(98)` — defined in
  `MAK-Physical-*_evolved.xml`, not in `RPR_FOM_v2.0_1516-2010.xml`. The tree is
  `GroundVehicle → NETN_GroundVehicle → MAK_GroundVehicle`, so a subscriber to an ancestor should
  still discover it by class promotion.

### Stock RPR 1.0 vs RPR 2.0, measured with our own parser ✅

| | RPR 1.0 (`MAK-RPR1-1-1.omt`) | RPR 2.0 (`RPR_FOM_v2.0_1516-2010.xml`) |
|---|---|---|
| classes / interactions / datatypes | 46 / 107 / 168 | 64 / 79 / 286 |
| `GroundVehicle` attributes | 44 | 46 |
| shared attribute names | — | **37, all encoding identically** |
| `EntityIdentifier` | `EntityIdentifierStruct` 6 B | same |
| `EntityType` | `EntityTypeStruct` 8 B | same |
| `Marking` | `MarkingStruct` 12 B | same |
| `WorldLocation` | `WorldLocationStruct` 24 B | **absent** — replaced by the `Spatial` variant |

The three shared attributes make a clean golden-file assertion (bytes in == bytes out), while
`WorldLocation → Spatial` is the field that proves real translation rather than relay.

Class paths differ by root: `BaseEntity.PhysicalEntity.Platform.GroundVehicle` (HLA 1.3) versus
`HLAobjectRoot.BaseEntity.PhysicalEntity.Platform.GroundVehicle` (Evolved).

---

## 9. The bridge, working ✅

`tests/native/run-bridge.ps1` runs the whole thing and exits 0. Configured by
`bin64\BTK-Bridge.xml` plus two `.bcf`, with `config/rid-fedA-rti451.mtl` and
`rid-fedB-rti501.mtl`.

```
  agent13 (RPR 1.0, HLA 1.3, RTI 4.5.1 vc14)
      -> FedA broker:  Discovered Entity: BTK-STOCK-01(4253024257) of class GroundVehicle(15)
      -> FedB broker:  Registered object BTK-STOCK-01(662700033) of class MAK_GroundVehicle(98)
      -> agent1516e (RPR 2.0, Evolved, RTI 5.0.1):  DISC obj=662700033 class=GroundVehicle
```

The object handle the FedB broker prints and the one our subscriber reports are the same
number, which is what makes this a chain rather than a coincidence.

| Attribute | Published on A | Received on B | |
|---|---|---|---|
| `EntityIdentifier` | `000100010065` | `000100010065` | identical |
| `EntityType` | `010100e101010000` | `010100e101010000` | identical |
| `Marking` | `0142544b2d53544f434b2d31` | `0142544b2d53544f434b2d31` | identical |
| `WorldLocation` | `c14491b8…4154fb1800000000` | *(no such attribute in RPR 2.0)* | → `Spatial` |

### 🚨 What actually blocked it: the FOM MODULE SET, not the federation execution

The VR-Exchange 1516e broker does **not** join with the bare RPR 2.0 FDD. VR-Link reads
`bin64\MAK-1516e-Config.xml` and brings **16 further FOM modules** to the join:

```
NETN-BASE.xml  NETN-Physical.xml  NETN-METOC.xml  NETN-MRM.xml
MAK-Physical-2_evolved.xml   MAK-Aerodrome-1_evolved.xml  MAK-METOC-1_evolved.xml
MAK-VRFExt-9_evolved.xml     MAK-DIGuy-7_evolved.xml      MAK-LgrControl-2_evolved.xml
MAK-VRFAggregate-6_evolved.xml  MAK-DynamicTerrain-2_evolved.xml
MAK-VRLExt-3_evolved.xml     MAK-DER-1_evolved.xml
RPR-Enumerations_Experimental_IFF.xml   RPR-MAK_Experimental_IFF-3.xml
```

A federate that joins with only `RPR_FOM_v2.0_1516-2010.xml` ends up with a different FDD
from the broker and **receives nothing at all — no exception, no warning, no log line.**
Passing the same 16 modules to `joinFederationExecution` (our agent's `joinfom=`) fixes it
outright; that one change is the difference between silence and a working bridge. The
modules must resolve to the broker's own copies, so the federation-B agent runs with
`RTI_CONFIG` pointed at `bin64`, not at `repo\config`.

This is the concrete instance of the general rule: **in HLA Evolved, "same FDD" means the
same module set, and a mismatch fails silently.** Worth checking first whenever an Evolved
federate joins cleanly and then sees nothing.

### ❌ `OK CREATE` without `existing=1` proves NOTHING — *corrects an earlier diagnosis*

An earlier read of this had the agents landing in a *different federation execution of the
same name*, because `createFederationExecution` kept succeeding instead of throwing
`FederationExecutionAlreadyExists` while the broker was already joined.

That inference was wrong. With `RTI_useRtiExec 0` there is no central authority to answer
"does this execution exist?", so create succeeds locally and `AlreadyExists` is simply never
raised. Both agents report a fresh create on **both** federations — and both federations
carry data end to end. Do not spend time on it; it is noise.

Ruled out by experiment along the way, worth not re-testing:

| Hypothesis | Verdict |
|---|---|
| Split federation execution (`OK CREATE` vs `existing=1`) | **No** — see above, the indicator is meaningless without rtiexec |
| Stale log evidence | No — logs truncated per run |
| Broker on the wrong RTI | No — loaded modules confirm `makRti4.5.1(vc14)\libRTI-NG_64.dll` and `makRti5.0.1\librti1516e64.dll` |
| Multicast discovery disabled | No — the LRCs find each other either way |
| MAK's stock rid instead of ours | Worse — `ConnectionFailed`, the rtiexec dependency of §4 |
| Assistant imposing a RID config (`chosen="0"`) | No effect |
| HLA class promotion | No — but see the subscription note below |

### Subscribe at the class you want reported ⚠️

The broker registers the MAK-extended `MAK_GroundVehicle`; the tree is
`GroundVehicle → NETN_GroundVehicle → MAK_GroundVehicle`. Class promotion works, but the
object is discovered **as the class you subscribed to**, and only that class's attributes
arrive:

- subscribe at `BaseEntity` → `DISC … class=BaseEntity`, and no `Marking` (it is declared on
  `PhysicalEntity`). Reads like a half-broken bridge; it is a misplaced subscription.
- subscribe at `GroundVehicle` → `DISC … class=GroundVehicle` with all four attributes.

### `WorldLocation` → `Spatial` needs the WHOLE dead-reckoning set ⚠️

RPR 2.0 has no `WorldLocation`; it was folded into the `Spatial` variant record,
discriminated by `DeadReckoningAlgorithm`. Publish `WorldLocation` **alone** on the RPR 1.0
side and the broker emits **no `Spatial` attribute at all** — the other three attributes
still cross, so the failure looks like a partial bridge. Publish the whole set
(`DeadReckoningAlgorithm`, `IsFrozen`, `Orientation`, `VelocityVector`, `WorldLocation`) and
a 60-byte `SpatialFPStruct` arrives:

```
02                        DeadReckoningAlgorithm = 2 (DRM_FPW) - the discriminant
00000000 000000           padding to 8-byte alignment
c14491b800000000
40934a0000000000          WorldLocation, 3 x float64  <-- byte-for-byte what A published
4154fb1800000000
00                        IsFrozen = false
000000                    padding
00000000 00000000 00000000    Orientation,    3 x float32
00000000 00000000 00000000    VelocityVector, 3 x float32
```

The 24 `WorldLocation` bytes reappear verbatim *inside* a record that does not exist in the
source FOM. That is the cleanest single piece of evidence that the broker translates
structure rather than relaying bytes — and it is the assertion `run-bridge.ps1` makes.

### 🚨 The broker NORMALISES Euler angles — plan golden files around it ✅

`Orientation` is the one field in `Spatial` that is not carried through untouched. Publish an
attitude outside the legal Euler ranges — psi and phi within ±π, **theta within ±π/2** — and
VR-Exchange normalises it. Measured, publishing psi/theta/phi = `1.0 / -2.0 / 0.5` rad:

```
sent      3f800000 c0000000 3f000000     1.0        -2.0        0.5
received  c0090fdb bf921fb5 c0290fdb    -2.1415927 -1.1415927  -2.6415927
```

The pitch is folded back inside ±π/2 and psi and phi are each shifted by π to preserve the
attitude. `WorldLocation` and `VelocityVector` in the same record are untouched, so this is
specifically angle normalisation, and it is correct behaviour, not a defect.

Consequence for the kit: **generated test data must keep theta inside ±π/2** or the golden file
fails on the broker's most defensible line of code. `tests/gen-volume-csv.ps1` restricts the
angles to ±1.5 and ±3.0 rad for exactly this reason.

### Volume behaviour ✅

4000 entities, one per row, published on A and verified on B one at a time:

```
4000 step(s): 4000 passed, 0 failed, 0 timed out  in 1589.9s
per-row round trip   min 0.356s   mean 0.397s   max 0.424s
```

The round trip is publish on A → broker translates → reflect on B → byte-compare four attributes,
`Spatial` included. **The spread is 68 ms across the whole run and row 4000 is as fast as row 1** —
4000 live objects in each federation cost the broker nothing measurable, and not one object was
lost or mismatched.

The portal's `maxObjectViewCount` (5000 by default, in `VR-Exchange.xml`) is the first ceiling
worth raising for a larger run.

### Bridge latency: ~43 ms, and it does not care how loaded the bridge is ✅

Send to receive — update handed to federation A, matching state observed on federation B — over
500 entities:

```
broker latency over 500 step(s):  min 16.6ms  mean 42.9ms  p50 47.5ms  p95 51.8ms  max 55.2ms
```

Two things worth reading off that:

- **It does not drift.** Bucketed 50 rows at a time the mean runs 42.3, 43.3, 44.8, 43.0, 44.2,
  41.1, 49.0, 38.5, 39.8, 42.8 ms. Five hundred live objects later the bridge is as quick as it
  was on the first row, which matches the 4000-row result above.
- **The spread is the shape of a POLL, not a queue.** Values sit roughly uniformly between ~17 ms
  and ~55 ms — a ~38 ms window — rather than clustering with a long tail. That is what a fixed
  broker tick looks like: how long a message waits depends on where in the tick it arrives, not on
  how much work is outstanding. `pollingInterval` in the `.bcf` is the knob to try if this matters.

The number is an **upper bound** on the broker: it is measured from outside, so agent A's own
dispatch is inside it. It is a small bound — the reflect on B routinely arrives before agent A has
finished printing its own `OK UPDATE`, which is what made an earlier attempt to measure from the
acknowledgement report a flat 0 ms.

### A dead broker does not fail the run by itself ⚠️

Every row has its own budget (`--timeout`, default 15 s). If the broker dies, publishing on A keeps
working — federation A is fine — nothing arrives on B, and each remaining row records `Timeout`
after waiting the full budget. Nothing in that picture distinguishes "the broker is gone" from
"these particular rows do not cross", so on a long run it is 4000 × 15 s of waiting to be told
something that was obvious after the third row.

`--abort-after N` stops the run after N consecutive timeouts. Off by default, because on a test
whose whole point is negative cases a run of timeouts can be the correct answer.
