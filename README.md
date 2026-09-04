# CFA835 System Monitor

A Windows-only system monitor for the Crystalfontz CFA835 USB LCD screen and keypad module.

The module is a 244x68-pixel, 16-shade greyscale panel with the 20x4 character API layered on top. `display.mode` chooses which layer the app drives: `text` (the default, described immediately below) or `graphic` (background artwork plus text boxes placed by pixel coordinate, described under [Graphic mode](#graphic-mode)).

Temperature sampling prefers the hottest valid LibreHardwareMonitor/PawnIO reading and falls back to the hottest Windows ACPI thermal zone when no primary reading is available. CPU-only temperature selection remains separate for the thermal-warning LED. A CPU sensor reporting 0 C or below is treated as unreadable rather than as a real reading, because LibreHardwareMonitor still enumerates CPU sensors when PawnIO is absent.

## Display pages

The page order in text mode is as follows:

1. Main: date/time, `CPU UTIL`, `TEMPERATURE`, and `AUTO` state.
2. Network: receive, transmit, and total Mbps.
3. Shutdown: manual-only shutdown controls.

Auto-cycle alternates between Main and Network and never lands on Shutdown.

## Graphic mode

Setting `"display": { "mode": "graphic" }` in `appsettings.json` switches to composited frames driven by `layout.json`, which lives beside `appsettings.json`. Background artwork and text are combined on the host, so the module only ever receives finished pixel rectangles — no microSD card, on-device image, or CFA835 font file is involved.

```jsonc
{
  "version": 1,
  "refreshMs": 250,
  "fontFamilies": [ "Bahnschrift SemiLight", "Times New Roman" ],
  "pages": [
    {
      "id": "DateTime",
      "background": "background.png",
      "fields": [
        { "source": "datetime", "format": "HH:mm:ss",
          "x": 64, "y": 4, "width": 176, "height": 18, "align": "center", "sizePx": 24 }
      ]
    }
  ]
}
```

- **Background**: an optional PNG per page, which must be exactly 244x68 pixels. Any DPI tag in the file is ignored; the copy is pixel-for-pixel. It is decoded once at start-up and composited with the text on the host, so a field can sit on any artwork. The panel resolves 16 greyscale levels, so artwork should not depend on fine tonal gradients; `invertBackground` flips the whole page if the panel reads inverted.
- **Fields**: `x`, `y`, `width`, and `height` are pixels with the origin at the top-left. `shade` (0-255) sets the text greyscale, so dark text on light artwork is simply a low value. Available `source` values are `literal`, `datetime`, `cpu.utilization`, `cpu.temperature`, `system.temperature`, `net.rx`, `net.tx`, `net.total`, `autocycle`, `shutdown.pendingSeconds`, `shutdown.remaining`, and `shutdown.confirm`. `fallback` supplies the text when a sensor is unreadable.
- **Fonts** are resolved from the system in `fontFamilies` order; the first installed family wins. Only printable ASCII is rasterized.
- **Pages** are data. Adding an entry to `pages` adds a page to the keypad ring. `"kind": "shutdown"` keeps a page out of auto-cycling and gives it the Idle/Confirm/CountingDown state machine; leaving its `fields` empty keeps the built-in wording.
- **Cost**: only fields whose formatted text changed are retransmitted, so a ticking clock costs about 1.4 KB per second rather than the 16.6 KB full frame. The whole frame is repainted every `fullRepaintSeconds` because the pixel stream is not CRC-protected.

Tune a layout without any hardware attached:

```powershell
.\Cfa835SystemMonitor.exe --layout-preview preview.png --preview-page DateTime --preview-scale 6
```

On a machine without PawnIO, `cpu.temperature` previews as `N/A`, which hides whether a real reading fits its box. Add `--simulate` to fill the sensor fields with realistic values:

```powershell
.\Cfa835SystemMonitor.exe --layout-preview preview.png --simulate thermal-90
```

## Keypad

- Left/right: previous or next page.
- Enter on Main or Network: toggle automatic cycling.
- Enter on Shutdown: open confirmation.
- Up/down during confirmation: adjust the shutdown delay in five-second increments.
- Left/right during confirmation: select Yes or No.
- Exit: cancel confirmation/countdown, or disable auto-cycle and return to the first page.

The keypad model is identical in graphic mode; only the rendering differs. Left/right walk the pages in the order `layout.pages` lists them, and pages marked `"kind": "shutdown"` stay out of auto-cycling exactly as the built-in shutdown page does.

## Command-line modes

```powershell
.\Cfa835SystemMonitor.exe --diagnose
.\Cfa835SystemMonitor.exe --hardware-test
.\Cfa835SystemMonitor.exe --hardware-test --noninteractive
.\Cfa835SystemMonitor.exe --simulate thermal-90
.\Cfa835SystemMonitor.exe --config C:\path\appsettings.json
.\Cfa835SystemMonitor.exe --layout-preview preview.png --preview-page DateTime --preview-scale 6
.\Cfa835SystemMonitor.exe --list-sensors
```

`--list-sensors` dumps every sensor LibreHardwareMonitor exposes, with a summary that distinguishes
an unavailable ring-0 sensor path from hardware whose CPU sensors genuinely are not enumerated. It
exits non-zero when no CPU temperature has a plausible value, so it is the first thing to run when
`cpu.temperature` shows its fallback. `--layout-preview` needs no CFA835 at all: it renders one layout page to a PNG using live metrics, which is the intended way to position boxes before touching hardware. `--diagnose` reads device/sensor state and prints the four rows currently stored in display RAM without changing the display, keypad configuration, LEDs, or persistent device settings. `--hardware-test` temporarily exercises the display and LEDs, watches keypad presses, and restores the state captured at startup.

## Administrator and process ownership

Every command mode requires an elevated Administrator token. The executable carries a
`requireAdministrator` manifest, so an interactive start displays a UAC prompt before the process
runs. The executable is not Authenticode-signed yet and Windows may therefore identify it as an
unknown publisher; keep production releases under an administrator-controlled directory. The
LocalSystem service already satisfies the token requirement. A defense-in-depth runtime check exits
with code 77 if the process does not actually have an enabled Administrators SID.

Only one monitor, diagnostic, or hardware-test process can own the CFA835. A new foreground process
asks an existing foreground instance to cancel any pending Windows shutdown, clean up the display,
close the COM port, and exit before it continues. A legacy build without that control endpoint is
forced to stop only after its PID, start time, executable path, product metadata, and session have
been verified. `--layout-preview` and `--list-sensors` do not claim the CFA835 and can run alongside
the monitor.

Configuration and graphic-layout validation complete before an existing foreground owner is asked
to exit. Invalid input therefore exits with code 78 without interrupting a healthy monitor.

Foreground and service processes deliberately never replace one another. If the NSSM service is
active, a foreground monitor, diagnostic, or hardware test exits with code 75; stop the service
explicitly first. If the service starts while a foreground process owns the device, it exits with
code 75 and NSSM retries according to its configured restart policy.

## Runtime prerequisites

1. Windows 10 or later on x64, with an elevated Administrator token for every invocation.
2. Crystalfontz CFA735/835 USB virtual-COM driver.
3. Signed normal-edition PawnIO 2.2.0 for LibreHardwareMonitor low-level sensor access. **The signed installer ships in `third-party/pawnio/`**, and `Install-Service.ps1` runs it automatically when the driver is absent, so no download is needed on the monitored machine. Pass `-SkipPawnIO` to decline. See [Why PawnIO is still an install step](#why-pawnio-is-still-an-install-step).
4. NSSM x64 2.24-101 at `C:\Program Files\nssm\win64\nssm.exe` when installing as a service. NSSM is not bundled; foreground operation does not need it.
5. For graphic mode only: the font families named in `layout.fontFamilies` installed on the monitored machine. The first installed family wins; if none is present the app logs a warning and falls back to generic sans-serif. `Bahnschrift SemiLight` ships with Windows 10 and later as a named instance of the `bahnschrift.ttf` variable font, and Windows exposes it to GDI+ as its own family name.

The runtime is a self-contained `win-x64` publish and does not require a separately installed .NET
SDK or runtime. Self-contained does **not** mean single-file: deploy the complete timestamped release
directory because the executable depends on the runtime DLLs and configuration beside it. On a
clean Windows installation, the Crystalfontz driver is still required for the physical USB device;
PawnIO is required only for CPU temperature access; and NSSM is required only for service mode.
`System.Drawing.Common` wraps GDI+, which is already part of Windows.

### Why PawnIO is still an install step

CPU temperatures live in ring-0 registers — MSR on Intel, SMN/MSR on AMD. `LibreHardwareMonitorLib`
reaches them by opening the kernel device `\\?\GLOBALROOT\Device\PawnIO`, which exists only while the
PawnIO driver service is loaded. A kernel-mode driver cannot run from an application folder: it has
to be registered as a system service by an administrator, and on x64 Windows with driver signature
enforcement it must carry a Microsoft-attested signature.

Bundling therefore removes the download and the version guesswork, not the installation. The PawnIO
*modules* (`IntelMSR.bin`, `AMDFamily17.bin`, `LpcIO.bin`, and others) are already embedded inside
`LibreHardwareMonitorLib.dll`, so the driver is the only missing piece.

Without it, `--diagnose` reports `PawnIO: NOT INSTALLED`, `cpu.temperature` renders its `fallback`
text, and the thermal-warning LED never arms. This is identical on Intel and AMD; a CPU-vendor
difference between a build workstation and the monitored machine is never the explanation.

Installing the driver is necessary but not sufficient: opening
`\\?\GLOBALROOT\Device\PawnIO` also requires **elevation**. All executable modes now enforce that
requirement before loading configuration or sensors, so normal diagnostic and preview output must
show `Elevated: True`. The installed service runs as LocalSystem and satisfies the same check.

`Install-Service.ps1` verifies the bundled installer's Authenticode signature and refuses to launch
it unless the status is `Valid` and the signer is `CN=namazso.eu`. When prompted by the wizard,
choose the **signed** edition — the "unrestricted" edition is unsigned and requires disabling driver
signature enforcement, which is not appropriate for a monitored production machine.

PawnIO is GPLv2 with a device-IOCTL exception; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
and `third-party/pawnio/README.md` for the obligations that come with redistributing it.

## Release builds

The repository pins .NET SDK 10.0.302. From the repository root:

```powershell
.\scripts\Build-Release.ps1
```

The pipeline restores locked dependencies, runs all tests, publishes the self-contained runtime, copies operational documentation and service scripts, and writes per-file SHA-256 hashes. Its deployable artifact is an uncompressed, time-stamped folder:

```text
artifacts\Cfa835SystemMonitor-<commit>-win-x64-<yyyyMMdd-HHmmss>\
```

It does not package the runtime as a ZIP or a standalone executable. Copy the whole folder. Every
release contains `COMMIT.txt`, `BUILD-TIMESTAMP.txt`, `SHA256SUMS.txt`, this README, the detailed
changelog, and the deployment guide.

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

## License

Cfa835SystemMonitor is released under the [MIT License](LICENSE).

Redistributed third-party components keep their own licences. In particular `third-party/pawnio/`
contains the PawnIO 2.2.0 driver installer under GPLv2 with a device-IOCTL exception, shipped
together with its corresponding source as GPLv2 section 3 requires. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the full list and
`third-party/pawnio/README.md` for provenance, hashes, and the verified signature.
