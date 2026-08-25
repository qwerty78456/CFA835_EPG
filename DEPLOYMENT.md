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
3. Signed normal-edition PawnIO 2.2.0. LibreHardwareMonitor uses PawnIO for primary low-level temperature access. **The signed installer ships inside the release at `third-party\pawnio\PawnIO_setup.exe`**, so an air-gapped machine needs no download. `Install-Service.ps1` detects the driver and runs that installer when it is missing; pass `-SkipPawnIO` to decline.

   Bundling removes the download, not the installation: PawnIO is a kernel-mode driver, so it must be registered as a system service by an administrator and must keep its Microsoft-attested signature. In the wizard choose the **signed** edition — the "unrestricted" edition is unsigned and requires disabling driver signature enforcement, which is not acceptable on a monitored machine.

   The install script refuses to launch the installer unless its Authenticode status is `Valid` and the signer is `CN=namazso.eu`. Verify by hand at any time:

   ```powershell
   Get-AuthenticodeSignature .\third-party\pawnio\PawnIO_setup.exe | Format-List Status, SignerCertificate
   ```

   PawnIO is GPLv2 with a device-IOCTL exception. `third-party\pawnio\` also carries `COPYING` and the corresponding source archive, which together satisfy GPLv2 section 3. Keep the whole directory together when copying a release onto removable media.
4. For service mode only, NSSM x64 2.24-101 at `C:\Program Files\nssm\win64\nssm.exe`, or pass an alternate `-NssmPath`.
5. For graphic mode only, the font families named in `layout.fontFamilies`. The default chain is `Bahnschrift SemiLight` then `Times New Roman`, both of which ship with Windows 10 and later. Verify before deployment rather than discovering a silent fallback on the panel:

   ```powershell
   Add-Type -AssemblyName System.Drawing
   (New-Object System.Drawing.Text.InstalledFontCollection).Families.Name -contains 'Bahnschrift SemiLight'
   ```

The release is self-contained; the monitored machine does not need the .NET SDK or runtime. Graphic mode uses GDI+, which is an operating-system component, so it adds no install step of its own.

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
- `display`: rendering mode, layout file path, auto-cycle startup state, interval, date format, and time format;
- `thermal`: TjMax, warning margin, and clear hysteresis;
- `shutdown`: default countdown duration.

Keep secrets out of this file; the current schema contains no secret-bearing settings.

### 4.1 Graphic mode and `layout.json`

`display.mode` defaults to `text` and reproduces every previous release exactly. Setting it to `graphic` additionally requires `layout.json`, resolved from `display.layoutPath` relative to the directory the configuration came from — so a service install reads `C:\ProgramData\Cfa835SystemMonitor\layout.json`, not the copy beside the executable.

Deploying a graphic layout:

1. Copy `layout.json` from the release folder to `C:\ProgramData\Cfa835SystemMonitor\` and edit it there.
2. Export the artwork as a PNG that is **exactly 244x68 pixels**, place it in the same directory, and reference it from the page with `"background": "background.png"`. Any other pixel size is rejected at start-up. The DPI tag in the file does not matter — the copy is pixel-for-pixel — so a 72 DPI export from a design tool is fine.
3. Set the text `shade` to suit the artwork. `248` is white text for dark artwork; use a low value such as `8` for dark text on light artwork. If the whole panel reads inverted, set `"invertBackground": true` rather than re-exporting the image.
4. Position the boxes with no hardware attached, iterating until the PNG looks right:

   ```powershell
   .\Cfa835SystemMonitor.exe --config C:\ProgramData\Cfa835SystemMonitor\appsettings.json --layout-preview preview.png --preview-scale 6
   ```

   On a build workstation without PawnIO, `cpu.temperature` previews as `N/A` and will not show whether a real reading fits its box. Append `--simulate thermal-90` to render a realistic value instead. This matters when the workstation and the monitored machine differ: temperature access needs PawnIO on both AMD and Intel, so a workstation lacking the driver tells you nothing about the sensor, only about the layout.

5. Confirm the parsed layout, then check alignment on the physical panel:

   ```powershell
   .\Cfa835SystemMonitor.exe --diagnose
   ```
   ```powershell
   .\Cfa835SystemMonitor.exe --hardware-test
   ```

   In graphic mode `--hardware-test` pushes the first page and then outlines every field rectangle, so misplaced boxes are visible against the artwork. It clears the graphic buffer during restore, because the CFA835 cannot read a graphic frame back.

An invalid `layout.json` — a rectangle leaving the 244x68 panel, an unknown `source`, a missing background file, a duplicate page id — fails at start-up with exit code 78 and a message naming the page and field index. It never reaches the monitor loop.

`layout.refreshMs` (100-60000) sets the graphic repaint cadence and replaces `sampling.displayMs` for this mode. 250 ms is a comfortable default; because only changed fields are retransmitted, a ticking clock costs roughly 1.4 KB per second.

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

Run it **from an elevated shell**. Opening the PawnIO device requires elevation, so an unelevated diagnostic reports `PawnIO: installed` and still shows no CPU temperature. The diagnostic prints an `Elevated:` line and warns when that is the situation. The service itself runs as LocalSystem and is always elevated, so this affects interactive runs only.

A passing diagnostic must report all of the following:

- `Opened CFA835 transport on <COM port>`;
- `Elevated: True`;
- a CFA835 firmware/hardware version;
- the four rows read directly from CFA835 display RAM;
- PawnIO installation state;
- CPU utilization;
- at least the physical-interface inventory;
- selected system temperature and its source, or a clearly reported `N/A` when no sensor path is readable;
- `Diagnostics completed.` and exit code 0.

When `display.mode` is `graphic`, or whenever a `layout.json` is present, the diagnostic additionally prints the resolved mode and every page, background path, and field rectangle. Use that to confirm the process is reading the layout you intend before looking at the panel.

Diagnostic mode deliberately does not repaint the LCD. To prove what the live build rendered, let the monitor reach Main, stop it, immediately run `--diagnose`, and inspect the four `Display rows` values before restarting the same new build. Note that `Display rows` reads back only text written by command 31; in graphic mode the CFA835 cannot report its graphic buffer, so use `--layout-preview` for expected output and the panel itself for actual output. Use the full live-monitor acceptance procedure in section 10 to verify navigation too.

`--layout-preview` never opens the COM port, so unlike `--diagnose` it does not require stopping the monitor. It does briefly sample metrics through LibreHardwareMonitor to fill the fields, so run it during a quiet moment if the machine is under sensor-polling load, or accept an empty snapshot in the image:

```powershell
.\Cfa835SystemMonitor.exe --config C:\ProgramData\Cfa835SystemMonitor\appsettings.json --layout-preview preview.png --preview-scale 6
```

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

`Update-Service.ps1` stops the service, copies the new runtime while preserving the ProgramData configuration and the shipped `appsettings.json` and `layout.json`, then restarts the service.

Operator-edited files under `C:\ProgramData\Cfa835SystemMonitor\` — `appsettings.json`, `layout.json`, and any background artwork — are never overwritten by an upgrade or a rollback. `Install-Service.ps1` seeds `layout.json` there only when the file is absent. A rollback to a build that predates graphic mode therefore leaves the layout in place, harmless because that build ignores it; a rollback with `display.mode` still set to `graphic` will fail on the older binary, so revert `display.mode` to `text` in the same step.

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

### 10.1 Additional steps when `display.mode` is `graphic`

Steps 4 and 5 above describe the text screen. When graphic mode is deployed, replace them with:

1. Verify the background artwork is drawn once and does not flicker or tear as fields update.
2. Verify each value sits inside its intended box and does not overrun it. If a value is clipped, widen the field rectangle in `layout.json` rather than shrinking `sizePx` first — the rectangle is also the transfer unit.
3. Watch the seconds digits for at least one minute. They must not shift horizontally; digits are rendered with a shared tabular advance, so any jitter indicates the wrong font resolved.
4. Confirm the resolved font in the startup log line `Glyph atlas ready: font '<name>'`. A fallback to generic sans-serif means the families in `layout.fontFamilies` are not installed on this machine.
5. Leave the monitor running for longer than `fullRepaintSeconds` and confirm the periodic full repaint is not visible as a flash.
6. Page through every entry in `layout.pages`, then reach the shutdown page manually and confirm auto-cycle never lands on it.

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

- **Check `Elevated:` first.** `PawnIO: installed` only proves the driver is registered; opening `\\?\GLOBALROOT\Device\PawnIO` additionally requires elevation, and an unelevated process gets `Access is denied`. `PawnIO: installed` together with `Elevated: False` and a missing CPU temperature is that case, not a driver fault — re-run from an elevated shell. The installed service runs as LocalSystem, so this only ever affects interactive `--diagnose` and `--layout-preview` runs.
- Confirm PawnIO normal edition is installed and readable by the process identity. `PawnIO installed: False` in the log is the usual answer on its own: without ring-0 access LibreHardwareMonitor cannot read CPU temperatures on **either** AMD or Intel, so a CPU-vendor difference between the build workstation and the monitored machine is not the explanation. Install it from the bundled `third-party\pawnio\PawnIO_setup.exe`, or re-run `Install-Service.ps1`, which does the same. Confirm afterwards that the driver service is actually loaded, not merely that the installer ran:

  ```powershell
  Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO' | Select-Object DisplayName, DisplayVersion
  ```
- Run `--diagnose` with the monitor stopped.
- Review stdout/stderr logs for LibreHardwareMonitor inventory or access errors.
- **The Windows ACPI thermal-zone fallback does not feed a `cpu.temperature` field.** `Windows ACPI thermal-zone fallback initialized with N counter(s)` only means the counters opened. Those readings carry `IsCpu = false` and are consulted solely for the text-mode `TEMPERATURE` row and the `system.temperature` layout source; `cpu.temperature` and the thermal-warning LED come from LibreHardwareMonitor CPU sensors only. A machine with a working ACPI zone but no PawnIO therefore shows a system temperature and `N/A` for CPU temperature, which is correct behaviour, not a fault.
- ACPI thermal zones are largely a laptop and OEM feature. Many desktop boards expose either no zone or a near-ambient stub — a value such as 16 C from `\_tz.tz10` is the firmware reporting a placeholder, not a misread. Use `Get-Counter '\Thermal Zone Information(*)\Temperature'` to see the raw Kelvin values.

### Temperature appears implausible

- Use diagnostic output to identify the selected sensor source.
- LibreHardwareMonitor/PawnIO chooses the hottest valid absolute temperature across hardware; some motherboard controllers expose poorly labelled channels.
- `Distance to TjMax` is excluded, but firmware/sensor labels may still require machine-specific interpretation.

### Graphic mode exits immediately with code 78

- The layout failed validation. The message names the page and field index, for example `layout page 'DateTime' field 2: rectangle (200, 4, 100, 18) does not fit the 244x68 display.`
- Check that the layout being read is the one you edited. A service reads `C:\ProgramData\Cfa835SystemMonitor\layout.json`, not the copy beside the executable.
- Confirm the background PNG exists at the resolved path and is exactly 244x68.

### Graphic screen is blank, inverted, or unreadable

- Run `--layout-preview` first. If the PNG looks correct, the layout is fine and the problem is shade polarity on the panel, not composition.
- Text invisible against artwork usually means `shade` matches the background. Use a low `shade` for light artwork and a high one for dark artwork.
- If the entire page reads inverted, set `"invertBackground": true` instead of re-exporting the image.
- The panel resolves 16 greyscale levels, so artwork relying on subtle tonal steps will band. Prefer flat fills and line art.

### Text is missing spaces or shows `?` characters

- Only printable ASCII (`0x20`-`0x7E`) is rasterized; anything else is drawn as `?`. This applies to accented characters, including Vietnamese diacritics.
- Missing spaces in a released binary older than this change indicate the pre-fix glyph advance bug; upgrade rather than editing the layout around it.

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

Add `-RemoveFiles` only when intentionally removing `C:\Program Files\Cfa835SystemMonitor`. PawnIO, configuration, and logs under ProgramData are preserved by design; that includes `layout.json` and any background artwork, so a later reinstall keeps the tuned layout.
