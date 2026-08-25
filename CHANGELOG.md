# Changelog

All notable changes are recorded here in reverse chronological order. Dates use the builder's local calendar date; `COMMIT.txt` in each release folder is the authoritative source revision.

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
- Quantized every pixel to the panel's 32 shades, which also guarantees the pixel stream can never contain `0x03`, the RLE escape byte in command 40 subcommand 2.
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
