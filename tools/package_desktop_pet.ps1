param([string]$Version = '0.6.0', [string]$Destination, [switch]$UpdateExisting, [string]$BackendSource)
$ErrorActionPreference = 'Stop'
$petWorkspace = Split-Path $PSScriptRoot -Parent
$clientSource = Join-Path $petWorkspace 'pet-frontend\pet-frontend\Builds\Windows'
$backendSource = if ($BackendSource) { [IO.Path]::GetFullPath($BackendSource) } else { Join-Path $petWorkspace '.artifacts\frozen-backend\XpetBackend' }
if (-not $Destination) { $Destination = Join-Path $petWorkspace ('dist\Xpet-' + $Version) }
$Destination = [IO.Path]::GetFullPath($Destination)
if (-not (Test-Path -LiteralPath (Join-Path $clientSource 'Xpet.Client.exe'))) { throw 'Xpet Windows build missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $backendSource 'XpetBackend.exe'))) { throw 'Frozen Xpet backend missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $petWorkspace 'Xpet.exe'))) { throw 'Windowless Xpet launcher missing.' }
if (Test-Path -LiteralPath $Destination) {
    if (-not $UpdateExisting -or -not (Test-Path -LiteralPath (Join-Path $Destination 'manifest-sha256.json'))) {
        throw 'Destination exists. Use an explicit UpdateExisting only for a previously generated package.'
    }
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$clientTarget = Join-Path $Destination 'dist\Xpet'
New-Item -ItemType Directory -Path $clientTarget -Force | Out-Null
foreach ($petFile in Get-ChildItem -LiteralPath $clientSource -Force) {
    if ($petFile.Name -match 'DoNotShip|BurstDebugInformation') { continue }
    if ($petFile.Name -in @('LitaDesktopPet.exe','LitaDesktopPet_Data')) { continue }
    Copy-Item -LiteralPath $petFile.FullName -Destination $clientTarget -Recurse -Force
}
$backendTarget = Join-Path $Destination 'pet-backend'
New-Item -ItemType Directory -Path $backendTarget -Force | Out-Null
foreach ($backendFile in Get-ChildItem -LiteralPath $backendSource -Force) {
    Copy-Item -LiteralPath $backendFile.FullName -Destination $backendTarget -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $petWorkspace 'Xpet.exe') -Destination $Destination
Copy-Item -LiteralPath (Join-Path $petWorkspace 'Start-DesktopPet.cmd'), (Join-Path $petWorkspace 'Start-DesktopPet.ps1') -Destination $Destination
$modelsTarget = Join-Path $Destination 'pet-models'
New-Item -ItemType Directory -Path $modelsTarget -Force | Out-Null
# Ship prepared models only. Import caches and original source files stay in development.
foreach ($petManifest in Get-ChildItem -LiteralPath (Join-Path $petWorkspace 'pet-models') -Filter model.json -Recurse) {
    if ($petManifest.FullName -match '[\\/]\.imports[\\/]') { continue }
    $modelData = Get-Content -LiteralPath $petManifest.FullName -Raw | ConvertFrom-Json
    $modelBundle = [IO.Path]::GetFullPath((Join-Path $petManifest.DirectoryName $modelData.bundle_file))
    if (-not [IO.Path]::GetDirectoryName($modelBundle).Equals($petManifest.DirectoryName,[StringComparison]::OrdinalIgnoreCase)) { continue }
    if (-not (Test-Path -LiteralPath $modelBundle) -or [IO.Path]::GetExtension($modelBundle) -notin @('.petmodel','.petraw')) { continue }
    $modelTarget = Join-Path $modelsTarget $petManifest.Directory.Name
    New-Item -ItemType Directory -Path $modelTarget -Force | Out-Null
    Copy-Item -LiteralPath $petManifest.FullName, $modelBundle -Destination $modelTarget
    foreach ($modelGuide in @('LICENSE', 'ATTRIBUTION.md', 'ADAPTATION.md')) {
        $modelGuidePath = Join-Path $petManifest.DirectoryName $modelGuide
        if (Test-Path -LiteralPath $modelGuidePath) { Copy-Item -LiteralPath $modelGuidePath -Destination $modelTarget -Force }
    }
    if ([IO.Path]::GetExtension($modelBundle) -eq '.petraw') {
        $modelTextures = Join-Path $petManifest.DirectoryName 'textures'
        if (Test-Path -LiteralPath $modelTextures) { Copy-Item -LiteralPath $modelTextures -Destination $modelTarget -Recurse }
    }
}
Copy-Item -LiteralPath (Join-Path $petWorkspace 'docs\PORTABLE_README.md') -Destination (Join-Path $Destination 'README.md')
Copy-Item -LiteralPath (Join-Path $petWorkspace 'LICENSE'), (Join-Path $petWorkspace 'THIRD_PARTY_NOTICES.md') -Destination $Destination
# Always include portable library folders. Only the repository's standard guide
# is shipped here; per-character definitions, skills and imported motions are local data.
foreach ($libraryName in @('pet-actions', 'pet-skills')) {
    $libraryTarget = Join-Path $Destination $libraryName
    New-Item -ItemType Directory -Path $libraryTarget -Force | Out-Null
    $libraryGuide = Join-Path $petWorkspace ($libraryName + '\README.md')
    if (Test-Path -LiteralPath $libraryGuide) { Copy-Item -LiteralPath $libraryGuide -Destination $libraryTarget -Force }
}
$hashes = foreach ($petBinary in Get-ChildItem -LiteralPath $Destination -File -Recurse) {
    if ($petBinary.FullName -eq (Join-Path $Destination 'manifest-sha256.json')) { continue }
    $relativePath = $petBinary.FullName.Substring($Destination.Length + 1)
    # Updating an in-use package preserves local settings; they are not a shipped asset.
    if ($relativePath -eq 'pet-backend\runtime_settings.json') { continue }
    if ($relativePath -match '^pet-(actions|skills)\\' -and $relativePath -notin @('pet-actions\README.md', 'pet-skills\README.md')) { continue }
    [pscustomobject]@{path=$relativePath;sha256=(Get-FileHash -LiteralPath $petBinary.FullName -Algorithm SHA256).Hash;bytes=$petBinary.Length}
}
$hashes | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $Destination 'manifest-sha256.json') -Encoding utf8
Write-Host ('Portable distribution: ' + $Destination)
