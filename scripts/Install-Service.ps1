[CmdletBinding()]
param(
    [string]$RuntimePath = $PSScriptRoot,
    [string]$NssmPath = 'C:\Program Files\nssm\win64\nssm.exe',
    [string]$InstallPath = 'C:\Program Files\Cfa835SystemMonitor',
    [string]$DataPath = 'C:\ProgramData\Cfa835SystemMonitor',
    [switch]$SkipPawnIO
)

$ErrorActionPreference = 'Stop'
$serviceName = 'Cfa835SystemMonitor'

function Get-PawnIOVersion {
    # Same key LibreHardwareMonitor and Cfa835SystemMonitor probe, checked in both registry views.
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
        try {
            $key = $base.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO')
            if ($key) {
                try { return [string]($key.GetValue('DisplayVersion', 'unknown version')) } finally { $key.Dispose() }
            }
        } finally {
            $base.Dispose()
        }
    }

    return $null
}
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

# CPU temperature needs ring-0 register access, which LibreHardwareMonitor obtains by opening the
# kernel device \\?\GLOBALROOT\Device\PawnIO. That device only exists while the PawnIO driver service
# is loaded, so a kernel driver has to be installed by an administrator; bundling the installer
# removes the download, not the installation. Without it cpu.temperature renders its fallback text
# and the thermal-warning LED never arms. This applies equally to Intel and AMD.
$pawnVersion = Get-PawnIOVersion
if ($pawnVersion) {
    Write-Host "PawnIO already installed: $pawnVersion"
}
elseif ($SkipPawnIO) {
    Write-Warning 'PawnIO is not installed and -SkipPawnIO was passed. CPU temperature will be unavailable.'
}
else {
    $pawnSetup = Join-Path $RuntimePath 'third-party\pawnio\PawnIO_setup.exe'
    if (-not (Test-Path -LiteralPath $pawnSetup -PathType Leaf)) {
        Write-Warning "PawnIO is not installed and no bundled installer was found at '$pawnSetup'. Install it from https://pawnio.eu/ or CPU temperature will be unavailable."
    }
    else {
        # The signature is what makes this binary trustworthy, not its presence in the release, and
        # an unsigned or tampered kernel driver must never be launched. Refuse rather than warn.
        $signature = Get-AuthenticodeSignature -LiteralPath $pawnSetup
        if ($signature.Status -ne 'Valid') {
            throw "Refusing to run '$pawnSetup': Authenticode status is '$($signature.Status)' ($($signature.StatusMessage))."
        }

        $subject = [string]$signature.SignerCertificate.Subject
        if ($subject -notmatch 'CN=namazso\.eu') {
            throw "Refusing to run '$pawnSetup': unexpected signer '$subject'."
        }

        Write-Host "Installing PawnIO from the bundled installer, signed by: $subject"
        Write-Host 'Complete the wizard when it appears. Choose the signed edition, not the unrestricted one.'
        $process = Start-Process -FilePath $pawnSetup -Wait -PassThru
        $pawnVersion = Get-PawnIOVersion
        if ($pawnVersion) {
            Write-Host "PawnIO installed: $pawnVersion"
        }
        else {
            Write-Warning "The PawnIO installer exited with code $($process.ExitCode) and the driver is still not registered. CPU temperature will be unavailable until it is installed."
        }
    }
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
