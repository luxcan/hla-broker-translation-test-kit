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

**Where it stops.** Anything VC10 (4.3, 4.4.2, plain 4.5.1) cannot be loaded by a VC14-built
binary — that is the *"Mismatching RTI compiler version. Expected VC14 but found VC10"* failure.
No `PATH` change fixes it; it needs either an agent compiled with the VC10 toolchain or MAK's
`vc14` build of that RTI. ⚠️

**Consequence for the app:** to bridge RTI 4.6 and RTI 5.0 you need **both RTIs installed** (DLLs,
licence, rid) but only **one agent binary**. Per-federation `RTI bin` + `RID file` settings are
enough. Bridging to a VC10 RTI additionally needs a second agent build.

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

**Port 6003 is NOT settable from the rid.** The rid's `RTI_udpPort` / `RTI_tcpPort` /
`RTI_destAddrString` separate *federation data* traffic — and MAK's stock rids already separate
those per version:

| RTI | udp/tcp | destAddr | distributedForwarderPort |
|---|---|---|---|
| 4.6.1 | 4000 | 229.7.7.7 | 5000 |
| 5.0.1 | 5000 | 229.7.7.10 | 5004 |

Separating those is necessary but **not sufficient** — verified: with distinct rids on distinct
ports, 4.6.1 joined and 5.0.1 still failed, purely on the Assistant. Killing the Assistants and
running 5.0.1 alone with the identical rid worked immediately. ✅

**Practical rule.** For a test harness, do not let the Assistant participate:
kill `rtiAssistant.exe` before a cross-version run, or keep the two RTIs on separate hosts.
❓ *To verify:* whether a documented switch disables the Assistant connection outright — no
`RTI_*assistant*` setting exists in any rid on this machine, and the string does not appear as a
setting name inside `libRTI-NG_64.dll`.

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
2. Both in the **same VC island**, or build a second agent for the other island.
3. **Kill `rtiAssistant.exe`** before the run, or accept that only one RTI version works at a time.
4. Per-federation `PATH` (the app sets this per agent process — each agent is its own process).
5. Licence server reachable; enough `makrtiLib` seats for the number of agents.
