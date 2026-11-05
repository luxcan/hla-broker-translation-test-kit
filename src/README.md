# src — HLA Broker Translation Test Kit (code)

See [../docs/DESIGN.md](../docs/DESIGN.md) for the design and
[../docs/vr-link-knowledge.md](../docs/vr-link-knowledge.md) for the VR-Link API notes.

## Projects (planned layout)

- **BrokerTestKit.Cli** — headless runner. Currently hosts **PRP-1** (join + resign
  Federation A). Will grow into the CI runner over the engine.
- *(later)* `BrokerTestKit.Engine` — class library: the two `ExerciseConnection`s, the
  §9.1 serialization loop, the codec, CSV parse/compare.
- *(later)* `BrokerTestKit.Gui` — thin **WPF** shell over the engine.

## PRP-1: join + resign Federation A (HLA 1.3)

Smallest proof: join Federation A, pump a few cycles, resign cleanly. Flushes out
licensing, native-DLL/PATH setup, and `.fed`/RID resolution before anything else.

### Prerequisites (hard requirements)
- **Windows x64**, **.NET Framework 4.6.2**, Visual Studio 2015+ / MSBuild 14+.
- VR-Link 5.9 installed at `C:\MAK\vrlink5.9` (needed at **runtime** for the native
  VR-Link + RTI DLLs). The managed `vrLinkSharp.dll` is **vendored** in `lib/vrlink/`
  and Copy-Local'd next to the exe — keep it in sync with the install on VR-Link upgrades.
- A valid **VR-Link license** (FlexLM) reachable.
- The MAK RTI reachable; if your federation needs `rtiexec`, start it first.

### Configure
- **RTI config** lives in [../config/](../config/): `rid.mtl` is provided; **drop your
  Federation A `.fed` there**. The CLI auto-points `RTI_CONFIG` at `config/` (walks up
  from the exe; override with env var `BTK_RTI_CONFIG`). See [../config/README.md](../config/README.md).
- Edit the `CONFIG` constants at the top of
  [BrokerTestKit.Cli/Program.cs](BrokerTestKit.Cli/Program.cs):
  `FederationName`, `FederateType`, `FedFile`, `RprVersion` — match Federation A.
- Use the **same MAK RTI version as the broker** (on `PATH` at runtime); 4.6.1 recommended.

### Build & run
```
msbuild HLABrokerTranslationTestKit.sln /p:Configuration=Debug /p:Platform=x64
src\BrokerTestKit.Cli\bin\x64\Debug\BrokerTestKit.Cli.exe
```
Expected output ends with `JOINED.` → `drainInput x5 OK` → `RESIGNED (Dispose).`

### Notes / gotchas
- Must run **x64** (mixed-mode `vrLinkSharp.dll`); the program aborts if launched x86.
- Native deps (`managedInterface*.dll`, RTI DLLs) are located via `SetDllDirectory` +
  PATH pointing at `bin64` — no need to copy them next to the exe.
- A license/native-load failure surfaces at the `CheckVRLinkLicense()` probe or at
  `new ExerciseConnection(...)` with the exception text.
- API verified by reflection: HLA 1.3 identity is `federateType` (there is **no**
  `federateName`); `CheckVRLinkLicense()` is **static**.

## BrokerTestKit.AgentNative (C++)

Two raw-RTI agents — `btk-agent13.exe` (HLA 1.3) and `btk-agent1516e.exe` (Evolved). They exist
because the C# VR-Link binding has no encoder for **custom** attributes, so those never reached
the wire. The native agents move exact bytes, resolved by name at runtime with no FOM compiled in.

Built separately (not in the `.sln`):

```bat
src\BrokerTestKit.AgentNativeuild-native.bat
tests
ativeun-smoke.bat
```

See [AgentNative/README.md](BrokerTestKit.AgentNative/README.md) for the line protocol.

## Engine: managed vs native paths

| | managed | native |
|---|---|---|
| agent host | `AgentHost` | `NativeAgentHost` |
| runner | `TestRunner` | `NativeTestRunner` |
| observed value | decoded `ObservedEntity` (x/y/z/marking) | raw `byte[]` per attribute |
| custom attributes | ✗ | ✓ |
| CLI mode | `run` | `native` |

The managed path is kept because it is a useful cross-check for *modelled* RPR state — if the two
disagree about a stock attribute, one of them is wrong and that is worth knowing.
