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
per requirement). Not yet under git — see the version-control note below.

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
  port-6003 collision that blocks two RTI versions at once, and the rid parameter diff across
  versions. **Consult before any cross-RTI or version question.**
- **[docs/vr-link-knowledge.md](docs/vr-link-knowledge.md)** — distilled VR-Link API
  knowledge (C# binding, the Universal HLA generic layer, joining federations, encoding,
  time management, HLA 1.3 vs Evolved). **Consult this for any VR-Link API question** instead
  of re-scanning `C:\MAK\vrlink5.9\doc`. Keep it updated as new facts are confirmed.

## Stack / environment facts

- Vendor: **MAK**. RTI = MAK RTI. Broker = **VR-Exchange 2.5.1** (`C:\MAK\vrexchange2.5.1`).
- Harness language = **C# on VR-Link 5.9** (`C:\MAK\vrlink5.9`; ref `vrLinkSharp.dll`,
  namespace `makVrl`). The C# binding selects HLA version at **runtime**, so one process
  holds both the 1.3 and the Evolved connection.
- Harness API of choice = the **Universal HLA layer** (`UniversalHlaObjStateMessage` /
  `…ObjDiscoveryMessage` / `…ObjRemovedMessage` / `UniversalHlaInteractionMessage` +
  `ExerciseConnection.addMessageHandler`/`sendMessage`) — FOM-agnostic, by-name, raw `byte[]`.
- The harness is **independent** of the C++ broker (tests it as a black box).
- **GUI = `src/BrokerTestKit.Gui`** (WPF, 4 tabs: Federations / FOM->CSV template / Test data /
  Run). It holds no RTI connection - the agents are child processes. The CLI stays the CI entry
  point (`entity`, `native`, `template`, `fom` modes).
- **Custom attributes require the NATIVE agents**, not C#. The C# VR-Link binding has no FOM
  Mapper/encoder, so unmodelled attributes never reach the wire. `src/BrokerTestKit.AgentNative/`
  holds two raw-RTI C++ agents that move exact bytes by name: `btk-agent13.exe` (HLA 1.3) and
  `btk-agent1516e.exe` (Evolved). Build with `build-native.bat`; prove with
  `tests/native/run-smoke.bat` (both federations, exit 0). Read its README before editing:
  `/std:c++14` and `/DRTI_USES_STD_FSTREAM` are mandatory, `RTIambassador` connects inside its
  constructor so it must never be a global, and on Evolved FOM modules go to **join**
  (`joinfom=`) because create-time modules are silently dropped.

## Conventions

- Keep `docs/DESIGN.md` and `docs/vr-link-knowledge.md` in sync with new findings; mark
  facts ✅ confirmed / ⚠️ caveat / ❓ to-verify as in vr-link-knowledge.md.
- Do **not** re-read `C:\MAK\vrlink5.9\doc\AddingContent.pdf` — it's a 3D-art/terrain manual,
  irrelevant to this project (64 MB).
- VR-Link developer detail lives in the **HTML reference** (`doc/reference/csharp/`,
  `doc/reference/hla1516e/`), not in the PDFs (the Getting Started PDF is concepts-only).
