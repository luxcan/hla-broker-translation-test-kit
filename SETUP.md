# Setup

This repo deliberately **does not** contain MAK's licensed files. Everything below comes from
your own MAK install; nothing here is redistributable by us. See `.gitignore` for the exact list.

## Fresh clone — checklist

In order; each step has a section below.

1. Install the MAK products in the table below, and plug in the licence dongle.
   `C:\MAK\MAKLicenseManager\lmutil.exe lmstat -c 27000@127.0.0.1` must show `maklmgrd: UP`.
   If it doesn't, run `lmutil lmreread -c 27000@127.0.0.1`; plugging the dongle in is not enough
   on its own.
2. Copy the FOMs into `config/` ([Files you must supply](#files-you-must-supply)).
3. Create the rid files in `config/` ([RID files](#rid-files)): `rid.mtl` for single-RTI work,
   plus `rid-fedA-rti451.mtl` and `rid-fedB-rti501.mtl` for the bridge.
4. Copy `vrLinkSharp.dll` to `lib/vrlink/`.
5. Build: `build-native.bat`, then the solution ([Build](#build)).
6. Prove the install: `tests\native\run-smoke.bat` ([Prove the install](#prove-the-install)).
7. For the bridge: recreate VR-Exchange's `BTK-Bridge.xml` and its two `.bcf` files in
   `C:\MAK\vrexchange2.9\bin64` ([The VR-Exchange side](#the-vr-exchange-side)), with paths that
   point at **this clone**. Then `tests\native\run-bridge.ps1` must exit 0.

## What you need installed

| Thing | Version proven here | Notes |
|---|---|---|
| MAK RTI | `makRti5.0.1` (Fed B), `makRti4.5.1(vc14)` (Fed A of the bridge) | `C:\MAK\makRti<ver>`; also proven on `4.6.1` |
| VR-Link | 5.10 | only for the legacy C# agent and `vrLinkSharp.dll`; the native agents do not need it |
| VR-Exchange | **2.9** (x64, VC14) | the broker under test. `vrexchange2.5.1` is x86/VC10 and cannot load a VC14+ RTI |
| Visual Studio | 2022, C++ workload | `/std:c++14` — see below |
| .NET Framework | 4.7.2 targeting pack | REQ-11 |
| FlexLM licence | dongle or server | `MAKLMGRD_LICENSE_FILE`, e.g. `27000@127.0.0.1` |

Two RTI **versions** run side by side only if each has its own RTI Assistant port: set
`RTI_ASSISTANT_PORT` per side (the bridge profile uses 6003 for A and 6004 for B). If they share
the default 6003, the second one is dropped with *"Invalid message version from Assistant"*. See
[docs/mak-rti-knowledge.md](docs/mak-rti-knowledge.md) §3.

## Files you must supply

Copy these into `config/` from your MAK install (paths are the defaults):

| File | Where to get it |
|---|---|
| `MAK-RPR1-1-1.fed` | `C:\MAK\vrlink5.10\data\hla\` (or your project's customised `.fed`) |
| `MAK-RPR1-1-1.omt` | the same folder — the datatypes the `.fed` lacks; the bridge profile's `[A] fom` |
| `RPR_FOM_v2.0_1516-2010.xml` | `C:\MAK\vrlink5.10\data\hla1516e\` (byte-identical to VR-Exchange's `bin64` copy) |
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

`lib/vrlink/vrLinkSharp.dll` — copy from your VR-Link install (`C:\MAK\vrlink5.10\…`). The C#
projects reference it at build time, but only the legacy C# agent uses it at run time; the native
agents talk to the RTI directly.

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

The bridge profile (`config/bridge-stock-rpr.profile`) expects two of them by name:

| File | Start from | For |
|---|---|---|
| `rid-fedA-rti451.mtl` | `C:\MAK\makRti4.5.1(vc14)\rid.mtl` | Federation A (HLA 1.3, RPR 1.0) |
| `rid-fedB-rti501.mtl` | `C:\MAK\makRti5.0.1\rid.mtl` | Federation B (Evolved, RPR 2.0) |

The `-autoprovide` variants differ by one line (`HLAautoProvide`); see mak-rti-knowledge §10.

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
msbuild HLABrokerTranslationTestKit.sln -p:Configuration=Debug -p:Platform=x64
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

## Configure the rig

Everything about the two federations lives in a **bridge profile**, not in code:

```
config\bridge-stock-rpr.profile
```

One `[A]` and one `[B]` section, each naming the agent binary, the RTI install whose `bin` goes
on `PATH`, the rid file, `RTI_ASSISTANT_PORT`, `RTI_CONFIG`, the federation and FOM to join, and
the HLA class the test data travels on. Paths are absolute or relative to the profile itself.

Point it at a different rig by editing that file — nothing is compiled in. The profile is
validated on load, so a missing rid, a missing FOM module or two RTI installs sharing one
Assistant port fail immediately with a named reason instead of an hour later as silence.

Two entries deserve attention because they are the ones that fail quietly:

- **`[B] configDir`** must be the **VR-Exchange `bin64`** directory, not `config\`. The Evolved
  broker joins with 16 FOM modules from `bin64\MAK-1516e-Config.xml` and those names have to
  resolve to the broker's own copies.
- **`[B] joinFom`** must list those same 16 modules. A federate that joins with only the base FDD
  has a different FDD from the broker and receives **nothing at all** — no error, no warning.

### The VR-Exchange side

The broker's own configuration lives in the VR-Exchange install, not in this repo:

| File in `C:\MAK\vrexchange2.9\bin64` | What it does |
|---|---|
| `BTK-Bridge.xml` | the two connections (one broker per federation) and **each one's environment**: `RTI_CONFIG`, `RTI_RID_FILE`, `RTI_ASSISTANT_PORT` |
| `BTK-FedA-RPR1-rti451.bcf` | broker A: `execName BTK-FedA`, the RPR 1.0 `.fed` |
| `BTK-FedB-RPR2-rti501.bcf` | broker B: `execName BTK-FedB`, RPR 2.0 plus the 16 join modules |

They are RTI configuration, excluded like the rids. Recreate them from
[docs/mak-rti-knowledge.md](docs/mak-rti-knowledge.md) §8–§9. Then:
- **Point every `RTI_CONFIG` / `RTI_RID_FILE` line at this clone's `config\`.** They are absolute
  paths. A broker whose paths are wrong logs *"could not connect to the RTI"* even though vrx
  opens TCP 5111. That is exactly what happened after this repo was renamed.
- **Check the licence before blaming the configs.** If vrx never opens TCP 5111 and stops at
  `Locking Mutex`, the vendor daemon is down (checklist step 1).

## Generate test data from a FOM

Nothing about any FOM is compiled in. Point the generator at whichever `.omt` or FDD you were
given, name a class or an interaction, and it emits the sheet:

```bat
BrokerTestKit.Cli.exe template <fom.omt|fom.xml> <ClassOrInteraction> out.csv ^
    [--expected] [--rows N] [--only a,b,c] [--max N] [--skip-variable]
```

The FOM decides the columns, so the author never writes a datatype:

- fixed records are **expanded to leaf sub-fields** with their offsets — `WorldLocation.X/.Y/.Z`,
  `EntityIdentifier.FederateIdentifier.SiteID` — because that is the level people reason about
  and the level tolerances apply at;
- **variant records expand once you say which alternative you mean.** A variant has no layout
  until its discriminant is fixed, so name one and the rest is ordinary:

  ```bat
  BrokerTestKit.Cli.exe template RPR_FOM_v2.0_1516-2010.xml GroundVehicle out.csv ^
      --variant Spatial=DRM_FPW
  ```

  writes `# variant: Spatial=DRM_FPW` into the sheet and turns one hex column into eleven values -
  `Spatial.WorldLocation.X`, `Spatial.Orientation.Psi`, and so on - at offsets 0/8/16/24/32/36…56,
  which is byte-for-byte the record captured off the wire. The parser reads the directive back, so
  the sheet stays self-describing. Name nothing and nothing changes: the column stays a single hex
  blob, and every existing sheet keeps working unaltered;

- **variable-length arrays are still ONE whole-attribute hex column**
  (`ArticulatedParametersArray`, `PropulsionSystemsData`, `VariableDatums`). Their length is not
  known until run time, so there is nothing honest to expand them into. This is now the only place
  the sheet is not fill-in-a-number;
- opaque byte arrays get a `:string` hint, since a FOM cannot say "these bytes are text".

**Nothing is dropped silently.** If the column cap is hit, the sheet carries a `*** TRUNCATED ***`
block naming every missing attribute, the CLI repeats it on stderr, and the exit code is 3. The
cap defaults to 512 — a backstop, not a way to narrow a sheet; use `--only` for that. It was 60,
which a normal RPR class walks straight past: `Aircraft` expands to 79 columns, so the old
template stopped inside `Marking` and quietly lost `Orientation`, `VelocityVector` and
`WorldLocation` from a file that looked complete.

Verified across ten class/FOM pairs — RPR 1.0 and 2.0, objects and interactions, 571 columns —
every generated column resolves back through the parser. Of those, **15 still require hex**, all of
them variable-length arrays; `WeaponFire` and `NatoIFFTransponder` on HLA 1.3 need none at all.

## Run a golden-file test

Start the bridge, then run the CSV pair:

```bat
cd C:\MAK\vrexchange2.9\bin64
start vrx.exe -l BTK-Bridge.xml -G

BrokerTestKit.Cli.exe entity in.csv expected.csv --profile config\bridge-stock-rpr.profile ^
    --quiesce 50 --timeout 15 --quiet --report run-report.csv --report run-report.xml --report run-report.html
```

`--quiet` prints failures and a rows/s heartbeat instead of a line per row; `--report` writes
every row's outcome regardless, and may repeat: the extension picks the format (`.xml` = JUnit XML
for CI, `.html` = a self-contained page, anything else = CSV). Exit code is 0 only if every row passed.

`tests\gen-volume-csv.ps1 -Rows N` generates a matching input/expected pair of any size for this
rig — useful for volume runs. Read its header before changing the values: the numbers are chosen
so that **no tolerance is needed anywhere**, and the Euler angles are deliberately kept inside
their legal ranges because the broker normalises anything outside them.

## Where the app keeps its data

The GUI stores its SQLite database and every imported FOM **beside the exe**
(`src\BrokerTestKit.Gui\bin\x64\Debug\`), so the tool is one folder you can copy or hand over.
That folder is build output — **a clean rebuild deletes it**, database included. Copy `btk.db`
and `assets\` somewhere else first if a run history matters.
