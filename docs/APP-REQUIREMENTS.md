# App Requirements (living doc)

> What the finished tool must do, as agreed. Numbered so we can refer to them.
> Companion docs: [DESIGN.md](DESIGN.md) (architecture + rationale),
> [vr-link-knowledge.md](vr-link-knowledge.md) (API facts + evidence),
> [FOM-HANDOVER.md](FOM-HANDOVER.md) (what the project side still owes, with samples).
> Status key: ✅ built · 🔨 in progress · ⏳ not started · ⛔ blocked

---

## REQ-01 — FOM file inputs depend on the HLA version ✅

When the user configures a federation, the app asks for different files per HLA version:

| User selects | App must ask for | Why |
|---|---|---|
| **HLA 1.3** | **`.fed`** *(required)* | the RTI loads this to create/join the federation |
| | **`.omt`** *(required for custom attrs)* | **the `.fed` contains NO datatypes**; the OMT carries them |
| **HLA Evolved (1516-2010)** | **`.xml`** FDD *(required)* | carries BOTH structure **and** datatypes (`<dataType>`) |
| | additional **FOM module `.xml`** *(optional, 0..n)* | merged at **join** — see the rules below |
| | **MIM `.xml`** *(optional)* | default comes from the RTI. Absent → "MOM is not available" warning |

**Rationale / evidence**
- `.fed` datatype count is literally zero (`grep -ci datatype MAK-RPR1-1-1.fed` → `0`); a row is
  only `(attribute WorldLocation best_effort receive)`.
- The `.omt` (DIF text) has `(Attribute (Name "WorldLocation") (DataType "WorldLocationStruct"))`
  plus `(ComplexDataType ... (ComplexComponent (FieldName "X") (DataType "double") ...))`.
- The 1516 FDD `.xml` has `<dataType>WorldLocationStruct</dataType>` inline.
- Confirmed against the API: the **1.3** initializer exposes only `fedFilename`; the **Evolved**
  initializer exposes `fedFilename` + `fomModules` (list) + `mim`.

### ⚠️ FOM module rules — verified against MAK RTI 5.0.1, 2026-08-18

Both were found the hard way while building the Evolved agent. Both must shape the UI.

1. **Modules must be supplied at JOIN, not at CREATE.** Passed to
   `createFederationExecution(name, {fdd, module})` the extra modules are **silently dropped** —
   the federation comes up with only the *first* file and the custom attributes simply are not
   there, with no error at all. Proven by reversing the order: with `{module, fdd}` the custom
   attributes resolved and the stock `EntityIdentifier` did not. Pass them as
   `joinFederationExecution(..., additionalFomModules)` instead.

2. **A module may add NEW SUBCLASSES only.** It may not add attributes to a class another
   module already defines. Re-declaring `BaseEntity` with extra attributes gives:
   *"Failed merging object classes. Object class's info for HLAobjectRoot.BaseEntity does not
   match other module"*. The existing class must appear as pure **scaffolding** (name only) with
   the new attributes on a new subclass — see `config/BTK-CustomRPR2-module.xml`.

**Consequence for the app — now decided (2026-08-18):** this project's customised RPR 2.0 adds
attributes to *existing* RPR classes, so it is delivered as a **single merged FDD**. The Evolved
side therefore needs only `fed=<merged FDD>`; `fomModules` stays supported but is not the primary
path. The app should still accept both shapes and say plainly which one it is dealing with.

**UI ✅** — implemented on the GUI's *Federations* tab (REQ-10). Selecting HLA 1.3 shows the `.fed`
and the `.omt` labelled "REQUIRED for datatypes"; selecting Evolved swaps to the FDD and reveals the
FOM-modules and MIM fields. 1516-only fields are never offered to a 1.3 federation. Loading a FOM
without the `.omt` on a 1.3 side fails with *"the .fed contains no datatypes at all"* rather than
silently producing an empty model.

**UI notes**
- `.omt` is only strictly needed if the test touches **custom / unmodelled** attributes; standard
  RPR fields are decoded by VR-Link. Ask for it, but allow "not supplied" with a clear warning.
- Validate on selection: `.fed` must parse as DIF-ish text; `.xml` must contain `<objectModel`.
- Remember paths per profile so users don't re-pick every run.

---

## REQ-02 — Per-federation connection configuration ✅

Independently for Federation A and Federation B: HLA version, federation (exec) name,
federate type/name, FOM files (REQ-01), RPR version, RTI selection.
The two sides are configured **separately** — they may differ in HLA version and FOM.

**Shape: a named RTI connection, tagged with its FOM.** The GUI's *Connections* screen registers
one entry per way into a federation — FOM + RTI install + rid + exec name + federate name — named
in the project's own convention, e.g. `Customised RPR 1.0 (sample) · RTI 5.0.1`. A run then picks
two connections, source and target. Persisted in the `connection` table.

Why the FOM belongs *inside* the connection rather than beside it: a federation execution is
**created with a FOM**, and every federate joining has to supply a compatible one, so a connection
that did not name its FOM would only be half an answer. Naming it there also settles the HLA
version, and with it which agent binary runs — `btk-agent13.exe` or `btk-agent1516e.exe`. One
fewer thing to get wrong at run time.

**Cross-version guard.** Picking two connections on different RTI *versions* warns before the run
and asks for confirmation: they collide on the RTI Assistant's fixed TCP 6003
(see [mak-rti-knowledge.md](mak-rti-knowledge.md) §3). The failure it otherwise produces —
*"Invalid message version from Assistant"* — does not point at the cause.

---

## REQ-03 — Golden-file CSV: one row per entity or interaction ✅

**One class per file. One column per attribute. One ROW per entity or interaction.**
The sheet reads like a list of CGF entities, which is how a test author thinks about it, and
row N is one step — publish, then verify — which is exactly the agreed verification model.

```csv
# class: Aircraft
# kind: object
# tolerance: WorldLocation.*=0.5, BtkCustomValue=0.001
entityId,Marking.MarkingData:string,WorldLocation.X,WorldLocation.Y,BtkCustomValue
1:1:101,TANK-01,-2696048.0,1234.5,1234.5
1:1:102,TANK-02,100.25,200.5,-0.5
```

- **No datatype column.** The `.omt` (1.3) / FDD (Evolved) already states every attribute's type
  and every struct field's offset. Restating it in the test data is duplication that silently
  drifts out of step with the FOM — and did: a hand-written table said
  `EntityIdentifier.site`, while the FOM actually says
  `EntityIdentifier.FederateIdentifier.SiteID`. Types now come from the FOM (REQ-09).
- **Sub-fields are addressed by name**: `WorldLocation.X`, `EntityIdentifier.EntityNumber`.
  Several columns of one attribute are assembled into a single value before sending, so
  `WorldLocation.X/.Y/.Z` is three columns but one 24-byte attribute and one reflect.
- `entityId` is the correlation key between the input and expectation sheets. It is not sent as
  an attribute; it pairs rows up and names the entity in the report.
- **Empty cell** = "do not set" on input, "do not check" on output.
- A row present in the input with no matching expectation row is a **negative test**.
- Input and expectation column names **may differ** — that difference *is* the translation.
- Directives: `# class:` (required), `# kind:` (object|interaction), `# tolerance:`
  (per column, `Attr=0.5`, wildcards `WorldLocation.*=0.5`, or a bare default).
- **One escape hatch**: `Attr:kind` in a header overrides interpretation, needed only when the
  FOM says opaque bytes (`octet[0+]`) and the author means text. It changes only HOW bytes are
  read, never WHERE — the offset still comes from the FOM.

Parser: `EntityCsvParser`. Codec: `ValueCodec`. The legacy long-form CSV (`step,attribute,value,
datatype` per row) still parses via `CsvParser` for the `native` CLI mode, but the entity format
is the one the GUI generates and the one to use.

---

## REQ-04 — Verification semantics ✅

Per DESIGN §9.1: quiesce B → publish step on A → **match-or-timeout on CONTENT** → record.
Never a fixed sleep. Per-field tolerance. Separate (shorter) timeout for negative tests.
Warn when a pass lands near the timeout (flakiness early-warning).

---

## REQ-05 — Results & CI ✅

Per-step Pass / Fail / Timeout with field-level diffs (`expected`, `actual`, `delta`, `tol`).
Non-zero process exit on any failure so it can gate CI.

**Kept and exportable ✅ (2026-11-02).** Every GUI run is saved as it happens - one record per run,
one per row, one per field compared - in the workspace's SQLite store, so a stopped, aborted or
crashed run still keeps every row that completed. *History…* on the Run tab reopens any past run
exactly as it looked (rows, field-level diffs, latency); *Export results…* writes it as CSV. The
CLI's `--report` and the GUI export share one writer (`Engine/RunReportCsv.cs`), so the two files
read the same: `#` header lines with the pair, both sheets and the summary, then
`row,entityId,outcome,seconds,latencyMs,message,failedFields`. Written with a UTF-8 BOM so Excel
opens it correctly. The agent transcript is not kept in history.

`Fail` and `Timeout` are deliberately distinct: **Fail** means an object arrived on B and the
values were wrong (the broker is alive and translating incorrectly); **Timeout** means nothing
arrived at all (the broker is silent, or a subscription is wrong). Different defects, different
first thing to check.

**Three formats, one rule ✅ (2026-11-05).** The file extension picks the format, in the CLI and
the GUI alike (`Engine/RunReportFormats.cs`, `RunReportWriter.Write`):

| extension | format | for |
|---|---|---|
| `.xml` | **JUnit XML** | CI (Jenkins, GitLab, Azure DevOps). One `<testcase>` per row. |
| `.html` / `.htm` | **HTML** | People. One self-contained page (no scripts, no external files): summary tiles, latency, run context, every row with failing rows expanded to their field diffs. Follows light/dark. |
| anything else | **CSV** | Excel, diffs (as above). |

JUnit keeps the Fail/Timeout distinction below: **Fail → `<failure type="Fail">`**, **Timeout →
`<error type="Timeout">`**, so CI counts them apart. An aborted run (`--abort-after`) adds one
synthetic `run aborted` test case with an `<error type="Aborted">`, otherwise a run that gave up
after row 2 of 4000 would show green in CI while the CLI exits 1. Field results go to
`<system-out>`; the run context and latency summary go to `<properties>`. The suite is named after
the expected sheet.

`--report` may repeat, so one run can write all three:
`… --report run.xml --report run.html --report run.csv`. In the GUI, *Export results…* offers the
three as file types.

Verified on the WeaponFire self-test (RTI 5.0.1): all-pass (exit 0), two wrong parameters (one
`<failure>` listing both diffs, exit 1), and A→B with no broker running and `--abort-after 2`
(two `<error type="Timeout">` + `run aborted`, exit 1). The JUnit XML is well-formed. The GUI
export path compiles but has not been clicked through.

Entry point: `BrokerTestKit.Cli.exe entity <input.csv> <expected.csv> --profile <file> [--report out.csv|.xml|.html]...`.

---

## REQ-06 — One federation per agent process ✅

VR-Link's `ManagedInterface` is a per-process singleton: two connections in one process join
but **cannot move data** (segfault / AccessViolation). So the app spawns **one agent process per
federation**, coordinated over IPC. GUI/Engine hold no RTI. Evidence: vr-link-knowledge §3y.

---

## REQ-07 — Licence awareness ⏳

Each agent consumes one VR-Link `vrl_run` seat; the site licence has **2** (= exactly A + B).
A third concurrent VR-Link process fails with FlexNet `-4,132`.
The app should detect and report licence failures clearly rather than surfacing raw FlexNet text.
Licence is **runtime**, dongle-locked (`FLEXID=9-4106D644`), expires **31-Mar-2027**.

**The native agents change this ceiling.** They link the RTI directly and consume `makrtiLib`
(**14** seats), not `vrl_run` (2). Once both sides are native the 2-agent limit is gone — and the
finished tool needs no VR-Link licence at runtime at all.

---

## REQ-08 — Custom (unmodelled) attributes ✅ BOTH federations

The broker translates **customised** RPR 1.0 → **customised** RPR 2.0, so custom attributes are
the main thing this tool exists to verify.

**Was blocked in C#.** The C# VR-Link binding exposes no FOM Mapper / encoder, so publishing an
unmodelled attribute returned OK locally and silently never reached the wire.

**Solved with a native raw-RTI agent** — [`src/BrokerTestKit.AgentNative/btk_agent13.cpp`](../src/BrokerTestKit.AgentNative/btk_agent13.cpp).
Raw HLA 1.3 has no encoder gap: `AttributeHandleValuePairSet::add(handle, buffer, length)` takes a
plain byte buffer, so the agent puts **exact bytes** on the wire and reads **exact bytes** back.
No FOM mapper, no dead reckoning, no thresholding between the test and the wire.

Proven end-to-end by `tests
ative
un-smoke.bat` (exit code 0):

| attribute | kind | bytes | round-trip |
|---|---|---|---|
| `BtkCustomValue` | **custom** (not in any RPR mapper) | 8 | identical |
| `BtkCustomTag` | **custom** (not in any RPR mapper) | 13 | identical |
| `WorldLocation` | stock struct | 24 | identical |
| `EntityIdentifier` | stock struct | 6 | identical |

Remaining: the same agent for **HLA Evolved** (B side), and a `NativeAgentHost` so the Engine
spawns it. `AgentHost` already takes the exe path as an argument, so nothing there is language-bound.

---

## REQ-09 — FOM-agnostic addressing ✅

No FOM is compiled into the app. Classes, interactions and attributes are addressed **by name**
at runtime, and datatypes are **read as data**.

**Agent side ✅** — `CLASS name=… attrs=…` resolves everything by string via
`getObjectClassHandle` / `getAttributeHandle` and moves opaque `byte[]`. An attribute missing
from the FOM produces a precise diagnostic, never a silent no-op.

**FOM side ✅** — `OmtParser` (HLA 1.3 DIF) and `FddParser` (1516-2010 XML) build a `FomModel`
that answers *"where does `WorldLocation.X` live and how do I read it?"*:

| source | classes | interactions | datatypes |
|---|---|---|---|
| `BTK-CustomRPR1.omt` | 46 | 107 | 168 |
| `BTK-CustomRPR2.xml` | 64 | 79 | 286 |

Resolution follows the full chain — attribute → datatype → simple/enum → basic representation —
computes field offsets for fixed records, and **refuses** to address into variable-length or
variant records rather than guessing (`Spatial` is a `SpatialVariantStruct`, so it compares as
whole-attribute bytes). Standard HLA/RPR basic types are built in, so a MIM is not required just
to know `HLAfloat64BE` is eight bytes.

Inspect any FOM with `BrokerTestKit.Cli.exe fom <file> [class] [attr.path …]` — use this to check
a customised FOM is readable *before* writing tests against it.

Renaming a FOM attribute requires **no rebuild**: new FOM file, regenerate the template, done.

---

## REQ-10 — WPF GUI ✅ built · 🔨 polish

`src/BrokerTestKit.Gui` — a four-tab shell over the Engine. It holds **no RTI connection of its
own**: the agents are separate processes, so the GUI stays VC-agnostic and a native fault kills an
agent rather than the app.

### 1 · Federations
Per-federation config for A and B. **REQ-01 is enforced in the UI**: choosing HLA 1.3 shows the
`.fed` *and* the `.omt` (with "REQUIRED for datatypes" on the label); choosing Evolved swaps to the
FDD and reveals the FOM-modules and MIM fields. 1516-only fields are never shown to a 1.3
federation. Pre-fills from `RTI_CONFIG` so the app is usable the moment it opens.

### 2 · FOM → CSV template  *(the core authoring loop)*
Load FOM → filterable list of every **object class and interaction** → tick attributes/parameters
(each row shows the FOM datatype and what it resolved to, e.g. `float64@8  8B`) → **Generate both
sheets**. Structs expand to addressable leaf fields; a *Preview* window shows the raw template text
before committing. Row count and default float tolerance are settable.

### 3 · Test data
Both sheets in editable grids, with the `#` directive block preserved across a load/save round
trip. **Editing in-app is the point**: Excel silently strips the leading zeros from hex IDs
(`000100010065` → `100010065`), and a golden file that is quietly wrong is worse than none.

### 4 · Run
Live per-row grid, colour-coded Pass / Fail / Timeout, field-level diff pane for the selected row
(expected · actual · note), and the full agent transcript. Runs on a background thread with row-by-
row updates marshalled through the Dispatcher, a Stop button, and a self-test checkbox that puts
both agents on Federation A for an identity mapping. Licence exhaustion (`-4,132`) is recognised
and explained in words, with the raw text kept underneath (REQ-07 partly satisfied).

*Export results…* and *History…* (REQ-05) keep and reopen every run.

**Remaining polish:** a FOM-diff view between the A and B models, and generating a matched sender +
receiver template pair from the selected connections in one step.

---

## REQ-11 — Build environment ⏳

- **.NET target is `v4.7.2`.** The constraint from `vrLinkSharp.dll` is only *CLR v4*; 4.7.2 was
  chosen because it is the oldest targeting pack installed on the dev machine. Any CLR v4
  framework works — drop back to 4.6.2 if you install its Developer Pack.
- **Native agents build separately** via `src/BrokerTestKit.AgentNative/build-native.bat`
  (not part of the `.sln`, which is AnyCPU/x64 C#). The C# side finds them by walking up to
  `build/native/`; a missing binary raises a clear error naming the build script.

---

## REQ-12 — Interactions ✅

A test row can be **one interaction** as easily as one entity — identical sheet shape, columns
are the interaction's parameters instead of the class's attributes.

```csv
# class: WeaponFire
# kind: interaction
entityId,FiringObjectIdentifier:string,FireMissionIndex,FuseType,WarheadType
fire-1,SHOOTER-01,42,1000,2000
```

**FOM + template ✅** — both parsers read interaction classes and parameters with inheritance
(107 interactions in the `.omt`, 79 in the FDD). They resolve through the same
`FomModel.Resolve`, so offsets and datatypes work identically to attributes, and
`CsvTemplateGenerator` emits `# kind: interaction` sheets.

**Agent path ✅** — both native agents gained three commands and two output lines:

| in | out |
|---|---|
| `INTCLASS name=<X> params=<a,b,c> mode=pub\|sub\|pubsub [as=<alias>]` | `INT class=<X> params=<n>` |
| `SETPARAM class=<X> name=<p> <value>` | `PARAM class=<X> name=<p> len=<n> hex=<…>` |
| `SENDINT class=<X> [tag=<t>]` | |

Parameters **stage then flush** on `SENDINT`, mirroring `SETATTR`/`UPDATE`, so one CSV row is one
interaction on the wire. Verified byte-identical on both federations.

**Runner ✅** — `NativeTestRunner` branches on the sheet's `# kind:`. Objects and interactions
differ only in where the bytes come from; the match-on-content rule is shared, which matters
because an interaction has **no instance identity at all** — there is nothing to register, nothing
to discover, and nothing to correlate by except the values themselves.

Proven end to end (`entity tests/weaponfire-input.csv tests/weaponfire-output.csv --selftest`):
3 interactions, 12 parameter comparisons, all pass in 1.7 s — and verified to fail correctly,
reporting `interaction arrived on B but 2 of 4 field(s) differ` with per-parameter diffs.

---

## REQ-13 — Multi-sheet test cases ⏳

A **test case** is one behaviour of the broker to check, and it may span **several classes on
each side**: N Send sheets on A, M Receive sheets on B. The motivating case is a one-to-many
translation, where one class on A becomes two on B:

| Send · A (RPR 1.0) | Receive · B (RPR 2.0) |
|---|---|
| `Aircraft` with `IFFCode1` | `Aircraft` **and** a separate `IFF` object whose `HostObjectIdentifier` points back at it |

One-to-many mappings like this are where a broker most easily goes wrong, and today they cannot
be tested at all.

**Today:** a run takes exactly one input sheet and one expected sheet, one class each
(`EntityCsvParser.ReadInput` / `ReadExpected`, [Program.cs](../src/BrokerTestKit.Cli/Program.cs) ~l.1053),
and rows correlate by `entityId` within that one class.

**Needs:**
1. **A test-case unit** that names its Send sheets and Receive sheets (a small manifest, or
   several `# class:` blocks in one file); each sheet still resolves against its own side's FOM
   exactly as in REQ-03/REQ-09.
2. **Cross-class correlation** — a Receive row can be tied to a Send row through a reference
   attribute (e.g. `IFF.HostObjectIdentifier` → the aircraft's `EntityIdentifier`), not only by
   its own `entityId`.
3. **A verdict that waits for every expected class**: a Send row passes only when all of its
   Receive rows, across all classes, have matched within the timeout (REQ-04). A missing `IFF`
   is a failure of the `Aircraft` row, not a silent pass.
4. **Reporting** (REQ-05) per test case, then per class, then per row and field.

**UI** — drawn in the *screen redesign* canvas: Pairs → Pair details → *Add / edit test case*
(class chips per side, one sheet per class) → *Test case result*. Interaction sheets (REQ-12)
should be allowed in the same test case as object sheets.
