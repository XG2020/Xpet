param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$xpetWorkspace = Split-Path $PSScriptRoot -Parent
if (-not $OutputPath) { $OutputPath = Join-Path $xpetWorkspace 'Xpet.exe' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$compilerCandidates = @(
    (Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw 'Install .NET Framework 4.x (C# compiler csc.exe) before building the launcher.' }
$source = Join-Path $PSScriptRoot 'XpetLauncher.cs'
$icon = Join-Path $xpetWorkspace 'branding\Xpet.ico'
if (-not (Test-Path -LiteralPath $icon)) { throw 'Missing branding/Xpet.ico. Run tools/create_xpet_icon.ps1 first.' }
New-Item -ItemType Directory -Path (Split-Path $OutputPath -Parent) -Force | Out-Null
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "/win32icon:$icon" "/out:$OutputPath" $source
if ($LASTEXITCODE -ne 0) { throw 'Xpet launcher compilation failed.' }
Write-Host ('Launcher built: ' + $OutputPath)
