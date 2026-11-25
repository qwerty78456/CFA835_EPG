param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Config,
    [Parameter(Mandatory)][string]$MonitorLog,
    [Parameter(Mandatory)][string]$LayoutPath,
    [Parameter(Mandatory)][int]$MonitorPid,
    [Parameter(Mandatory)][string]$WorkDir
)

# Runs ELEVATED. Reproduces "an accepted handover whose answer cannot be delivered" against a running
# monitor, without racing a real requester:
#   1. Hold the global startup mutex, so a genuine Cfa835SystemMonitor.exe started now parks harmlessly
#      inside InstanceCoordinator.Acquire (no sensors, no COM port) and can be verified by the owner.
#   2. Send the owner a handover request that describes that parked process, then close the pipe at
#      once: the owner verifies the requester, prepares, accepts, and its answer hits a closed pipe.
#   3. Check keys/reload stay paused while the "requester" lives, end it, check the monitor resumes.
# The parked process is killed before the mutex is released, so it can never go on to request a
# handover (or fall back to terminating the owner) itself.

$ErrorActionPreference = 'Stop'
$results = [System.Collections.Generic.List[object]]::new()
$utf8 = [Text.UTF8Encoding]::new($false)

function Get-LogLines { if (Test-Path -LiteralPath $MonitorLog) { @(Get-Content -LiteralPath $MonitorLog) } else { @() } }

function Wait-LogLine([string]$Pattern, [int]$After, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $started = Get-Date
    while ((Get-Date) -lt $deadline) {
        $lines = Get-LogLines
        for ($i = $After; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match $Pattern) {
                return [pscustomobject]@{ Line = $lines[$i]; Seconds = [Math]::Round(((Get-Date) - $started).TotalSeconds, 1) }
            }
        }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function Add-Result([string]$Id, [string]$Check, [bool]$Pass, [string]$Evidence) {
    $results.Add([pscustomobject]@{ Id = $Id; Check = $Check; Result = $(if ($Pass) { 'PASS' } else { 'FAIL' }); Evidence = $Evidence })
}

function Show([object]$Hit) { if ($Hit) { "after $($Hit.Seconds)s: $($Hit.Line.Substring($Hit.Line.IndexOf(']') + 2))" } else { 'no matching line' } }

$mutex = $null
$standIn = $null
try {
    # 1. Park a genuine instance behind the startup gate.
    $mutex = [Threading.Mutex]::OpenExisting('Global\Cfa835SystemMonitor.Startup.v1')
    Add-Result 'T0a' 'Harness holds the startup mutex' ($mutex.WaitOne(5000)) 'Global\Cfa835SystemMonitor.Startup.v1'
    $standIn = Start-Process -FilePath $Exe -ArgumentList @('--diagnose', '--config', "`"$Config`"") `
        -WorkingDirectory (Split-Path -Parent $Exe) -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $WorkDir 'standin.out.txt') -RedirectStandardError (Join-Path $WorkDir 'standin.err.txt')
    Start-Sleep -Seconds 2
    $process = Get-Process -Id $standIn.Id
    $descriptor = [ordered]@{
        pid = $process.Id
        startTimeUtc = $process.StartTime.ToUniversalTime().ToString('o')
        executablePath = $process.MainModule.FileName
        version = 'hardware-test stand-in'
        mode = 1          # AppMode.Diagnose
        sessionId = $process.SessionId
        runContext = 0    # InstanceRunContext.Foreground
    }
    Add-Result 'T0b' 'Stand-in requester is parked at the startup gate' (-not $process.HasExited) "PID $($process.Id), $($descriptor.executablePath)"

    # 2. Send its handover request and hang up before the owner can answer.
    $request = [ordered]@{ protocolVersion = 1; requester = $descriptor } | ConvertTo-Json -Compress
    $before = (Get-LogLines).Count
    $client = [IO.Pipes.NamedPipeClientStream]::new(
        '.', 'Cfa835SystemMonitor.Control.v1', [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::None, [Security.Principal.TokenImpersonationLevel]::Identification)
    $client.Connect(3000)
    $bytes = $utf8.GetBytes($request + "`n")
    $client.Write($bytes, 0, $bytes.Length)
    $client.Flush()
    $client.Dispose()

    $undelivered = Wait-LogLine 'could not be confirmed to it.*stay paused until that process exits' $before 5
    Add-Result 'T1' 'Owner accepted, could not deliver the answer, and paused (the bug''s trigger)' ($null -ne $undelivered) (Show $undelivered)
    $stopped = @((Get-LogLines) | Select-Object -Skip $before | Where-Object { $_ -match 'Accepted CFA835 handover request|CFA835 monitor stopped' })
    Add-Result 'T2' 'Owner did not stop for an answer nobody received' (($stopped.Count -eq 0) -and [bool](Get-Process -Id $MonitorPid -ErrorAction SilentlyContinue)) "$($stopped.Count) stop line(s); PID $MonitorPid alive"

    # 3a. While the accepted requester is alive, a valid edit must be held back.
    $layout = [IO.File]::ReadAllText($LayoutPath)
    $before = (Get-LogLines).Count
    [IO.File]::WriteAllText($LayoutPath, $layout.Replace('"x": 64, "y": 2,', '"x": 64, "y": 4,'), $utf8)
    $held = Wait-LogLine 'valid but deferred: this process accepted a handover' $before 6
    Add-Result 'T3' 'A valid edit is held back while the accepted requester is alive' ($null -ne $held) (Show $held)
    $applied = @((Get-LogLines) | Select-Object -Skip $before | Where-Object { $_ -match 'Configuration reloaded' })
    Add-Result 'T4' 'Nothing was applied during the pause' ($applied.Count -eq 0) "$($applied.Count) reload line(s)"
    Add-Result 'T5' 'Stand-in was still alive for the whole pause check' (-not $process.HasExited) "PID $($process.Id)"

    # 3b. The requester goes away without taking over.
    $before = (Get-LogLines).Count
    Stop-Process -Id $process.Id -Force
    $process.WaitForExit(5000) | Out-Null
    $resumed = Wait-LogLine 'exited without taking over; the monitor resumed' $before 5
    Add-Result 'T6' 'Owner resumes once the accepted requester has exited' ($null -ne $resumed) (Show $resumed)
    $heldApplied = Wait-LogLine 'Configuration reloaded' $before 6
    Add-Result 'T7' 'The held edit applies after resuming, without another save' ($null -ne $heldApplied) (Show $heldApplied)
}
finally {
    if ($standIn -and -not $standIn.HasExited) { Stop-Process -Id $standIn.Id -Force -ErrorAction SilentlyContinue; $standIn.WaitForExit(5000) | Out-Null }
    if ($mutex) { try { $mutex.ReleaseMutex() } catch { }; $mutex.Dispose() }
}

# 4. Reload works normally afterwards.
$before = (Get-LogLines).Count
$layout = [IO.File]::ReadAllText($LayoutPath)
[IO.File]::WriteAllText($LayoutPath, $layout.Replace('"x": 64, "y": 4,', '"x": 64, "y": 6,'), $utf8)
$again = Wait-LogLine 'Configuration reloaded' $before 8
Add-Result 'T8' 'A later edit applies normally' ($null -ne $again) (Show $again)

$results | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $WorkDir 'trigger-results.json') -Encoding utf8
