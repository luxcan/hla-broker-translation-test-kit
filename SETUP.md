# Setup

This repo deliberately **does not** contain MAK's licensed files. Everything below comes from
your own MAK install; nothing here is redistributable by us. See `.gitignore` for the exact list.

## What you need installed

| Thing | Version proven here | Notes |
|---|---|---|
| MAK RTI | `makRti5.0.1` (also proven on `4.6.1`) | `C:\MAK\makRti<ver>` |
| VR-Link | 5.9 | only for the legacy C# agent; the native agents do not need it |
| VR-Exchange | 2.5.1 | the broker under test |
| Visual Studio | 2022, C++ workload | `/std:c++14` — see below |
| .NET Framework | 4.7.2 targeting pack | REQ-11 |
| FlexLM licence | dongle or server | `MAKLMGRD_LICENSE_FILE`, e.g. `27000@127.0.0.1` |

Two RTI versions **cannot run at the same time**: they collide on the RTI Assistant's fixed
TCP 6003. Kill `rtiAssistant.exe` before a cross-version run. See
[docs/mak-rti-knowledge.md](docs/mak-rti-knowledge.md) §3.

## Files you must supply

Copy these into `config/` from your MAK install (paths are the defaults):

| File | Where to get it |
|---|---|
| `MAK-RPR1-1-1.fed` | `C:\MAK\vrlink5.9\data\hla\` (or your project's customised `.fed`) |
| `RPR_FOM_v2.0_1516-2010.xml` | `C:\MAK\vrlink5.9\data\hla1516e\` |
| `HLAstandardMIM.xml` | `C:\MAK\makRti<ver>\data\` |

And the **customised** FOMs the project under test actually uses — these are modified copies of
MAK-shipped files, so they inherit MAK's licence and stay out of the repo:

| File | What it is |
|---|---|
| `BTK-CustomRPR1.fed` | customised RPR 1.0, HLA 1.3 — structure only |
| `BTK-CustomRPR1.omt` | the matching DIF/OMT — **required**, the `.fed` carries no datatypes at all |
| `BTK-CustomRPR2.xml` | customised RPR 2.0 as a single merged FDD (1516-2010) |

See [docs/FOM-HANDOVER.md](docs/FOM-HANDOVER.md) for what each one has to contain and why the
`.omt` is not optional.

`lib/vrlink/vrLinkSharp.dll` — copy from `C:\MAK\vrlink5.9\lib\`. Only the legacy C# agent
references it; the native agents talk to the RTI directly.

### RID files

`config/rid.mtl` is **not** in the repo either. Start from your RTI's shipped rid
(`C:\MAK\makRti<ver>\rid.mtl`) and set:

```lisp
(setqb RTI_extend13and1516interop 1)   ;; one RTI serves HLA 1.3 AND 1516 at once
(setqb RTI_useRtiExec 0)               ;; peer-to-peer, no rtiexec process
```

Bridging two RTIs needs one rid per side on **distinct** `RTI_udpPort` / `RTI_tcpPort` /
`RTI_destAddrString` — MAK's stock rids already differ per version, so start from those rather
than one shared file. [docs/mak-rti-knowledge.md](docs/mak-rti-knowledge.md) §4 lists every
parameter and what changed between versions.

### Test data

`tests/*.csv`, `tests/templates/` and `tests/native/*.txt` are excluded too. A generated CSV
template carries the FOM's attribute names, datatypes and **byte offsets**, and an agent script
names the customised attributes — that is FOM structure in another format, so it is excluded on
the same grounds as the FOMs.

Regenerate the templates from your own FOMs: register them in the GUI, then
**CSV template → Download template…**. `tests/native/run-smoke.bat` is in the repo but needs its
`.txt` scripts recreated for your FOM before it will run.

## Build

```bat
build-native.bat                 REM the two raw-RTI C++ agents
msbuild HLAUnitTestingKit.sln -p:Configuration=Debug -p:Platform=x64
```

`/std:c++14` and `/DRTI_USES_STD_FSTREAM` are **mandatory** for the native agents — C++17 removed
the dynamic exception specifications the HLA 1.3 headers use, and without the fstream define the
headers reach for a pre-standard `<fstream.h>`. `src/BrokerTestKit.AgentNative/README.md` has the
rest.

## Prove the install

```bat
tests\native\run-smoke.bat
```

Expected, on both federations:

```
RESULT: PASS - custom attributes round-tripped byte-for-byte on BOTH federations.
```

If it cannot reach the RTI, check `RTI_RID_FILE` points at **this repo's** `config\rid.mtl` —
MAK's stock rid expects an `rtiexec` and fails with *"Failed to create a reliable connection to
the RTI Forwarder"*.

## Where the app keeps its data

The GUI stores its SQLite database and every imported FOM **beside the exe**
(`src\BrokerTestKit.Gui\bin\x64\Debug\`), so the tool is one folder you can copy or hand over.
That folder is build output — **a clean rebuild deletes it**, database included. Copy `btk.db`
and `assets\` somewhere else first if a run history matters.
