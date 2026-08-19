[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RuntimePath,
    [string]$NssmPath = 'C:\Program Files\nssm\win64\nssm.exe',
    [string]$InstallPath = 'C:\Program Files\Cfa835SystemMonitor'
)

$ErrorActionPreference = 'Stop'
$serviceName = 'Cfa835SystemMonitor'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell session.'
}

if (-not (Test-Path -LiteralPath (Join-Path $RuntimePath 'Cfa835SystemMonitor.exe') -PathType Leaf)) {
    throw "No self-contained runtime was found in '$RuntimePath'."
}

if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
    throw "Service '$serviceName' is not installed. Use Install-Service.ps1 first."
}

& $NssmPath stop $serviceName | Out-Null
Get-ChildItem -LiteralPath $RuntimePath -Force | Where-Object Name -NotIn @('Install-Service.ps1', 'Update-Service.ps1', 'Uninstall-Service.ps1', 'appsettings.json') | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $InstallPath -Recurse -Force
}
& $NssmPath start $serviceName | Out-Null
Get-Service -Name $serviceName | Format-Table Status, Name, DisplayName
