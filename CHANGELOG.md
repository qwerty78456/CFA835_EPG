# Changelog

All notable changes are recorded here in reverse chronological order. Dates use the builder's local calendar date; `COMMIT.txt` in each release folder is the authoritative source revision.

## 2026-08-25 — Sensor diagnostics

### Added

- Added `--list-sensors`, which dumps every sensor LibreHardwareMonitor exposes, grouped by hardware, and ends with a verdict. It separates the three reasons a CPU temperature can be missing: a sensor present but reading `0.0` (the ring-0 read was denied), no CPU temperature sensor enumerated at all (unsupported CPU), or a plausible value present (the sensor path works, so an `N/A` on the display is a layout or selection problem). It exits non-zero in the first two cases.
- `--layout-preview` now prints an `Elevated:` line. Previously only `--diagnose` did, so an unelevated preview rendered `cpu.temperature` as its fallback with nothing to indicate that permissions, not the layout, were responsible.
- `TemperatureMonitor` logs a one-time warning when PawnIO is installed, the process is not elevated, and no CPU temperature was obtained. This covers every mode, not just the diagnostics.

### Notes

- Installing PawnIO is necessary but not sufficient for an interactive run: opening `\\?\GLOBALROOT\Device\PawnIO` also requires elevation. When it is denied, LibreHardwareMonitor still enumerates `Core (Tctl/Tdie)` but the sensor reports **0**, not null — which is exactly why `CpuTemperatureSelector` treats a non-positive CPU reading as no reading. The Windows service runs as LocalSystem and is unaffected.
- Confirmed against LibreHardwareMonitor 0.9.6 that AMD family `0x1A` (Zen 5, e.g. Ryzen 7 9800X3D) maps to `Amd17Cpu` and is supported, alongside `0x17` and `0x19`.

## 2026-08-25 — Open source licensing and bundled PawnIO

### Added

- Added an MIT `LICENSE` for the project.
- Bundled the signed **PawnIO 2.2.0** driver installer at `third-party/pawnio/`, so a monitored machine — including an air-gapped one — no longer needs to download it. `Install-Service.ps1` detects the driver through the registry key LibreHardwareMonitor itself uses and runs the bundled installer when it is missing; `-SkipPawnIO` declines.
- The install script verifies the installer's Authenticode signature before launching it and refuses unless the status is `Valid` and the signer is `CN=namazso.eu`. `Build-Release.ps1` performs the same check when packaging, so a tampered binary fails the build rather than reaching a release.
- Shipped `COPYING` (GPLv2) and the corresponding PawnIO source archive for tag `2.2.0` alongside the binary, satisfying GPLv2 section 3(a) without a separate written offer. `third-party/pawnio/README.md` records every SHA-256, the upstream commit, and the verified signer.
- Marked `*.exe`, `*.zip`, `*.pdf`, `*.png`, `*.bin` and `*.sys` as binary in `.gitattributes`. The repository's `text=auto` rule would otherwise be free to rewrite line endings inside the installer, and a single changed byte voids its signature.
- `Build-Release.ps1` now copies `third-party/` and `LICENSE` into each release, and every bundled file appears in `SHA256SUMS.txt`.

### Diagnostics

- `--diagnose` now prints an `Elevated:` line, and warns when PawnIO is installed but the process is not elevated. The registry key only proves the driver is registered; opening `\\?\GLOBALROOT\Device\PawnIO` additionally requires elevation, so an unelevated run previously reported `PawnIO: installed` alongside a missing CPU temperature with nothing to explain the contradiction. The service runs as LocalSystem and was never affected.

### Notes

- Bundling removes the download, not the installation. PawnIO is a kernel-mode driver: LibreHardwareMonitor reaches CPU temperatures by opening `\\?\GLOBALROOT\Device\PawnIO`, which exists only while the driver service is loaded, and loading one requires an administrator plus a Microsoft-attested signature. The PawnIO *modules* were already embedded in `LibreHardwareMonitorLib.dll`, so the driver was the only missing piece.
- Cfa835SystemMonitor communicates with PawnIO solely through its device IO control interface, by way of `LibreHardwareMonitorLib`, and never loads a module over the Pawn interface. PawnIO's special exception therefore applies and the project's MIT licence is unaffected.
- `Uninstall-Service.ps1` still leaves PawnIO in place, and now says why: other hardware-monitoring tools on the same machine may depend on the driver.

## 2026-08-25 — Background DPI fix

### Fixed

- Fixed background artwork being scaled up and clipped. `GrayscaleImage.Load` used `Graphics.DrawImageUnscaled`, which despite its name draws at the image's *physical* size rather than its pixel size. A 244x68 PNG tagged 72 DPI — what most exporters emit — was therefore drawn at 325x91 on the 96 DPI compositing surface, anchored top-left, so roughly the bottom and right thirds fell outside the frame. The pixel-size validation passed because the file genuinely was 244x68. Loading now specifies source and destination rectangles explicitly in `GraphicsUnit.Pixel` with nearest-neighbour interpolation, which is DPI-independent.

### Added

- Allowed `--simulate` alongside `--layout-preview`. On a workstation without PawnIO, `cpu.temperature` previews as `N/A`, which hides whether a real reading fits its box; `--layout-preview preview.png --simulate thermal-90` renders `90C` instead.
- Added command-line parsing tests covering the preview flags, their range checks, and which modes accept `--simulate`.
- Added background-loading tests that assert a pixel-for-pixel copy at 72, 96, and 300 DPI, and that a file of the wrong pixel size is still rejected.

## 2026-08-25 — Updated documentation

Documentation-only pass over every file in the repository. No behavior changed.

### Corrected

- Corrected the shade depth stated throughout the documentation and code comments. The committed datasheet at `tmp/pdfs/CFA835_Datasheet_HW2_FW1.6.pdf` (hardware v2.0 / firmware v1.6, released 2022-08-24) specifies **16 shades from the top 4 bits** of each pixel byte. The previously cited figure of 32 shades from 5 bits comes from the older hardware v1.3 / firmware v1.1 datasheet and does not describe this hardware. Everything else in the graphic command group — 244x68 geometry, the `0-243`/`0-67` valid ranges, and the subcommand 0/1/2/5/7 payloads — is identical across both revisions.
- Documented that `GrayscaleImage.Quantize` masks to 5 bits and therefore keeps one bit more than a v2.0 panel resolves. This is harmless on the wire, and the `0x03` RLE-escape guarantee holds either way, but it does make an anti-aliased `--layout-preview` PNG marginally smoother than the physical display.

### Added

- Recorded the committed datasheet as the authoritative protocol reference in the maintainer guide, and warned that the publicly linked copy is the older revision.
- Added `System.Drawing.Common` and its `Microsoft.Win32.SystemEvents` dependency to the third-party notices, with a note that GDI+ is an operating-system component and that no native imaging library or font file is redistributed.
- Added the font-family requirement for graphic mode to the runtime prerequisites in both the README and the deployment guide, including a verification snippet.
- Added a graphic-mode addendum to the physical acceptance test: artwork drawn without tearing, values inside their boxes, no horizontal jitter on the seconds digits, the resolved font logged at start-up, and an invisible periodic full repaint.
- Added deployment guidance on `layout.json` and background artwork surviving upgrade, rollback, and uninstall, and on reverting `display.mode` when rolling back to a build that predates graphic mode.
- Added troubleshooting entries for exit code 78 on an invalid layout, blank or inverted graphic output, and missing spaces or `?` characters from the printable-ASCII-only glyph set.
- Documented that `--layout-preview` does not open the COM port and so, unlike `--diagnose`, does not require stopping the monitor.
- Documented that `--diagnose` can read back only text written by command 31; the CFA835 cannot report its graphic buffer.
- Documented that a CPU sensor reporting 0 C or below is treated as unreadable.

## 2026-08-25

### Graphic display mode

- Added `display.mode`. The default `text` keeps the existing 20x4 character rendering unchanged; `graphic` drives the CFA835's 244x68 monochrome/greyscale panel directly.
- Added `layout.json`: a separate, operator-editable file describing pages, an optional per-page 244x68 background image, and every text box by pixel rectangle, font size, alignment, and greyscale shade.
- Pages are now data, not an enum. Adding an entry to `layout.pages` adds a page to the keypad navigation ring; `"kind": "shutdown"` keeps a page out of auto-cycling exactly as the built-in shutdown page always has been.
- Bound layout fields to `datetime`, `cpu.utilization`, `cpu.temperature`, `system.temperature`, `net.rx`, `net.tx`, `net.total`, `autocycle`, the three shutdown values, and static `literal` text, each with its own format string and null placeholder.
- Shipped a default layout matching the approved mock-up: large `HH:mm:ss` over small `dd/MM/yyyy` in the upper box, CPU utilization and CPU temperature in the two lower boxes, no labels.
- Made the refresh cadence configurable in milliseconds through `layout.refreshMs`, independent of `sampling.displayMs`.

### Rendering

- Composited background artwork and text on the host and sent the module finished pixel rectangles, so a text box sits on arbitrary artwork without relying on device-side transparency, and no microSD card or on-device font file is required.
- Rasterized every printable ASCII character once per configured size into a glyph atlas at start-up, keeping GDI+ out of the per-frame path.
- Gave digits a shared tabular advance so a running clock does not jitter and each field's transfer rectangle stays a fixed size.
- Diffed at field level: a field is retransmitted only when its formatted text changes, so a ticking clock costs roughly 1.4 KB per second instead of the full 16.6 KB frame.
- Quantized every pixel to a multiple of 8, which guarantees the pixel stream can never contain `0x03`, the RLE escape byte in command 40 subcommand 2.
- Repainted the whole frame every `layout.fullRepaintSeconds` because the raw pixel stream is not CRC-protected and a corrupted rectangle would otherwise persist.

### Device protocol

- Added `ICfaTransport.SendStreamingCommandAsync` for command 40 subcommand 2, whose acknowledgement only arrives after the un-packetized pixel stream. Its timeout scales with payload size and it deliberately never retries, since a retried command packet would be consumed as pixel data.
- Added typed wrappers for command 6 (clear display) and command 40 subcommands 0, 1, 2, 5, and 7.
- Used manual buffer flush in graphic mode so a frame is never shown half-drawn.

### Diagnostics

- Added `--layout-preview [file]`, `--preview-page <id>`, and `--preview-scale <1-16>`, which render a layout page to a PNG with live metrics and no CFA835 attached.
- Extended `--diagnose` to print the resolved display mode and every layout page, background path, and field rectangle.
- Extended `--hardware-test` to push the first graphic page and then outline each field rectangle, so box alignment against the artwork can be judged on the physical panel.
- Validated the layout at start-up so a bad file exits with the configuration exit code 78 instead of failing inside the monitor loop.

### Dependencies

- Added `System.Drawing.Common` for PNG decoding and font rasterization. GDI+ ships with Windows, so the self-contained publish gains no native payload.

## 2026-08-24

### Display and navigation

- Removed the standalone CPU-utilization page. CPU utilization now appears only on row 2 of the main screen, between date/time and temperature.
- Replaced the former `SYSTEM MONITOR` title row with live date/time so all four main-screen rows carry useful state.
- Consolidated temperature reporting into one `TEMPERATURE` value on the main screen.
- Removed the standalone temperature page, per-core sensor rows, temperature pagination, and their keypad-navigation states.
- Reduced the page sequence to Main, Network, and manual-only Shutdown.
- Changed auto-cycle to alternate only between Main and Network while continuing to skip Shutdown.
- Preserved Exit behavior: disable auto-cycle and return directly to Main.

### Temperature selection

- Prefer the hottest finite LibreHardwareMonitor temperature exposed through PawnIO.
- Fall back to the hottest finite Windows ACPI thermal-zone value only when LibreHardwareMonitor/PawnIO has no readable temperature.
- Exclude `Distance to TjMax` readings from system-temperature aggregation because they are offsets, not absolute temperatures.
- Keep CPU-only temperature selection independent for LED thermal-warning thresholds and hysteresis.
- Report the selected system temperature and source in diagnostic mode instead of dumping every discovered sensor.

### Release pipeline

- Replaced runtime and source ZIP generation with an uncompressed, time-stamped `win-x64` release directory.
- Name releases `Cfa835SystemMonitor-<12-character-commit>-win-x64-<yyyyMMdd-HHmmss>` so multiple builds remain independently deployable.
- Add `BUILD-TIMESTAMP.txt` and retain `COMMIT.txt` for provenance.
- Generate `SHA256SUMS.txt` inside each release with a SHA-256 entry for every shipped file.
- Include `README.md`, `DEPLOYMENT.md`, `CHANGELOG.md`, third-party notices, and all service-management scripts in every runtime release.

### Documentation

- Added an exhaustive Windows deployment guide covering artifact validation, foreground and NSSM service deployment, upgrades, rollbacks, remote Windows builds, physical-screen acceptance, and troubleshooting.
- Updated the README to document the exact screen/page model and the uncompressed release format.
- Updated the repository maintainer guide to remove stale references to per-sensor pages and old pinned artifacts.
- Updated third-party notices to describe redistribution in a release directory rather than an archive.

### Tests and verification

- Extended read-only diagnostics to print all four rows read back from CFA835 display RAM, allowing the deployed physical layout to be verified without trusting process state alone.
- Updated page-navigation tests for the three-page sequence.
- Assert that auto-cycle visits only Main and Network.
- Retained exact 20-character ASCII row validation across every renderable page and shutdown state.
- Retained main-screen assertions for date/time, CPU utilization, selected temperature, and auto-cycle status.
