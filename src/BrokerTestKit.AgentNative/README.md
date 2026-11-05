# BrokerTestKit native agents (raw MAK RTI)

One federation, one process, no VR-Link. These agents exist because the C# / VR-Link
agent **cannot publish custom attributes** — see [REQ-08](../../docs/APP-REQUIREMENTS.md).

| | managed agent (`BrokerTestKit.Agent`) | native agent (this) |
|---|---|---|
| API | VR-Link `ExerciseConnection` | raw HLA `RTIambassador` |
| Custom attributes | ❌ no encoder in the C# binding | ✅ exact bytes both ways |
| FOM knowledge | RPR FOM mapper compiled in | none — everything by name at runtime |
| Dead reckoning | VR-Link may smooth/suppress values | none — a sent byte is the wire byte |
| Licence seat | `vrl_run` (2 available) | `makrtiLib` (14 available) |

## Build

```bat
src\BrokerTestKit.AgentNative\build-native.bat
```

Produces two binaries — one per HLA version, because the two RTI APIs share almost no
vocabulary (see *HLA Evolved specifics* below):

| binary | federation | RTI libs |
|---|---|---|
| `btk-agent13.exe` | HLA 1.3 (Fed A) | `libRTI-NG_64.lib`, `libfedtime_64.lib` |
| `btk-agent1516e.exe` | HLA Evolved / 1516-2010 (Fed B) | `librti1516e64.lib`, `libfedtime1516e64.lib` |

Two toolchain constraints, both non-obvious and both fatal if missed:

- **`/std:c++14`** — HLA 1.3 headers use dynamic exception specifications (`throw(A, B)`),
  which C++17 *removed*. `/std:c++17` will not compile.
- **`/DRTI_USES_STD_FSTREAM`** — without it `RTI.hh` includes `<fstream.h>`, a pre-standard
  header no modern MSVC ships.

VS2022 (VC17) links fine against the VC15 RTI libraries — VC14 through VC17 share the
universal CRT and are one ABI island. The RTI itself confirms this at startup:
*"...HLA 1.3 for Microsoft Visual C++ version 14.1 Release Mode."*

## Protocol

Same stdin/stdout line protocol as the managed agent, so
`BrokerTestKit.Engine.AgentHost` can spawn either — it takes the exe path as an argument.

### Commands

| command | meaning |
|---|---|
| `JOIN exec=<name> fed=<file> federate=<name> [create=1]` | create (idempotent) + join |
| `JOIN … [joinfom=<a.xml,b.xml>] [mim=<m.xml>] [type=<federateType>]` | **1516e only** — extra FOM modules / MIM |
| `CLASS name=<Class> attrs=<A,B,C> mode=pub\|sub\|pubsub [as=<alias>]` | resolve handles by name, publish/subscribe |
| `REGISTER id=<localId> class=<Class> [name=<instName>]` | register an instance we own |
| `SETATTR id=<localId> name=<Attr> <value>` | **stage** a value (does not send) |
| `UPDATE id=<localId> [tag=<t>]` | flush staged values as ONE `updateAttributeValues` |
| `DELETE id=<localId>` | delete the instance |
| `INTCLASS name=<X> params=<a,b,c> mode=pub\|sub\|pubsub [as=<alias>]` | resolve + publish/subscribe an **interaction** |
| `SETPARAM class=<X> name=<p> <value>` | **stage** an interaction parameter |
| `SENDINT class=<X> [tag=<t>]` | flush staged parameters as ONE interaction |
| `SNAPSHOT` | dump everything currently discovered |
| `WAIT ms=<n>` | tick for n ms — lets a script file drive the agent with no parent |
| `PING` / `RESIGN` / `QUIT` | as named |

Lines starting with `#` or `;` are ignored, so command files can be commented.

### Value forms for `SETATTR`

`hex=` is canonical — it is the only one that can express an arbitrary FOM struct. The
typed forms are conveniences so hand-written tests need no codec in front of them. All
multi-byte forms are **big-endian** (network order), as HLA/RPR requires.

| form | example | bytes |
|---|---|---|
| `hex=` | `hex=40934a0000000000` | exactly what you wrote |
| `f64=` | `f64=1234.5` | 8, big-endian |
| `f32=` | `f32=1.5` | 4, big-endian |
| `i32=` / `i16=` / `u8=` | `i32=42` | 4 / 2 / 1, big-endian |
| `str=` | `str=TANK-01` | raw ASCII, no terminator |
| `str=` + `width=` | `str=TANK-01 width=11` | fixed width, zero padded (RPR marking) |

Values may not contain spaces — use `hex=` for anything binary or space-bearing.

### Output

| line | meaning |
|---|---|
| `OK <verb> k=v …` | command succeeded |
| `DISC obj=<h> class=<C> name=<inst>` | an instance was discovered |
| `ATTR obj=<h> class=<C> name=<A> len=<n> hex=<…>` | raw reflected bytes |
| `GONE obj=<h>` | instance removed |
| `INT class=<X> params=<n>` | an interaction arrived |
| `PARAM class=<X> name=<p> len=<n> hex=<…>` | raw interaction parameter bytes |
| `ERR <message>` | command failed |

Diagnostics go to **stderr** so they never pollute the protocol stream on stdout.

## Interactions

Interactions have **no instance identity** — they are fire-and-forget events, so there is nothing
to `REGISTER` and nothing to discover. `SETPARAM` stages and `SENDINT` flushes, exactly mirroring
`SETATTR`/`UPDATE`, so one test row is one interaction on the wire.

HLA publishes an interaction **class as a whole** (there is no per-parameter publish), so the
`params=` list on `INTCLASS` exists only to resolve and cache handles — and to fail loudly if the
FOM does not define one.

## Why `SETATTR` stages instead of sends

A CSV step is one stimulus. Staging then flushing on `UPDATE` means all of a step's
attributes land on the far side as a **single reflect**, which is what the
match-or-timeout verification model in [DESIGN §9.1](../../docs/DESIGN.md) assumes.
Sending per attribute would fragment one step into several reflects and make the
B-side comparison racy.

## HLA 1.3 specifics

- ⚠️ **Attribute and parameter handles are only unique WITHIN a class.** In HLA 1.3 they are
  small integers: `EmitterSystem`'s first attribute and `EmitterBeam`'s first attribute can both
  be handle 1. The agent therefore maps names by `"<class label>#<handle>"`, never by handle
  alone. Until 2026-11-05 it did map by handle alone, and declaring a second class renamed the
  first one's attributes: `EmitterType` came back labelled as `EmitterBeam`'s attribute and the
  parent reported it "not received". Single-class runs never noticed; multi-sheet test cases
  (REQ-13) did at once. The Evolved agent is not affected, because 1516e handles are opaque objects.

## HLA Evolved specifics

- Class names are normally **fully qualified**:
  `HLAobjectRoot.BaseEntity.BtkCustomEntity`. Use `as=` to give it a short alias that
  `REGISTER` / `SETATTR` can use.
- Pass extra FOM modules with **`joinfom=`, not `fom=`**. MAK merges modules at *join*;
  modules handed to `createFederationExecution` are **silently dropped**.
- A FOM module may add **new subclasses only** — never attributes to a class another module
  already defines. See the header comment in `config/BTK-CustomRPR2-module.xml`.
- Instance names are **reserved asynchronously**. The agent calls
  `reserveObjectInstanceName()`, pumps until the callback arrives (5 s cap), then registers;
  on refusal or timeout it falls back to an RTI-assigned name and logs why.

## Run the REQ-08 proof

```bat
tests\native\run-smoke.bat
```

Runs the round-trip on **both** federations — HLA 1.3 against `BTK-CustomRPR1.fed`, and
HLA Evolved against stock RPR 2.0 plus `BTK-CustomRPR2-module.xml`. Two federates join, one
publishes stock and **custom** attributes, the other asserts every byte came back unchanged.
Exits non-zero on failure, so it gates CI.

## Environment

The agent does not guess. Point it at the right RTI before it runs:

```bat
set PATH=C:\MAK\makRti5.0.1\bin;%PATH%
set RTI_CONFIG=<repo>\config
set RTI_RID_FILE=<repo>\config\rid.mtl
```

or pass `--rid <path>` / `--config <dir>`, which the agent applies with `_putenv_s`
**before** constructing the ambassador.

> ⚠️ MAK's `RTIambassador` dials the RTI **inside its constructor**. It is therefore
> created lazily on `JOIN`, never as a global — a global would connect during static
> init, before `RTI_RID_FILE` is set and before `--version` could answer. Using MAK's
> default `rid.mtl` instead of ours fails with *"Failed to create a reliable connection
> to the RTI Forwarder"*, because that file enables `rtiexec`.
