param([switch]$SkipInstall)
# Keep UTF-8 with BOM for Windows PowerShell 5.1 compatibility.
$ErrorActionPreference = 'Stop'
$backendDirectory = $PSScriptRoot
$venvPython = Join-Path $backendDirectory '.venv\Scripts\python.exe'

function Test-PetPython([string]$Executable) {
    if (-not (Test-Path -LiteralPath $Executable)) { return $false }
    try {
        & $Executable -c 'import sys; assert sys.version_info >= (3, 11)' 2>$null | Out-Null
        return ($LASTEXITCODE -eq 0)
    } catch { return $false }
}

if (-not (Test-PetPython $venvPython)) {
    $pythonCandidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python313\python.exe'),
        (Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe')
    )
    $pythonCommand = Get-Command python -ErrorAction SilentlyContinue
    if ($pythonCommand) { $pythonCandidates = @($pythonCommand.Source) + $pythonCandidates }
    $basePython = $pythonCandidates | Where-Object { Test-PetPython $_ } | Select-Object -First 1
    if (-not $basePython) { throw '需要 Python 3.11 或更新版本。请安装 Python 后再次运行。' }
    & $basePython -m venv (Join-Path $backendDirectory '.venv')
    if ($LASTEXITCODE -ne 0) { throw '无法创建 Python 虚拟环境。' }
}

if (-not $SkipInstall) {
    & $venvPython -m pip install -r (Join-Path $backendDirectory 'requirements.txt')
    if ($LASTEXITCODE -ne 0) { throw '依赖安装失败，请检查网络后重试。' }
}
& $venvPython -c 'import fastapi, uvicorn, openai, edge_tts, mss, pydantic_settings'
if ($LASTEXITCODE -ne 0) { throw '依赖不完整。请不带 -SkipInstall 参数运行安装脚本。' }
Write-Host '后端环境已就绪。'
