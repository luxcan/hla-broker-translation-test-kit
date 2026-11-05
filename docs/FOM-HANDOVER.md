# FOM handover — what the test kit needs from you

> **Purpose.** The harness is finished and self-verified; it is waiting on five inputs that only
> the project side can supply. Every one of them has a **working sample in this repo** — hand the
> same shapes back with real content and the kit runs against the real broker unchanged.
>
> Companion docs: [APP-REQUIREMENTS.md](APP-REQUIREMENTS.md) (REQ-01 for the file rules),
> [vr-link-knowledge.md](vr-link-knowledge.md) (evidence for every claim below).

---

## Checklist

| # | What | Sample to copy | Status |
|---|---|---|---|
| 1 | Customised RPR **1.0 `.fed`** | [`config/BTK-CustomRPR1.fed`](../config/BTK-CustomRPR1.fed) | ⏳ |
| 2 | Customised RPR **1.0 `.omt`** | [`config/BTK-CustomRPR1.omt`](../config/BTK-CustomRPR1.omt) | ⏳ |
| 3 | Customised RPR **2.0 `.xml`** | [`config/BTK-CustomRPR2.xml`](../config/BTK-CustomRPR2.xml) *(merged)* or [`…-module.xml`](../config/BTK-CustomRPR2-module.xml) *(module)* | ⏳ |
| 4 | **What is customised** | the table in §4 below | ⏳ |
| 5 | **Broker mapping** A→B | [`tests/sample-broker-mapping.csv`](../tests/sample-broker-mapping.csv) | ⏳ |

Plus: a **broker instance** to point at, and the two federation exec names.

---

## 1 + 2. HLA 1.3 needs BOTH `.fed` and `.omt`

This is the one that surprises people, so here is the evidence rather than the assertion:

```
BTK-CustomRPR1.fed   datatype mentions:   0
BTK-CustomRPR1.omt   DataType mentions: 912
```

A `.fed` row is only `(attribute BtkCustomValue best_effort receive)` — it tells the RTI the
attribute **exists**, and nothing whatsoever about its bytes. The `.omt` is the only place the
**datatypes** live:

```lisp
(Attribute (Name "BtkCustomValue")
           (DataType "double")
           (Cardinality "1")
           (Units "unitless") ...)

(ComplexDataType (Name "WorldLocationStruct")
     (ComplexComponent (FieldName "X") (DataType "double") (Cardinality "1") (Units "meters") ...)
     (ComplexComponent (FieldName "Y") (DataType "double") ...)
     (ComplexComponent (FieldName "Z") (DataType "double") ...))
```

Without the `.omt` the harness cannot know that `WorldLocation` is 24 bytes of three big-endian
doubles, so it cannot address `WorldLocation.x` or check a custom attribute's width. **If the
`.omt` is unavailable**, tests can still run using explicit byte offsets in the CSV
(`datatype=float64@8`) — it just means writing offsets by hand instead of reading them from the FOM.

> The sample `.omt` was derived from `MAK-RPR1-1-1.omt` and matches `BTK-CustomRPR1.fed`
> attribute-for-attribute. Keep those two files in step: the `.fed` and `.omt` must agree, and
> nothing enforces that but you.

---

## 3. HLA Evolved: merged FDD **or** module — and the choice is not free

Evolved can take an FDD plus 0..n FOM modules. But **verified against MAK RTI 5.0.1**:

> **A FOM module may add NEW SUBCLASSES only. It may NOT add attributes to a class another
> module already defines.**
>
> Trying it gives: *"Failed merging object classes. Object class's info for
> HLAobjectRoot.BaseEntity does not match other module"*.

### ✅ DECIDED (2026-08-18): single merged FDD

The project's customisation **merges attributes into existing RPR classes**, so it must be
delivered as a **single merged FDD** — `config/BTK-CustomRPR2.xml` is the sample to match.
The module form cannot express this and is kept only as a documented alternative.

| Your customisation | Deliver as | Sample |
|---|---|---|
| adds attributes to **existing** RPR classes | ✅ **single merged FDD** ← *this project* | `config/BTK-CustomRPR2.xml` |
| adds **new subclasses** only | stock FDD + module | `config/BTK-CustomRPR2-module.xml` |

Verified with the merged sample — the custom attributes resolve on `BaseEntity` **and** are
inherited by `…Platform.GroundVehicle`, which the module form cannot achieve:

```
OK CLASS name=BE handle=12 attrs=3 mode=sub      <- HLAobjectRoot.BaseEntity
OK CLASS name=GV handle=25 attrs=3 mode=pubsub   <- …Platform.GroundVehicle, inherited
```

> ⚠️ If you do use modules, they must be passed at **join**, not create. Modules handed to
> `createFederationExecution` are **silently dropped** — no error, the attributes are simply absent.

---

## 4. What is customised — please fill this in

One row per addition or change versus stock RPR. This is what tells us which tests to write, and
it is also the fastest way for us to spot a translation that cannot work.

| RPR ver | Class | Attribute | Datatype | Bytes | Stock or custom | Notes |
|---|---|---|---|---|---|---|
| 1.0 | `BaseEntity` | `BtkCustomValue` | `double` | 8 | **custom** | *sample row* |
| 1.0 | `BaseEntity` | `BtkCustomTag` | `octet[0+]` | var | **custom** | *sample row* |
| 2.0 | `BaseEntity` | `BtkCustomValue` | `HLAfloat64BE` | 8 | **custom** | *sample row* |
| 2.0 | `BaseEntity` | `BtkCustomTag` | `HLAopaqueData` | var | **custom** | *sample row* |
| | | | | | | ← your rows here |

Also worth stating in prose:

- Which **classes** the broker actually translates (the whole FOM, or a subset?).
- Any attribute whose **datatype differs** between 1.0 and 2.0 — those are the interesting tests.
- Any attribute that is **intentionally dropped** in translation. Those become *negative tests*:
  the harness asserts nothing arrives, which is just as much a requirement as the positive cases.
- Whether a **custom VR-Link FOM Mapper** already exists on the broker side. If so, say where —
  it is the authoritative statement of the encoding and would save us guessing.

---

## 5. Broker mapping — how one row becomes a test

Fill in [`tests/sample-broker-mapping.csv`](../tests/sample-broker-mapping.csv). Each row says
"attribute X on A must become attribute Y on B, transformed like this". Each row then generates
one input row and one expectation row:

```
mapping:  BaseEntity,BtkCustomValue,float64  ->  …BtkCustomEntity,BtkCustomValue,float64,identity

input.csv   1,publish,BaseEntity,1:1:101,BtkCustomValue,1234.5,float64
output.csv  1,…BtkCustomEntity,1:1:101,BtkCustomValue,1234.5,0.001,float64
```

The A-side and B-side **names may differ** — that difference *is* the translation under test.

Transform vocabulary the harness understands today:

| transform | meaning | expectation |
|---|---|---|
| `identity` | bytes unchanged | same value; use `hex` datatype for a byte-exact assertion |
| `rename` | same value, different attribute name | same value under the B-side name |
| `scale:<k>` | numeric multiply (e.g. feet→metres) | input × k, with a `tolerance` |
| `enum:<map>` | enumeration remap | the mapped value |
| `drop` | not translated | **negative test** — no output row for that step |

---

## What happens when these land

Nothing in the harness needs rebuilding. The FOM files are configuration, attribute names come
from the CSV header, and datatypes and offsets come from the **FOM itself** (REQ-09) — the CSV has
no datatype column. Concretely:

1. drop the files into `config/`
2. copy `config/bridge-stock-rpr.profile` to a new `.profile` and point each side at them: the
   `fedFile` / `fom`, the Evolved side's `joinFom` modules (they must match what the broker joins
   with, or federation B receives nothing, silently — see mak-rti-knowledge.md §9), and `class`
3. generate the CSV templates from the FOMs (GUI: Tools › CSV template; CLI: `template`), naming any
   `--variant` / `--array` choices, and fill them in from the mapping table
4. run `BrokerTestKit.Cli.exe entity input.csv expected.csv --profile <your.profile>` (or
   `case <file.testcase> --profile …` for several classes), or in the GUI create a pair with the
   profile's values, add a test case from the filled-in sheets and run it

Do **not** use the CLI's `native` mode for this: it predates profiles and hard-codes federation
names, FOM files and the class, so it cannot pick an RTI, rid or join modules.

The stock-RPR profile already proves the rig **through the broker**
(`tests/native/run-bridge.ps1`). What it cannot prove is your customisations — that needs the
files above.
