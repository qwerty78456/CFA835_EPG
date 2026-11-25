# Hardware test: live configuration reload — 2026-11-25

**Result: 26 of 26 automated checks passed** on a physical CFA835. Visual and keypad-only steps were not
covered by this run (see [Not verified](#not-verified)).

## Setup

| | |
|---|---|
| Build under test | `1.0.0+96eb9a6ba3cb2de323160dd40ab112fd97b6a9e3` (branch `feature/config-hot-reload`), self-contained `win-x64`, .NET runtime 10.0.15 |
| How it was built | `dotnet publish -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true` on the remote builder. SDK **11.0.100** with `global.json` rolled forward in a scratch copy, because a Visual Studio update had removed the pinned SDK 10.0.302 from the builder the same day. All 205 unit tests passed on that build. Not an official `Build-Release.ps1` release. |
| Machine | Windows 10 Pro 19045, Intel Core i7-7700, PawnIO 2.2.0 |
| Module | `CFA835:h1.0,f0.6` (hardware v1.0, firmware v0.6) on COM4, USB `VID_223B&PID_0005` |
| Configuration | Copies of the operator's graphic-mode `appsettings.json` (no `reload` group, i.e. the upgrade path), `layout.json` (3 pages, `invertBackground: true`, Bahnschrift SemiLight) and a 244x68 `background.png`, in a scratch folder passed with `--config` |
| Starting state | Release `4649cf6` running in the foreground (PID 1700) and owning COM4 |

The module is an older revision than the hardware v2.0 / firmware v1.6 datasheet the project is written
against. Graphic mode, handover and every reload transition worked on it without errors.

## Method

`Run-HotReloadHwTest.ps1` (in this folder) starts the build under test hidden and elevated
(`Start-HwMonitor.ps1`), captures stdout/stderr, then edits the scratch configuration files and waits
for the expected log line after each edit. COM4 ownership is checked with `Inspect-Cfa835Owner.ps1`,
which lists the monitor's open handles from an elevated process without opening the port. The steps
follow DEPLOYMENT.md section 10.2. At the end the operator's original monitor was relaunched and took the
panel back through a normal handover. `monitor-output.txt` is the monitor's complete log (USB serial
number redacted) and `results.json` the raw results.

## Results

| Id | Check | Result | Evidence |
|---|---|---|---|
| H0a | New build takes the CFA835 over from the running monitor | PASS | PID 5968; `Connected to CFA835:h1.0,f0.6 on COM4` |
| H0b | Reload is on by default for a config without a `reload` group | PASS | `Live configuration reload is on: watching ...appsettings.json, ...layout.json, ...background.png every 1000 ms` |
| H0c | Previous owner handed over and exited | PASS | PID 1700 gone |
| H0d | New monitor holds COM4 | PASS | handle scan |
| H1 | Moving a field applies live | PASS | 2.1 s: `Configuration reloaded (Layout)` |
| H2a | Broken `layout.json` is rejected, naming the file | PASS | 2.1 s: `...layout.json: Expected depth to be zero at the end of the JSON payload...` |
| H2b | The rejection is logged once, not on every poll | PASS | 1 warning in ~5 s |
| H2c | Restoring the file applies the edit | PASS | 2.4 s |
| H3a | A 100x50 background is rejected, naming the PNG | PASS | `Background '...bad.png' is 100x50; the CFA835 needs exactly 244x68.` |
| H3b | Replacing only the PNG (layout untouched) applies it | PASS | 2.1 s |
| H4a | A background that does not exist yet is rejected | PASS | `...background '...late.png' was not found.` |
| H4b | Copying that PNG in is picked up without another save | PASS | 15.9 s (the 15 s failed-build retry) |
| H5a | Adding a page grows the ring | PASS | `4 page(s)` |
| H5b | Removing the page on screen falls back to the first page | PASS | `2 page(s) ... current page 'Network'` |
| H6 | Toggling `gammaCorrection` applies live | PASS | 2.1 s |
| H7 | `autoCycleSeconds` and `countdownSeconds` apply live | PASS | `(Layout, Shutdown, Display)` |
| H9 | `device.fallbackPort` change waits for the next reconnect | PASS | `device.* settings changed; they apply at the next reconnect and the open port is kept` |
| H10a | `display.mode` → `text` applies live | PASS | `(DisplayMode, Device, Display): text mode` |
| H10b | `display.mode` → `graphic` applies live | PASS | `(DisplayMode, Layout, Display): graphic mode` |
| H11a | An out-of-range setting is rejected, naming `appsettings.json` | PASS | `sampling.activityMs must be between 50 and 5000.` |
| H11b | Restoring it applies | PASS | 2.1 s |
| H12a | `reload.enabled: false` is applied and announced | PASS | `Live configuration reload is now disabled` |
| H12b | Further edits are ignored once reload is off | PASS | 0 reload lines in 6 s |
| INV1 | Same process throughout (no restart) | PASS | PID 5968 alive at the end |
| INV2 | COM port never dropped | PASS | exactly 1 `Connected to` line, 0 connection failures |
| INV3 | COM4 still held at the end | PASS | handle scan |
| INV4 | Nothing written to stderr | PASS | empty |

The only warnings in the log are the four deliberate rejections (H2a, H3a, H4a, H11a). Typical latency
from save to applied is about 2 seconds with the default `pollMs` of 1000.

Afterwards the operator's release `4649cf6` was relaunched (PID 2896), the build under test accepted the
handover and logged `CFA835 monitor stopped`, and a handle scan confirmed PID 2896 holds COM4.

## Not verified

- **Anything visual.** The run had no one watching the panel, so "no blank frame on a same-mode reload",
  "one brief blank on a gamma change", "artwork cleared before text rows after a mode switch" and the
  correct position of the moved field are verified only by the code paths and the unit tests
  (`RuntimeStateTests`, `GraphicWriterTests`), not by eye.
- **Deferral behind the shutdown dialog** (DEPLOYMENT.md 10.2 step 8) needs keypad presses.
- **Unplug/replug** (step 12) needs physical access.
- **Service mode.** The run used a foreground process; the service uses the same code path with a
  ProgramData `--config`.
- **An official release build.** See *How it was built* above.
