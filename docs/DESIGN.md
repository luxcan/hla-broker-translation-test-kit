# HLA Broker Test Kit — Design Notes

> Status: **exploratory / pre-implementation.** This document records what we know
> and the decisions still open. Update it as facts get confirmed.
> Last updated: 2026-06-29.
>
> See also: **[APP-REQUIREMENTS.md](APP-REQUIREMENTS.md)** — numbered app requirements.
> See also: **[vr-link-knowledge.md](vr-link-knowledge.md)** — distilled VR-Link API
> reference (consult for any VR-Link API detail).
>
> ## ⚠️ CORRECTION 2026-08-13 — read before §4/§9
> The **Universal HLA layer does NOT work** for send/receive in an ordinary federate
> (verified empirically; control-tested against MAK's own talk/listen). §4 and §9 below
> chose it as the primary API — **that choice is superseded.** The harness uses the
> **typed path**: `ReflectedEntityList` + `EntityStateRepository` decoded properties, with
> `StateRepository.getAttribute(name)` for *unmapped* attributes only. Details and evidence:
> [vr-link-knowledge.md §3 / §3a](vr-link-knowledge.md).
>
> **Consequences (good):** VR-Link decodes mapped RPR attributes for us, so the byte codec —
> previously "the main build effort" — is largely **unnecessary**. FOM renames are absorbed by
> the **FOM Mapper config**, so harness code stays unaffected. The **CSV schema should be
> SR-field-based** (`worldPosition.x`, `damageState`, …), not raw FOM-attribute-based.
>
> ## 🚨 CORRECTION 2 (2026-08-13) — harness MUST be MULTI-PROCESS
> A/B tested: a single process holding TWO ExerciseConnections **joins fine but cannot move
> data** (observer saw 0 entities; segfault/AccessViolation). One connection = works
> perfectly. `ManagedInterface` is a per-process singleton. → **One agent process per
> federation**, IPC-coordinated by the WPF GUI (mirrors VR-Exchange's own Portal + per-broker
> process design). Evidence: [vr-link-knowledge.md §3y](vr-link-knowledge.md).
>
> **Status:** PRP-1 ✅ and PRP-2 ✅ both PASS on VR-Link 5.10 + MAK RTI 5.0.1.
> Environment/toolchain is settled; see §3/§4.1.

## 0. Requirement clarification (2026-08-13) — CUSTOMISED FOMs

The tool verifies **broker-to-broker translation between CUSTOMISED FOMs**:
**customised RPR 1.0  →  customised RPR 2.0** (not stock RPR).

This splits every attribute into two classes, and the harness must handle BOTH:

| Attribute kind | Arrives as | Codec |
|---|---|---|
| **Standard RPR** (WorldLocation, EntityType, Marking, …) | decoded typed properties on `EntityStateRepository` | none — VR-Link decodes |
| **CUSTOMISED / extended** (site-specific additions) | **not modelled** by VR-Link → generic `StateRepository.getAttribute(name)` as raw `byte[]` | **ours** (encode/decode per FOM datatype) |

⚠️ This partially reverses the earlier "the byte codec is largely unnecessary" note: it is
unnecessary for *standard* RPR, but **required for the customised attributes** — which are
exactly the ones most likely to regress and therefore the main point of the tool.

VR-Link announces unmodelled attributes explicitly, e.g. on the current stock FED:
`Can't add Decoder for Attribute DIGuyBodyWeight in object class ObjectRoot.DIGuyObject`.
Expect the same for site customisations.

**Inputs still needed from the project:**
1. the **customised RPR 1.0 `.fed`** and **customised RPR 2.0 `.xml`** actually used by the broker;
2. what is customised (added attributes / new classes / renames);
3. whether a **custom VR-Link FOM Mapper** exists, or all customisations are handled generically;
4. a few real translation mappings (custom attr on A -> custom attr on B) to seed golden files.

The FOMs currently in `config/` are **stock placeholders** used to bring the plumbing up.

## 1. Goal

Build a tool that **automatically verifies a VR-Exchange broker's FOM-to-FOM
translation** every time the broker changes, so regressions are caught before
they reach production.

The broker under test translates **HLA 1.3 → HLA Evolved (IEEE 1516-2010)**.

## 2. Test concept

Golden-file, black-box regression test driven by two CSV files:

```
                input.csv                         output.csv
                   │                                  ▲
                   ▼                                  │  (compare)
        ┌──────────────────┐    broker     ┌──────────────────┐
        │  Federate A      │  (under test) │  Federate B      │
        │  Federation A    │ ────────────► │  Federation B    │
        │  (HLA 1.3)       │   translate   │  (HLA Evolved)   │
        └──────────────────┘               └──────────────────┘
            pump in                            verify received
```

- **input.csv** = the data pumped into Federation A.
- The app joins Federation A as **Federate A** and Federation B as **Federate B**.
- Federate A publishes per `input.csv`; the broker translates A → B.
- Federate B receives and **verifies** against **output.csv**.
- A pumps in, B verifies. The two federates work in sync.

The broker is treated as a **black box** — the harness is independent of the
broker's source so it tests the real runtime behaviour.

## 3. Environment / vendor facts

- Vendor: **MAK RTI** — version **4.6.1 (VC14)**, chosen to match VR-Link 5.9 (VC14).
  ⚠️ RTI 4.3/4.4.2 are VC10 and incompatible with VR-Link 5.9 (PRP-1 proved this). The
  broker must be a **VC14** VR-Exchange. See §4.1.
- VR-Exchange install: `C:\MAK\vrexchange2.5.1`.
- Translation direction: **HLA 1.3 → HLA Evolved (1516-2010)**.
- **FOMs (confirmed): RPR FOM 1.0 (HLA 1.3, Fed A) → RPR FOM 2.0 (HLA Evolved, Fed B).**
  Both ship with MAK (no download). Files copied into `config/`:
  `MAK-RPR1-1-1.fed` (RPR 1.0) and `RPR_FOM_v2.0_1516-2010.xml` (RPR 2.0, released SISO).
- License: FlexLM server `MAKLMGRD_LICENSE_FILE=27000@127.0.0.1` (covers federation + hours).
- Target test-kit platform: Windows, GUI app (WPF) — see §4.1.

## 4. Language / API decision

| Layer | C# support? | Notes |
|-------|-------------|-------|
| Bare MAK RTI (raw HLA API) | **No** | HLA standard defines only C++ and Java bindings. |
| **MAK VR-Link** | **Yes** | Has a documented C# binding; protocol- and version-independent (same code for 1.3 and Evolved). |
| Third-party (coreDS C#) | Yes | Alternative, supports all RTIs/HLA versions. |

**Decision:** build the harness in **C# on VR-Link**. VR-Link's C# binding gives us
"one app, two HLA versions" (Federate A on 1.3, Federate B on Evolved) and the
same publish/reflect patterns the broker itself uses.

**CONFIRMED (2026-06-29, local install `C:\MAK\vrlink5.9`, since replaced by 5.10 / 5.9.1):** VR-Link 5.9 ships a
C# binding (`C-Sharp/`, `bin64/managedInterface*HLA13/HLA1516/HLA1516e.dll`) **and a
generic/universal HLA layer** that gives FOM-agnostic, raw access in pure C# — no
C++/CLI shim needed. Key classes (see `doc/reference/csharp`):

- `UniversalHlaObjDiscoveryMessage` — `objClass`, `objName` (any class, generically).
- `UniversalHlaInteractionMessage` — `objClass` + `byte[] getAttribute(string name)`
  / `setAttribute(string name, byte[] val)` (internally `Dictionary<string,byte[]>`).
- `makVrl.ExerciseConnection` routes them: `addMessageHandler`,
  `handleUniDiscoveredMessage`, `handleUniInteractionMessage`.

So attributes are read/written **by name as raw `byte[]`** — fully generic and
rename-proof. The typed path also exists (state-repository wrappers, e.g. the
`laserDesignator` C# example, generated via `C-Sharp/bin/messageCodeGen-cs.exe`), but
the harness does **not** need it.

> **License: confirmed available (2026-06-29)** — VR-Link license is in place and also
> covers the federation execution + runtime hours. Not a blocker.
> Version note: broker is built against VR-Exchange 2.5.1 (older VR-Link); the
> harness is independent and targets the installed **VR-Link 5.9** C# binding.

### 4.1 Platform & project layout (DECIDED 2026-06-29)

Hard constraints, verified from the install: `vrLinkSharp.dll` is **x64 (Amd64)**, CLR
`v4.0.30319`, a **mixed-mode C++/CLI** assembly over the native `managedInterface*.dll`.
The official C# examples target **.NET Framework 4.6.2 / x64 / AllowUnsafeBlocks**.

- **Runtime/target:** **.NET Framework 4.6.2, x64, Windows-only.** *Not* .NET 5+/MAUI/Avalonia
  (mixed-mode C++/CLI won't load reliably off .NET Framework). All projects build **x64**.
- **GUI framework:** **WPF** (MVVM). Chosen for live results-grid binding, per-row
  pass/fail/timeout updates, and a diff view; clean separation from the engine.
- **Project layout (engine separate from GUI — CLI-first):**
  - `Engine` — class library: the two `ExerciseConnection`s, the §9.1 serialization loop,
    the codec, CSV parse/compare. No UI. The reusable core.
  - `Cli` — headless runner over `Engine` (exit non-zero on mismatch, JUnit/HTML report) → **CI**.
  - `Gui` — thin **WPF** shell over `Engine` for authoring/debugging.
  - (later) `Engine.Tests` — unit tests for the codec/compare logic.
  - `config/` — harness-owned **`rid.mtl`** + the FOM files (`.fed`/`.xml`). `RTI_CONFIG`
    points here. The MAK install RIDs are left untouched. Key RID choices: keep
    `RTI_extend13and1516interop 1` (broker bridges versions); `configureConnectionWithRid 1`
    for headless determinism; lightweight (no rtiexec) by default with documented
    reliable / single-host-isolation upgrades. **RTI = MAK RTI 4.6.1 (VC14)** — chosen to
    match VR-Link 5.9 (VC14); broker must be VC14 too. Make it active via the RTI Chooser;
    CLI also prepends its `bin` to `PATH`. POC launchers in `C:\Users\projAdmin\Desktop\POC`
    (open-rti / start-rtiexec / select-rti-4.6.1).

## 5. FOM file formats

| | Federation A (HLA 1.3) | Federation B (HLA Evolved) |
|---|---|---|
| File the RTI loads | **`.fed`** | **`.xml`** (FDD), also `.fdd` |
| Format | Lisp-like parenthesized text | XML against the 1516 XSD |
| Root class | `ObjectRoot` | `HLAobjectRoot` |
| Datatypes inside file? | **No** | **Yes** |
| Modular? | Single file | **FOM modules** + MIM (`HLAstandardMIM.xml`) |

- **OMT** (Object Model Template, IEEE 1516.2) is the *format spec*, not a file the
  RTI consumes. A `.omt` is typically a FOM-authoring tool's project file; you
  **export** `.fed` / `.xml` from it.
- **Encoding implication (the hard part):** the 1516 `.xml` is self-describing
  (datatypes included) so side B can be data-driven from it. The 1.3 `.fed` has
  **no datatype info** — side A's byte layout must come from OMT datatype tables,
  the RPR-FOM spec, or a hand-authored datatype map.

## 6. VR-Exchange architecture (from examining `examples/` and `include/portal/`)

Hub-and-spoke around a canonical **Portal**; each protocol is a spoke:

```
Fed A (HLA 1.3) ──plugin──► Portal (canonical) ◄──plugin── Fed B (HLA Evolved)
```

- Ships ready brokers in `brokers/`: `disBroker`, **`rprBroker`** (HLA-RPR),
  `ddsBroker`. User work = **translators/plugins** layered on these
  (e.g. `examples/brokerPlugin` extends disBroker; `examples/timeMgmtHlaBroker`
  uses rprBroker).
- Translation is **hand-written C++** on State Repositories — either the base class
  does it and you override `translate()` to tweak, or you copy fields explicitly.
- `vrexchange2.5.1/translations/` is **Qt localization** (`.qm`/`.ts`), *not* FOM
  translation — ignore it.

### Two translation paths (this decides the CSV schema)

1. **Typed / abstracted** (`DtEntityStateRepository`, `setLocation()`…): FOM
   class/attribute names are bound to VR-Link concepts in the **FOM Mapper config**,
   not in the C++. CSV columns = SR fields (`location.x`, `velocity.y`, …).
2. **Generic name/value** (`DtPortalGenericObject` = `className` string +
   `GenericAttributeValuePairList`; `pGenericInter.h` for interactions):
   FOM-agnostic. Maps directly to a CSV of `(className, attributeName, value)` rows.
3. **Fully custom Portal objects** (`examples/modPortalApp`: `pFruit`, `pBoomInter`)
   — a third option if neither fits.

> **Q1 — which path does the production broker use? — NO LONGER A BLOCKER (2026-06-29).**
> Not yet confirmed, but it doesn't matter: the harness uses the C# **Universal HLA
> layer** (generic, see §4), so it works regardless of the broker's internal path.
> The harness can even *reveal* the path empirically (pump rich input, see what
> survives on B).

### Time management

`examples/timeMgmtHlaBroker` shows VR-Exchange can run **time-regulating +
time-constrained** (`enableTimeRegulation`, `enableTimeConstrained`,
`timeAdvanceRequest`, GALT/LBTS queries, `enableAsynchronousDelivery`), and brokers
coordinate LBTS across the Portal.

- If the harness federates are **time-managed**, verification becomes deterministic
  lockstep (A publishes at logical time T → B receives in TSO → verify at grant).
  No flaky wall-clock waits → reproducible CI.
- If the broker path is **RO / wall-clock**, fall back to a **settle-window +
  timeout** ("expected output never arrived = FAIL").

> **OPEN Q2 — is the broker's translation path time-managed or RO/wall-clock?**

## 7. FOM-rename workflow (dev vs prod)

Plan: rename FOM attribute names for development (e.g. `platform.aircraft` →
`platform.XXX`), revert to original for production. **Byte arrays do not change.**

Verdict: **sound** — names are only lookup keys; the wire carries handles + bytes,
which are name-independent. Conditions:

- Rename must be applied **consistently** everywhere a name is resolved: the FOM
  file(s), the **broker's FOM Mapper config / translation mapping**, and the
  federates' publish/subscribe declarations.
  - Generic path: names are pure data → trivially rename-proof.
  - Typed path: rename lives in the **FOM Mapper config** (not the C++, not bytes).
- **Do not** rename standard/RPR well-known names that tooling special-cases.
- Keep class/attribute renames only; leave datatype names alone (or update all refs).
- Preserve attribute order / class hierarchy so handles stay consistent.
- **Best practice:** keep the *original* FOM as source of truth in version control;
  **generate** the renamed variant as a derived dev artifact via a single bijective
  rename table. Production just uses the original — never "mutate then revert"
  (corruption risk).

## 8. What "changing the broker" means for regression

Regressions originate in two places, both caught by the golden-CSV harness:

1. **Custom translator C++** — an edited `translate()` override or SR field copy.
2. **FOM Mapper config** — a changed FOM-name ↔ concept mapping (also where the
   rename workflow lives).

## 9. Proposed harness shape

**Decision (2026-06-29): build on the VR-Link C# Universal HLA layer** (generic,
FOM-agnostic, rename-proof — see §4). No dependence on the broker's internal path.

- **Federate A** (C#/VR-Link, HLA 1.3): for each `input.csv` row, build a
  `UniversalHla*` object/interaction, `setAttribute(name, bytes)` per attribute,
  publish.
- **Federate B** (C#/VR-Link, HLA Evolved): handle universal discovery/interaction
  messages via `ExerciseConnection.addMessageHandler`, `getAttribute(name)`, compare
  to `output.csv`.
- **CSV schema (generic):** `(objClass, objName/id, attrName, value [, datatype])`
  rows. Same shape for objects and interactions.
- **The codec is the real work:** attributes cross as `byte[]`. Need a datatype-driven
  encoder/decoder — Fed B from the FDD `.xml` (self-describing); Fed A from an external
  1.3 datatype source (OMT/RPR/hand-authored). See §5.
- **Correlation:** need a stable key surviving translation (marking / callsign /
  name) to match an output row to its input stimulus.
- **Comparison:** per-field tolerance (epsilon) for floats/coords, not string
  equality; declared expectations for enum remaps and derived fields.
- **CLI-first:** a headless runner (exit non-zero on mismatch, JUnit/HTML report)
  so it runs in **CI**; the GUI sits on top for authoring/debugging.
- **Self-test:** loopback (A publishes, A reads back) so failures point at the
  broker, not the rig.
- **Scope to cover explicitly:** objects *and* interactions, attribute updates vs
  full discovery, deletions/removes, dropped/unmapped fields (assert they're
  dropped), and negative tests (data the broker should filter).

### 9.1 Verification model — app-level serialization (DECIDED 2026-06-29)

Process the input **one row at a time, synchronously**, driven by the test app (no HLA
time management needed):

```
for each input row:
    flush/drain B's pending input          # so a straggler can't be mis-attributed
    A publishes the row
    loop drainInput(A) + drainInput(B):
        if B's received state MATCHES this row's expected output  -> PASS, break
        if elapsed > X                                            -> TIMEOUT/FAIL, break
    record result (pass / fail / timeout + actual decoded values)
proceed to next row
```

**This makes Q2 moot for correctness** — determinism comes from serializing at the
driver level, which works in pure C# regardless of whether the broker is time-managed.

Rules that make it robust (do not skip):
1. **Match-or-timeout, not a fixed sleep.** Proceed the instant the expected result
   matches; only hit X on failure. (Fixed X-second waits make the suite needlessly slow.)
2. **Wait on CONTENT, not "a callback fired."** HLA delivers heartbeats / dead-reckon
   resends (~5 s) and late stragglers. The wait condition is "the reflection matching this
   row's expected **identity + changed attributes** has arrived," compared to `output.csv`.
3. **Negative tests need a separate, shorter timeout.** Drop-expected rows always burn the
   full timeout (nothing ever arrives), so a big X there dominates runtime.
4. **Flush B between rows** (quiescence) so prior-row stragglers aren't counted.
5. **Tune X above the real worst case:** A→RTI + broker processing/tick + RTI→B. Make X
   configurable; **log near-timeout passes** as an early flakiness warning.

**State model (must choose):** HLA objects persist across rows.
- (a) **Independent rows** → reset/delete instances between rows (costly), or
- (b) **Cumulative scenario/timeline** → output expectations are cumulative. ← natural fit.
  Note partial updates: a row changing only position reflects only position; decide whether
  to compare only-changed attributes or full accumulated state.

Interactions are transient/stateless → no persistence problem; just capture the matching
interaction within X. Objects are the hard part.

## 10. Open questions / next steps

- ~~Q1 — broker translation path~~ — **resolved as non-blocking** (§6): harness is
  generic via the Universal layer.
- ~~Q2 — time-managed vs RO verification~~ — **resolved via app-level serialization**
  (§9.1): one row at a time, match-or-timeout. No HLA time management needed; the C#
  time-mgmt gap no longer matters. (Caveats live in §9.1.)
- ~~Q3 — C# raw/generic HLA access~~ — **CONFIRMED** present in VR-Link 5.9 C# (§4).
- ~~VR-Link C# licensing~~ — **confirmed available** (covers federation + hours) (§4).

Resolved since this list was written (2026-11-02):
- ~~0 — do Fed A & Fed B share one RTI?~~ — **No, and it does not matter.** The harness is
  multi-process exactly as predicted: one native agent per federation, each with its own RTI
  `bin`, rid and `RTI_ASSISTANT_PORT`, held together by a bridge profile. Proven across RTI 4.5.1
  (vc14) ↔ 5.0.1 through VR-Exchange 2.9 — mak-rti-knowledge.md §9.
- ~~1 — the byte[] codec~~ — **done, FOM-driven.** Datatypes, offsets, alignment padding,
  variant records (`--variant`) and dynamic arrays (`--array`) all come from the parsed
  `.omt` / FDD. The CSV never states a type.
- ~~2 — PRP-1…4~~ — **done.** Both federations, loopback, and through the broker with
  wait-or-timeout and measured latency.

Open:
- **Linked objects (parked 2026-11-02 — 1:1 testing first).** RPR links an aircraft to its
  emitters by REFERENCE, not containment: `EmbeddedSystem.HostObjectIdentifier` and
  `EmitterBeam.EmitterSystemIdentifier` are `RTIobjectId` — an RTI object-instance NAME. The kit
  today runs one class per run and has no reference value type. The trap for when this is
  built: VR-Exchange re-registers every object on federation B under its OWN names, so the
  correct check is "B's emitter points at B's copy of the aircraft", never a string compare of
  what A sent. Needs multi-class runs (host registered first), a reference column type (e.g.
  `@1:1:1` = "the instance of row 1:1:1"), and resolution on both sides before comparing.
- **Late join** — measured, see mak-rti-knowledge.md §10. Open there: the federation-B result
  assumes a passive consumer (neither agent calls `requestAttributeValueUpdate`).
- **Delete / remove** (§9 scope) — the agents implement `DELETE` and the removal callback, but no
  CSV syntax or runner step drives it.
- ~~**Run history**~~ — **done 2026-11-02**: every GUI run is saved row by row; History reopens it,
  Export writes CSV (APP-REQUIREMENTS REQ-05).
- **Fresh clone** — licensed inputs are gitignored by design; the copy-in steps are not written
  down yet.

## 11. Source references (in `C:\MAK\vrexchange2.5.1`)

> ⚠️ 2.5.1 is the x86 / VC10 install, a dead end for running anything (see CLAUDE.md). These paths
> are for reading example source only; the broker under test is VR-Exchange **2.9**.

- `examples/simpleBroker/` — minimal DIS↔Portal entity/fire translators (typed path).
- `examples/brokerPlugin/` — plugin extending disBroker; `modDataInterTrans` shows
  base-class `translate()` + override pattern.
- `examples/modPortalApp/` — custom Portal object/interaction (`pFruit`, `pBoomInter`).
- `examples/timeMgmtHlaBroker/` — HLA time-managed broker (1.3 / 1516 / 1516e paths).
- `examples/modRprBroker/` — RPR-FOM broker (HLA) — **review next**.
- `include/portal/pGenericObject.h` — generic className + attribute/value-pair object.
- `brokers/` — shipped `disBroker`, `rprBroker`, `ddsBroker`.
