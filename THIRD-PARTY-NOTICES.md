# Third-party notices

This application uses the following packages. Their licenses remain with their respective authors.

- LibreHardwareMonitorLib — Mozilla Public License 2.0 and the additional licenses identified by the LibreHardwareMonitor project: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
- Microsoft.Extensions.Hosting and Microsoft.Extensions.Logging.Console — MIT License: https://github.com/dotnet/runtime
- System.IO.Ports — MIT License: https://github.com/dotnet/runtime
- System.Drawing.Common and its Microsoft.Win32.SystemEvents dependency — MIT License: https://github.com/dotnet/runtime
- xUnit.net test packages — Apache License 2.0: https://github.com/xunit/xunit

`System.Drawing.Common` wraps GDI+, which is a Windows operating-system component. The release directory therefore carries only the managed assemblies; no native imaging library is redistributed.

Fonts are never redistributed. Graphic mode rasterizes whichever families `layout.fontFamilies` names from those already installed on the monitored machine.

PawnIO and NSSM are installed separately and are not redistributed in the application release directory.
