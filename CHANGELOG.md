# Changelog

## 2026-08-24

### Changed

- Consolidated temperature reporting into one `TEMPERATURE` value on the main system-monitor page.
- Prefer the hottest valid LibreHardwareMonitor/PawnIO temperature and use the hottest Windows ACPI thermal zone only when the primary path has no readable value.
- Exclude `Distance to TjMax` values from system-temperature aggregation.
- Removed the separate temperature page, per-core sensor rows, temperature pagination, and related keypad navigation.
- Kept CPU-only temperature selection independent for thermal-warning LED behavior.

### Diagnostics

- Report the selected system temperature and its source instead of listing every discovered sensor.
