# CFA835 System Monitor

A Windows-only system monitor for the Crystalfontz CFA835 USB LCD/keypad/LED module. The main 20x4 screen shows local date/time, total CPU utilization, one selected system temperature, and auto-cycle state. A second page shows aggregate physical-network throughput. A manually reached shutdown page provides confirmation, an adjustable countdown, and cancellation.

Temperature sampling prefers the hottest valid LibreHardwareMonitor/PawnIO reading and falls back to the hottest Windows ACPI thermal zone when no primary reading is available. CPU-only temperature selection remains separate for the thermal-warning LED.

## Display pages

The page order is deliberately small:

1. Main: date/time, `CPU UTIL`, `TEMPERATURE`, and `AUTO` state.
2. Network: receive, transmit, and total Mbps.
3. Shutdown: manual-only shutdown controls.

There are no separate CPU or temperature pages. Auto-cycle alternates between Main and Network and never lands on Shutdown.

## Keypad

- Left/right: previous or next page.
- Enter on Main or Network: toggle automatic cycling.
- Enter on Shutdown: open confirmation.
- Up/down during confirmation: adjust the shutdown delay in five-second increments.
- Left/right during confirmation: select Yes or No.
- Exit: cancel confirmation/countdown, or disable auto-cycle and return to Main.

## Command-line modes

```powershell
.\Cfa835SystemMonitor.exe --diagnose
.\Cfa835SystemMonitor.exe --hardware-test
.\Cfa835SystemMonitor.exe --hardware-test --noninteractive
.\Cfa835SystemMonitor.exe --simulate thermal-90
.\Cfa835SystemMonitor.exe --config C:\path\appsettings.json
```

`--diagnose` reads device/sensor state and prints the four rows currently stored in display RAM without changing the display, keypad configuration, LEDs, or persistent device settings. `--hardware-test` temporarily exercises the display and LEDs, watches keypad presses, and restores the state captured at startup. Only one process can own the CFA835 COM port at a time; stop the running monitor before either hardware mode.

## Runtime prerequisites

1. Windows 10 or later on x64.
2. Crystalfontz CFA735/835 USB virtual-COM driver.
3. Signed normal-edition PawnIO 2.2.0 for LibreHardwareMonitor low-level sensor access.
4. NSSM x64 2.24-101 at `C:\Program Files\nssm\win64\nssm.exe` when installing as a service.

The runtime is a self-contained `win-x64` publish and does not require a separately installed .NET runtime.

## Release builds

The repository pins .NET SDK 10.0.302. From the repository root:

```powershell
.\scripts\Build-Release.ps1
```

The pipeline restores locked dependencies, runs all tests, publishes the self-contained executable, copies operational documentation and service scripts, and writes per-file SHA-256 hashes. Its runtime artifact is an uncompressed, time-stamped folder:

```text
artifacts\Cfa835SystemMonitor-<commit>-win-x64-<yyyyMMdd-HHmmss>\
```

It does not package the runtime as a ZIP. Every release folder contains `COMMIT.txt`, `BUILD-TIMESTAMP.txt`, `SHA256SUMS.txt`, this README, the detailed changelog, and the deployment guide.

## Deployment

See [DEPLOYMENT.md](DEPLOYMENT.md) for fresh installation, foreground operation, service installation/update, rollback, checksum validation, remote-builder synchronization, physical-screen acceptance testing, and troubleshooting.

The shortest service-install path from an elevated PowerShell prompt inside a release folder is:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Install-Service.ps1
```

Persistent configuration is stored at `C:\ProgramData\Cfa835SystemMonitor\appsettings.json`; service logs are stored under `C:\ProgramData\Cfa835SystemMonitor\logs`.

## Development

```powershell
dotnet restore .\Cfa835SystemMonitor.slnx --locked-mode
dotnet test .\Cfa835SystemMonitor.slnx -c Release --no-restore
.\scripts\Build-Release.ps1
```

When intentionally changing dependencies, restore once without `--locked-mode`, review both generated `packages.lock.json` files, and commit them. Locked restore is required for ordinary builds and releases.

The protocol implementation follows the Crystalfontz CFA835 hardware v2.0/firmware v1.6 datasheet and does not write boot-state EEPROM settings.
