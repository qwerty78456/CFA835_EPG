# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository layout

The common workstation layout contains two sibling folders, not one project root:

- `Cfa835SystemMonitor-640d1d649e28-source/` — the actual C# solution. All development happens here.
- `Cfa835SystemMonitor-640d1d649e28-win-x64/` — a storage root for immutable, timestamped, self-contained `win-x64` release folders. Treat every release as an artifact, not source; do not hand-edit it.

There is no git repository at the top level, but `Cfa835SystemMonitor-640d1d649e28-source/` is its own git repo (`origin` → `https://github.com/qwerty78456/CFA835_EPG.git`, branch `main`). Never assume a running process matches the source checkout or newest release directory: resolve its executable path and embedded ProductVersion.

All commands below assume the working directory is `Cfa835SystemMonitor-640d1d649e28-source/`.

`tmp/pdfs/CFA835_Datasheet_HW2_FW1.6.pdf` is committed and is the authoritative protocol reference for the hardware this drives (release 2022-08-24, hardware v2.0 / firmware v1.6). Check it — not a web copy — before changing anything on the wire; the publicly linked datasheet is the older hardware v1.3 / firmware v1.1 revision and differs, notably on shade depth. `tmp/pdfs/rendered/` holds page images of the command-set sections.

## What this project is

A Windows-only .NET 10 service/console app that drives a Crystalfontz CFA835 USB LCD+keypad+LED module.

The module is a 244x68-pixel, 16-shade greyscale graphic panel; its 20x4 character API is one layer on top of that framebuffer. `display.mode` picks which layer the app drives:

- `text` (default) — the 4-line/20-column screen: date/time, total CPU utilization, one selected system temperature, and auto-cycle state, plus an aggregate physical-NIC throughput page.
- `graphic` — host-composited frames described by `layout.json`: an optional 244x68 background image per page with text boxes placed by pixel rectangle.

Both modes share the same keypad model. A manually reachable Shutdown page confirms, adjusts the countdown, and shells out to `shutdown.exe`. The four bi-color LEDs show power, disk, network, and CPU thermal state.

## Commands

```powershell
# Restore using the committed lock file (do this normally)
dotnet restore .\Cfa835SystemMonitor.slnx --locked-mode

# Run all tests
dotnet test .\Cfa835SystemMonitor.slnx -c Release --no-restore

# Run a single test (xunit, by fully-qualified name or filter expression)
dotnet test .\Cfa835SystemMonitor.slnx --no-restore --filter "FullyQualifiedName~ProtocolTests.ParserResynchronizesAfterNoiseAndBadCrc"

# Full release build: locked restore, tests, and an uncompressed timestamped win-x64 folder + per-file SHA256SUMS
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
.\Cfa835SystemMonitor.exe --layout-preview out.png --preview-page DateTime --preview-scale 6   # render a graphic layout page to PNG; no device needed
```

Simulation scenarios: `thermal-89|thermal-90|thermal-92|disk|network-rx|network-tx|network-both`. `--simulate` is accepted only in the default monitor mode and with `--layout-preview`; in monitor mode it additionally throws when `Environment.UserInteractive` is false (i.e. running as a Windows service). Pairing it with `--layout-preview` is how a layout gets checked on a workstation that has no PawnIO driver, where `cpu.temperature` would otherwise render as `N/A`.

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
DisplayAndLeds.cs          → PageController (keypad/auto-cycle state machine over a PageDescriptor ring + text page rendering, including the Shutdown page's Confirm/CountingDown sub-states), ScreenWriter (diff-based row writes), LedStateMachine (LED policy), SimulationMetricSource
Layout.cs                   → layout.json model: LayoutDocument/LayoutPage/LayoutField, source+format+rectangle validation, ShutdownTemplates
GraphicRendering.cs          → GrayscaleImage (PNG decode, shade quantize, preview PNG), IGlyphSource/GdiGlyphSource (start-up glyph atlas), FrameComposer (host-side compositing + field-level diff)
GraphicWriter.cs              → GraphicRuntime (layout + atlas + backgrounds, built once), GraphicScreenWriter (rectangle pushes + one buffer flush per frame)
Models.cs                   → CfaKey enum, TemperatureReading/InterfaceReading/MetricSnapshot records, IMetricSource interface
Configuration.cs             → MonitorOptions and sub-option records, JSON config loading/validation, CommandLineOptions parsing
ShutdownExecutor.cs          → IShutdownExecutor / WindowsShutdownExecutor: shells out to shutdown.exe /s and /a; never throws (runs on the transport's key-event thread)
```

Key design points:

- **`RunAsync` owns a reconnect loop.** Each iteration creates a fresh `SerialCfaTransport` + `Cfa835Device` and reconnects with exponential backoff (1s → 30s cap) on any failure, so the outer loop — not individual methods — is where device-loss resilience lives.
- **Two sampling cadences run independently in the same tick.** `WindowsMetricSource.Sample` refreshes CPU%/temperatures only every `sampling.temperatureMs` (LibreHardwareMonitor sensor polling is comparatively expensive), while disk/network are sampled every call. The display itself repaints on the `sampling.displayMs` cadence or immediately when `forceRender` is set (keypad input, page auto-advance). The activity loop tick rate is `sampling.activityMs`.
- **`ScreenWriter` only writes rows that changed** (byte-for-byte diff against the last-written row) to minimize serial traffic; `PageController.Render` is otherwise stateless per call.
- **`display.mode` selects one of two rendering stacks.** `text` (default) is the historical 20x4 path through `ScreenWriter` and command `0x1F`. `graphic` composites 244x68 greyscale frames on the host from `layout.json` and pushes rectangles with command `0x28` subcommand 2. **Never mix the two on one page**: `0x1F` bypasses the graphic buffer flush and paints an opaque character cell, so it would tear through composited artwork.
- **The ACPI thermal-zone fallback never feeds `HottestCpuC`.** `TemperatureReadingSelector.SelectSystem` derives `hottestCpu` from the LibreHardwareMonitor list only; the ACPI list is passed separately and is consulted solely for `SystemTemperature` (and its readings are constructed with `IsCpu = false`, so `CpuTemperatureSelector.Hottest` would drop them anyway). So the `cpu.temperature` layout source and the thermal-warning LED require PawnIO, while `system.temperature` degrades to ACPI. Without PawnIO the expected output is a system temperature plus `N/A` for CPU temperature.
- **Background PNGs are copied by pixel, never by physical size.** `GrayscaleImage.Load` passes explicit source and destination rectangles in `GraphicsUnit.Pixel`. Do not reach for `Graphics.DrawImageUnscaled` or `DrawImage(image, x, y)`: both honour the file's DPI tag, so a 244x68 PNG exported at 72 DPI renders as 325x91 and spills off the frame while the pixel-size validation still passes.
- **Graphic mode composites on the host, not the device.** `FrameComposer` crops the page background to a field rectangle, blits glyphs over it, and hands `GraphicScreenWriter` a finished `byte[width*height]`. This is why no microSD card, on-device BMP, or CFA835 font file is needed, and why text can sit on arbitrary artwork without the device-side transparency flag.
- **The glyph atlas is built once at start-up**, in `GdiGlyphSource.Create`, for every distinct `sizePx` in the layout. Rasterizing per frame would put GDI+ in the hot path of a Session 0 service; rasterizing once keeps per-frame work to array blits. Digits share the widest digit advance (tabular) so clocks do not jitter and dirty rectangles stay fixed-size. A space's advance is measured as `"n n"` minus `"nn"` because `GenericTypographic` reports ~0 for a lone space.
- **Graphic writes diff at field level, not pixel level.** A field is only retransmitted when its formatted string changes; a page or shutdown sub-state change forces the background plus every field. `layout.fullRepaintSeconds` repaints periodically because the subcommand-2 pixel stream is **not CRC-protected** — a corrupted rectangle would otherwise stay on the panel indefinitely.
- **`SendStreamingCommandAsync` is deliberately retry-free.** The CFA835 only acknowledges subcommand 2 after the whole un-packetized pixel stream arrives, so a retried command packet would be consumed as pixel data. Its timeout scales with payload size instead of the fixed 750 ms that packetized commands get. Every pixel is quantized to a multiple of 8, which also guarantees the stream never contains `0x03`, the RLE escape byte.
- **Shade depth differs between hardware revisions.** Hardware v2.0 / firmware v1.6 renders **16 shades** from the top 4 bits of each pixel byte; the older hardware v1.3 / firmware v1.1 datasheet documented 32 shades from the top 5 bits. `GrayscaleImage.Quantize` currently masks with `0xF8` (5 bits), which is safe on both but keeps one more bit than a v2.0 panel resolves, so a `--layout-preview` PNG is marginally smoother than the physical display. Everything else in the graphic command group — the 244x68 geometry, the `0-243`/`0-67` valid ranges, and the subcommand 0/1/2/5/7 payloads — is identical across both revisions.
- **Pages are data in graphic mode.** `PageController` navigates an `IReadOnlyList<PageDescriptor>`; text mode derives that list from `PageCategory`, graphic mode from `layout.pages`, so operators add pages by editing JSON. `PageController.Category` is a derived convenience for the text path only.
- **Display categories are Main, Network, and Shutdown.** CPU utilization and the selected system temperature exist only on Main. There are no standalone CPU or temperature pages. Auto-cycle alternates Main/Network and skips Shutdown; manual Left/Right navigation reaches all three.
- **`IMetricSource` is the seam for `--simulate`.** `SimulationMetricSource` wraps a real `WindowsMetricSource` and overrides only the fields a scenario cares about (see `DisplayAndLeds.cs`), leaving everything else pass-through — this is also the pattern to follow for injecting fakes in tests.
- **CFA835 packet framing**: `CfaPacket.Type` packs class into its top 2 bits (`0x00` command from host, `0x40` ACK, `0x80` unsolicited report, `0xC0` NAK/error) and command code into the low 6 bits. `CfaPacketParser.Feed` is a resynchronizing byte-stream parser — on CRC mismatch it doesn't just drop one packet, it scans forward for the next byte offset that yields a valid CRC, so partial/corrupt reads self-heal without disconnecting.
- **`SerialCfaTransport.SendCommandAsync`** serializes all commands through a single semaphore (one in-flight command at a time), retries up to 3 times on a 750ms timeout, and correlates responses to the pending command purely by matching command code + packet class (no sequence numbers in this protocol).
- **LED GPIO mapping is hardcoded** in `Cfa835Device.LedGpio` (green/red GPIO pin pairs per logical LED 0–3) per the datasheet; `SetLedAsync` caches last-written levels per LED to skip redundant writes.
- **Registry-based device discovery**: `CfaDeviceLocator` looks up `HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_xxxx&PID_xxxx\<serial>\Device Parameters\PortName`, filtered by configured serial, falling back to `device.fallbackPort` if no match is present in `SerialPort.GetPortNames()`.
- **Configuration** (`appsettings.json`) is loaded from `%ProgramData%\Cfa835SystemMonitor\appsettings.json` when installed as a service, else `AppContext.BaseDirectory`, else `--config <path>`; every options group self-validates range/format on load and throws `InvalidDataException` (mapped to exit code 78) on bad values.
- **The Shutdown page is opt-in only.** `PageController` excludes `PageCategory.Shutdown` from auto-cycling — it's reachable only by manually paging to it — and its state machine (`Idle → Confirm → CountingDown`) lets Left/Right adjust the countdown (`ShutdownOptions.MinCountdownSeconds`/`MaxCountdownSeconds`) before Enter commits to `IShutdownExecutor.RequestShutdown`; any key during the countdown calls `Abort()` (`shutdown /a`).
- **`--diagnose` and `--hardware-test` are read-only/restore-on-exit by design** — `--diagnose` reads and prints display RAM but never writes display/LED/keymask state; `--hardware-test` snapshots keymasks/rows/LEDs before running and restores them in a `finally`, even on timeout.
- **A release is not deployed until the live process is verified.** Releases are uncompressed timestamped folders containing `COMMIT.txt`, `BUILD-TIMESTAMP.txt`, and `SHA256SUMS.txt`. After switching builds, resolve the live process path/ProductVersion and complete the physical-screen sequence in `DEPLOYMENT.md`; never restart the previous build after validating the new one.
- Native interop (P/Invoke) is used directly for CPU times (`kernel32!GetSystemTimes`), disk throughput (`pdh.dll`), and NIC stats (`iphlpapi!GetIfTable2`) rather than going through WMI/PerformanceCounter, for lower overhead in the tight polling loop.

### Tests

`tests/Cfa835SystemMonitor.Tests` covers the protocol (`ProtocolTests.cs`: CRC vectors, fragmented/resynchronizing parsing, device command mapping and the `0x28` subcommands via a hand-written `FakeTransport`), display/paging (`DisplayTests.cs`, including layout-driven page rings), layout parsing and validation (`LayoutTests.cs`), graphic composition and DPI-independent background loading (`GraphicRenderingTests.cs`), argument parsing (`CommandLineTests.cs`), and metrics/LED policy (`MetricsAndLedTests.cs`). No mocking framework is used — fakes are small hand-rolled classes implementing the relevant interface (`ICfaTransport`, `INetworkCounterProvider`, `IGlyphSource`, etc.). `GraphicRenderingTests` composes against a deterministic block-font `IGlyphSource` so assertions are byte-exact and GDI+ stays out of most of the run; the two tests that do exercise `GdiGlyphSource` deliberately request an unknown family so they fall back to generic sans-serif and do not depend on which fonts a machine has installed.

## Licensing

The project is MIT (`LICENSE`). `third-party/pawnio/` redistributes the **PawnIO 2.2.0** signed driver installer under **GPLv2 with a device-IOCTL exception**, alongside `COPYING` and the corresponding source archive — the source is what satisfies GPLv2 §3, so **never ship the installer without the rest of that directory**. The exception applies because this app reaches the driver solely through its IOCTL interface (via `LibreHardwareMonitorLib`) and never loads a module over the Pawn interface; that is what keeps the project MIT rather than GPL. Do not modify `PawnIO_setup.exe` — any byte change, including a line-ending rewrite, voids its Authenticode signature and Windows will refuse to load the driver. `.gitattributes` marks `*.exe` binary for exactly this reason. When bumping the version, update the binary, the source archive, `COPYING`, and the hash table in `third-party/pawnio/README.md` together.

## Runtime prerequisites (not needed to build/test, only to run against real hardware)

1. Crystalfontz CFA735/835 USB virtual-COM driver.
2. Signed normal-edition PawnIO 2.2.0 (temperature sensor access via LibreHardwareMonitor). Bundled at `third-party/pawnio/PawnIO_setup.exe` and installed by `Install-Service.ps1` when absent, which verifies the Authenticode signer is `CN=namazso.eu` first and refuses otherwise. Bundling removes the download, **not** the install: it is a kernel driver, so it needs an admin-registered service and a Microsoft-attested signature. The PawnIO *modules* are already embedded in `LibreHardwareMonitorLib.dll`, so the driver is the only missing piece.
3. NSSM x64 2.24-101 at `C:\Program Files\nssm\win64\nssm.exe` for the Windows service scripts (`scripts/Install-Service.ps1`, `Update-Service.ps1`, `Uninstall-Service.ps1`).

See `DEPLOYMENT.md` for artifact validation, foreground and service installation, update/rollback, remote Windows building, live-process verification, and physical CFA835 acceptance.
