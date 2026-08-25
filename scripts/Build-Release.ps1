[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifacts = Join-Path $repository 'artifacts'
$commit = (& git -C $repository rev-parse --short=12 HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'The repository must have a Git commit before creating a release.'
}
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$releaseName = "Cfa835SystemMonitor-$commit-win-x64-$timestamp"
$release = Join-Path $artifacts $releaseName

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
if (Test-Path -LiteralPath $release) {
    throw "Release directory already exists: $release"
}

& dotnet restore (Join-Path $repository 'Cfa835SystemMonitor.slnx') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
& dotnet test (Join-Path $repository 'Cfa835SystemMonitor.slnx') -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed.' }
& dotnet publish (Join-Path $repository 'src\Cfa835SystemMonitor\Cfa835SystemMonitor.csproj') -c $Configuration -r win-x64 --self-contained true --no-restore -o $release
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Copy-Item -LiteralPath (Join-Path $repository 'README.md'), (Join-Path $repository 'CHANGELOG.md'), (Join-Path $repository 'DEPLOYMENT.md'), (Join-Path $repository 'THIRD-PARTY-NOTICES.md'), (Join-Path $repository 'LICENSE') -Destination $release
Copy-Item -LiteralPath (Join-Path $repository 'scripts\Install-Service.ps1'), (Join-Path $repository 'scripts\Update-Service.ps1'), (Join-Path $repository 'scripts\Uninstall-Service.ps1') -Destination $release

# The PawnIO driver installer ships with the release so an air-gapped machine can enable CPU
# temperature access without a download. GPLv2 section 3 requires the corresponding source to travel
# with the binary, so the whole directory is copied, never just the executable.
$thirdParty = Join-Path $repository 'third-party'
if (Test-Path -LiteralPath $thirdParty) {
    Copy-Item -LiteralPath $thirdParty -Destination $release -Recurse -Force

    $pawnSetup = Join-Path $release 'third-party\pawnio\PawnIO_setup.exe'
    if (Test-Path -LiteralPath $pawnSetup -PathType Leaf) {
        $signature = Get-AuthenticodeSignature -LiteralPath $pawnSetup
        if ($signature.Status -ne 'Valid') {
            throw "Bundled PawnIO installer failed signature validation: $($signature.Status) - $($signature.StatusMessage)"
        }

        Write-Host "Bundled PawnIO: $((Get-Item -LiteralPath $pawnSetup).VersionInfo.ProductVersion), signed by $($signature.SignerCertificate.Subject)"
    }
}
Set-Content -LiteralPath (Join-Path $release 'COMMIT.txt') -Value $commit -Encoding ascii
Set-Content -LiteralPath (Join-Path $release 'BUILD-TIMESTAMP.txt') -Value (Get-Date -Format 'yyyy-MM-ddTHH:mm:ssK') -Encoding ascii

$hashFile = Join-Path $release 'SHA256SUMS.txt'
Get-ChildItem -LiteralPath $release -File -Recurse |
    Where-Object FullName -ne $hashFile |
    Sort-Object FullName |
    ForEach-Object {
        $relativePath = $_.FullName.Substring($release.Length).TrimStart('\').Replace('\', '/')
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        "$hash  $relativePath"
    } | Set-Content -LiteralPath $hashFile -Encoding ascii

Write-Host "Commit: $commit"
Write-Host "Uncompressed release: $release"
Write-Host "Manifest: $hashFile"
