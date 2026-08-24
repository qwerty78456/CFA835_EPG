# Changelog

All notable changes are recorded here in reverse chronological order. Dates use the builder's local calendar date; `COMMIT.txt` in each release folder is the authoritative source revision.

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
