param(
    [string]$PackageRoot,
    [string]$ClientSource,
    [string]$BackendSource,
    [string]$ReportPath,
    [ValidateRange(1, 100)][int]$ExpectedModelCount = 7,
    [string]$ExpectedModelUnityVersion
)
# Read-only package/source verification. The only write is the report outside the package.
# Unknown, stale, or user-added files are reported; this script never removes them.
$ErrorActionPreference = 'Stop'
$xpetWorkspace = Split-Path $PSScriptRoot -Parent
if (-not $PackageRoot) { $PackageRoot = Join-Path $xpetWorkspace 'dist\Xpet-0.6.0' }
if (-not $ClientSource) { $ClientSource = Join-Path $xpetWorkspace 'pet-frontend\pet-frontend\Builds\Windows' }
if (-not $BackendSource) { $BackendSource = Join-Path $xpetWorkspace '.artifacts\frozen-backend\XpetBackend' }
if (-not $ReportPath) { $ReportPath = Join-Path $xpetWorkspace 'verification\release-integrity-v060.json' }
$PackageRoot = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')
$ClientSource = [IO.Path]::GetFullPath($ClientSource).TrimEnd('\')
$BackendSource = [IO.Path]::GetFullPath($BackendSource).TrimEnd('\')
$ReportPath = [IO.Path]::GetFullPath($ReportPath)
if ($ReportPath.StartsWith($PackageRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The integrity report must be outside the package being verified.'
}
$failures = New-Object 'System.Collections.Generic.List[string]'
$hashCache = @{}
$forbiddenPaths = New-Object 'System.Collections.Generic.List[string]'
$debugDirectoryPaths = New-Object 'System.Collections.Generic.List[string]'
$manifestEntries = @{}
$expectedFiles = @{}
$sourceMatches = @{}
$modelDetails = New-Object 'System.Collections.Generic.List[object]'
$localDataFiles = New-Object 'System.Collections.Generic.List[object]'
# Existing portable-install settings and role resource exports belong to the user.
# They stay in place and must never be listed in the release manifest.
$localDataRelativePath = 'pet-backend\runtime_settings.json'
$localDataExcludedFromManifest = $true

function Add-XpetFailure([string]$Message) { $failures.Add($Message) }
function Test-XpetLocalData([string]$Relative) {
    return $Relative -ieq $localDataRelativePath -or
        ($Relative -match '^pet-(actions|skills)\\' -and $Relative -notin @('pet-actions\README.md', 'pet-skills\README.md'))
}
function Get-XpetFiles([string]$Root, [string]$Label, [switch]$ClientExclusions, [switch]$ModelExclusions, [switch]$CheckForbidden) {
    $files = @{}
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        Add-XpetFailure ($Label + ' directory is missing: ' + $Root)
        return ,$files
    }
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        try { $entries = @(Get-ChildItem -LiteralPath $directory -Force) }
        catch {
            Add-XpetFailure ('Cannot enumerate ' + $Label + ' directory ' + $directory + ': ' + $_.Exception.Message)
            continue
        }
        foreach ($entry in $entries) {
            $relative = $entry.FullName.Substring($Root.Length + 1)
            if ($ClientExclusions -and $directory -eq $Root -and
                ($entry.Name -match 'DoNotShip|BurstDebugInformation' -or $entry.Name -in @('LitaDesktopPet.exe', 'LitaDesktopPet_Data'))) { continue }
            if ($ModelExclusions -and $relative -match '(^|[\\/])\.imports([\\/]|$)') { continue }
            if ($CheckForbidden -and $relative -match 'XpetWindowShutdown|DoNotShip|BurstDebugInformation') {
                $forbiddenPaths.Add($relative)
                if ($entry.PSIsContainer -and $entry.Name -match 'DoNotShip|BurstDebugInformation') { $debugDirectoryPaths.Add($relative) }
                Add-XpetFailure ('Forbidden release artifact: ' + $relative)
            }
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                Add-XpetFailure ($Label + ' contains a reparse point; it was not followed: ' + $relative)
                continue
            }
            if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
            else { $files[$relative] = $entry }
        }
    }
    return ,$files
}
function Get-XpetHash([IO.FileInfo]$File) {
    if ($hashCache.ContainsKey($File.FullName)) { return $hashCache[$File.FullName] }
    try {
        $File.Refresh()
        $beforeBytes = $File.Length
        $beforeWrite = $File.LastWriteTimeUtc
        $hash = (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash
        $File.Refresh()
        if ($beforeBytes -ne $File.Length -or $beforeWrite -ne $File.LastWriteTimeUtc) {
            throw 'File changed while it was being hashed.'
        }
        $hashCache[$File.FullName] = $hash
        return $hash
    } catch {
        Add-XpetFailure ('Cannot verify file ' + $File.FullName + ': ' + $_.Exception.Message)
        $hashCache[$File.FullName] = $null
        return $null
    }
}
function Add-XpetExpectedFile([string]$Relative, [IO.FileInfo]$File) {
    if ($expectedFiles.ContainsKey($Relative)) {
        Add-XpetFailure ('More than one source maps to package path: ' + $Relative)
    } else { $expectedFiles[$Relative] = $File }
}
function Get-XpetRelativeManifestPath([object]$Value) {
    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value) -or
        [IO.Path]::IsPathRooted($Value) -or $Value -match '[:\x00-\x1f]') { return $null }
    $relative = $Value.Replace('/', '\')
    $parts = @($relative -split '\\')
    if (@($parts | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' }).Count -gt 0) { return $null }
    try {
        $absolute = [IO.Path]::GetFullPath((Join-Path $PackageRoot $relative))
        if (-not $absolute.StartsWith($PackageRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { return $null }
        return $absolute.Substring($PackageRoot.Length + 1)
    } catch { return $null }
}
function Test-XpetSourcePrefix([string]$Prefix) {
    $keys = @($expectedFiles.Keys | Where-Object { $_.StartsWith($Prefix, [StringComparison]::OrdinalIgnoreCase) })
    if ($keys.Count -eq 0) { return $false }
    foreach ($key in $keys) { if ($sourceMatches[$key] -ne $true) { return $false } }
    foreach ($key in $packageFiles.Keys) {
        if (Test-XpetLocalData $key) { continue }
        if ($key.StartsWith($Prefix, [StringComparison]::OrdinalIgnoreCase) -and -not $expectedFiles.ContainsKey($key)) { return $false }
    }
    return $true
}

$packageFiles = Get-XpetFiles $PackageRoot 'Package' -CheckForbidden
$clientFiles = Get-XpetFiles $ClientSource 'Client build' -ClientExclusions
$backendFiles = Get-XpetFiles $BackendSource 'Frozen backend'
foreach ($key in $clientFiles.Keys) { Add-XpetExpectedFile ('dist\Xpet\' + $key) $clientFiles[$key] }
foreach ($key in $backendFiles.Keys) {
    if (('pet-backend\' + $key) -ieq $localDataRelativePath) { continue }
    Add-XpetExpectedFile ('pet-backend\' + $key) $backendFiles[$key]
}
foreach ($name in @('Xpet.exe', 'Start-DesktopPet.cmd', 'Start-DesktopPet.ps1', 'LICENSE', 'THIRD_PARTY_NOTICES.md')) {
    $source = Join-Path $xpetWorkspace $name
    try {
        if (Test-Path -LiteralPath $source -PathType Leaf) { Add-XpetExpectedFile $name (Get-Item -LiteralPath $source) }
        else { Add-XpetFailure ('Required workspace source file is missing: ' + $name) }
    } catch { Add-XpetFailure ('Cannot read workspace source file ' + $name + ': ' + $_.Exception.Message) }
}
$portableReadme = Join-Path $xpetWorkspace 'docs\PORTABLE_README.md'
if (Test-Path -LiteralPath $portableReadme -PathType Leaf) {
    Add-XpetExpectedFile 'README.md' (Get-Item -LiteralPath $portableReadme)
} else { Add-XpetFailure 'Required portable README source is missing: docs/PORTABLE_README.md' }
foreach ($libraryName in @('pet-actions', 'pet-skills')) {
    $libraryGuide = Join-Path $xpetWorkspace ($libraryName + '\README.md')
    if (Test-Path -LiteralPath $libraryGuide -PathType Leaf) {
        Add-XpetExpectedFile ($libraryName + '\README.md') (Get-Item -LiteralPath $libraryGuide)
    }
    if (-not (Test-Path -LiteralPath (Join-Path $PackageRoot $libraryName) -PathType Container)) {
        Add-XpetFailure ('Root resource library is missing: ' + $libraryName)
    }
}
foreach ($name in @('Xpet.Client.exe', 'UnityPlayer.dll', 'Xpet.Client_Data\Managed\Assembly-CSharp.dll')) {
    if (-not $clientFiles.ContainsKey($name)) { Add-XpetFailure ('Required client build file is missing: ' + $name) }
}
if (-not $backendFiles.ContainsKey('XpetBackend.exe')) { Add-XpetFailure 'Required frozen backend executable is missing.' }

$modelFiles = Get-XpetFiles (Join-Path $xpetWorkspace 'pet-models') 'Model source' -ModelExclusions
foreach ($modelManifest in @($modelFiles.Values | Where-Object { $_.Name -eq 'model.json' } | Sort-Object FullName)) {
    try {
        $model = Get-Content -LiteralPath $modelManifest.FullName -Raw | ConvertFrom-Json
        if ($model.bundle_file -isnot [string] -or [string]::IsNullOrWhiteSpace($model.bundle_file) -or
            $model.bundle_file -match '[\\/:]' -or [IO.Path]::GetExtension($model.bundle_file) -notin @('.petmodel', '.petraw')) {
            throw 'bundle_file must name a .petmodel or .petraw file in the same directory.'
        }
        $bundle = Join-Path $modelManifest.DirectoryName $model.bundle_file
        if (-not (Test-Path -LiteralPath $bundle -PathType Leaf)) { throw 'Prepared model bundle is missing.' }
        $modelPrefix = 'pet-models\' + $modelManifest.Directory.Name + '\'
        Add-XpetExpectedFile ($modelPrefix + 'model.json') $modelManifest
        Add-XpetExpectedFile ($modelPrefix + $model.bundle_file) (Get-Item -LiteralPath $bundle)
        foreach ($guideName in @('LICENSE', 'ATTRIBUTION.md', 'ADAPTATION.md')) {
            $guidePath = Join-Path $modelManifest.DirectoryName $guideName
            if (Test-Path -LiteralPath $guidePath -PathType Leaf) {
                Add-XpetExpectedFile ($modelPrefix + $guideName) (Get-Item -LiteralPath $guidePath)
            }
        }
        if ([IO.Path]::GetExtension($bundle) -eq '.petraw') {
            $texturesPath = Join-Path $modelManifest.DirectoryName 'textures'
            if (Test-Path -LiteralPath $texturesPath -PathType Container) {
                $textures = Get-XpetFiles $texturesPath ('Model textures: ' + $modelManifest.Directory.Name)
                foreach ($key in $textures.Keys) { Add-XpetExpectedFile ($modelPrefix + 'textures\' + $key) $textures[$key] }
            }
        }
        if ($ExpectedModelUnityVersion -and [IO.Path]::GetExtension($bundle) -eq '.petmodel' -and $model.unity_version -cne $ExpectedModelUnityVersion) {
            Add-XpetFailure ('Model ' + $modelManifest.Directory.Name + ' uses Unity ' + $model.unity_version + ', expected ' + $ExpectedModelUnityVersion + '.')
        }
        $modelDetails.Add([pscustomobject]@{ id = $modelManifest.Directory.Name; unityVersion = $model.unity_version; manifestPath = $modelPrefix + 'model.json'; bundlePath = $modelPrefix + $model.bundle_file })
    } catch { Add-XpetFailure ('Invalid model source ' + $modelManifest.FullName + ': ' + $_.Exception.Message) }
}
if ($modelDetails.Count -ne $ExpectedModelCount) {
    Add-XpetFailure ('Prepared model count is ' + $modelDetails.Count + ', expected ' + $ExpectedModelCount + '.')
}

$manifestPath = Join-Path $PackageRoot 'manifest-sha256.json'
try {
    $manifest = @(Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)
    if ($manifest.Count -eq 0) { throw 'Manifest is empty.' }
    foreach ($entry in $manifest) {
        $relative = Get-XpetRelativeManifestPath $entry.path
        $entryBytes = 0L
        if (-not $relative -or $relative -eq 'manifest-sha256.json') {
            Add-XpetFailure ('Manifest contains an invalid or self-referencing path: ' + $entry.path)
            continue
        }
        if (Test-XpetLocalData $relative) {
            Add-XpetFailure ('Local settings and role resource exports must not be included in the release manifest: ' + $relative)
            $localDataExcludedFromManifest = $false
            continue
        }
        if ($manifestEntries.ContainsKey($relative)) {
            Add-XpetFailure ('Manifest contains a duplicate path: ' + $relative)
            continue
        }
        if ($entry.sha256 -isnot [string] -or $entry.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or
            -not [long]::TryParse([string]$entry.bytes, [ref]$entryBytes) -or $entryBytes -lt 0) {
            Add-XpetFailure ('Manifest contains an invalid SHA256 or byte count: ' + $relative)
            continue
        }
        $manifestEntries[$relative] = [pscustomobject]@{ sha256 = $entry.sha256; bytes = $entryBytes }
    }
} catch { Add-XpetFailure ('Cannot read package manifest: ' + $_.Exception.Message) }

$verifiedFiles = 0
$actualBytes = 0L
$manifestFileSetMatches = $localDataExcludedFromManifest
$sourceFileSetMatches = $true
foreach ($relative in $manifestEntries.Keys) {
    if (-not $packageFiles.ContainsKey($relative)) {
        Add-XpetFailure ('Manifest file is missing from package: ' + $relative)
        $manifestFileSetMatches = $false
        continue
    }
    $file = $packageFiles[$relative]
    $expected = $manifestEntries[$relative]
    $hash = Get-XpetHash $file
    if (-not $hash -or $file.Length -ne $expected.bytes -or $hash -ine $expected.sha256) {
        Add-XpetFailure ('Manifest hash or size mismatch: ' + $relative)
    } else { $verifiedFiles++ }
}
foreach ($relative in $packageFiles.Keys) {
    if ($relative -eq 'manifest-sha256.json') { continue }
    if (Test-XpetLocalData $relative) {
        $localHash = Get-XpetHash $packageFiles[$relative]
        try {
            $localDataFiles.Add([pscustomobject]@{ path = $relative; bytes = $packageFiles[$relative].Length; sha256 = $localHash })
        } catch { Add-XpetFailure ('Cannot read local data file metadata ' + $relative + ': ' + $_.Exception.Message) }
        continue
    }
    try { $actualBytes += $packageFiles[$relative].Length }
    catch { Add-XpetFailure ('Cannot read packaged file size ' + $relative + ': ' + $_.Exception.Message) }
    if (-not $manifestEntries.ContainsKey($relative)) {
        Add-XpetFailure ('Package file is not in manifest: ' + $relative)
        $manifestFileSetMatches = $false
    }
    if (-not $expectedFiles.ContainsKey($relative)) {
        Add-XpetFailure ('Package file has no current build/source counterpart: ' + $relative)
        $sourceFileSetMatches = $false
    }
}
foreach ($relative in $expectedFiles.Keys) {
    $sourceMatches[$relative] = $false
    if (-not $packageFiles.ContainsKey($relative)) {
        Add-XpetFailure ('Current build/source file is absent from package: ' + $relative)
        $sourceFileSetMatches = $false
        continue
    }
    $sourceFile = $expectedFiles[$relative]
    $packagedFile = $packageFiles[$relative]
    $sourceHash = Get-XpetHash $sourceFile
    $packagedHash = Get-XpetHash $packagedFile
    if (-not $sourceHash -or -not $packagedHash -or $sourceFile.Length -ne $packagedFile.Length -or $sourceHash -ine $packagedHash) {
        Add-XpetFailure ('Current build/source hash or size mismatch: ' + $relative)
    } else { $sourceMatches[$relative] = $true }
}

$unityVersion = $null
if ($packageFiles.ContainsKey('dist\Xpet\UnityPlayer.dll')) {
    try { $unityVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($packageFiles['dist\Xpet\UnityPlayer.dll'].FullName).ProductVersion }
    catch { Add-XpetFailure ('Cannot read UnityPlayer version: ' + $_.Exception.Message) }
}
$report = [ordered]@{
    verifiedFiles = $verifiedFiles
    bytes = $actualBytes
    failures = @($failures.ToArray())
    modelCount = $modelDetails.Count
    launcherMatches = ($sourceMatches['Xpet.exe'] -eq $true)
    backendMatches = ($sourceMatches['pet-backend\XpetBackend.exe'] -eq $true)
    clientAssemblyMatches = ($sourceMatches['dist\Xpet\Xpet.Client_Data\Managed\Assembly-CSharp.dll'] -eq $true)
    debugDirectories = $debugDirectoryPaths.Count
    checkedAtUtc = [DateTime]::UtcNow.ToString('o')
    packageRoot = $PackageRoot
    clientSource = $ClientSource
    backendSource = $BackendSource
    manifestEntries = $manifestEntries.Count
    actualFiles = @($packageFiles.Keys | Where-Object { $_ -ne 'manifest-sha256.json' -and $_ -ine $localDataRelativePath }).Count
    expectedSourceFiles = $expectedFiles.Count
    manifestFileSetMatches = $manifestFileSetMatches
    sourceFileSetMatches = $sourceFileSetMatches
    clientFilesMatch = (Test-XpetSourcePrefix 'dist\Xpet\')
    backendFilesMatch = (Test-XpetSourcePrefix 'pet-backend\')
    modelFilesMatch = (Test-XpetSourcePrefix 'pet-models\')
    unityPlayerMatches = ($sourceMatches['dist\Xpet\UnityPlayer.dll'] -eq $true)
    unityPlayerProductVersion = $unityVersion
    readmeMatches = ($sourceMatches['README.md'] -eq $true)
    startScriptsMatch = ($sourceMatches['Start-DesktopPet.cmd'] -eq $true -and $sourceMatches['Start-DesktopPet.ps1'] -eq $true)
    expectedModelCount = $ExpectedModelCount
    expectedModelUnityVersion = $ExpectedModelUnityVersion
    models = @($modelDetails.ToArray())
    forbiddenPaths = @($forbiddenPaths.ToArray())
    localDataFiles = @($localDataFiles.ToArray())
    localDataExcludedFromManifest = $localDataExcludedFromManifest
}
New-Item -ItemType Directory -Path (Split-Path $ReportPath -Parent) -Force | Out-Null
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding utf8
Write-Host ('Package integrity report: ' + $ReportPath)
Write-Host ('Verified manifest files: ' + $verifiedFiles + '; current source files: ' + $expectedFiles.Count + '; failures: ' + $failures.Count)
if ($failures.Count -gt 0) { throw ('Package integrity verification failed. See ' + $ReportPath) }

