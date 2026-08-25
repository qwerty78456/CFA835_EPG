[CmdletBinding()]
param(
    [string]$RuntimePath = $PSScriptRoot,
    [string]$NssmPath = 'C:\Program Files\nssm\win64\nssm.exe',
    [string]$InstallPath = 'C:\Program Files\Cfa835SystemMonitor',
    [string]$DataPath = 'C:\ProgramData\Cfa835SystemMonitor'
)

$ErrorActionPreference = 'Stop'
$serviceName = 'Cfa835SystemMonitor'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell session.'
}

if (-not (Test-Path -LiteralPath $NssmPath -PathType Leaf)) {
    throw "NSSM was not found at '$NssmPath'. Install the x64 2.24-101 build there or pass -NssmPath."
}

$sourceExe = Join-Path $RuntimePath 'Cfa835SystemMonitor.exe'
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    throw "The self-contained runtime was not found at '$sourceExe'."
}

New-Item -ItemType Directory -Force -Path $InstallPath, $DataPath, (Join-Path $DataPath 'logs') | Out-Null
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    & $NssmPath stop $serviceName | Out-Null
}

Get-ChildItem -LiteralPath $RuntimePath -Force | Where-Object Name -NotIn @('Install-Service.ps1', 'Update-Service.ps1', 'Uninstall-Service.ps1') | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $InstallPath -Recurse -Force
}

$configPath = Join-Path $DataPath 'appsettings.json'
if (-not (Test-Path -LiteralPath $configPath)) {
    Copy-Item -LiteralPath (Join-Path $InstallPath 'appsettings.json') -Destination $configPath
}

# The service is launched with --config pointing at $DataPath, so graphic mode resolves layout.json
# (and any background artwork it names) from there. Seed it once and never overwrite operator edits.
$layoutPath = Join-Path $DataPath 'layout.json'
if (-not (Test-Path -LiteralPath $layoutPath)) {
    Copy-Item -LiteralPath (Join-Path $InstallPath 'layout.json') -Destination $layoutPath
}

$installedExe = Join-Path $InstallPath 'Cfa835SystemMonitor.exe'
if (-not $existing) {
    & $NssmPath install $serviceName $installedExe | Out-Null
}

& $NssmPath set $serviceName Application $installedExe | Out-Null
& $NssmPath set $serviceName AppDirectory $InstallPath | Out-Null
& $NssmPath set $serviceName AppParameters "--config `"$configPath`"" | Out-Null
& $NssmPath set $serviceName ObjectName LocalSystem | Out-Null
& $NssmPath set $serviceName DisplayName 'CFA835 System Monitor' | Out-Null
& $NssmPath set $serviceName Description 'Displays Windows health metrics and activity on the Crystalfontz CFA835.' | Out-Null
& $NssmPath set $serviceName Start SERVICE_DELAYED_AUTO_START | Out-Null
& $NssmPath set $serviceName AppExit Default Restart | Out-Null
& $NssmPath set $serviceName AppRestartDelay 5000 | Out-Null
& $NssmPath set $serviceName AppStopMethodConsole 5000 | Out-Null
& $NssmPath set $serviceName AppStdout (Join-Path $DataPath 'logs\stdout.log') | Out-Null
& $NssmPath set $serviceName AppStderr (Join-Path $DataPath 'logs\stderr.log') | Out-Null
& $NssmPath set $serviceName AppRotateFiles 1 | Out-Null
& $NssmPath set $serviceName AppRotateOnline 1 | Out-Null
& $NssmPath set $serviceName AppRotateBytes 10485760 | Out-Null

& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null
& sc.exe failureflag $serviceName 1 | Out-Null
& icacls.exe $DataPath /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null

& $NssmPath start $serviceName | Out-Null
Get-Service -Name $serviceName | Format-Table Status, Name, DisplayName
Write-Host "Configuration: $configPath"
Write-Host "Logs: $(Join-Path $DataPath 'logs')"
