param()
$ErrorActionPreference = 'Stop'
$xpetWorkspace = Split-Path $PSScriptRoot -Parent
$testDirectory = Join-Path $xpetWorkspace ('.artifacts\xpet-launcher-tests\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$report = New-Object System.Collections.Generic.List[string]
$processes = New-Object System.Collections.Generic.List[System.Diagnostics.Process]

function Assert-Xpet([bool]$Passed, [string]$Description) {
    if (-not $Passed) { throw ('FAILED: ' + $Description) }
    $report.Add('PASSED: ' + $Description)
    Write-Host ('PASSED: ' + $Description)
}
function Free-Port {
    $socket = New-Object System.Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0)
    $socket.Start(); $result = $socket.LocalEndpoint.Port; $socket.Stop(); return $result
}
function Port-Open([int]$Port) {
    $socket = New-Object System.Net.Sockets.TcpClient
    try { $socket.Connect('127.0.0.1',$Port); return $true } catch { return $false } finally { $socket.Dispose() }
}
function Wait-File([string]$Path,[int]$Seconds=15) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while (-not (Test-Path -LiteralPath $Path) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    return (Test-Path -LiteralPath $Path)
}
function Wait-PortClosed([int]$Port,[int]$Seconds=8) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ((Port-Open $Port) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    return (-not (Port-Open $Port))
}
function Start-Hidden([string]$Path,[string]$Arguments) {
    $info = New-Object System.Diagnostics.ProcessStartInfo($Path,$Arguments)
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $process = [Diagnostics.Process]::Start($info); $processes.Add($process); return $process
}
function Context([string]$Name) {
    $root = Join-Path $testDirectory $Name
    New-Item -ItemType Directory -Path (Join-Path $root 'pet-backend'),(Join-Path $root 'dist\Xpet'),(Join-Path $root 'data') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $xpetWorkspace 'Xpet.exe') -Destination $root
    Copy-Item -LiteralPath $clientTemplate -Destination (Join-Path $root 'dist\Xpet\Xpet.Client.exe')
    Copy-Item -LiteralPath $backendTemplate -Destination (Join-Path $root 'pet-backend\XpetBackend.exe')
    [pscustomobject]@{Root=$root; Port=(Free-Port); Data=(Join-Path $root 'data'); Client=(Join-Path $root 'dist\Xpet'); Backend=(Join-Path $root 'pet-backend')}
}
function Launch($Context, [bool]$Isolated = $true) {
    $arguments = '--silent-errors --port ' + $Context.Port
    if ($Isolated) { $arguments += ' --data-dir "' + $Context.Data + '"' }
    Start-Hidden (Join-Path $Context.Root 'Xpet.exe') $arguments
}

$fakeClient = @'
using System; using System.Diagnostics; using System.IO; using System.Threading;
class Client { static int Main() { string root=AppDomain.CurrentDomain.BaseDirectory; File.WriteAllText(Path.Combine(root,"client.pid"),Process.GetCurrentProcess().Id.ToString()); DateTime until=DateTime.UtcNow.AddSeconds(45); while(!File.Exists(Path.Combine(root,"stop.flag")) && DateTime.UtcNow<until) Thread.Sleep(50); return 0; } }
'@
$fakeBackend = @'
using System; using System.Diagnostics; using System.IO; using System.Net; using System.Text;
class Backend { static int Main(string[] args) { int port=0; string token="",data=""; for(int i=0;i<args.Length;i++) { if(args[i]=="--port")port=int.Parse(args[++i]); else if(args[i]=="--shutdown-token")token=args[++i]; else if(args[i]=="--data-dir")data=args[++i]; }
string root=AppDomain.CurrentDomain.BaseDirectory; File.WriteAllText(Path.Combine(root,"backend.pid"),Process.GetCurrentProcess().Id.ToString());
using(var listener=new HttpListener()) { listener.Prefixes.Add("http://127.0.0.1:"+port+"/"); listener.Start(); while(true) { var context=listener.GetContext(); string path=context.Request.Url.AbsolutePath,json="{}"; bool stop=false;
if(path=="/health")json="{\"app\":\""+(data==""?"desktop-pet-backend":"desktop-pet-backend-qa")+"\",\"ok\":true,\"version\":\""+(File.Exists(Path.Combine(data,"old-version.flag"))?"0.5.0":"0.6.0")+"\"}";
else if(context.Request.Headers["X-Xpet-Shutdown-Token"]!=token || token=="") context.Response.StatusCode=403;
else if(path=="/internal/launcher/status")json="{\"ok\":true,\"pid\":"+Process.GetCurrentProcess().Id+"}";
else if(path=="/internal/launcher/shutdown") { json="{\"ok\":true}"; stop=!File.Exists(Path.Combine(data,"ignore-shutdown.flag")); }
else context.Response.StatusCode=404;
byte[] body=Encoding.UTF8.GetBytes(json); context.Response.ContentType="application/json";context.Response.ContentLength64=body.Length;context.Response.KeepAlive=false;context.Response.OutputStream.Write(body,0,body.Length);context.Response.Close();if(stop) { File.WriteAllText(Path.Combine(root,"graceful.txt"),"ok");System.Threading.Thread.Sleep(150);break; }
} } return 0; } }
'@
$clientSource = Join-Path $testDirectory 'Client.cs'; $backendTestSource = Join-Path $testDirectory 'Backend.cs'
[IO.File]::WriteAllText($clientSource,$fakeClient); [IO.File]::WriteAllText($backendTestSource,$fakeBackend)
$clientTemplate = Join-Path $testDirectory 'Client.exe'; $backendTemplate = Join-Path $testDirectory 'Backend.exe'
& $compiler /nologo /target:winexe /platform:x64 "/out:$clientTemplate" $clientSource
Assert-Xpet ($LASTEXITCODE -eq 0) 'Compiled windowless client lifecycle fixture'
& $compiler /nologo /target:winexe /platform:x64 "/out:$backendTemplate" $backendTestSource
Assert-Xpet ($LASTEXITCODE -eq 0) 'Compiled windowless HTTP backend fixture'

try {
    $cold = Context 'owned-cold-start'
    $launcher = Launch $cold
    Assert-Xpet (Wait-File (Join-Path $cold.Client 'client.pid')) 'Cold QA launch waits for healthy owned backend before starting client'
    $coldHealth = Invoke-RestMethod -Uri ('http://127.0.0.1:'+$cold.Port+'/health') -TimeoutSec 2
    Assert-Xpet ($coldHealth.app -eq 'desktop-pet-backend-qa') 'Explicit data-dir mode accepts the QA backend identity'
    $backendId = [int](Get-Content -LiteralPath (Join-Path $cold.Backend 'backend.pid'))
    Assert-Xpet ($launcher.MainWindowHandle -eq 0 -and (Get-Process -Id $backendId).MainWindowHandle -eq 0) 'Launcher and backend have no visible window handles'
    [IO.File]::WriteAllText((Join-Path $cold.Client 'stop.flag'),'ok')
    Assert-Xpet ($launcher.WaitForExit(15000) -and $launcher.ExitCode -eq 0) 'Client close exits launcher successfully'
    Assert-Xpet ((Wait-PortClosed $cold.Port) -and (Test-Path -LiteralPath (Join-Path $cold.Backend 'graceful.txt'))) 'Owned backend stops through authenticated graceful shutdown'

    $reuse = Context 'existing-backend'
    $nonce = ([Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N'))
    $existing = Start-Hidden (Join-Path $reuse.Backend 'XpetBackend.exe') ('--port '+$reuse.Port+' --shutdown-token '+$nonce+' --data-dir "'+$reuse.Data+'"')
    Assert-Xpet (Wait-File (Join-Path $reuse.Backend 'backend.pid')) 'Independent test-owned backend started'
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    while (-not (Port-Open $reuse.Port) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    $launcher = Launch $reuse
    Assert-Xpet (Wait-File (Join-Path $reuse.Client 'client.pid')) 'Existing backend is reused'
    [IO.File]::WriteAllText((Join-Path $reuse.Client 'stop.flag'),'ok')
    Assert-Xpet ($launcher.WaitForExit(15000) -and $launcher.ExitCode -eq 0 -and (Port-Open $reuse.Port) -and -not $existing.HasExited) 'Client close preserves pre-existing backend'
    Invoke-RestMethod -Uri ('http://127.0.0.1:'+$reuse.Port+'/internal/launcher/shutdown') -Method Post -Headers @{'X-Xpet-Shutdown-Token'=$nonce} -TimeoutSec 2 | Out-Null
    Assert-Xpet ($existing.WaitForExit(5000)) 'Fixture-owned independent backend cleaned up gracefully'

    $old = Context 'incompatible-existing-backend'
    [IO.File]::WriteAllText((Join-Path $old.Data 'old-version.flag'),'ok')
    $oldNonce = ([Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N'))
    $oldBackend = Start-Hidden (Join-Path $old.Backend 'XpetBackend.exe') ('--port '+$old.Port+' --shutdown-token '+$oldNonce+' --data-dir "'+$old.Data+'"')
    Assert-Xpet (Wait-File (Join-Path $old.Backend 'backend.pid')) 'Old-version fixture backend started'
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    while (-not (Port-Open $old.Port) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    $launcher = Launch $old
    Assert-Xpet ($launcher.WaitForExit(8000) -and $launcher.ExitCode -eq 1 -and -not (Test-Path -LiteralPath (Join-Path $old.Client 'client.pid'))) 'Incompatible pre-existing backend is refused before client starts'
    Assert-Xpet ((Port-Open $old.Port) -and -not $oldBackend.HasExited) 'Incompatible pre-existing backend remains untouched'
    Invoke-RestMethod -Uri ('http://127.0.0.1:'+$old.Port+'/internal/launcher/shutdown') -Method Post -Headers @{'X-Xpet-Shutdown-Token'=$oldNonce} -TimeoutSec 2 | Out-Null
    Assert-Xpet ($oldBackend.WaitForExit(5000)) 'Old-version fixture cleaned up by its owner'

    foreach ($identityCase in @(
        @{Name='normal-refuses-qa'; Isolated=$false; BackendIsolated=$true; Description='Normal launcher refuses the QA backend identity'},
        @{Name='qa-refuses-normal'; Isolated=$true; BackendIsolated=$false; Description='QA launcher refuses the normal backend identity'}
    )) {
        $mismatch = Context $identityCase.Name
        $mismatchNonce = ([Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N'))
        $backendArguments = '--port '+$mismatch.Port+' --shutdown-token '+$mismatchNonce
        if ($identityCase.BackendIsolated) { $backendArguments += ' --data-dir "'+$mismatch.Data+'"' }
        $mismatchBackend = Start-Hidden (Join-Path $mismatch.Backend 'XpetBackend.exe') $backendArguments
        $deadline = [DateTime]::UtcNow.AddSeconds(8)
        while (-not (Port-Open $mismatch.Port) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        Assert-Xpet (Port-Open $mismatch.Port) ($identityCase.Name + ' fixture started')
        $launcher = Launch $mismatch $identityCase.Isolated
        Assert-Xpet ($launcher.WaitForExit(8000) -and $launcher.ExitCode -eq 1 -and -not (Test-Path -LiteralPath (Join-Path $mismatch.Client 'client.pid'))) $identityCase.Description
        Assert-Xpet ((Port-Open $mismatch.Port) -and -not $mismatchBackend.HasExited) ($identityCase.Name + ' preserves the mismatched backend')
        Invoke-RestMethod -Uri ('http://127.0.0.1:'+$mismatch.Port+'/internal/launcher/shutdown') -Method Post -Headers @{'X-Xpet-Shutdown-Token'=$mismatchNonce} -TimeoutSec 2 | Out-Null
        Assert-Xpet ($mismatchBackend.WaitForExit(5000)) ($identityCase.Name + ' fixture cleaned up by its owner')
    }

    $abnormal = Context 'abnormal-launcher-exit'
    $launcher = Launch $abnormal
    Assert-Xpet (Wait-File (Join-Path $abnormal.Client 'client.pid')) 'Abnormal-exit fixture reached running state'
    $clientId = [int](Get-Content -LiteralPath (Join-Path $abnormal.Client 'client.pid'))
    $backendId = [int](Get-Content -LiteralPath (Join-Path $abnormal.Backend 'backend.pid'))
    $launcher.Kill(); $launcher.WaitForExit()
    Assert-Xpet (Wait-PortClosed $abnormal.Port) 'Windows job closes backend after launcher termination'
    Assert-Xpet (-not (Get-Process -Id $clientId -ErrorAction SilentlyContinue) -and -not (Get-Process -Id $backendId -ErrorAction SilentlyContinue)) 'Abnormal exit leaves neither owned client nor owned backend process'

    $timeout = Context 'shutdown-timeout'
    [IO.File]::WriteAllText((Join-Path $timeout.Data 'ignore-shutdown.flag'),'ok')
    $launcher = Launch $timeout
    Assert-Xpet (Wait-File (Join-Path $timeout.Client 'client.pid')) 'Unresponsive-shutdown fixture reached running state'
    $timer = [Diagnostics.Stopwatch]::StartNew(); [IO.File]::WriteAllText((Join-Path $timeout.Client 'stop.flag'),'ok')
    Assert-Xpet ($launcher.WaitForExit(16000) -and $launcher.ExitCode -eq 0) 'Shutdown timeout exits without a prompt'
    $timer.Stop()
    Assert-Xpet ($timer.Elapsed.TotalSeconds -ge 9 -and (Wait-PortClosed $timeout.Port)) 'Backend receives grace period then only owned job is terminated'
    $report.Add('All fixtures used random local ports and isolated data; no existing user backend, preferences, chat, AI, screenshot or speech request was accessed.')
} finally {
    foreach ($process in $processes) { try { if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(3000) | Out-Null } } catch {} }
    $report | Set-Content -LiteralPath (Join-Path $testDirectory 'results.txt') -Encoding utf8
    Write-Host ('Xpet launcher lifecycle report: '+$testDirectory)
}



