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

**UI ✅** — implemented in the GUI's *FOM registry · Add a FOM* form (REQ-10); a pair then picks a
registered FOM per side. Selecting HLA 1.3 shows the `.fed`
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
crashed run still keeps every row that completed. Any past run reopens exactly as it looked (rows,
field-level diffs, latency) from the Dashboard, All results or *Runs of this test case* on its
result screen; *Export results…* writes it. The
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

## REQ-07 — Licence awareness ✅ (2026-11-05)

**Built** (`Engine/LicenceDiagnosis.cs`):
- **The common failure is silent.** With the licence server unreachable or `maklmgrd` down, a
  native agent connects to the RTI Assistant, loads the rid and then blocks inside the RTI waiting
  for a seat. It prints nothing. So `NativeAgentHost.Join` treats a JOIN timeout as the moment to
  run `lmutil lmstat -c <that agent's server>`. It then says which case applies:
  - no server answered (check the setting and lmgrd);
  - vendor daemon down (`lmutil lmreread`, re-seat the dongle);
  - the licence is fine, so look at the Assistant port, the rid or the FOM.
- **FlexNet codes are translated into words wherever they appear**: `-4,132` seats exhausted, `-97`
  daemon down, `-15`/`-96` no server, `-10` expired, `-5` feature missing. The CLI prints a
  `LICENCE:` line ahead of the stack trace. The GUI's error dialog puts the words first and the raw
  text underneath, and its banner uses the same advice.
- **Verified** with the licence server pointed at a dead port (27999): the CLI reported *"No MAK
  licence server answered at 27999@127.0.0.1. Check the licence server setting… and that the MAK
  License Manager (lmgrd) is running."* A normal run is unaffected.

Background, as first written:

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

**Evolved too ✅** — `btk_agent1516e.cpp` does the same on the B side, and the Engine spawns both
through `NativeAgentHost`. `tests/native/run-smoke.bat` proves the round trip on both federations,
and `tests/native/run-bridge.ps1` proves it through VR-Exchange.

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

## REQ-10 — WPF GUI ✅ (redesigned 2026-11-05)

`src/BrokerTestKit.Gui` is a task-shaped shell built around two saved objects, the **pair** and
the **test case** (PRP: [`PRPs/gui-redesign-pairs.md`](../PRPs/gui-redesign-pairs.md)). It holds
**no RTI connection of its own**: the agents are child processes, so the GUI stays VC-agnostic and
a native fault kills an agent, not the app.

```
Dashboard ──► Pairs ──► Pair details ──► Add / edit test case ──(save/cancel)──► Pair details
                              └──► Test case result ◄── (Run, Run again, any run anywhere)
Sidebar: Dashboard · Pairs   …   Tools: CSV template · FOM registry   …   [Test run box]
```

- **Dashboard**: library counts, last run, and the last 10 runs. On first launch it shows the
  empty states and the three first steps.
- **Pairs / Pair details**: a pair is one broker crossing, A → B. Each direction is its own
  pair. Pair details shows Send · A → VR-Exchange → Receive · B, then the test cases (Edit, Run,
  Delete), then recent runs. "Run all test cases" queues every test case of the pair.
- **New / edit pair**: covers both sides (FOM from the registry, federation, federate, RTI bin,
  rid, `RTI_CONFIG` folder, Assistant port) with a live check per side. It also has:
  - the join-modules box with its warning on an Evolved side;
  - the cross-version / port check (REQ-02);
  - an **Advanced** section with self-test and the shared licence server.

  Each side is saved as a connection row the pair owns (`pair-<id>-A/B`), so the run path is
  unchanged. Deleting a pair is a hard delete of the pair, its test cases and all their runs,
  behind a confirm that states the counts (D5).
- **Add / edit test case** (REQ-13): Send and Receive columns, with a class chip and a sheet card
  per class and **several classes per side**.
  - **+ Class** picks a class or interaction from that side's FOM and the columns to include,
    or uploads a CSV instead.
  - Each card has *Upload CSV…* (checks `# class:`), *Download template*, an editable grid and
    *+ Add row*.
  - Receive sheets get a **± tolerance** row (D2), read from and written back to the sheet's own
    `# tolerance:` directive.
  - Save validates exactly as a run reads the case (`TestCase.Load`: FOM resolution, unique ids,
    references, every Receive row tied to a Send row). Sheets are stored as content-addressed
    assets, so a test case is self-contained.
- **Test case result**: the live run filling in row by row, or any saved run. It shows:
  - a verdict strip with Passed / Failed / Timed out and latency p50 / p95;
  - a per-class line (REQ-13), and a per-class *parts* list for each row;
  - the field comparison and the agent transcript (live only);
  - Runs of this test case, and *Export results…* (CSV / HTML / JUnit, REQ-05).
- **All results**: filter by pair, result and test-case text, with a tally for the filter and
  25 runs per page.
- **Test run box**: Running (pair, test case, progress, pass/fail, what is queued) or Idle.
- **Rig banner**: shown only when something is wrong (D4). It covers `maklmgrd` down (checked
  with `lmutil lmstat`, which also blocks Run), VR-Exchange not on TCP 5111 (when a non-self-test
  pair exists), and a milder "can't check" when lmutil is missing. Checks run at start, before
  every run, and every 60 s.
- **Tools**: the CSV template tool (preview now inline) and the FOM registry, which gains a
  *Used by* column and a remove guard.

**Storage**: new tables `pair`, `test_case`, `test_case_sheet`, and `run_part` (per-class parts
of a step); `run` gains `pair_id` and `test_case_id`. A one-time migration (`migrated.pairs`)
groups every earlier run into a pair and a test case, importing the sheets where their paths
still exist and marking the case *"sheets missing — re-upload"* where they don't. GUI runs go
through `TestCaseRunner` (single-class cases included), so a GUI test case and a CLI `.testcase`
behave identically.

**Verified 2026-11-05** by driving the app with UI Automation on a copy of a real workspace:
- the migration grouped the old run into a pair and a test case;
- created a self-test pair, then built a 3-class test case by uploading the emitter-loopback
  sheets;
- **Pass 2/2** (Aircraft 2/2 · EmitterSystem 2/2 · WeaponFire 1/1, references resolved);
- after uploading the broken EmitterSystem sheet, **Fail 1/2** with both diffs;
- both runs show under Runs of this test case and on All results;
- the saved `# tolerance:` line is correct;
- the empty state and the delete confirm match the boards.

**Stop, verified through the broker**: the migrated 12-row GroundVehicle case ran over
VR-Exchange at about 19 ms per row. Stop after row 7 gave "Aborted", the 7 completed rows saved,
the rest marked *Not run*, and the run box back to Idle.

**Not verified**: the licence-down banner blocking Run. That needs the vendor daemon stopped,
which takes the licence away from everything on the machine. The same probe is verified through
the CLI (REQ-07).

**Remaining polish:** a FOM-diff view between the A and B models, and generating a matched
sender + receiver template pair from a pair in one step.

---

## REQ-11 — Build environment ✅

`msbuild HLABrokerTranslationTestKit.sln -p:Configuration=Debug -p:Platform=x64` plus
`build-native.bat` builds everything; SETUP.md has the fresh-clone steps.

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

## REQ-13 — Multi-sheet test cases ✅

A **test case** is one behaviour of the broker to check, and it may span **several classes on
each side**: N Send sheets on A, M Receive sheets on B. The motivating case is a one-to-many
translation, where one class on A becomes two on B:

| Send · A (RPR 1.0) | Receive · B (RPR 2.0) |
|---|---|
| `Aircraft` with `IFFCode1` | `Aircraft` **and** a separate `IFF` object whose `HostObjectIdentifier` points back at it |

One-to-many mappings like this are where a broker most easily goes wrong, and today they cannot
be tested at all.

**Built ✅ (2026-11-05) — engine + CLI.** `BrokerTestKit.Cli.exe case <file.testcase>
[--profile <file> | --selftest] [--timeout s] [--report …]…` (`Engine/TestCase.cs`,
`Engine/TestCaseRunner.cs`). The single-sheet `entity` mode is unchanged.

1. **The test-case unit is a manifest** — a small text file, sheet paths relative to it. Every
   sheet is an ordinary REQ-03 entity CSV and resolves against its own side's FOM. Object and
   interaction sheets mix freely.
   ```
   name:    aircraft-iff
   send:    aircraft-in.csv
   receive: aircraft-out.csv
   receive: iff-out.csv
   ```
2. **Rows group into steps.** One step = publish every Send row in the group, then wait for every
   Receive row in it.
   - A Send row joins the group of its own `entityId`, unless it holds a reference, in which case
     it joins the group of the row it references (an emitter joins its host's step).
   - A Receive row joins the step of the Send row with the **same entityId**. Only when there is
     none (an IFF object the broker creates out of an aircraft's `IFFCode1`) does it follow its
     reference instead. So a wrong reference fails in the right step rather than moving the row.
   - A group with no Receive rows is a negative test. A Receive row tied to no Send row is a load
     error. In a test case an `entityId` may appear once per sheet.
3. **Reference cells — the cross-class correlation.** `@<entityId>` (or `@<Class>/<entityId>` if
   ambiguous) in an `RTIobjectId` column (`HostObjectIdentifier`, `FiringObjectIdentifier`,
   `EmitterSystemIdentifier` …) means *the RTI instance name of that row's object on this side*.
   On A it is the name the harness registered; on B it is the name of whichever object **matched
   that row on B**, because VR-Exchange re-registers everything under its own names. The check is
   "B's IFF points at B's aircraft", never a string compare against what A sent. References
   resolve within the step, or to a row matched in an earlier step. `@@` escapes a literal `@`.
   ✅ Encoded as NUL-terminated ASCII (RPR 2.0 declares `RTIobjectId` an `RPRnullTerminatedArray`
   of `HLAASCIIchar`) and compared with trailing NULs ignored. **Confirmed across the broker for
   RPR 1.0's `RTIObjectIdStruct` too**: VR-Exchange read our RPR 1.0 `FiringObjectIdentifier` as
   the shooter's name and re-issued it on RPR 2.0 (see *Verified* below).
4. **The verdict waits for every expected class.** Each observed object or interaction can satisfy
   only one Receive row. Step outcomes:
   - **Pass**: every Receive row matched.
   - **Timeout**: nothing of any expected class arrived.
   - **Fail**: anything in between. *A missing IFF fails the aircraft's step* even though the
     aircraft arrived intact.
5. **Reporting** = test case → class → row → field. Each step carries one *part* per Receive row
   (`Pass Aircraft 1:1:101 matched object 12 'X'`, `Timeout IFF iff-1 no IFF arrived`). The run
   carries a per-class tally (expected / matched / wrong / never arrived). Both appear in the
   console, the CSV header, the HTML page (a class table plus parts per row) and the JUnit
   `<properties>` / `<system-out>`.

**Verified** on a loopback self-test over RTI 5.0.1 with `tests/cases/emitter-loopback/`
(gitignored test data, like all FOM-derived CSVs). It has three Send sheets (`Aircraft`,
`EmitterSystem` → `@aircraft`, `WeaponFire` interaction → `@aircraft`) and the matching Receive
sheets.
- `emitter-loopback.testcase`: all pass, exit 0, references resolved on B.
- `emitter-loopback-broken.testcase`: exit 1. It has:
  - an emitter pointing at the wrong aircraft, with a wrong `EmitterType` (Fail, both diffs listed);
  - an `EmitterBeam` nobody publishes (Timeout part; the step Fails while its aircraft and emitter
    match).

**Verified across the broker ✅ (2026-11-05)** with `tests/cases/shooter-bridge/` over
`config/bridge-stock-rpr.profile` (stock RPR 1.0 / RTI 4.5.1 → VR-Exchange 2.9 → stock RPR 2.0 /
RTI 5.0.1): a `GroundVehicle` plus the `WeaponFire` it shoots, `FiringObjectIdentifier=@1:1:1`, two
steps.
- `shooter-bridge.testcase`: exit 0. Both classes match in every step, and B's WeaponFire names
  B's copy of the shooter. Latency is about 365 ms per step.
- `shooter-bridge-wrongref.testcase` (fire-1 expected to name the other shooter): exit 1. Only
  that WeaponFire row fails; its shooter still passes.

⚠️ On this rig VR-Exchange **kept A's instance names** on B (`BTK-1-1-1` on both sides), so this
run cannot show the "B's own name" resolution doing anything a string compare would not. The
resolution is still the right check, because the name is not guaranteed to survive.

**Still not verified:** the motivating Aircraft → Aircraft + IFF one-to-many case. Stock RPR 1.0
has no IFF object class, so it waits for the customised FOMs and broker mapping (FOM-HANDOVER).

Building this found and fixed an HLA 1.3 agent bug: attribute handles are per-class, and
declaring a second class used to rename the first class's attributes (agent README, "HLA 1.3
specifics").

**UI ✅ (2026-11-05)** — the Add / edit test case and Test case result screens of the redesign
(REQ-10): several classes per side, one sheet card per class, a per-class parts list per row, and
per-class tallies. Interaction sheets mix with object sheets.
