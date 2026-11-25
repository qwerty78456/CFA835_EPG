param(
    [string]$Root = 'D:\CFA835\hardware-test',
    [string]$BuildDir = 'D:\CFA835\hardware-test\build-b0ad0a09ce11',
    [string]$InspectScript = (Join-Path $PSScriptRoot 'Inspect-Cfa835Owner.ps1'),
    [string]$LauncherScript = (Join-Path $PSScriptRoot 'Start-HwMonitor.ps1'),
    [string]$TriggerScript = (Join-Path $PSScriptRoot 'Invoke-UndeliveredHandover.ps1'),
    [Parameter(Mandatory)][string]$RestoreExe
)

# Hardware test for "recover when an accepted handover cannot be delivered". Runs unelevated; the
# monitor start, the trigger and handle scans run elevated through Start-Process -Verb RunAs.
$ErrorActionPreference = 'Stop'
$cfg = Join-Path $Root 'config'
$work = Join-Path $Root 'handover'
$log = Join-Path $work 'monitor.out.log'
$pwsh = (Get-Process -Id $PID).Path
$results = [System.Collections.Generic.List[object]]::new()
$utf8 = [Text.UTF8Encoding]::new($false)

function Get-LogLines { if (Test-Path -LiteralPath $log) { @(Get-Content -LiteralPath $log) } else { @() } }
function Wait-LogLine([string]$Pattern, [int]$After, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $lines = Get-LogLines
        for ($i = $After; $i -lt $lines.Count; $i++) { if ($lines[$i] -match $Pattern) { return $lines[$i] } }
        Start-Sleep -Milliseconds 250
    }
    return $null
}
function Add-Result([string]$Id, [string]$Check, [bool]$Pass, [string]$Evidence) {
    $results.Add([pscustomobject]@{ Id = $Id; Check = $Check; Result = $(if ($Pass) { 'PASS' } else { 'FAIL' }); Evidence = $Evidence })
}
function Test-HoldsCom4([int]$ProcessId, [string]$Tag) {
    $out = Join-Path $work "handles-$Tag.txt"
    Remove-Item -LiteralPath $out -ErrorAction SilentlyContinue
    Start-Process -FilePath $pwsh -Verb RunAs -WindowStyle Hidden -Wait -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$InspectScript`"",
        '-OutFile', "`"$out`"", '-TargetPid', $ProcessId, '-ComPort', 'COM4') | Out-Null
    return ((Get-Content -LiteralPath $out -Raw) -match "PID $ProcessId HOLDS COM4")
}

# Fresh copy of the configuration under test.
[IO.File]::WriteAllText((Join-Path $cfg 'layout.json'), (Get-Content -LiteralPath (Join-Path $Root 'layout.original.json') -Raw), $utf8)
[IO.File]::WriteAllText((Join-Path $cfg 'appsettings.json'), (Get-Content -LiteralPath (Join-Path $Root 'appsettings.original.json') -Raw), $utf8)
Remove-Item -LiteralPath $log, (Join-Path $work 'monitor.err.log'), (Join-Path $work 'monitor.pid'), (Join-Path $work 'trigger-results.json') -ErrorAction SilentlyContinue

# Start the build under test; it takes the panel over from the operator's monitor.
$previousOwner = @(Get-CimInstance Win32_Process -Filter "Name='Cfa835SystemMonitor.exe'" | Select-Object -ExpandProperty ProcessId)
Start-Process -FilePath $pwsh -Verb RunAs -WindowStyle Hidden -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$LauncherScript`"",
    '-Exe', "`"$(Join-Path $BuildDir 'Cfa835SystemMonitor.exe')`"", '-Config', "`"$(Join-Path $cfg 'appsettings.json')`"", '-LogDir', "`"$work`"") | Out-Null
$deadline = (Get-Date).AddSeconds(30)
while (-not (Test-Path -LiteralPath (Join-Path $work 'monitor.pid')) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
Start-Sleep -Milliseconds 250
$monitorPid = [int](Get-Content -LiteralPath (Join-Path $work 'monitor.pid') -Raw).Trim()
$connected = Wait-LogLine 'Connected to CFA835' 0 60
Add-Result 'S0' 'Build under test takes the panel over and holds COM4' (($null -ne $connected) -and (Test-HoldsCom4 $monitorPid 'start')) "PID $monitorPid; previous owner(s) $($previousOwner -join ', ')"
Start-Sleep -Seconds 4

# The trigger and the pause/resume checks (elevated).
Start-Process -FilePath $pwsh -Verb RunAs -WindowStyle Hidden -Wait -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$TriggerScript`"",
    '-Exe', "`"$(Join-Path $BuildDir 'Cfa835SystemMonitor.exe')`"", '-Config', "`"$(Join-Path $cfg 'appsettings.json')`"",
    '-MonitorLog', "`"$log`"", '-LayoutPath', "`"$(Join-Path $cfg 'layout.json')`"", '-MonitorPid', $monitorPid, '-WorkDir', "`"$work`"") | Out-Null
$trigger = Get-Content -LiteralPath (Join-Path $work 'trigger-results.json') -Raw | ConvertFrom-Json
foreach ($row in $trigger) { $results.Add($row) }

# Invariants.
$lines = Get-LogLines
$connects = @($lines | Where-Object { $_ -match 'Connected to CFA835' })
$failures = @($lines | Where-Object { $_ -match 'Monitor connection failed|receive loop stopped|Unrecoverable|control pipe stopped' })
Add-Result 'INV1' 'Same process, one connection, no connection or pipe failures' (([bool](Get-Process -Id $monitorPid -ErrorAction SilentlyContinue)) -and $connects.Count -eq 1 -and $failures.Count -eq 0) "PID $monitorPid; $($connects.Count) connect(s); $($failures.Count) failure line(s)"
Add-Result 'INV2' 'COM4 still held after the incident' (Test-HoldsCom4 $monitorPid 'after') "PID $monitorPid"

# A real handover must still be served by the same control pipe: give the panel back to the operator.
$before = (Get-LogLines).Count
Start-Process -FilePath $RestoreExe -WorkingDirectory (Split-Path -Parent $RestoreExe) -Verb RunAs | Out-Null
$accepted = Wait-LogLine 'Accepted CFA835 handover request from Monitor process' $before 20
$stoppedLine = Wait-LogLine 'CFA835 monitor stopped' $before 20
Start-Sleep -Seconds 3
# An unelevated query cannot read an elevated process's path, so let the elevated handle scan report
# both the path and COM4 ownership for every remaining instance.
$owner = @(Get-CimInstance Win32_Process -Filter "Name='Cfa835SystemMonitor.exe'" | Where-Object ProcessId -ne $monitorPid | Select-Object -ExpandProperty ProcessId)
$restoredHolds = $false
foreach ($candidate in $owner) {
    if (Test-HoldsCom4 $candidate "restored-$candidate") {
        $scan = Get-Content -LiteralPath (Join-Path $work "handles-restored-$candidate.txt") -Raw
        $restoredHolds = $scan.Contains("ExecutablePath: $RestoreExe")
    }
}
Add-Result 'R1' 'The control pipe still serves a real handover afterwards (graceful, not forced)' (($null -ne $accepted) -and ($null -ne $stoppedLine)) "$accepted"
Add-Result 'R2' 'Operator''s monitor is back and holds COM4' $restoredHolds "PID $($owner -join ', ')"
$errText = if (Test-Path (Join-Path $work 'monitor.err.log')) { Get-Content (Join-Path $work 'monitor.err.log') -Raw } else { '' }
Add-Result 'INV3' 'Nothing written to stderr' ([string]::IsNullOrWhiteSpace($errText)) $(if ($errText) { $errText.Trim() } else { 'empty' })

# Leave the test configuration as found.
[IO.File]::WriteAllText((Join-Path $cfg 'layout.json'), (Get-Content -LiteralPath (Join-Path $Root 'layout.original.json') -Raw), $utf8)

$results | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $work 'results.json') -Encoding utf8
$results | ForEach-Object { "[{0}] {1}: {2} -- {3}" -f $_.Id, $_.Result, $_.Check, $_.Evidence }
