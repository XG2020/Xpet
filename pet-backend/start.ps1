param([int]$Port = 18080)
# Keep UTF-8 with BOM for Windows PowerShell 5.1 compatibility.
$ErrorActionPreference = 'Stop'
$backendPython = Join-Path $PSScriptRoot '.venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $backendPython)) {
    throw '后端环境未安装，请先运行 pet-backend\setup.ps1。'
}
Set-Location -LiteralPath $PSScriptRoot
& $backendPython -m uvicorn app:app --host 127.0.0.1 --port $Port
exit $LASTEXITCODE
