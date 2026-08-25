[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$NssmPath = 'C:\Program Files\nssm\win64\nssm.exe',
    [string]$InstallPath = 'C:\Program Files\Cfa835SystemMonitor',
    [switch]$RemoveFiles
)

$ErrorActionPreference = 'Stop'
$serviceName = 'Cfa835SystemMonitor'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell session.'
}

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    if ($PSCmdlet.ShouldProcess($serviceName, 'Stop and remove Windows service')) {
        & $NssmPath stop $serviceName | Out-Null
        & $NssmPath remove $serviceName confirm | Out-Null
    }
}

if ($RemoveFiles) {
    $expected = [IO.Path]::GetFullPath('C:\Program Files\Cfa835SystemMonitor')
    $resolved = [IO.Path]::GetFullPath($InstallPath)
    if ($resolved -ne $expected) {
        throw "Refusing to recursively remove unexpected path '$resolved'."
    }

    if ((Test-Path -LiteralPath $resolved) -and $PSCmdlet.ShouldProcess($resolved, 'Remove application files')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

# PawnIO is a system-wide kernel driver that other tools (Fan Control, LibreHardwareMonitor itself,
# hardware monitors generally) may also be using, so removing it here could break them. Uninstall it
# from Apps & features if it is genuinely no longer wanted.
Write-Host 'PawnIO and C:\ProgramData\Cfa835SystemMonitor were intentionally preserved.'
Write-Host 'Remove PawnIO separately via Apps & features only if no other tool depends on it.'
