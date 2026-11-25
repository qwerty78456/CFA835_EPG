# Usage (from an unelevated shell; the elevated parts go through Start-Process -Verb RunAs):
#   1. Copy the configuration under test into <Root>\config (appsettings.json, layout.json, artwork),
#      and also save it as <Root>\layout.original.json and <Root>\appsettings.original.json.
#   2. .\Run-HotReloadHwTest.ps1 -Root D:\CFA835\hardware-test -BuildDir <published build folder>
# The edits in steps H1-H12 match the three-page DateTime/Network/Shutdown layout that was tested;
# adjust the string replacements if the layout under test differs.
param(
    [string]$Root = 'D:\CFA835\hardware-test',
    [string]$BuildDir = 'D:\CFA835\hardware-test\build-96eb9a6ba3cb',
    [string]$InspectScript = (Join-Path $PSScriptRoot 'Inspect-Cfa835Owner.ps1'),
    [string]$LauncherScript = (Join-Path $PSScriptRoot 'Start-HwMonitor.ps1')
)

# Hardware acceptance harness for live configuration reload (DEPLOYMENT.md section 10.2), automated
# as far as logs and handles allow. Runs unelevated; elevation is only needed to start the monitor
# and to inspect its handles, which go through Start-Process -Verb RunAs.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$cfg = Join-Path $Root 'config'
$log = Join-Path $Root 'monitor.out.log'
$results = [System.Collections.Generic.List[object]]::new()
$pwsh = (Get-Process -Id $PID).Path

function Write-Utf8([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Get-LogLines {
    if (-not (Test-Path -LiteralPath $log)) { return @() }
    # Get-Content opens with FileShare.ReadWrite, so it can read while the monitor writes.
    return @(Get-Content -LiteralPath $log)
}

function Wait-LogLine([string]$Pattern, [int]$After, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $started = Get-Date
    while ((Get-Date) -lt $deadline) {
        $lines = Get-LogLines
        for ($i = $After; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match $Pattern) {
                return [pscustomobject]@{ Line = $lines[$i]; Index = $i; Seconds = [Math]::Round(((Get-Date) - $started).TotalSeconds, 1) }
            }
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Get-MonitorPid { [int](Get-Content -LiteralPath (Join-Path $Root 'monitor.pid') -Raw).Trim() }

function Test-HoldsCom4([int]$ProcessId, [string]$Tag) {
    $out = Join-Path $Root "handles-$Tag.txt"
    Remove-Item -LiteralPath $out -ErrorAction SilentlyContinue
    Start-Process -FilePath $pwsh -Verb RunAs -WindowStyle Hidden -Wait -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$InspectScript`"",
        '-OutFile', "`"$out`"", '-TargetPid', $ProcessId, '-ComPort', 'COM4') | Out-Null
    $text = Get-Content -LiteralPath $out -Raw
    return ($text -match "PID $ProcessId HOLDS COM4")
}

function Add-Result([string]$Id, [string]$Check, [bool]$Pass, [string]$Evidence) {
    $results.Add([pscustomobject]@{ Id = $Id; Check = $Check; Result = $(if ($Pass) { 'PASS' } else { 'FAIL' }); Evidence = $Evidence })
    Write-Host ("[{0}] {1}: {2} -- {3}" -f $Id, $(if ($Pass) { 'PASS' } else { 'FAIL' }), $Check, $Evidence)
}

function Invoke-Step([string]$Id, [string]$Check, [scriptblock]$Edit, [string]$Expect, [int]$TimeoutSeconds = 10, [string]$Reject) {
    $before = (Get-LogLines).Count
    & $Edit
    $hit = Wait-LogLine -Pattern $Expect -After $before -TimeoutSeconds $TimeoutSeconds
    $pass = $null -ne $hit
    $evidence = if ($hit) { "after $($hit.Seconds)s: $($hit.Line.Substring([Math]::Max(0, $hit.Line.IndexOf(']') + 2)))" } else { "no line matching /$Expect/ within ${TimeoutSeconds}s" }
    if ($Reject) {
        $bad = (Get-LogLines) | Select-Object -Skip $before | Where-Object { $_ -match $Reject }
        if ($bad) { $pass = $false; $evidence += " | UNEXPECTED: $($bad -join ' / ')" }
    }
    Add-Result $Id $Check $pass $evidence
    return $hit
}

$layoutPath = Join-Path $cfg 'layout.json'
$settingsPath = Join-Path $cfg 'appsettings.json'
$originalLayout = Get-Content -LiteralPath (Join-Path $Root 'layout.original.json') -Raw
$originalSettings = Get-Content -LiteralPath (Join-Path $Root 'appsettings.original.json') -Raw

# ---------------------------------------------------------------- H0: start and take over
$previousOwner = @(Get-CimInstance Win32_Process -Filter "Name='Cfa835SystemMonitor.exe'" | Select-Object -ExpandProperty ProcessId)
Remove-Item -LiteralPath $log, (Join-Path $Root 'monitor.err.log'), (Join-Path $Root 'monitor.pid') -ErrorAction SilentlyContinue
# No -Wait: PowerShell would also wait for the launcher's descendants, i.e. the monitor itself.
Start-Process -FilePath $pwsh -Verb RunAs -WindowStyle Hidden -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$LauncherScript`"",
    '-Exe', "`"$(Join-Path $BuildDir 'Cfa835SystemMonitor.exe')`"", '-Config', "`"$settingsPath`"", '-LogDir', "`"$Root`"") | Out-Null
$pidDeadline = (Get-Date).AddSeconds(30)
while (-not (Test-Path -LiteralPath (Join-Path $Root 'monitor.pid')) -and (Get-Date) -lt $pidDeadline) { Start-Sleep -Milliseconds 250 }
Start-Sleep -Milliseconds 250
$monitorPid = Get-MonitorPid
$connected = Wait-LogLine -Pattern 'Connected to CFA835' -After 0 -TimeoutSeconds 60
Add-Result 'H0a' 'New build takes the CFA835 over from the running monitor' ($null -ne $connected) $(if ($connected) { "PID $monitorPid; $($connected.Line.Substring($connected.Line.IndexOf(']') + 2))" } else { 'not connected within 60 s' })
$watching = Wait-LogLine -Pattern 'Live configuration reload is on' -After 0 -TimeoutSeconds 5
Add-Result 'H0b' 'Reload is on by default for a config without a reload group (upgrade path)' ($null -ne $watching) $(if ($watching) { $watching.Line.Substring($watching.Line.IndexOf(']') + 2) } else { 'missing' })
Start-Sleep -Seconds 2
$gone = @($previousOwner | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
Add-Result 'H0c' 'Previous owner handed over and exited' ($gone.Count -eq 0) "previous PID(s) $($previousOwner -join ', ') still running: $($gone -join ', ')"
Add-Result 'H0d' 'New monitor holds COM4' (Test-HoldsCom4 $monitorPid 'start') "PID $monitorPid handle scan"
Start-Sleep -Seconds 3

# ---------------------------------------------------------------- H1: move a field
Invoke-Step 'H1' 'Moving a field applies live' {
    Write-Utf8 $layoutPath ($originalLayout.Replace('"x": 64, "y": 2,', '"x": 64, "y": 4,'))
} 'Configuration reloaded \(Layout\)' 10 'rejected' | Out-Null

# ---------------------------------------------------------------- H2: broken JSON, then restore
$current = Get-Content -LiteralPath $layoutPath -Raw
$before = (Get-LogLines).Count
Invoke-Step 'H2a' 'Broken layout.json is rejected with one warning naming the file' {
    Write-Utf8 $layoutPath ($current.Substring(0, $current.LastIndexOf('}')))
} 'Configuration change rejected.*layout\.json' 10 'Configuration reloaded' | Out-Null
Start-Sleep -Seconds 4
$warnings = @((Get-LogLines) | Select-Object -Skip $before | Where-Object { $_ -match 'Configuration change rejected' })
Add-Result 'H2b' 'The rejection is logged once, not on every poll' ($warnings.Count -eq 1) "$($warnings.Count) warning line(s) in ~5 s"
Invoke-Step 'H2c' 'Restoring the file applies the edit' { Write-Utf8 $layoutPath $current } 'Configuration reloaded' 10 | Out-Null

# ---------------------------------------------------------------- H3: wrong-size PNG, then fixed in place
$bad = Join-Path $cfg 'bad.png'
$bitmap = [System.Drawing.Bitmap]::new(100, 50); $bitmap.Save($bad, [System.Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()
Invoke-Step 'H3a' 'A 100x50 background is rejected naming the PNG' {
    Write-Utf8 $layoutPath ($current.Replace('"background": "background.png"', '"background": "bad.png"'))
} 'Configuration change rejected.*bad\.png.*244x68' 10 'Configuration reloaded' | Out-Null
Invoke-Step 'H3b' 'Replacing the PNG alone (layout untouched) applies it' {
    Copy-Item -LiteralPath (Join-Path $cfg 'background.png') -Destination $bad -Force
    (Get-Item -LiteralPath $bad).LastWriteTimeUtc = [DateTime]::UtcNow
} 'Configuration reloaded' 10 | Out-Null

# ---------------------------------------------------------------- H4: PNG referenced before it exists
$late = Join-Path $cfg 'late.png'
Remove-Item -LiteralPath $late -ErrorAction SilentlyContinue
Invoke-Step 'H4a' 'A background that does not exist yet is rejected' {
    Write-Utf8 $layoutPath ($current.Replace('"background": "background.png"', '"background": "late.png"'))
} 'Configuration change rejected.*late\.png' 10 | Out-Null
Invoke-Step 'H4b' 'Copying the PNG in is picked up without another save' {
    Copy-Item -LiteralPath (Join-Path $cfg 'background.png') -Destination $late -Force
} 'Configuration reloaded' 25 | Out-Null

# ---------------------------------------------------------------- H5: add a page, remove the current one
$extraPage = '{ "id": "Extra", "kind": "normal", "fields": [ { "source": "literal", "text": "HOT RELOAD", "x": 0, "y": 20, "width": 244, "height": 26, "align": "center", "sizePx": 24 } ] },' + "`n" + '    {' + "`n" + '      // Reachable only'
Invoke-Step 'H5a' 'Adding a page grows the ring' {
    Write-Utf8 $layoutPath ($current.Replace('{' + "`n" + '      // Reachable only', $extraPage))
} 'Configuration reloaded.*4 page\(s\)' 10 | Out-Null
$withoutMain = [regex]::Replace($current, '(?s)\{\s*"id": "DateTime".*?\n    \},\s*\n    \{\s*"id": "Network"', '{' + "`n" + '      "id": "Network"')
Invoke-Step 'H5b' 'Removing the page on screen falls back to the first page' {
    Write-Utf8 $layoutPath $withoutMain
} "Configuration reloaded.*2 page\(s\).*current page 'Network'" 10 | Out-Null

# ---------------------------------------------------------------- H6: gamma
Invoke-Step 'H6' 'Toggling gammaCorrection applies live' {
    Write-Utf8 $layoutPath ($current.Replace('"gammaCorrection": true', '"gammaCorrection": false'))
} 'Configuration reloaded' 10 | Out-Null
Write-Utf8 $layoutPath $current
Wait-LogLine -Pattern 'Configuration reloaded' -After ((Get-LogLines).Count) -TimeoutSeconds 10 | Out-Null

# ---------------------------------------------------------------- H7: appsettings values
Invoke-Step 'H7' 'autoCycleSeconds and countdownSeconds apply live' {
    Write-Utf8 $settingsPath ($originalSettings.Replace('"autoCycleSeconds": 5', '"autoCycleSeconds": 7').Replace('"countdownSeconds": 30', '"countdownSeconds": 45'))
} 'Configuration reloaded \(.*Shutdown, Display\)' 10 | Out-Null

# ---------------------------------------------------------------- H9: device settings
Invoke-Step 'H9' 'device.fallbackPort change is deferred to the next reconnect' {
    Write-Utf8 $settingsPath ($originalSettings.Replace('"fallbackPort": "COM3"', '"fallbackPort": "COM9"'))
} 'device\.\* settings changed; they apply at the next reconnect' 10 | Out-Null

# ---------------------------------------------------------------- H10: mode switch
Invoke-Step 'H10a' 'Switching display.mode to text applies live' {
    Write-Utf8 $settingsPath ($originalSettings.Replace('"mode": "graphic"', '"mode": "text"'))
} 'Configuration reloaded \(.*DisplayMode.*\): text mode' 10 | Out-Null
Start-Sleep -Seconds 3
Invoke-Step 'H10b' 'Switching back to graphic applies live' {
    Write-Utf8 $settingsPath $originalSettings
} 'Configuration reloaded \(.*DisplayMode.*\): graphic mode' 15 | Out-Null

# ---------------------------------------------------------------- H11: invalid appsettings value
Invoke-Step 'H11a' 'An out-of-range setting is rejected naming appsettings.json' {
    Write-Utf8 $settingsPath ($originalSettings.Replace('"activityMs": 100', '"activityMs": 1'))
} 'Configuration change rejected.*appsettings\.json.*sampling\.activityMs' 10 'Configuration reloaded' | Out-Null
Invoke-Step 'H11b' 'Restoring it applies' { Write-Utf8 $settingsPath $originalSettings } 'Configuration reloaded' 10 | Out-Null

# ---------------------------------------------------------------- H12: switch reload off
Invoke-Step 'H12a' 'reload.enabled = false is applied and announced' {
    Write-Utf8 $settingsPath ($originalSettings.TrimEnd().TrimEnd('}').TrimEnd() + ",`n  `"reload`": { `"enabled`": false }`n}`n")
} 'Live configuration reload is now disabled' 10 | Out-Null
$before = (Get-LogLines).Count
Write-Utf8 $layoutPath ($current.Replace('"x": 64, "y": 2,', '"x": 64, "y": 6,'))
Start-Sleep -Seconds 6
$after = @((Get-LogLines) | Select-Object -Skip $before | Where-Object { $_ -match 'Configuration (reloaded|change rejected)' })
Add-Result 'H12b' 'Further edits are ignored once reload is off' ($after.Count -eq 0) "$($after.Count) reload line(s) in 6 s after an edit"

# ---------------------------------------------------------------- Invariants across the whole run
$lines = Get-LogLines
$connects = @($lines | Where-Object { $_ -match 'Connected to CFA835' })
$failures = @($lines | Where-Object { $_ -match 'Monitor connection failed|receive loop stopped|Unrecoverable' })
$stillPid = [bool](Get-Process -Id $monitorPid -ErrorAction SilentlyContinue)
Add-Result 'INV1' 'Same process throughout (no restart)' $stillPid "PID $monitorPid alive: $stillPid"
Add-Result 'INV2' 'COM port never dropped (exactly one connect, no connection failures)' (($connects.Count -eq 1) -and ($failures.Count -eq 0)) "$($connects.Count) connect line(s), $($failures.Count) failure line(s)"
Add-Result 'INV3' 'COM4 still held at the end' (Test-HoldsCom4 $monitorPid 'end') "PID $monitorPid handle scan"
$errText = if (Test-Path (Join-Path $Root 'monitor.err.log')) { (Get-Content (Join-Path $Root 'monitor.err.log') -Raw) } else { '' }
Add-Result 'INV4' 'Nothing written to stderr' ([string]::IsNullOrWhiteSpace($errText)) $(if ($errText) { $errText.Trim() } else { 'empty' })

# Leave the test config as it was found.
Write-Utf8 $layoutPath $originalLayout
Write-Utf8 $settingsPath $originalSettings

$results | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $Root 'results.json') -Encoding utf8
$results | Format-Table -AutoSize -Wrap | Out-String -Width 220
