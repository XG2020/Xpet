@echo off
setlocal
if "%~1"=="" if exist "%~dp0Xpet.exe" (
    start "" "%~dp0Xpet.exe"
    exit /b 0
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-DesktopPet.ps1" %*
set "petExitCode=%errorlevel%"
if not "%petExitCode%"=="0" pause
exit /b %petExitCode%
