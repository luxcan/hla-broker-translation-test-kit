# PRP — GUI redesign: Dashboard → Pairs → Test cases → Result

> **PRP** = product requirement prompt: everything an implementer (human or agent) needs to build
> this in one pass, including the context, the order of work and how to prove it works.
> Written 2026-11-04. Source of truth for the look:
> the canvas **"HLA Broker Translation Test Kit — screen redesign"**
> (https://claude.ai/artifact/TJeAJ4MY9G2Etzx654igHq, private to the owner).
> Its boards: **main flow** Dashboard → Pairs → Pair details → Add / edit test case → Test case
> result; **second row** Dashboard first launch, All results, New / edit pair, Delete pair —
> confirm; **third row** FOM registry · Add a FOM, CSV template. Every screen in §3 has a board.
> Related requirements: REQ-03/04/05/07/10/13 in [docs/APP-REQUIREMENTS.md](../docs/APP-REQUIREMENTS.md).

---

> **Status 2026-11-05: built.** T1–T10 are done, with REQ-13 folded in: several classes per side,
> run through `TestCaseRunner`. Deviations from the plan:
> - `pair.conn_a`/`conn_b` are plain names, not foreign keys, so a migrated pair can name a
>   removed connection and still open;
> - a `run_part` table keeps per-class results;
> - "Run" is blocked only when the licence daemon is down, and only warned about when the broker
>   is down (a self-test pair needs no broker).
>
> Checklist item 4 (Stop in the middle of a run) is verified through the broker. Item 7 (licence
> down blocks Run) is not, because it needs the vendor daemon stopped. See APP-REQUIREMENTS REQ-10.

## 1. Goal

Replace the GUI's five positional tabs (FOM registry / Connections / Test data / Run / CSV template)
with a task-shaped flow built around two new saved objects, the **pair** and the **test case**:

```
Dashboard ──► Pairs ──► Pair details ──► Add / edit test case ──(save/cancel)──► Pair details
                              │                                                     
                              └──► Test case result ◄── (Run, Run again, any run on the Dashboard)
Sidebar:  Dashboard · Pairs  ……  Tools: CSV template · FOM registry  ……  [Test run status box]
```

## 2. Why

- Today a "pair" exists only as the A/B combos picked on the Run tab, and a "test case" is two
  loose CSV paths the user re-opens every time. Nothing ties a run back to *what* was tested, so
  history can't answer "how has *this* behaviour done over time?".
- The tabs are ordered by implementation, not by the job. Users think in *which broker crossing*
  (pair) → *which behaviour* (test case) → *did it pass* (result).
- REQ-13 (multi-sheet test cases) needs a test-case unit to exist first. This PRP creates it with
  one sheet per side, so REQ-13 becomes an extension, not a rewrite.

## 3. What — user-visible behaviour (one section per screen on the canvas)

### 3.1 Sidebar (every screen)
- Brand, then **Dashboard**, **Pairs** (Pairs stays highlighted on Pair details, Add/edit test case
  and Test case result).
- **Tools** (bottom): *CSV template* and *FOM registry* — today's two tabs, restyled, behaviour
  almost unchanged (§3.11).
- **Test run box** (bottom-left), two states:
  - *Running*: badge "Running", pair name, test case name, progress bar, `7 / 12 rows · 7 pass ·
    0 fail`, next queued test case. Click → that run's Test case result screen.
  - *Idle*: grey badge "Idle", **"No test running"**, "Runs you start from a pair show their
    progress here." Not clickable.
- The *Rig status* box (licence · broker) is **not** in the sidebar. Those signals appear as a
  **warning banner only when something is wrong** (§3.8, decision D4).

### 3.8 Rig warning banner (every screen, D4)
- Hidden while healthy. Shown full-width at the top of the main area, amber, with a one-line cause
  and a fix, e.g. **"MAK licence daemon is down — runs will stall. Run `lmutil lmreread -c
  27000@127.0.0.1`."** or **"VR-Exchange is not running — nothing will reach B."**
- Checks (new — the GUI does none of this today; it only stores the licence server and decodes
  `-4,132` after a failure):
  1. **Licence:** `C:\MAK\MAKLicenseManager\lmutil.exe lmstat -c <licence server>` must report
     `maklmgrd: UP` (lmgrd up is not enough — CLAUDE.md).
  2. **Broker:** vrx listens on **TCP 5111** once licensed and started.
- When: at app start, before every run (a failing check blocks *Run* with the same message), and
  every 60 s while idle. Off the UI thread; result marshalled back.

### 3.2 Dashboard
- **Library** card: `FOMs n · Pairs n`, links *FOM registry* and *All pairs*.
- **Last run** card: outcome icon, `Pass · 12 / 12`, `pair · test case · dd MMM yy HH:mm`.
- **Last 10 test runs** table, newest first: When · Pair · Test case · Result · Latency p50 · Open.
  Result colours: pass = accent + tick, fail = red + cross, timed out = amber + clock.
  Pair name → Pair details; *Open* → Test case result for that run.
- **Empty state (first launch)**: Library `0 · 0` greyed; Last run **"No runs yet" / "Your most
  recent result will appear here."**; runs table "No test runs yet" + three numbered first steps
  (Add your FOMs → Create a pair → Add a test case and run it) + buttons *Add a FOM* /
  *Create your first pair*. Header button reads *Create your first pair* when there are no pairs.

### 3.3 Pairs
- Title + one-line explanation; *Find a pair* search box (name, FOM or RTI; filters as you type).
- Table, one row per pair: Pair (name) · Send · A (HLA badge, FOM, RTI) · → · Receive · B (same) ·
  Test cases (count) · Last run (outcome) · When · Open. Row/name click → Pair details.
- *+ New pair* link at the bottom of the table (no big header button). No direction filter chips.
- Each **direction is its own pair** (A→B and B→A have different test cases).

### 3.4 Pair details
- Breadcrumb `Pairs / <pair>`; title; `n test cases · last run <date> · <outcome>`.
- Buttons: *Edit pair*, *Run all test cases*, primary *+ Add test case*.
- Three cards: Send · A (FOM, HLA/RTI, federation · port) → VR-Exchange → Receive · B (same, plus
  `16 join modules` when Evolved).
- **Test cases** table: Name (→ result) · Send → Receive classes · Rows · Last result ·
  Actions (*Edit* → Add/edit test case, *Run* → runs it and opens its result). *+ Add test case* under
  the table too.
- **Recent runs** for this pair, *All results* link.

### 3.5 Add / edit test case
- Breadcrumb `Pairs / <pair> / Add test case` (or `Edit test case`).
- **Details**: Name, Timeout per row (s). No float tolerance here (D2).
- **Tolerance is per field, on the Receive sheets only** (D2): a "± tolerance" row directly under
  the column headers, editable only for float columns, blank = exact. It reads from and writes to
  the sheet's existing `# tolerance:` directive (e.g. `WorldLocation.*=0.5`), so no storage change.
  New float columns are pre-filled from the CSV-template default (0.001).
- **Send** (on A · HLA · FOM) and **Receive** (on B · HLA · FOM) side by side. Each: class chip(s)
  with ×, *+ Class*, and per class a sheet card (*Upload CSV…*, *Download template*, editable grid,
  *+ Add row*). Classes come from that side's FOM (reuse the template tab's class picker).
- Excel warning line (leading zeros / dates).
- Sticky footer: *Cancel* and *Save test case* (edit mode: *Save changes*). **Both return to Pair
  details**; Save validates first (§5 task T6).
- Phase 1 limit: **one class per side** (*+ Class* disabled once one is chosen, tooltip "More than
  one class per side arrives with REQ-13"). The canvas shows two Receive classes — that is REQ-13.

### 3.6 Test case result
- Breadcrumb `Pairs / <pair> / <test case>`; *Edit test case*, primary *Run again*.
- Send · A → broker → Receive · B summary (FOM, HLA · RTI · federation, sheet + row count).
- Verdict strip: outcome + `12 rows in 9.1 s · <date>`; Passed / Failed / Timed out / latency
  p50 / p95; *Export CSV* (existing `RunReportCsv`).
- **Rows** grid (# · Entity · Result · Fields n/n · Latency) and **field comparison** for the
  selected row (Field · Expected · Received · ✓/✗) — today's `ResultGrid` + `FieldGrid`.
- *Agent transcript* expander (live runs only; transcripts are not persisted today).
- **Runs of this test case**: When · Result · Duration · View; the shown run is highlighted.

### 3.9 New / edit pair (D1)
- Breadcrumb `Pairs / <pair> / Edit pair`; *New pair* opens the same screen empty, titled
  *New pair*, with no *Delete pair*.
- **Pair name**, suggested from the two FOMs (`<FOM A> → <FOM B>`), free to type over.
- **Send · A** and **Receive · B** cards side by side, a "VR-Exchange 2.9 translates" arrow between.
  Each card: HLA badge (follows the FOM), *FOM — from the registry* (+ *Manage FOMs* →
  FOM registry), Federation (exec) name, Federate name, then **RTI**: bin folder, RID file,
  FOM search folder (`RTI_CONFIG`, blank = the rid's folder), RTI Assistant port ("must differ from
  the other side"), and a status line with *Re-check* (`RTI found · rid parses · FOM resolves`).
- Evolved side only: a **Join modules** box — `16 modules from bin64\MAK-1516e-Config.xml`,
  *Edit list*, and the warning "Without them B joins cleanly and receives nothing" (CLAUDE.md, §9).
- Under the cards: the `CrossVersionWarning` / port check as one line (tick when fine, amber when not).
- **Advanced** (collapsed): *Self-test* (moved here, §3.7) and **Licence server (shared by every
  pair)** — it moves here from the old Connections tab; still one value in `setting`.
- Sticky footer: *Delete pair…* (red outline, left) · *Cancel* · *Save pair*. Cancel / Save return
  to Pair details (to Pairs for a new pair).
- *Delete pair…* opens the **confirm dialog** (board *Delete pair — confirm*): title "Delete this
  pair?", the D5 sentence with the real counts, "Export any results you want to keep first.
  Uploaded sheets stay on disk.", buttons *Keep pair* and red *Delete pair and 41 runs* (count in
  the label). Afterwards → Pairs.

### 3.10 All results
- Reached from *All results* on the Dashboard (unfiltered) and on Pair details (pre-filtered to
  that pair). Breadcrumb `Dashboard / All results`; sidebar highlights Dashboard.
- Filters: **Pair** (All pairs), **Result** (Any result / Pass / Fail / Timed out), **Test case**
  text search.
- Card header `Runs · 41` and a tally `Pass 22 · Fail 14 · Timed out 5` for the current filter.
- Table as the Dashboard's plus **Duration**: When · Pair · Test case · Result · Latency p50 ·
  Duration · Open. Paged (`1–25 of 41`, *Previous* / *Next*), newest first.

### 3.11 Tools: FOM registry and CSV template
- **FOM registry** — title + *Add a FOM* button (scrolls to / focuses the form). Left: *Registered
  FOMs* table Name · HLA · Files · Contents (parse status; amber + reason when a file is missing) ·
  **Used by** (`n pairs`, new), toolbar *Re-check all* / *Remove*. A FOM used by a pair cannot be
  removed (footnote says so). Right: **Add a FOM** form — Name, HLA version toggle, then the
  per-version files (REQ-01: `.fed` + required `.omt` for 1.3; FDD `.xml`, join modules, optional
  MIM for Evolved), *Register* / *Clear*. Every *Add a FOM* link in the app lands here.
- **CSV template** — unchanged behaviour: *From* FOM picker + class/interaction counts; class tree
  with filter; attributes grid (include, Name, FOM datatype, Variant / elements, Resolved) with
  *All* / *None*; *Generate*: rows to pre-create, default float tolerance (0.001 — the same default
  §3.5 pre-fills), *Download template…*, *Preview template text* (shown inline below the buttons).

### 3.7 Removed
- Connections tab as a top-level page (its editor becomes the pair editor, §5 T5).
- *Import bridge profile…* and *Export profile* buttons (the CLI keeps `--profile`;
  `BridgeProfile.cs` stays).
- *Self-test* checkbox on the main path (move to the pair editor as an advanced option; it is a rig
  check, not a test).
- "Step N of 4" wizard and the stepper — the flow is navigation, not a wizard.

### Formatting rules
- Every timestamp: `dd MMM yy HH:mm` (e.g. `02 Nov 26 22:08`), local time, `CultureInfo.InvariantCulture`
  month names. Never-run shows `—`.

## 4. Success criteria
- [ ] Every screen in §3 exists and matches its canvas board in layout and copy (dark theme tokens, no hard-coded hex).
- [ ] A user can: create a pair → add a test case (pick class each side, fill or upload sheets) →
      save → run it from Pair details → see the result → open an older run of the same test case.
- [ ] Every run is linked to its pair and test case in `btk.db`; the Dashboard, Pair details and
      result screen read from those links.
- [ ] Existing `btk.db` files open without data loss; existing runs appear on the Dashboard
      (pair inferred, test case "Imported" — §5 T3).
- [ ] First launch on an empty workspace shows the empty states from §3.2 and the idle run box.
- [ ] `tests/native/run-bridge.ps1` still exits 0 and the CLI is untouched (CI entry point).
- [ ] REQ-10 in APP-REQUIREMENTS.md rewritten for the new shell; CLAUDE.md GUI line updated.

## 5. Implementation blueprint

### Context you must know (facts from the current code)

| Area | Fact | Where |
|---|---|---|
| Project | Old-style csproj, **.NET Framework 4.7.2, x64**, plain code-behind, no MVVM lib. **Add every new `.xaml`/`.cs` to the csproj by hand** (`Page` / `Compile`). | `src/BrokerTestKit.Gui/BrokerTestKit.Gui.csproj` |
| Navigation | `ListBox Nav` + `TabControl Tabs`, **positional**: `NavChanged` sets `Tabs.SelectedIndex = Nav.SelectedIndex`; `GoTo(int)`; constants `Page*`. Uncommitted change reorders them. | `MainWindow.xaml:39-51`, `MainWindow.xaml.cs:27,98,103` |
| Size | `MainWindow.xaml` 571 lines, `.xaml.cs` 1337 lines — one class owns every tab. | |
| Storage | One SQLite db `btk.db` via Microsoft.Data.Sqlite (WAL, FKs on), next to the exe. Assets content-addressed by SHA-256 under `assets\`. Schema = `CREATE TABLE IF NOT EXISTS` + `ALTER TABLE ADD COLUMN` for upgrades. | `Workspace.cs:37,111`, `Store.cs:88-191` |
| Tables today | `asset, fom, fom_model, connection, setting, run, run_step, run_field`. `run` has `conn_a/conn_b`, input/expected **path + hash**, counts, seconds, abort_reason, finished_utc. | `Store.cs` |
| Run API | `BeginRun` → `SaveStep` per row → `EndRun`; `RecentRuns`, `StepsOf`, `FieldHistory`. **Store calls are marshalled to the UI thread.** | `Store.cs:534-680`, `MainWindow.xaml.cs:1124-1190` |
| Running | `RunTest` validates, saves sheets, builds `RunContext`, starts a `Thread` → `RunWorker`: two `NativeAgentHost` child processes, `NativeTestRunner.Prepare` once, then `Run([step])` **per row** so the grid fills live. Stop = `volatile bool _abort` checked between rows. Agent lines → log via `BeginInvoke` (must stay non-blocking: blocking skewed latency). | `MainWindow.xaml.cs:847-1310`, `Engine/NativeTestRunner.cs:51`, `Engine/NativeAgentHost.cs:73` |
| Results | `RunReport` / `StepResult` (Outcome Pass/Fail/Timeout/Skipped, Latency) / `FieldResult`; `RunOptions` (Timeout 15 s, Quiesce 400 ms). | `Engine/TestModel.cs:59-187` |
| Sheets | `CsvGrid.Sheet` keeps the `#` directive block across load/save; `EntityCsvParser.ReadInput/ReadExpected` — one class per sheet. | `CsvGrid.cs`, `Engine/EntityCsvParser.cs` |
| Theme | Dark only. Brush keys: `AppBackground, SurfaceBackground, SurfaceAltBackground, Divider, TextPrimary, TextSecondary, AccentBackground…`; styles `NavList, NavItem, Hint, Mono, AccentButton, SubtleButton, OutcomePass/Fail/WarnRow`, `…Text`. | `Themes/Precision.Dark.xaml`, `Themes/Controls.xaml` |
| Connections | `ConnectionRecord` (Name, Fom, ExecName, FederateName, `Rti`: SideSettings) + `CrossVersionWarning(a,b)`. | `ConnectionRegistry.cs:22,305` |

### Data model (new)

```sql
CREATE TABLE IF NOT EXISTS pair (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL UNIQUE,
  conn_a TEXT NOT NULL REFERENCES connection(name) ON UPDATE CASCADE,
  conn_b TEXT NOT NULL REFERENCES connection(name) ON UPDATE CASCADE,
  self_test INTEGER NOT NULL DEFAULT 0,
  created_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS test_case (
  id INTEGER PRIMARY KEY,
  pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
  name TEXT NOT NULL,
  timeout_s REAL,                       -- NULL = RunOptions default
  created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL,
  UNIQUE(pair_id, name)
);
-- one row per sheet; phase 1 enforces exactly one 'send' and one 'receive' per test case.
-- REQ-13 lifts that limit without a schema change.
CREATE TABLE IF NOT EXISTS test_case_sheet (
  id INTEGER PRIMARY KEY,
  test_case_id INTEGER NOT NULL REFERENCES test_case(id) ON DELETE CASCADE,
  side TEXT NOT NULL CHECK (side IN ('send','receive')),
  class_name TEXT NOT NULL,
  asset_hash TEXT NOT NULL REFERENCES asset(hash),   -- sheet text stored as a content-addressed asset
  ord INTEGER NOT NULL DEFAULT 0
);
-- D5: deleting a pair or test case deletes its runs (run_step / run_field already cascade from run)
ALTER TABLE run ADD COLUMN pair_id INTEGER REFERENCES pair(id) ON DELETE CASCADE;
ALTER TABLE run ADD COLUMN test_case_id INTEGER REFERENCES test_case(id) ON DELETE CASCADE;
```
(SQLite allows a `REFERENCES … ON DELETE CASCADE` clause on `ADD COLUMN` as long as the default is
NULL. Foreign keys are already switched on per connection in `Store.cs`.)
Sheets live **inside** the workspace as assets (not loose paths) so a test case is self-contained
and the existing hash-based "test changed vs result changed" logic keeps working.

### Tasks, in order (each ends green: builds, app opens, existing runs still listed)

**T1 · Store: schema + API** (`Store.cs`, `GuiModels.cs`)
- Add the tables/columns above using the file's existing upgrade pattern.
- Records: `PairRecord`, `TestCaseRecord`, `TestCaseSheet`. API: `Pairs()`, `SavePair`,
  `DeletePair`, `TestCases(pairId)`, `SaveTestCase(tc, sheets)` (single transaction),
  `DeleteTestCase`, `RunsOf(testCaseId, n)`, `RecentRuns(n)` gains pair/test-case names,
  `Runs(pairId?, outcome?, text?, offset, limit)` + `RunTally(same filter)` for All results,
  `FomUsage(fomId)` for the registry's *Used by* column and its remove guard,
  `BeginRun(..., pairId, testCaseId)`, `RunCount(pairId | testCaseId)` for the delete dialog.
- **Delete is a hard delete including runs (D5).** UI: *Delete pair* (on Edit pair) and *Delete* on
  a test case row / in the editor, each behind a confirm dialog that states the cost:
  **"Delete 'Stock RPR 1.0 → RPR 2.0'? This also deletes its 3 test cases and 41 saved runs. This
  can't be undone."** Disabled while one of its test cases is running. Sheet assets are left in
  `assets\` (content-addressed, may be shared); an orphan sweep is out of scope.
- Counts for the Dashboard: `CountFoms()`, `CountPairs()`.

**T2 · Shell: named navigation** (`MainWindow.xaml(.cs)`, new `Views/`)
- Replace the positional Nav/Tabs with a `ContentControl` host and a tiny navigator:
  `Navigate(Page page, object arg)` where `Page` is an enum
  (`Dashboard, Pairs, PairDetail, PairEdit, TestCaseEdit, TestCaseResult, AllResults, CsvTemplate,
  FomRegistry`) and
  `arg` carries the id (pair / test case / run). Keep a back stack for breadcrumbs.
- Split each screen into its own `UserControl` under `src/BrokerTestKit.Gui/Views/`
  (`DashboardView`, `PairsView`, `PairDetailView`, `PairEditView`, `TestCaseEditView`,
  `TestCaseResultView`, `AllResultsView`;
  move the existing template and registry tabs into `CsvTemplateView`, `FomRegistryView` **without
  behaviour changes**). This is also how `MainWindow.xaml.cs` stops being 1,300 lines.
- Sidebar: `NavList` styles reused; Pairs item selected for every pair-scoped page.

**T3 · Migration of existing data** (`Store.cs`, one-time, idempotent)
- For every distinct `(conn_a, conn_b)` in `run`: create a pair named
  `"<FOM A> → <FOM B>"` (dedupe with ` (2)`).
- For every distinct `(pair, input_hash, expected_hash)`: create a test case named from the
  sheet's `# class:` (`"GroundVehicle"`, then `"GroundVehicle (2)"` …), import both sheets as
  assets if the paths still exist; otherwise create it with no sheets and mark *"sheets missing —
  re-upload"*. Back-fill `run.pair_id/test_case_id`.
- Guard with a `setting` key `migrated.pairs=1`. Test on a **copy** of a real `btk.db`.

**T4 · Pairs + Pair details** (`PairsView`, `PairDetailView`)
- Pairs table + search (filter in memory; tens of rows, not thousands).
- Pair details reads `TestCases(pairId)` + last run per test case + `RunsOf` for the pair.
- *Run* / *Run all test cases*: queue test cases and run them one after another (§T7).

**T5 · Pair editor** (*New pair* / *Edit pair*, `PairEditView`) — as §3.9 and the canvas boards
*New / edit pair* and *Delete pair — confirm* (D1). Breadcrumb, sidebar with Pairs selected,
sticky Delete / Cancel / Save footer returning to Pair details (or Pairs for a new pair).
- Reuse today's Connections/Federations editor controls for each side (HLA version, FOM from
  registry, RTI bin, rid, assistant port, federation, join modules), laid out Send · A | Receive · B,
  plus pair name and advanced *Self-test*. Keep the existing checks: REQ-01 field switching,
  `CrossVersionWarning`, distinct assistant ports.
- Implementation may store the two sides as `connection` rows (names `<pair>·A`, `<pair>·B`) so the
  run path is untouched.

**T6 · Add / edit test case** (`TestCaseEditView`)
- Class picker per side from that side's FOM (reuse the template tab's class list + filter).
- Picking a class creates a sheet from `CsvTemplateGenerator` (same columns as *Download
  template*); *Upload CSV…* replaces it after checking its `# class:` matches; *Download template*
  writes the generator output.
- Grid = `CsvGrid` (keeps directives). Variant/array expansion stays as on the template tab.
- Save: validate (name unique in pair, one class per side, sheets parse with
  `EntityCsvParser` against the right FOM, ids unique) → `SaveTestCase` → navigate to Pair details.
  Cancel with unsaved edits → confirm dialog. Both return to Pair details.

**T7 · Run + result** (`TestCaseResultView`, run service)
- Extract `RunTest`/`RunWorker`/`RunContext` out of `MainWindow` into a `RunService` that owns the
  thread, the queue (for *Run all*) and `_abort`, and raises `Started/RowCompleted/Finished`
  events on the Dispatcher. **Keep the per-row `Run([step])` loop and the non-blocking log
  path exactly as they are** (latency accuracy depends on it).
- Sheets are written from assets to a temp folder before parsing (engine takes paths).
- `BeginRun` with pair + test case ids. Result view binds to the live run or loads a saved one
  via `StepsOf`. *Runs of this test case* = `RunsOf(testCaseId, 20)`.

**T8 · Sidebar test-run box + rig banner** — the box subscribes to `RunService`; Running vs Idle
as §3.1. The banner is a `RigMonitor` (licence + TCP 5111 checks, §3.8) raising `Changed` on the
Dispatcher; `RunService` asks it before starting. **Ships in the same change as the box** (R3).

**T9 · Dashboard + All results** — Library counts, Last run, last 10 runs, empty states (§3.2);
`AllResultsView` with filters and paging (§3.10). Tools restyle (§3.11): *Used by* column and
remove guard on the FOM registry; CSV template preview inline.

**T10 · Docs** — rewrite REQ-10 for the new shell; note the pair/test-case model under REQ-03;
update CLAUDE.md's GUI bullet ("5 tabs" → the new flow); DESIGN.md if the storage section names
tables.

### Out of scope (separate PRPs)
- ~~**REQ-13**~~ — done in the same change (APP-REQUIREMENTS REQ-13).
- FOM-diff view; light theme; JUnit/HTML reports; persisting agent transcripts.

## 6. Decisions (answered by the owner, 2026-11-04)

| # | Question | Decision | Lands in |
|---|---|---|---|
| D1 | New / Edit pair screen is not on the canvas | First answer: build from the PRP text. **Superseded the same day: drawn on the canvas** (New / edit pair + Delete confirm, §3.9) | §3.9, T5 |
| D2 | Float tolerance: one per test case, or per field? | **Per field, on the Receive sheets** — a ± row under the float columns, backed by `# tolerance:` | §3.5, T6 |
| D3 | Name of the A→B concept | **Pairs** (not Routes / Bridges) | all copy |
| D4 | Where licence / broker status goes | **Warning banner, only when something is wrong** | §3.8, T8 |
| D5 | Deleting a pair or test case that has runs | **Hard delete, runs included**, behind a confirm that states the count | T1, data model |

## 7. Risks and gotchas
- **R1 — positional navigation.** Any page still addressed by index will silently open the wrong
  screen after T2. Remove `Page*` int constants entirely; grep for `SelectedIndex` and `GoTo(`.
- **R2 — UI-thread store calls.** Keep the existing rule (all `Store` calls on the Dispatcher) inside
  `RunService`; SQLite access from the worker thread is what that rule avoids.
- **R3 — losing the licence signal.** vrx stalls silently when the MAK vendor daemon is down
  (CLAUDE.md, mak-rti-knowledge §9). The banner (§3.8) is the only place it shows now, so it lands
  with T8, not later. `lmutil` may be missing on a CI box — treat "can't run the check" as a
  distinct, milder banner, never as healthy.
- **R8 — hard delete (D5) is irreversible** and takes history with it. The confirm dialog must name
  the counts; no bulk delete; never delete from a migration.
- **R4 — sheets as assets.** Once sheets live in the workspace, *Upload CSV…* must normalise
  encoding (strip a UTF-8 BOM, store UTF-8) and keep leading zeros (treat every cell as text).
  Files written for Excel keep their BOM on purpose (`RunReportCsv.cs:23`); don't "fix" that.
- **R5 — migration.** Old runs whose CSV paths no longer exist must still list with their results;
  only re-running needs the sheets.
- **R6 — csproj.** New files that are not added to the csproj compile in an SDK project but **not
  here** — the build fails late with "type not found".
- **R7 — C# version.** Old-style csproj on 4.7.2 → default C# 7.3: no records, no `init`, no
  switch expressions, no nullable reference types.

## 8. Validation loop

```bat
:: build (from the repo root, x64)
msbuild HLABrokerTranslationTestKit.sln -p:Configuration=Debug -p:Platform=x64
:: rig still proven end to end (CLI path untouched)
powershell -ExecutionPolicy Bypass -File tests\native\run-bridge.ps1
```
Manual checklist in the app (record outcome in the PR):
1. Empty workspace (rename `btk.db`) → Dashboard empty states + idle run box exactly as §3.2.
2. Copy of a real `btk.db` → migration creates pairs/test cases; old runs listed with names; open one.
3. New pair (stock RPR 1.0 → RPR 2.0 per `config/bridge-stock-rpr.profile` values) → Add test case
   `GroundVehicle` → upload a sheet pair made by `tests/gen-volume-csv.ps1 -Rows 12` → Save →
   back on Pair details. (Not `tests/sample-*.csv`: those use the old long format, not the
   entity-sheet format.)
4. Run it → sidebar box shows Running + progress → result screen fills live → Pass; Stop mid-run
   leaves an aborted run with partial rows.
5. Edit the test case (change one expected value) → Run again → Fail on that field; *Runs of this
   test case* shows both runs.
6. Dates everywhere in `dd MMM yy HH:mm`; never-run shows `—`.
7. Licence daemon stopped (`lmutil lmstat -c 27000@127.0.0.1` shows maklmgrd down) → banner
   visible and *Run* blocked; vrx stopped (nothing on TCP 5111) → broker banner.
8. Tolerance: set `± 0.5` on a Receive float column → saved file's `# tolerance:` line has it;
   a 0.3 difference passes, 0.7 fails.
9. Delete a test case with runs → dialog states the run count → runs gone from Dashboard and
   `btk.db` (`run`, `run_step`, `run_field` rows removed).
