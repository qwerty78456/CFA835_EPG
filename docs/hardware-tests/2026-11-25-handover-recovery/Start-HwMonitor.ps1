param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Config,
    [Parameter(Mandatory)][string]$LogDir
)

# Runs elevated. Starts the monitor hidden with stdout/stderr captured, records its PID, and exits.
$out = Join-Path $LogDir 'monitor.out.log'
$err = Join-Path $LogDir 'monitor.err.log'
$process = Start-Process -FilePath $Exe -ArgumentList @('--config', "`"$Config`"") `
    -WorkingDirectory (Split-Path -Parent $Exe) -WindowStyle Hidden `
    -RedirectStandardOutput $out -RedirectStandardError $err -PassThru
Set-Content -LiteralPath (Join-Path $LogDir 'monitor.pid') -Value $process.Id -Encoding ascii
