[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifacts = Join-Path $repository 'artifacts'
$publish = Join-Path $artifacts 'publish'
$commit = (& git -C $repository rev-parse --short=12 HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'The repository must have a Git commit before creating a release.'
}

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
if (Test-Path -LiteralPath $publish) {
    Remove-Item -LiteralPath $publish -Recurse -Force
}

& dotnet restore (Join-Path $repository 'Cfa835SystemMonitor.slnx') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
& dotnet test (Join-Path $repository 'Cfa835SystemMonitor.slnx') -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed.' }
& dotnet publish (Join-Path $repository 'src\Cfa835SystemMonitor\Cfa835SystemMonitor.csproj') -c $Configuration -r win-x64 --self-contained true --no-restore -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Copy-Item -LiteralPath (Join-Path $repository 'README.md'), (Join-Path $repository 'THIRD-PARTY-NOTICES.md') -Destination $publish
Copy-Item -LiteralPath (Join-Path $repository 'scripts\Install-Service.ps1'), (Join-Path $repository 'scripts\Update-Service.ps1'), (Join-Path $repository 'scripts\Uninstall-Service.ps1') -Destination $publish
Set-Content -LiteralPath (Join-Path $publish 'COMMIT.txt') -Value $commit -Encoding ascii

$runtimeZip = Join-Path $artifacts "Cfa835SystemMonitor-$commit-win-x64.zip"
$sourceZip = Join-Path $artifacts "Cfa835SystemMonitor-$commit-source.zip"
Remove-Item -LiteralPath $runtimeZip, $sourceZip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $runtimeZip -CompressionLevel Optimal
& git -C $repository archive --format=zip --output=$sourceZip HEAD
if ($LASTEXITCODE -ne 0) { throw 'git archive failed.' }

$hashes = Get-FileHash -Algorithm SHA256 -LiteralPath $runtimeZip, $sourceZip
$hashFile = Join-Path $artifacts "Cfa835SystemMonitor-$commit-SHA256.txt"
$hashes | ForEach-Object { "{0}  {1}" -f $_.Hash.ToLowerInvariant(), [IO.Path]::GetFileName($_.Path) } | Set-Content -LiteralPath $hashFile -Encoding ascii
$hashes | Format-Table Algorithm, Hash, Path
Write-Host "Commit: $commit"
