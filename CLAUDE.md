# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository layout

This directory contains two sibling folders, not one project root:

- `Cfa835SystemMonitor-640d1d649e28-source/` — the actual C# solution. All development happens here.
- `Cfa835SystemMonitor-640d1d649e28-win-x64/` — a pre-built, self-contained `win-x64` publish output of that same source (commit `640d1d649e28`, see `COMMIT.txt`). Treat it as a build artifact, not source; don't hand-edit files in it.

There is no git repository at the top level or inside the source folder (`git status` fails with "not a git repository"). `scripts/Build-Release.ps1` shells out to `git rev-parse`/`git archive` and will fail until the source folder is initialized as a git repo with at least one commit.

All commands below assume the working directory is `Cfa835SystemMonitor-640d1d649e28-source/`.

## What this project is

A Windows-only .NET 10 service/console app that drives a Crystalfontz CFA835 USB LCD+keypad+LED module as a system monitor: date/time, total CPU%, every LibreHardwareMonitor temperature sensor, and aggregate physical-NIC throughput on a 4-line/20-column text display, with 4 bi-color LEDs for power/disk/network/CPU-thermal state. The keypad cycles pages.

## Commands

```powershell
# Restore using the committed lock file (do this normally)
dotnet restore .\Cfa835SystemMonitor.slnx --locked-mode

# Run all tests
dotnet test .\Cfa835SystemMonitor.slnx -c Release --no-restore

# Run a single test (xunit, by fully-qualified name or filter expression)
dotnet test .\Cfa835SystemMonitor.slnx --no-restore --filter "FullyQualifiedName~ProtocolTests.ParserResynchronizesAfterNoiseAndBadCrc"

# Full release build: restore --locked-mode, test, publish win-x64 self-contained, zip artifacts + SHA256SUMS
.\scripts\Build-Release.ps1
```

When intentionally changing dependencies: restore once *without* `--locked-mode` to regenerate `packages.lock.json` in `src/.../` and `tests/.../`, review the diff, then commit it. `--locked-mode` restores must otherwise always be used (matches CI and `Build-Release.ps1`).

The app itself (`Cfa835SystemMonitor.exe`) has its own diagnostic CLI modes, useful when iterating against real hardware:

```powershell
.\Cfa835SystemMonitor.exe --diagnose                              # read-only device + sensor probe, no display/LED/keypad changes
.\Cfa835SystemMonitor.exe --hardware-test                         # exercises LCD/LEDs/keypad, restores prior state after
.\Cfa835SystemMonitor.exe --hardware-test --noninteractive         # timed variant for unattended acceptance testing
.\Cfa835SystemMonitor.exe --simulate thermal-90                   # fake a metric scenario instead of reading real hardware (interactive sessions only)
.\Cfa835SystemMonitor.exe --config C:\path\appsettings.json
```

Simulation scenarios: `thermal-89|thermal-90|thermal-92|disk|network-rx|network-tx|network-both`. `--simulate` throws if the mode isn't the default monitor mode, or if `Environment.UserInteractive` is false (i.e. running as a Windows service).

Pinned SDK: `.NET 10.0.302` (`global.json`, `rollForward: latestPatch`). Target framework is `net10.0-windows`; nullable + implicit usings are enabled solution-wide via `Directory.Build.props`.

## Architecture

Two projects: `src/Cfa835SystemMonitor` (the app) and `tests/Cfa835SystemMonitor.Tests` (xunit, references the app project directly — no separate testable-core library).

### Layered flow

```
Program.cs            → parses argv, loads config, wires logging/cancellation, dispatches to a mode
MonitorApplication.cs → the three top-level modes: RunAsync (service loop), DiagnoseAsync, HardwareTestAsync
CfaTransport.cs        → ICfaTransport / SerialCfaTransport: raw serial I/O, framing via CfaPacketParser, request/response correlation
CfaProtocol.cs          → CfaPacket: wire encode/decode + CRC-16/CCITT (reversed) per the CFA835 datasheet
CfaDevice.cs             → CfaDeviceLocator (registry-based USB VID/PID/serial → COM port resolution) and Cfa835Device (typed command wrappers: rows, LEDs, keymasks, version)
WindowsMetrics.cs         → IMetricSource / WindowsMetricSource: CPU% (GetSystemTimes), disk activity (PDH), NIC throughput (GetIfTable2 via iphlpapi), temperatures (LibreHardwareMonitor)
DisplayAndLeds.cs          → PageController (keypad/auto-cycle state machine + page rendering), ScreenWriter (diff-based row writes), LedStateMachine (LED policy), SimulationMetricSource
Models.cs                   → CfaKey enum, TemperatureReading/InterfaceReading/MetricSnapshot records, IMetricSource interface
Configuration.cs             → MonitorOptions and sub-option records, JSON config loading/validation, CommandLineOptions parsing
```

Key design points:

- **`RunAsync` owns a reconnect loop.** Each iteration creates a fresh `SerialCfaTransport` + `Cfa835Device` and reconnects with exponential backoff (1s → 30s cap) on any failure, so the outer loop — not individual methods — is where device-loss resilience lives.
- **Two sampling cadences run independently in the same tick.** `WindowsMetricSource.Sample` refreshes CPU%/temperatures only every `sampling.temperatureMs` (LibreHardwareMonitor sensor polling is comparatively expensive), while disk/network are sampled every call. The display itself repaints on the `sampling.displayMs` cadence or immediately when `forceRender` is set (keypad input, page auto-advance). The activity loop tick rate is `sampling.activityMs`.
- **`ScreenWriter` only writes rows that changed** (byte-for-byte diff against the last-written row) to minimize serial traffic; `PageController.Render` is otherwise stateless per call.
- **`IMetricSource` is the seam for `--simulate`.** `SimulationMetricSource` wraps a real `WindowsMetricSource` and overrides only the fields a scenario cares about (see `DisplayAndLeds.cs`), leaving everything else pass-through — this is also the pattern to follow for injecting fakes in tests.
- **CFA835 packet framing**: `CfaPacket.Type` packs class into its top 2 bits (`0x00` command from host, `0x40` ACK, `0x80` unsolicited report, `0xC0` NAK/error) and command code into the low 6 bits. `CfaPacketParser.Feed` is a resynchronizing byte-stream parser — on CRC mismatch it doesn't just drop one packet, it scans forward for the next byte offset that yields a valid CRC, so partial/corrupt reads self-heal without disconnecting.
- **`SerialCfaTransport.SendCommandAsync`** serializes all commands through a single semaphore (one in-flight command at a time), retries up to 3 times on a 750ms timeout, and correlates responses to the pending command purely by matching command code + packet class (no sequence numbers in this protocol).
- **LED GPIO mapping is hardcoded** in `Cfa835Device.LedGpio` (green/red GPIO pin pairs per logical LED 0–3) per the datasheet; `SetLedAsync` caches last-written levels per LED to skip redundant writes.
- **Registry-based device discovery**: `CfaDeviceLocator` looks up `HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_xxxx&PID_xxxx\<serial>\Device Parameters\PortName`, filtered by configured serial, falling back to `device.fallbackPort` if no match is present in `SerialPort.GetPortNames()`.
- **Configuration** (`appsettings.json`) is loaded from `%ProgramData%\Cfa835SystemMonitor\appsettings.json` when installed as a service, else `AppContext.BaseDirectory`, else `--config <path>`; every options group self-validates range/format on load and throws `InvalidDataException` (mapped to exit code 78) on bad values.
- **`--diagnose` and `--hardware-test` are read-only/restore-on-exit by design** — `--diagnose` never touches display/LED/keymask state; `--hardware-test` snapshots keymasks/rows/LEDs before running and restores them in a `finally`, even on timeout.
- Native interop (P/Invoke) is used directly for CPU times (`kernel32!GetSystemTimes`), disk throughput (`pdh.dll`), and NIC stats (`iphlpapi!GetIfTable2`) rather than going through WMI/PerformanceCounter, for lower overhead in the tight polling loop.

### Tests

`tests/Cfa835SystemMonitor.Tests` covers the protocol (`ProtocolTests.cs`: CRC vectors, fragmented/resynchronizing parsing, device command mapping via a hand-written `FakeTransport`), display/paging (`DisplayTests.cs`), and metrics/LED policy (`MetricsAndLedTests.cs`). No mocking framework is used — fakes are small hand-rolled classes implementing the relevant interface (`ICfaTransport`, `INetworkCounterProvider`, etc.).

## Runtime prerequisites (not needed to build/test, only to run against real hardware)

1. Crystalfontz CFA735/835 USB virtual-COM driver.
2. Signed normal-edition PawnIO 2.2.0 (temperature sensor access via LibreHardwareMonitor).
3. NSSM x64 2.24-101 at `C:\Program Files\nssm\win64\nssm.exe` for the Windows service scripts (`scripts/Install-Service.ps1`, `Update-Service.ps1`, `Uninstall-Service.ps1`).
