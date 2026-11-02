param([switch]$BackendOnly)
# Keep this file as UTF-8 with BOM: the .cmd entry point uses Windows PowerShell 5.1.
$ErrorActionPreference = 'Stop'
$petRoot = $PSScriptRoot
$xpetLauncher = Join-Path $petRoot 'Xpet.exe'
if (-not $BackendOnly -and (Test-Path -LiteralPath $xpetLauncher)) {
    $compatibilityProcess = Start-Process -FilePath $xpetLauncher -WorkingDirectory $petRoot -WindowStyle Hidden -PassThru
    $compatibilityProcess.WaitForExit()
    exit $compatibilityProcess.ExitCode
}
$backendRoot = Join-Path $petRoot 'pet-backend'
$backendPython = Join-Path $backendRoot '.venv\Scripts\python.exe'
$backendExecutable = Join-Path $backendRoot 'XpetBackend.exe'
if (-not (Test-Path -LiteralPath $backendExecutable)) { $backendExecutable = Join-Path $backendRoot 'DesktopPetBackend.exe' }
$petExe = Join-Path $petRoot 'dist\Xpet\Xpet.Client.exe'
if (-not (Test-Path -LiteralPath $petExe)) { $petExe = Join-Path $petRoot 'dist\LitaDesktopPet\LitaDesktopPet.exe' }
$serviceUrl = 'http://127.0.0.1:18080/health'
$ownedBackend = $null

function Get-PetHealth {
    try { Invoke-RestMethod -Uri $serviceUrl -TimeoutSec 2 } catch { $null }
}

try {
    if (-not $BackendOnly -and -not (Test-Path -LiteralPath $petExe)) {
        throw '找不到 Windows 客户端，请先在 Unity 中构建 dist\LitaDesktopPet\LitaDesktopPet.exe。'
    }
    $health = Get-PetHealth
    if ($health -and ($health.app -ne 'desktop-pet-backend' -or $health.ok -ne $true)) { throw '18080 端口已有服务，但不是可用的桌宠后端。' }
    if (-not $health) {
        if (-not (Test-Path -LiteralPath $backendExecutable) -and -not (Test-Path -LiteralPath $backendPython)) {
            throw '后端文件缺失：请完整解压发行包；开发工程可运行 pet-backend\setup.ps1 安装依赖。'
        }
        $logRoot = Join-Path $backendRoot '.artifacts'
        New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
        $serverPath = $backendPython
        $serverArgs = @('-m','uvicorn','app:app','--host','127.0.0.1','--port','18080')
        if (Test-Path -LiteralPath $backendExecutable) { $serverPath = $backendExecutable; $serverArgs = @('--port','18080') }
        $ownedBackend = Start-Process -FilePath $serverPath -ArgumentList $serverArgs -WorkingDirectory $backendRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logRoot 'backend-out.log') -RedirectStandardError (Join-Path $logRoot 'backend-error.log')
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while (-not ($health = Get-PetHealth) -and [DateTime]::UtcNow -lt $deadline) {
            if ($ownedBackend.HasExited) { throw '后端启动失败，请查看 pet-backend\.artifacts\backend-error.log。' }
            Start-Sleep -Milliseconds 300
        }
        if (-not $health -or $health.app -ne 'desktop-pet-backend' -or $health.ok -ne $true) { throw '后端未能正常启动，请查看 pet-backend\.artifacts\backend-error.log。' }
    }
    if ($BackendOnly) {
        Write-Host '丽塔后端已就绪：http://127.0.0.1:18080'
        $ownedBackend = $null
    } else {
        $petProcess = Start-Process -FilePath $petExe -WorkingDirectory (Split-Path $petExe) -PassThru
        $petProcess.WaitForExit()
        if ($petProcess.ExitCode -ne 0) {
            throw "客户端异常退出（退出码 $($petProcess.ExitCode)）。请查看 %USERPROFILE%\AppData\LocalLow 下桌宠目录中的 Player.log。"
        }
    }
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
} finally {
    # Only stop the service started by this launcher; preserve any pre-existing service.
    if ($ownedBackend -and -not $ownedBackend.HasExited) {
        # A Windows venv python.exe can be a redirector with a child interpreter.
        # Terminate only our process tree so the real server cannot be orphaned.
        try {
            & (Join-Path $env:SystemRoot 'System32\taskkill.exe') /PID $ownedBackend.Id /T /F 2>$null | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Windows 拒绝结束进程。' }
        } catch {
            Write-Host "本次启动的后端进程 $($ownedBackend.Id) 未能自动关闭，请在任务管理器中结束该进程。" -ForegroundColor Yellow
            exit 1
        }
    }
}
exit 0

