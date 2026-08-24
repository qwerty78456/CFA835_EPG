# CFA835 System Monitor deployment guide

This guide covers Windows deployment of a self-contained CFA835 System Monitor release. Commands use PowerShell syntax and assume an x64 machine. The CFA835 serial port is exclusive: never run two monitor, diagnostic, or hardware-test processes at the same time.

## 1. Release artifact and provenance

`scripts\Build-Release.ps1` produces an uncompressed directory, not a ZIP:

```text
artifacts\Cfa835SystemMonitor-<commit>-win-x64-<yyyyMMdd-HHmmss>\
```

Each directory is immutable deployment input and contains:

- `Cfa835SystemMonitor.exe` and its self-contained .NET runtime files;
- `appsettings.json`, the shipped configuration example;
- `COMMIT.txt`, containing the 12-character source commit;
- `BUILD-TIMESTAMP.txt`, containing the builder's timestamp and UTC offset;
- `SHA256SUMS.txt`, containing hashes of every other shipped file;
- `README.md`, `CHANGELOG.md`, `DEPLOYMENT.md`, and `THIRD-PARTY-NOTICES.md`;
- `Install-Service.ps1`, `Update-Service.ps1`, and `Uninstall-Service.ps1`.

Do not edit a release folder in place. Keep each timestamped folder intact so it remains a reliable rollback target.

## 2. Monitored-machine prerequisites

Install these before launching the application:

1. Windows 10 or later, x64.
2. The Crystalfontz USB virtual-COM driver for CFA735/CFA835 devices.
3. Signed normal-edition PawnIO 2.2.0. LibreHardwareMonitor uses PawnIO for primary low-level temperature access.
4. For service mode only, NSSM x64 2.24-101 at `C:\Program Files\nssm\win64\nssm.exe`, or pass an alternate `-NssmPath`.

The release is self-contained; the monitored machine does not need the .NET SDK or runtime.

Confirm the device appears before deployment:

```powershell
[System.IO.Ports.SerialPort]::GetPortNames()
```

The default configuration resolves USB VID `223B`, PID `0005`, and the configured serial number, then uses `COM3` as a fallback. Edit a copied configuration file when the hardware differs.

## 3. Validate a release before running it

Open PowerShell in the timestamped release directory:

```powershell
$releasePath = (Get-Location).Path
$commit = (Get-Content -LiteralPath .\COMMIT.txt -Raw).Trim()
$version = (Get-Item -LiteralPath .\Cfa835SystemMonitor.exe).VersionInfo.ProductVersion
$commit
$version
```

The product version must contain the full commit whose first 12 characters equal `COMMIT.txt`.

Validate every manifest entry:

```powershell
$releasePath = (Get-Location).Path
$failures = foreach ($line in Get-Content -LiteralPath .\SHA256SUMS.txt) {
    if ($line -notmatch '^([0-9a-f]{64})  (.+)$') {
        "Malformed manifest line: $line"
        continue
    }

    $expected = $Matches[1]
    $relative = $Matches[2].Replace('/', '\')
    $file = Join-Path $releasePath $relative
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        "Missing: $relative"
        continue
    }

    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        "Hash mismatch: $relative"
    }
}

if ($failures) { $failures; throw 'Release validation failed.' }
'Release validation passed.'
```

## 4. Configuration behavior

Configuration is selected in this order:

1. The path passed with `--config`.
2. `C:\ProgramData\Cfa835SystemMonitor\appsettings.json` when it exists.
3. `appsettings.json` beside the executable.

The service scripts intentionally preserve the ProgramData configuration across upgrades and uninstall operations. Review these keys before deployment:

- `device`: USB identity and fallback COM port;
- `sampling`: temperature, activity, and display intervals;
- `display`: auto-cycle startup state, interval, date format, and time format;
- `thermal`: TjMax, warning margin, and clear hysteresis;
- `shutdown`: default countdown duration.

Keep secrets out of this file; the current schema contains no secret-bearing settings.

## 5. Stop competing COM-port owners

Identify monitor instances before diagnostics, upgrades, or foreground launches:

```powershell
Get-Process -Name Cfa835SystemMonitor -ErrorAction SilentlyContinue |
    Select-Object Id, StartTime, Path
```

If Path is blank because the process is elevated, run the command from an elevated PowerShell prompt. For a service deployment, stop the service instead of killing its process:

```powershell
Stop-Service Cfa835SystemMonitor
```

For a foreground deployment, record the exact executable path, then stop only the verified process:

```powershell
Stop-Process -Id <verified-pid>
```

`System.UnauthorizedAccessException: Access to the path 'COM3' is denied` means another process owns the serial port or the current shell lacks permission to manage its process. Do not start additional copies and hope one wins.

## 6. Read-only diagnostic acceptance

With no monitor process or service holding the COM port:

```powershell
.\Cfa835SystemMonitor.exe --diagnose
if ($LASTEXITCODE -ne 0) { throw "Diagnostics failed with exit code $LASTEXITCODE" }
```

A passing diagnostic must report all of the following:

- `Opened CFA835 transport on <COM port>`;
- a CFA835 firmware/hardware version;
- the four rows read directly from CFA835 display RAM;
- PawnIO installation state;
- CPU utilization;
- at least the physical-interface inventory;
- selected system temperature and its source, or a clearly reported `N/A` when no sensor path is readable;
- `Diagnostics completed.` and exit code 0.

Diagnostic mode deliberately does not repaint the LCD. To prove what the live build rendered, let the monitor reach Main, stop it, immediately run `--diagnose`, and inspect the four `Display rows` values before restarting the same new build. Use the full live-monitor acceptance procedure in section 10 to verify navigation too.

## 7. Foreground deployment

Foreground mode is appropriate for interactive testing or a machine where the user session starts the monitor independently.

```powershell
$releasePath = 'E:\path\to\Cfa835SystemMonitor-<commit>-win-x64-<timestamp>'
$exe = Join-Path $releasePath 'Cfa835SystemMonitor.exe'
Start-Process -FilePath $exe -WorkingDirectory $releasePath -WindowStyle Hidden
```

When elevation is required for PawnIO or process replacement:

```powershell
Start-Process -FilePath $exe -Verb RunAs -WorkingDirectory $releasePath -WindowStyle Hidden
```

Do not automatically restart the old build after validating the new one. The process that remains active must resolve to the new timestamped directory.

## 8. Fresh NSSM service installation

Run an elevated PowerShell prompt in the release directory:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Install-Service.ps1
```

Optional non-default paths:

```powershell
.\Install-Service.ps1 `
    -NssmPath 'D:\Tools\nssm\win64\nssm.exe' `
    -InstallPath 'C:\Program Files\Cfa835SystemMonitor' `
    -DataPath 'C:\ProgramData\Cfa835SystemMonitor'
```

The script installs `Cfa835SystemMonitor` as delayed-auto-start LocalSystem, configures restart-on-failure, writes stdout/stderr logs under ProgramData, and restricts the data directory to SYSTEM and Administrators.

Verify installation:

```powershell
Get-Service Cfa835SystemMonitor
Get-CimInstance Win32_Service -Filter "Name='Cfa835SystemMonitor'" |
    Select-Object State, StartMode, PathName
Get-Content 'C:\ProgramData\Cfa835SystemMonitor\logs\stdout.log' -Tail 100
Get-Content 'C:\ProgramData\Cfa835SystemMonitor\logs\stderr.log' -Tail 100
```

## 9. Upgrade and rollback

Preserve the old timestamped release until the new build passes physical acceptance.

Upgrade an installed service from an elevated PowerShell prompt:

```powershell
$newRelease = 'E:\path\to\Cfa835SystemMonitor-<new-commit>-win-x64-<timestamp>'
& (Join-Path $newRelease 'Update-Service.ps1') -RuntimePath $newRelease
```

`Update-Service.ps1` stops the service, copies the new runtime while preserving the ProgramData configuration and shipped `appsettings.json`, then restarts the service.

Rollback uses the same command with the previously validated release folder:

```powershell
$oldRelease = 'E:\path\to\Cfa835SystemMonitor-<old-commit>-win-x64-<timestamp>'
& (Join-Path $oldRelease 'Update-Service.ps1') -RuntimePath $oldRelease
```

For foreground mode, stop the exact new PID and launch the exact older executable. Confirm there is only one process afterward.

## 10. Physical CFA835 acceptance test

This is the authoritative check that a deployment manifested on hardware.

1. Confirm exactly one `Cfa835SystemMonitor` process is running.
2. Confirm that process resolves to the intended timestamped release and its product version contains the intended commit.
3. Press Exit on the CFA835 keypad to disable auto-cycle and return to Main.
4. Verify the four rows are date/time, `CPU UTIL`, `TEMPERATURE`, and `AUTO: OFF`. The text `SYSTEM MONITOR` must not appear.
5. Press Right once. Verify the Network page appears. A standalone `CPU UTILIZATION` page must not appear.
6. Press Right again. Verify the manual Shutdown page appears.
7. Press Right again. Verify the display returns to Main.
8. On Main, press Enter. Verify auto-cycle changes to `AUTO: ON` and alternates only between Main and Network.
9. Press Exit. Verify the display returns to Main and shows `AUTO: OFF`.
10. Leave the intended new process running and re-check its PID/path after at least one complete auto-cycle interval.

For a foreground process, verify the live binary from an elevated shell:

```powershell
$process = Get-Process -Name Cfa835SystemMonitor
if (@($process).Count -ne 1) { throw 'Expected exactly one monitor process.' }
$path = $process.MainModule.FileName
$version = (Get-Item -LiteralPath $path).VersionInfo.ProductVersion
[pscustomobject]@{ Pid = $process.Id; Path = $path; ProductVersion = $version }
```

For service mode, also verify the service remains Running after the acceptance sequence.

## 11. Remote Windows builder workflow

The builder is Windows; use PowerShell paths and commands throughout. From the builder checkout:

```powershell
Set-Location 'D:\cfa835\Cfa835SystemMonitor-640d1d649e28-source'
git fetch origin
git switch main
git pull --ff-only origin main
git status --short --branch
.\scripts\Build-Release.ps1
```

The final build output prints the exact uncompressed release directory. Copy that directory recursively to the monitored machine without compressing it:

```powershell
scp.exe -r User@builder:'D:/cfa835/Cfa835SystemMonitor-640d1d649e28-source/artifacts/Cfa835SystemMonitor-<commit>-win-x64-<timestamp>' 'E:\releases\'
```

After transfer, re-run section 3 on the monitored machine. Network transfer success alone is not deployment success; complete sections 6, 7 or 8, and 10.

## 12. Troubleshooting

### COM port access denied

- Find and stop the single existing monitor/service owner.
- Check Device Manager and `[System.IO.Ports.SerialPort]::GetPortNames()`.
- Confirm `device.fallbackPort` matches the actual port.
- Do not run diagnostics while the live monitor owns the port.

### New files copied but old screen remains

- The old process is still running from an older folder.
- Resolve the live process path and embedded ProductVersion; do not trust folder timestamps or a completed copy operation.
- Stop the old PID or service, launch the intended executable, and repeat the physical acceptance test.

### Temperature is `N/A`

- Confirm PawnIO normal edition is installed and readable by the process identity.
- Run `--diagnose` with the monitor stopped.
- Check whether the diagnostic initialized Windows ACPI counters as fallback.
- Review stdout/stderr logs for LibreHardwareMonitor inventory or access errors.

### Temperature appears implausible

- Use diagnostic output to identify the selected sensor source.
- LibreHardwareMonitor/PawnIO chooses the hottest valid absolute temperature across hardware; some motherboard controllers expose poorly labelled channels.
- `Distance to TjMax` is excluded, but firmware/sensor labels may still require machine-specific interpretation.

### Service repeatedly restarts

- Inspect ProgramData logs and NSSM service parameters.
- Validate the persisted configuration separately from the shipped example.
- Confirm no foreground process owns the CFA835 port.
- Run the same release interactively with `--diagnose` after stopping the service.

## 13. Uninstall

From an elevated release-directory PowerShell prompt:

```powershell
.\Uninstall-Service.ps1
```

Add `-RemoveFiles` only when intentionally removing `C:\Program Files\Cfa835SystemMonitor`. PawnIO, configuration, and logs under ProgramData are preserved by design.
