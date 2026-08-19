# CFA835 System Monitor

A Windows system monitor for the Crystalfontz CFA835. It displays the local date/time, total CPU use, every temperature exposed by LibreHardwareMonitor, and aggregate physical-network throughput. The keypad navigates the pages and the four bi-color LEDs show power, disk, network, and CPU thermal state.

## Keypad

- Left/right: previous or next category.
- Up/down: temperature subpages.
- Enter: toggle five-second automatic cycling.
- Exit: turn off automatic cycling and return to date/time.

## Command-line modes

```powershell
.\Cfa835SystemMonitor.exe --diagnose
.\Cfa835SystemMonitor.exe --hardware-test
.\Cfa835SystemMonitor.exe --hardware-test --noninteractive
.\Cfa835SystemMonitor.exe --simulate thermal-90
.\Cfa835SystemMonitor.exe --config C:\path\appsettings.json
```

`--diagnose` does not change the display, keypad configuration, LEDs, or persistent device settings. `--hardware-test` temporarily exercises the display and LEDs, watches keypad presses, and restores the state it read at startup.

## Prerequisites on the monitored PC

1. Crystalfontz CFA735/835 USB virtual-COM driver.
2. Signed normal-edition PawnIO 2.2.0 for low-level temperature sensors.
3. NSSM x64 2.24-101 at a fixed path, normally `C:\Program Files\nssm\win64\nssm.exe`.

The release is a self-contained `win-x64` publish and does not require a .NET runtime on the monitored PC.

## Service installation

From an elevated PowerShell prompt in the extracted release directory:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Install-Service.ps1
```

Configuration is stored at `C:\ProgramData\Cfa835SystemMonitor\appsettings.json`. Logs are stored below `C:\ProgramData\Cfa835SystemMonitor\logs`.

To update an installed service:

```powershell
.\Update-Service.ps1 -RuntimePath C:\path\to\new\release
```

To remove the service while preserving configuration, logs, and PawnIO:

```powershell
.\Uninstall-Service.ps1
```

## Development

The repository pins .NET SDK 10.0.302. Restore once without lock enforcement when intentionally updating dependencies, review and commit `packages.lock.json`, then use:

```powershell
dotnet restore .\Cfa835SystemMonitor.slnx --locked-mode
dotnet test .\Cfa835SystemMonitor.slnx -c Release --no-restore
.\scripts\Build-Release.ps1
```

The CFA835 protocol implementation follows the official hardware v2.0/firmware v1.6 datasheet and does not write boot-state EEPROM settings.
