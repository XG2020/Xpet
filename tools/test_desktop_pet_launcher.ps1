param()
$ErrorActionPreference = 'Stop'
$petRoot = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path $petRoot ('.artifacts\launcher-tests\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
$windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$report = New-Object System.Collections.Generic.List[string]

function Assert-PetTest([bool]$Passed, [string]$Description) {
    if (-not $Passed) { throw "FAIL: $Description" }
    $report.Add("PASS: $Description")
    Write-Host "PASS: $Description"
}

function Get-FreePetTestPort {
    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    return $port
}

function Test-PetTestPort([int]$Port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try { $client.Connect('127.0.0.1', $Port); return $true } catch { return $false } finally { $client.Dispose() }
}

function Write-PetFixture([string]$Name, [int]$Port, [string]$ClientPath, [string]$PythonPath) {
    $fixture = $launcher.Replace('18080', [string]$Port)
    # Only redirect endpoints and files; retain the launcher's control flow unchanged.
    $fixture = $fixture.Replace('$backendRoot = Join-Path $petRoot ''pet-backend''', ('$backendRoot = ''' + $backendRoot.Replace("'", "''") + "'"))
    $fixture = $fixture.Replace('$petExe = Join-Path $petRoot ''dist\LitaDesktopPet\LitaDesktopPet.exe''', ('$petExe = ''' + $ClientPath.Replace("'", "''") + "'"))
    $fixture = $fixture.Replace('$logRoot = Join-Path $backendRoot ''.artifacts''', ('$logRoot = ''' + (Join-Path $testRoot $Name).Replace("'", "''") + "'"))
    if ($PythonPath) {
        $fixture = $fixture.Replace('$backendPython = Join-Path $backendRoot ''.venv\Scripts\python.exe''', ('$backendPython = ''' + $PythonPath.Replace("'", "''") + "'"))
    }
    $fixturePath = Join-Path $testRoot ($Name + '.ps1')
    [System.IO.File]::WriteAllText($fixturePath, $fixture, $utf8Bom)
    return $fixturePath
}

try {
    foreach ($relative in @('Start-DesktopPet.ps1', 'pet-backend/start.ps1', 'pet-backend/setup.ps1')) {
        $script = Join-Path $petRoot $relative
        $bytes = [System.IO.File]::ReadAllBytes($script)
        Assert-PetTest ($bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191) "$relative retains a UTF-8 BOM"
        $parseFile = Join-Path $testRoot 'parse.ps1'
        $parseCode = '$tokens = $null; $errors = $null; [System.Management.Automation.Language.Parser]::ParseFile(''' + $script.Replace("'", "''") + ''', [ref]$tokens, [ref]$errors) | Out-Null; if ($errors.Count) { $errors; exit 1 }; exit 0'
        [System.IO.File]::WriteAllText($parseFile, $parseCode, $utf8Bom)
        & $windowsPowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $parseFile
        Assert-PetTest ($LASTEXITCODE -eq 0) "$relative parses under Windows PowerShell 5.1"
    }

    $launcher = [System.IO.File]::ReadAllText((Join-Path $petRoot 'Start-DesktopPet.ps1'))
    $backendRoot = Join-Path $petRoot 'pet-backend'
    $fakeClient = Join-Path $testRoot 'TestPet.exe'
    $sourcePath = Join-Path $testRoot 'TestPet.cs'
    [System.IO.File]::WriteAllText($sourcePath, 'using System; using System.IO; using System.Threading; class TestPet { static int Main() { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client-started.txt"), "ok"); Thread.Sleep(400); return 0; } }')
    $compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compiler /nologo /target:winexe "/out:$fakeClient" $sourcePath
    Assert-PetTest ($LASTEXITCODE -eq 0) 'Compiled the windowless test client'

    $missingPort = Get-FreePetTestPort
    $missingClient = Write-PetFixture 'missing-client' $missingPort (Join-Path $testRoot 'missing.exe') ''
    $missingOutput = & $windowsPowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $missingClient 2>&1
    Assert-PetTest ($LASTEXITCODE -eq 1) 'Missing client exits 1 without an interactive prompt'
    Assert-PetTest (-not (Test-PetTestPort $missingPort)) 'Missing client does not start a backend'
    $missingOutput | Out-File (Join-Path $testRoot 'missing-client.txt') -Encoding utf8

    $missingPython = Write-PetFixture 'missing-python' (Get-FreePetTestPort) $fakeClient (Join-Path $testRoot 'missing-python.exe')
    & $windowsPowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $missingPython -BackendOnly | Out-File (Join-Path $testRoot 'missing-python.txt') -Encoding utf8
    Assert-PetTest ($LASTEXITCODE -eq 1) 'Missing Python exits 1 with setup instructions'

    $ownedPort = Get-FreePetTestPort
    $ownedFixture = Write-PetFixture 'owned-backend' $ownedPort $fakeClient ''
    & $windowsPowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $ownedFixture | Out-File (Join-Path $testRoot 'owned-backend.txt') -Encoding utf8
    Assert-PetTest ($LASTEXITCODE -eq 0) 'Cold start reaches the real FastAPI backend and exits successfully'
    Assert-PetTest (Test-Path -LiteralPath (Join-Path $testRoot 'client-started.txt')) 'Client starts after backend health succeeds'
    Assert-PetTest (-not (Test-PetTestPort $ownedPort)) 'Closing client terminates the owned venv interpreter tree'

    # Reuse is checked against a separate real backend which belongs to this test,
    # never stopping a service already running on the user-facing port 18080.
    $existingPort = Get-FreePetTestPort
    $existingBackend = Start-Process -FilePath (Join-Path $backendRoot '.venv\Scripts\python.exe') -ArgumentList @('-m', 'uvicorn', 'app:app', '--host', '127.0.0.1', '--port', $existingPort) -WorkingDirectory $backendRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testRoot 'existing-out.log') -RedirectStandardError (Join-Path $testRoot 'existing-error.log')
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $ready = $null
        try { $ready = Invoke-RestMethod -Uri "http://127.0.0.1:$existingPort/health" -TimeoutSec 2 } catch {}
        if (-not $ready) { Start-Sleep -Milliseconds 200 }
    } while (-not $ready -and [DateTime]::UtcNow -lt $deadline)
    Assert-PetTest ($ready.app -eq 'desktop-pet-backend') 'Independent backend is ready'
    $reuseFixture = Write-PetFixture 'reuse-backend' $existingPort $fakeClient ''
    & $windowsPowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $reuseFixture -BackendOnly | Out-File (Join-Path $testRoot 'reuse-backend-only.txt') -Encoding utf8
    Assert-PetTest ($LASTEXITCODE -eq 0) 'BackendOnly reuses an existing healthy backend'
    & $windowsPowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $reuseFixture | Out-File (Join-Path $testRoot 'reuse-backend.txt') -Encoding utf8
    Assert-PetTest ($LASTEXITCODE -eq 0 -and (Test-PetTestPort $existingPort)) 'Client exit preserves a pre-existing backend'

    $report.Add('No chat, speech, screenshot, or external AI API request was made.')
} finally {
    if ($existingBackend -and -not $existingBackend.HasExited) {
        & (Join-Path $env:SystemRoot 'System32\taskkill.exe') /PID $existingBackend.Id /T /F 2>$null | Out-Null
    }
    $report | Out-File (Join-Path $testRoot 'results.txt') -Encoding utf8
    Write-Host "Launcher test artifacts: $testRoot"
}
