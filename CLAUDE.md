# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this project is

**HLAUnitTestingKit** — a GUI + CLI tool that **regression-tests a MAK VR-Exchange
broker** which translates between an **HLA 1.3** federation and an **HLA Evolved
(IEEE 1516-2010)** federation. The tool joins both federations as federates: it pumps
data into Federation A from an input CSV, lets the broker translate A→B, and verifies
what Federation B receives against an output CSV (golden-file black-box testing).

Status: **working prototype.** Join, multi-process agents, IPC and the CSV golden-file
runner all work end-to-end against MAK RTI 5.0.1 (see `docs/APP-REQUIREMENTS.md` for status
per requirement). Under git: `github.com:luxcan/hla-unit-test-kit`, private. Commit straight to
`main`. MAK licensed files (rids, FOMs, `vrLinkSharp.dll`) are gitignored, so a fresh clone needs
them copied in before it builds or runs.

## Key references — read these first

- **[docs/FOM-HANDOVER.md](docs/FOM-HANDOVER.md)** — the five inputs still owed by the project
  side (customised `.fed` + `.omt`, customised RPR 2.0 `.xml`, what-is-customised table, broker
  mapping), each with a working sample in `config/` / `tests/`. Read this before asking the user
  for files.
- **[docs/APP-REQUIREMENTS.md](docs/APP-REQUIREMENTS.md)** — numbered requirements for the
  finished app (REQ-01 = which FOM files to ask for per HLA version, etc.). Living doc.
- **[docs/DESIGN.md](docs/DESIGN.md)** — the design: goal, test concept, architecture
  decisions, the harness shape, FOM-rename workflow, and the open questions. **Start here.**
- **[docs/mak-rti-knowledge.md](docs/mak-rti-knowledge.md)** - MAK RTI facts proven on this machine:
  the VC/ABI islands per install, why ONE agent binary serves 4.6.1 and 5.0.1, the rtiAssistant
  port-6003 collision and the `RTI_ASSISTANT_PORT` fix, the rid parameter diff across versions,
  the VR-Exchange config map (§8) and the working A→broker→B bridge (§9). **Consult before any cross-RTI or version question.**
- **[docs/vr-link-knowledge.md](docs/vr-link-knowledge.md)** — distilled VR-Link API
  knowledge (C# binding, the Universal HLA generic layer, joining federations, encoding,
  time management, HLA 1.3 vs Evolved). **Consult this for any VR-Link API question** instead
  of re-scanning `C:\MAK\vrlink5.10\doc`. Keep it updated as new facts are confirmed.

## Stack / environment facts

- Vendor: **MAK**. RTI = MAK RTI. Broker = **VR-Exchange 2.9** (`C:\MAK\vrexchange2.9`) — x64,
  built **VC14**, `bin64` only. ⚠️ `vrexchange2.5.1` is also installed but is **x86 / VC10** and a
  dead end: it cannot load any VC14+ RTI. Always check which one you are looking at.
- Harness language = **C# on VR-Link 5.10** (`C:\MAK\vrlink5.10`, set in `MakEnvironment.cs`;
  5.9.1 is also installed, 5.9 is not; ref `vrLinkSharp.dll`,
  namespace `makVrl`). The C# binding selects HLA version at **runtime**, so one process
  holds both the 1.3 and the Evolved connection.
- Harness API of choice = the **Universal HLA layer** (`UniversalHlaObjStateMessage` /
  `…ObjDiscoveryMessage` / `…ObjRemovedMessage` / `UniversalHlaInteractionMessage` +
  `ExerciseConnection.addMessageHandler`/`sendMessage`) — FOM-agnostic, by-name, raw `byte[]`.
- The harness is **independent** of the C++ broker (tests it as a black box).
- **GUI = `src/BrokerTestKit.Gui`** (WPF, 5 tabs: FOM registry / Connections / CSV template /
  Test data / Run). It holds no RTI connection - the agents are child processes. The CLI stays the CI entry
  point (`entity`, `native`, `template`, `fom` modes).
- **Custom attributes require the NATIVE agents**, not C#. The C# VR-Link binding has no FOM
  Mapper/encoder, so unmodelled attributes never reach the wire. `src/BrokerTestKit.AgentNative/`
  holds two raw-RTI C++ agents that move exact bytes by name: `btk-agent13.exe` (HLA 1.3) and
  `btk-agent1516e.exe` (Evolved). Build with `build-native.bat`; prove with
  `tests/native/run-smoke.bat` (both federations, exit 0). Read its README before editing:
  `/std:c++14` and `/DRTI_USES_STD_FSTREAM` are mandatory, `RTIambassador` connects inside its
  constructor so it must never be a global, and on Evolved FOM modules go to **join**
  (`joinfom=`) because create-time modules are silently dropped.
- **The bridge works end to end** — `tests/native/run-bridge.ps1` publishes stock RPR 1.0 on
  HLA 1.3 / RTI 4.5.1(vc14), lets VR-Exchange 2.9 translate, and asserts what stock RPR 2.0 on
  Evolved / RTI 5.0.1 receives. Exits 0. `EntityIdentifier`/`EntityType`/`Marking` arrive
  byte-for-byte; `WorldLocation` arrives inside the RPR 2.0 `Spatial` variant record, which is
  the proof of real translation. **The one thing that blocked it for a long time:** the 1516e
  broker joins with 16 extra FOM modules from `bin64\MAK-1516e-Config.xml`, and a federate that
  joins with only the base FDD receives *nothing, silently*. See
  [mak-rti-knowledge.md](docs/mak-rti-knowledge.md) §9 before debugging any Evolved federate
  that joins cleanly and then sees no data.
- **The rig is DATA, not code** — `config/bridge-stock-rpr.profile` (`BridgeProfile.cs`) holds both
  sides: agent binary, RTI bin, rid, `RTI_ASSISTANT_PORT`, `RTI_CONFIG`, federation, FOM + join
  modules, HLA class. `BrokerTestKit.Cli.exe entity in.csv expected.csv --profile <file>` runs a
  golden file over it (`--quiesce ms`, `--timeout s`, `--quiet`, `--report out.csv`). Two spellings
  of the class are deliberate: the CSV's `# class:` is what the FOM PARSER resolves against (bare
  `GroundVehicle`), `class=` in the profile is what the RTI knows (fully qualified). Generate test
  data of any size with `tests/gen-volume-csv.ps1 -Rows N`.
- **Variant records expand on request** — `template … --variant Spatial=DRM_FPW` writes a
  `# variant:` directive and turns RPR 2.0's `Spatial` from one hex blob into eleven values at the
  offsets the FOM implies. Opt-in: name nothing and the column stays hex, so old sheets still pass.
  Dynamic arrays expand the same way with `--array Attr=N` (GUI: the "Variant / elements"
  column). Still hex by design: `HLAvariableArray` (its count prefix is not synthesised), arrays
  whose element holds a variant (`ArticulatedParametersArray`), and `HLAopaqueData`. Arrays are
  verified on an RPR 2.0 → 2.0 loopback over RTI 5.0.1, **not across the broker**: stock RPR 1.0
  `GroundVehicle` has no `PropulsionSystemsData` / `VectoringNozzleSystemData`, so there is
  nothing on side A to publish.
- ⚠️ **The broker normalises Euler angles.** Keep generated `Orientation` inside ±π (psi, phi) and
  ±π/2 (theta) or the golden file fails on correct broker behaviour — see §9.
- ⚠️ **If vrx never opens TCP 5111, check the licence first.** vrx stalls silently at `Locking
  Mutex: VRX-…` when the MAK vendor daemon is down, even though `lmgrd` is up and port 27000
  answers. `C:\MAK\MAKLicenseManager\lmutil.exe lmstat -c 27000@127.0.0.1` must show
  `maklmgrd: UP`; if not, `lmutil lmreread -c 27000@127.0.0.1` (needed after plugging in the
  FLEXID dongle). See §9.

## Conventions

- Keep `docs/DESIGN.md` and `docs/vr-link-knowledge.md` in sync with new findings; mark
  facts ✅ confirmed / ⚠️ caveat / ❓ to-verify as in vr-link-knowledge.md.
- Do **not** re-read `AddingContent.pdf` (in any `C:\MAK\vrlink*\doc`) — it's a 3D-art/terrain manual,
  irrelevant to this project (64 MB).
- VR-Link developer detail lives in the **HTML reference** (`doc/reference/csharp/`,
  `doc/reference/hla1516e/`), not in the PDFs (the Getting Started PDF is concepts-only).
