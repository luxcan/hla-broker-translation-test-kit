# config — RTI / FOM files for the harness

`RTI_CONFIG` should point at **this folder** (the CLI sets it automatically by
walking up from the exe to find `config/`, or honor `BTK_RTI_CONFIG`).

The MAK RTI / VR-Link look for these in the working dir, then in `RTI_CONFIG`.

## What lives here

- **`rid.mtl`** — the harness RID (provided). See comments inside for the choices and
  the optional reliable / single-host-isolation blocks.
- **`MAK-RPR1-1-1.fed`** — Federation A FOM: **HLA 1.3 + RPR FOM 1.0** (copied from
  `C:\MAK\vrlink5.9\bin64`). Used by PRP-1.
- **`RPR_FOM_v2.0_1516-2010.xml`** — Federation B FOM: **HLA Evolved + RPR FOM 2.0**
  (released SISO RPR 2.0). Used from PRP-2 on. (May pull in the standard MIM; FOM modules
  added if needed at PRP-2.)

The broker under test translates **RPR 1.0 (HLA 1.3) → RPR 2.0 (HLA Evolved)**. Both FOMs
ship with MAK — no download needed. These copies pin the test to known FOM versions.

### Verification (2026-06-29)
- **`MAK-RPR1-1-1.fed`** = RPR 1.0 (`FEDversion v1.3`, root `ObjectRoot`) **+ MAK extensions**
  (IFF, GriddedData, Logger Control, DI-Guy, AggregateEntity kept…) — not stock SISO RPR 1.0.
  **SHA-256-identical** to the broker's copy (`vrexchange2.5.1\bin\MAK-RPR1-1-1.fed`) and to
  `vrlink5.9\bin64`. → Fed A is in sync with the broker. ✅
- **`RPR_FOM_v2.0_1516-2010.xml`** = SISO-STD-001.1-2015 RPR FOM, `<version>2.0</version>`,
  root `HLAobjectRoot`. The broker bin ships this exact file too. ✅
- ⚠️ TO CONFIRM: which RPR 2.0 Evolved file the **broker config** actually loads for Fed B —
  pristine `RPR_FOM_v2.0_1516-2010.xml` vs a MAK draft-17 `MAK-RPR20017-*_evolved.xml`.
  Match `config/` to the broker's choice.

## Things to confirm for your environment

1. **MAK RTI version = 4.6.1 (VC14)** (chosen to match VR-Link 5.9, which is VC14).
   The harness and the broker must both use it. Make 4.6.1 the active RTI via the
   **RTI Chooser** (`Desktop\POC\select-rti-4.6.1.bat`); the CLI also prepends
   `C:\MAK\makRti4.6.1\bin` to PATH. ⚠️ RTI 4.3 / 4.4.2 are **VC10** and are NOT
   compatible with VR-Link 5.9 (confirmed by PRP-1: "Expected VC14 but found VC10").
   The VR-Exchange broker must likewise be **VC14**.
2. **Connection settings** — `rid.mtl` sets `RTI_configureConnectionWithRid 1`, so the
   broker (and any other federates) must use the **same ports / dest address** to join
   the same federations. If your existing setup is GUI-configured, either match these
   values or set `RTI_configureConnectionWithRid 0` to inherit the GUI config.
3. **rtiexec** — not needed in the default lightweight mode. Enable OPTIONAL A in
   `rid.mtl` (and start an rtiexec per federation) if you see dropped-update timeouts.

## Sample / template FOMs

Placeholders for the project's real customised FOMs. Each is a **working sample of the shape
we need back** — see [docs/FOM-HANDOVER.md](../docs/FOM-HANDOVER.md).

| file | HLA | what it is |
|---|---|---|
| `BTK-CustomRPR1.fed` | 1.3 | MAK RPR 1.0 `.fed` + 2 custom attributes on `BaseEntity` |
| `BTK-CustomRPR1.omt` | 1.3 | the matching OMT — **the only place 1.3 datatypes exist** |
| `BTK-CustomRPR2.xml` | Evolved | RPR 2.0 FDD **merged** with the same 2 custom attributes |
| `BTK-CustomRPR2-module.xml` | Evolved | the same additions as a FOM **module** (new subclass) |
| `rid.mtl` | both | harness-owned RTI config (`RTI_useRtiExec 0`, 1.3/1516 interop on) |

**Merged FDD vs module — not interchangeable.** A module may add *new subclasses* only; it may
not add attributes to a class another module already defines. If the real customisation extends
existing RPR classes, it must be a single merged FDD. Modules also have to be passed at **join**
(`joinfom=`) — handed to create they are silently dropped.

Datatypes live in different places per HLA version, which is why 1.3 needs two files:

```
BTK-CustomRPR1.fed   datatype mentions:   0     <- structure only
BTK-CustomRPR1.omt   DataType mentions: 912     <- the datatypes
BTK-CustomRPR2.xml   <dataType> inline           <- FDD carries both
```
