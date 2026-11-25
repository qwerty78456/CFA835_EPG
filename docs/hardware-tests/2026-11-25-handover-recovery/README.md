# Hardware test: abandoned handover recovery — 2026-11-25

**Result: the fix works on a physical CFA835.** An accepted handover whose answer could not be
delivered no longer latches the monitor. It paused while the requester lived, resumed when the
requester exited, applied the edit it had held back, kept the COM port throughout, and still served a
real handover afterwards. 14 of 16 harness checks passed as run. The other two (T3, R2) failed because
of mistakes in the harness itself; the behaviour they check was confirmed from the log and an
elevated handle scan (see below). The committed harness has both checks corrected.

## Setup

| | |
|---|---|
| Build under test | `1.0.0+b0ad0a09ce113008f88cb68737c163f4b8786feb` (branch `fix/handover-undelivered-acceptance`), self-contained `win-x64`, .NET runtime 10.0.15. Built on the remote builder with SDK 11.0.100 rolled forward in a scratch copy (the pinned SDK 10.0.302 is missing there); all 220 unit tests passed on that build. |
| Machine and module | Same as the live-reload run: Windows 10 Pro 19045, `CFA835:h1.0,f0.6` on COM4 |
| Configuration | Scratch copies of the operator's graphic-mode configuration, passed with `--config` |
| Starting state | Operator's release `4649cf6` (PID 2896) driving the panel |

## How the failure was produced

The bug needs an accepted handover whose answer the requester never reads. A real requester reads
its answer within milliseconds, so racing one would be unreliable. `Invoke-UndeliveredHandover.ps1`
produces the condition deterministically, elevated:

1. It holds the global startup mutex (`Global\Cfa835SystemMonitor.Startup.v1`). A genuine
   `Cfa835SystemMonitor.exe --diagnose` started now parks inside `InstanceCoordinator.Acquire` waiting
   for that mutex, before any sensor access or COM port use. It is a real, verifiable process, so it
   passes the owner's requester verification (PID, start time, executable path, product metadata,
   non-service session).
2. It sends the owner a protocol-1 handover request describing that parked process, then closes
   the pipe immediately. The owner verifies, prepares, accepts, and its answer hits a closed pipe.
3. While the parked process lives it saves a layout edit, then kills the parked process (before
   releasing the mutex, so it can never go on to request a handover or terminate the owner itself),
   and watches the owner resume.

The test monitor, the trigger and the handle scans run elevated through `Start-Process -Verb RunAs`.
COM4 ownership is checked by listing the monitor's open handles (`Inspect-Cfa835Owner.ps1`), never by
opening the port.

## Results

| Id | Check | As run | Evidence |
|---|---|---|---|
| S0 | Build under test takes the panel over and holds COM4 | PASS | PID 9204 took over from PID 2896 |
| T0a | Harness holds the startup mutex | PASS | |
| T0b | Stand-in requester parked at the startup gate | PASS | PID 10392 |
| T1 | Owner accepted, could not deliver the answer, and paused | PASS | `Accepted CFA835 handover to Diagnose process 10392 could not be confirmed to it (Pipe is broken.); keypad and configuration reload stay paused until that process exits` |
| T2 | Owner did not stop for an answer nobody received | PASS | no stop line; PID 9204 alive |
| T3 | A valid edit is held back while the requester is alive | FAIL (harness) | The harness waited for the earlier wording `cannot be applied`. The log shows the edit **was** held back 1.5 s after the save: `Configuration change is valid but deferred: this process accepted a handover to a replacement that has not taken over. It applies if that handover is abandoned; restart the monitor if this persists`. The fix branch had reworded this line. |
| T4 | Nothing applied during the pause | PASS | 0 reload lines |
| T5 | Stand-in alive for the whole pause check | PASS | |
| T6 | Owner resumes once the requester has exited | PASS | `Handover requester process 10392 exited without taking over; the monitor resumed keypad input and configuration reload` |
| T7 | The held edit applies after resuming, without another save | PASS | `Configuration reloaded (Layout)` 0.1 s after resuming |
| T8 | A later edit applies normally | PASS | 2.2 s |
| INV1 | Same process, one connection, no connection or pipe failures | PASS | PID 9204; 1 connect; 0 failures |
| INV2 | COM4 still held after the incident | PASS | handle scan |
| R1 | The control pipe still serves a real handover afterwards | PASS | `Accepted CFA835 handover request from Monitor process 8124`, then `CFA835 monitor stopped` (graceful, not the forced legacy path) |
| R2 | Operator's monitor is back and holds COM4 | FAIL (harness) | The harness looked the process up by executable path from an unelevated query, which cannot read an elevated process's path. An elevated handle scan right afterwards showed PID 8124, `...\Cfa835SystemMonitor-4649cf6150c0-win-x64-20260904-232320\Cfa835SystemMonitor.exe`, ProductVersion `1.0.0+4649cf6...`, **HOLDS COM4**. |
| INV3 | Nothing written to stderr | PASS | empty |

`results.json` is the harness output exactly as run, with T3 and R2 still showing FAIL.
`monitor-output.txt` is the test monitor's full log, with the USB serial number redacted.

## Not verified

- **Keypad input during and after the pause.** No one was at the panel. Unit tests cover keys being
  refused while paused and accepted after `ResumeAfterFailedHandover`.
- **A cancelled shutdown countdown.** The run deliberately armed no Windows shutdown, so the path
  that leaves a cancelled countdown cancelled was not exercised on hardware. Unit tests cover it.
- **The "exit cannot be tracked" branch** (the owner stays paused and asks for a restart). It needs a
  requester whose process cannot be opened.
- **Anything visual on the panel.**
