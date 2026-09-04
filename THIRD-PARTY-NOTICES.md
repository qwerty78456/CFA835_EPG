# Third-party notices

Cfa835SystemMonitor itself is MIT licensed; see [LICENSE](LICENSE).

This application uses the following packages. Their licenses remain with their respective authors.

- LibreHardwareMonitorLib — Mozilla Public License 2.0 and the additional licenses identified by the LibreHardwareMonitor project: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
- Microsoft.Extensions.Hosting and Microsoft.Extensions.Logging.Console — MIT License: https://github.com/dotnet/runtime
- System.IO.Ports, System.ServiceProcess.ServiceController, and the latter's System.Diagnostics.EventLog dependency — MIT License: https://github.com/dotnet/runtime
- System.Drawing.Common and its Microsoft.Win32.SystemEvents dependency — MIT License: https://github.com/dotnet/runtime
- xUnit.net test packages — Apache License 2.0: https://github.com/xunit/xunit

`System.Drawing.Common` wraps GDI+, which is a Windows operating-system component. The release directory therefore carries only the managed assemblies; no native imaging library is redistributed.

Fonts are never redistributed. Graphic mode rasterizes whichever families `layout.fontFamilies` names from those already installed on the monitored machine.

## Redistributed binaries

**PawnIO 2.2.0** — GNU General Public License version 2, with a special exception for programs that
communicate with the driver solely through its device IO control interface:
<https://github.com/namazso/PawnIO>. Copyright © 2026 namazso <admin@namazso.eu>.

The signed installer is redistributed unmodified in `third-party/pawnio/`, together with the GPLv2
text (`COPYING`) and the corresponding source archive for the same tag, which satisfies GPLv2
section 3(a). `third-party/pawnio/README.md` records the SHA-256 of each file, the upstream commit,
and the verified Authenticode signer. **Keep those files together whenever this software is
redistributed.**

Cfa835SystemMonitor reaches PawnIO only through the device IO control interface, by way of
`LibreHardwareMonitorLib`, and never loads a module over the Pawn interface. The special exception
therefore applies and this project's MIT licence is unaffected.

NSSM is installed separately and is not redistributed in the application release directory.
