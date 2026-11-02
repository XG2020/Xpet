param(
    [string]$PackageRoot,
    [string]$ReportRoot,
    [string]$EntryPoint,
    [switch]$AwaitInteractiveClose,
    [ValidateRange(0,90)][int]$AutomaticExitSeconds = 0,
    [ValidateRange(15, 60)][int]$StartupTimeoutSeconds = 55,
    [string]$ExpectedUnityVersion
)
# Real Windows release smoke test. Run from 64-bit Windows PowerShell 5.1 or PowerShell 7.
# Uses the launcher's existing --port/--data-dir/--silent-errors arguments.
# Backend data is isolated. Unity still uses this user's Xpet preferences and LocalLow;
# preferences are backed up/restored, while existing user files are never deleted or replaced.
$ErrorActionPreference = 'Stop'
if ($ExpectedUnityVersion -and $AutomaticExitSeconds -eq 0) {
    throw 'ExpectedUnityVersion requires AutomaticExitSeconds so the real player emits runtime diagnostics.'
}
$xpetWorkspace = Split-Path $PSScriptRoot -Parent
if (-not $PackageRoot) { $PackageRoot = Join-Path $xpetWorkspace 'dist\Xpet-0.6.0' }
$PackageRoot = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')
if (-not $ReportRoot) { $ReportRoot = Join-Path $xpetWorkspace '.artifacts\xpet-release-tests' }
$testDirectory = Join-Path ([IO.Path]::GetFullPath($ReportRoot)) ([Guid]::NewGuid().ToString('N'))
$clientPath = Join-Path $PackageRoot 'dist\Xpet\Xpet.Client.exe'
$backendPath = Join-Path $PackageRoot 'pet-backend\XpetBackend.exe'
$launcherPath = if ($EntryPoint) { [IO.Path]::GetFullPath($EntryPoint) } else { Join-Path $PackageRoot 'Xpet.exe' }
$registryPath = 'Software\DesktopPet\Xpet'
$localLow = Join-Path (Split-Path $env:LOCALAPPDATA -Parent) 'LocalLow\DesktopPet\Xpet'
$report = New-Object 'System.Collections.Generic.List[string]'
$tracked = New-Object 'System.Collections.Generic.List[System.Diagnostics.Process]'
$launcher = $null
$client = $null
$backend = $null
$preferencesBefore = $null
$preferencesCaptured = $false
$failure = $null

function Assert-XpetRelease([bool]$Passed, [string]$Description) {
    if (-not $Passed) { throw ('FAILED: ' + $Description) }
    $report.Add('PASSED: ' + $Description)
    Write-Host ('PASSED: ' + $Description)
}
function Get-XpetPreferences {
    $values = @{}
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registryPath)
    if ($key) {
        try {
            foreach ($name in $key.GetValueNames()) {
                $values[$name] = [pscustomobject]@{
                    Kind = [int]$key.GetValueKind($name)
                    Value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                }
            }
        } finally { $key.Dispose() }
    }
    return ,$values
}
function Same-XpetPreference($Left, $Right) {
    if ($null -eq $Left -or $null -eq $Right) { return $null -eq $Left -and $null -eq $Right }
    return $Left.Kind -eq $Right.Kind -and
        [Management.Automation.PSSerializer]::Serialize($Left.Value) -ceq [Management.Automation.PSSerializer]::Serialize($Right.Value)
}
function Get-OtherXpetClients {
    # Refuse concurrent desktop clients: both products share global hotkeys, and Xpet shares prefs.
    return @(Get-CimInstance Win32_Process -Filter "Name = 'Xpet.Client.exe' OR Name = 'LitaDesktopPet.exe'")
}
function Restore-XpetPreferences {
    if (-not $preferencesCaptured) { return }
    if (@(Get-OtherXpetClients).Count -ne 0) {
        throw 'A desktop client is still running; preferences were not restored. Backup retained in the report directory.'
    }
    $after = Get-XpetPreferences
    $after | Export-Clixml -LiteralPath (Join-Path $testDirectory 'client-preferences-after.clixml')
    $names = @(@($preferencesBefore.Keys) + @($after.Keys) | Select-Object -Unique)
    $changed = @($names | Where-Object { -not (Same-XpetPreference $preferencesBefore[$_] $after[$_]) })
    if ($changed.Count -eq 0) { $report.Add('PASSED: Client preferences were unchanged.'); return }
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($registryPath)
    try {
        foreach ($name in $changed) {
            $current = $null
            if ($key.GetValueNames() -contains $name) {
                $current = [pscustomobject]@{ Kind = [int]$key.GetValueKind($name); Value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
            }
            # Never overwrite a value that changed after our post-exit snapshot.
            if (-not (Same-XpetPreference $current $after[$name])) { throw 'Client preferences changed concurrently; restoration stopped and backup retained.' }
            if ($preferencesBefore.ContainsKey($name)) {
                $prior = $preferencesBefore[$name]
                $key.SetValue($name, $prior.Value, [Microsoft.Win32.RegistryValueKind]$prior.Kind)
            } else { $key.DeleteValue($name, $false) }
        }
    } finally { $key.Dispose() }
    $report.Add('PASSED: Restored ' + $changed.Count + ' client preference changes; no registry key or user file was removed.')
}
function Get-XpetMotionManifest {
    $directory = Join-Path $localLow 'pet-motions'
    if (-not (Test-Path -LiteralPath $directory)) { return @() }
    return @(Get-ChildItem -LiteralPath $directory -File -Recurse | ForEach-Object {
        [pscustomobject]@{ Path = $_.FullName.Substring($directory.Length); Bytes = $_.Length; Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    } | Sort-Object Path)
}
function Free-XpetPort {
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}
function Test-XpetPort([int]$Port) {
    $socket = New-Object Net.Sockets.TcpClient
    try { $socket.Connect('127.0.0.1', $Port); return $true } catch { return $false } finally { $socket.Dispose() }
}
function Start-XpetLauncher([int]$Port, [string]$Data, [string]$Executable = $launcherPath) {
    # Reject quotes rather than constructing an ambiguous Windows command line.
    if ($Data.Contains('"')) { throw 'A quote is not permitted in the test data path.' }
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $Executable
    $info.Arguments = '--silent-errors --port ' + $Port + ' --data-dir "' + $Data + '"'
    if ($AutomaticExitSeconds -gt 0) { $info.Arguments += ' --verify-exit-after ' + $AutomaticExitSeconds }
    $info.WorkingDirectory = $PackageRoot
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $process = [Diagnostics.Process]::Start($info)
    $null = $process.Handle
    $tracked.Add($process)
    return $process
}
function Get-VerifiedChild([int]$ChildId, [string]$ExpectedPath) {
    $process = [Diagnostics.Process]::GetProcessById($ChildId)
    $null = $process.Handle
    $identity = Get-CimInstance Win32_Process -Filter ('ProcessId = ' + $ChildId)
    if (-not $identity -or $identity.ParentProcessId -ne $launcher.Id -or
        -not $ExpectedPath.Equals($identity.ExecutablePath, [StringComparison]::OrdinalIgnoreCase) -or
        $process.StartTime.ToUniversalTime() -lt $launcher.StartTime.ToUniversalTime()) {
        $process.Dispose()
        throw 'Child process identity did not match the launcher and exact package executable.'
    }
    $tracked.Add($process)
    return $process
}
function Read-XpetAccessibility([IntPtr]$Window, [int]$ClientProcessId) {
    # A read through UI Automation activates Unity's accessibility provider before Quit
    # (UUM-146676). Target only the verified test window; do not enumerate the desktop.
    # Use Windows PowerShell's framework assemblies so this also works from PowerShell 7.
    # Bound the worker lifetime so an unresponsive provider cannot bypass test cleanup.
    $uiaScript = @'
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$element = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]__WINDOW__)
if ($null -eq $element) { throw 'UI Automation returned no element for the test window.' }
$current = $element.Current
if ($current.ProcessId -ne __PROCESS__) { throw 'UI Automation target no longer belongs to the test client.' }
$bounds = $current.BoundingRectangle
[pscustomobject]@{
    processId = $current.ProcessId
    windowHandle = $current.NativeWindowHandle
    x = $bounds.X
    y = $bounds.Y
    width = $bounds.Width
    height = $bounds.Height
} | ConvertTo-Json -Compress
'@
    $uiaScript = $uiaScript.Replace('__WINDOW__', $Window.ToInt64().ToString([Globalization.CultureInfo]::InvariantCulture)).Replace('__PROCESS__', $ClientProcessId.ToString([Globalization.CultureInfo]::InvariantCulture))
    $uiaInfo = New-Object Diagnostics.ProcessStartInfo
    $uiaInfo.FileName = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $uiaInfo.Arguments = '-NoProfile -NonInteractive -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($uiaScript))
    $uiaInfo.UseShellExecute = $false
    $uiaInfo.CreateNoWindow = $true
    $uiaInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $uiaInfo.RedirectStandardOutput = $true
    $uiaInfo.RedirectStandardError = $true
    $uiaWorker = [Diagnostics.Process]::Start($uiaInfo)
    try {
        if (-not $uiaWorker.WaitForExit(10000)) { throw 'UI Automation read timed out after 10 seconds.' }
        $uiaOutput = $uiaWorker.StandardOutput.ReadToEnd()
        $uiaError = $uiaWorker.StandardError.ReadToEnd()
        if ($uiaWorker.ExitCode -ne 0) { throw ('UI Automation read failed: ' + $uiaError.Trim()) }
        return ($uiaOutput | ConvertFrom-Json)
    } finally {
        if (-not $uiaWorker.HasExited) {
            $uiaWorker.Kill()
            $uiaWorker.WaitForExit(5000) | Out-Null
        }
        $uiaWorker.Dispose()
    }
}

if (-not ('XpetReleaseSmoke.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace XpetReleaseSmoke {
    public sealed class Window {
        public IntPtr Handle; public long ExStyle; public bool Visible; public string ClassName;
    }
    public static class Native {
        private delegate bool EnumProc(IntPtr window, IntPtr state);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr state);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int size);
        [DllImport("user32.dll", SetLastError=true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
        public static Window[] Windows(int processId) {
            var result = new List<Window>();
            EnumWindows((window, state) => {
                uint owner; GetWindowThreadProcessId(window, out owner);
                if (owner == processId) {
                    var name = new StringBuilder(256); GetClassName(window, name, name.Capacity);
                    result.Add(new Window {Handle=window, ExStyle=GetWindowLongPtr(window,-20).ToInt64(), Visible=IsWindowVisible(window), ClassName=name.ToString()});
                }
                return true;
            }, IntPtr.Zero);
            return result.ToArray();
        }
        public static bool Close(IntPtr window, int processId) {
            uint owner; GetWindowThreadProcessId(window, out owner);
            return owner == processId && PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
'@
}

New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
try {
    Assert-XpetRelease ([IntPtr]::Size -eq 8) '64-bit Windows process'
    foreach ($path in @($launcherPath, $clientPath, $backendPath)) {
        Assert-XpetRelease (Test-Path -LiteralPath $path -PathType Leaf) ('Required executable: ' + [IO.Path]::GetFileName($path))
    }
    $appInfo = Join-Path $PackageRoot 'dist\Xpet\Xpet.Client_Data\app.info'
    $appIdentity = @(Get-Content -LiteralPath $appInfo)
    Assert-XpetRelease ($appIdentity.Count -ge 2 -and $appIdentity[0] -eq 'DesktopPet' -and $appIdentity[1] -eq 'Xpet') 'Unity package identity is DesktopPet/Xpet'
    Assert-XpetRelease (@(Get-OtherXpetClients).Count -eq 0) 'No concurrent Xpet or legacy client shares preferences or hotkeys'
    $preferencesBefore = Get-XpetPreferences
    $preferencesBefore | Export-Clixml -LiteralPath (Join-Path $testDirectory 'client-preferences-before.clixml')
    $preferencesCaptured = $true
    $motionsBefore = @(Get-XpetMotionManifest)
    $motionsBefore | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $testDirectory 'client-motions-before.json') -Encoding utf8
    foreach ($name in @('Player.log', 'Player-prev.log')) {
        $source = Join-Path $localLow $name
        if (Test-Path -LiteralPath $source -PathType Leaf) { Copy-Item -LiteralPath $source -Destination (Join-Path $testDirectory ('before-' + $name)) }
    }
    $port = Free-XpetPort
    Assert-XpetRelease ($port -notin @(18080,18081) -and -not (Test-XpetPort $port)) 'Unused random local port; normal backend ports remain untouched'
    $data = Join-Path $testDirectory 'backend-data'
    $logPath = Join-Path $data 'logs\launcher.log'
    $baseUrl = 'http://127.0.0.1:' + $port
    $launcher = Start-XpetLauncher $port $data
    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $windows = @()
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($launcher.HasExited) { throw ('Launcher exited before client readiness, code ' + $launcher.ExitCode + '. See isolated launcher.log.') }
        if (Test-Path -LiteralPath $logPath) {
            $log = [IO.File]::ReadAllText($logPath)
            if (-not $backend -and $log -match 'Started owned backend PID (\d+)\.') { $backend = Get-VerifiedChild ([int]$Matches[1]) $backendPath }
            if (-not $client -and $log -match 'Started client PID (\d+)\.') { $client = Get-VerifiedChild ([int]$Matches[1]) $clientPath }
        }
        if ($client -and -not $client.HasExited) {
            $windows = @([XpetReleaseSmoke.Native]::Windows($client.Id) | Where-Object { $_.Visible -and $_.ClassName -eq 'UnityWndClass' })
            if ($windows.Count -gt 0 -and @($windows | Where-Object { ($_.ExStyle -band 0x80) -eq 0 -or ($_.ExStyle -band 0x40000) -ne 0 }).Count -eq 0) { break }
        }
        Start-Sleep -Milliseconds 200
    }
    Assert-XpetRelease ($null -ne $backend -and $null -ne $client -and -not $client.HasExited) 'Cold launch started only package-owned backend and real Unity client'
    $health = Invoke-RestMethod -Uri ($baseUrl + '/health') -TimeoutSec 3
    Assert-XpetRelease ($health.ok -eq $true -and $health.app -eq 'desktop-pet-backend-qa' -and [Version]$health.version -ge [Version]'0.6.0' -and $health.mode -eq 'offline') 'Packaged backend is healthy, compatible, isolated, and offline'
    Assert-XpetRelease ($windows.Count -gt 0) 'Real Unity client created its desktop window'
    Assert-XpetRelease (@($windows | Where-Object { ($_.ExStyle -band 0x80) -eq 0 -or ($_.ExStyle -band 0x40000) -ne 0 }).Count -eq 0) 'Client has TOOLWINDOW set and APPWINDOW clear (taskbar and Alt+Tab suppression)'
    Assert-XpetRelease (@([XpetReleaseSmoke.Native]::Windows($launcher.Id) | Where-Object Visible).Count -eq 0 -and @([XpetReleaseSmoke.Native]::Windows($backend.Id) | Where-Object Visible).Count -eq 0) 'Launcher and backend have no visible windows'
    $duplicate = Start-XpetLauncher $port $data (Join-Path $PackageRoot 'Xpet.exe')
    Assert-XpetRelease ($duplicate.WaitForExit(5000) -and $duplicate.ExitCode -eq 0) 'Repeated launch returns successfully without a second instance'
    Start-Sleep -Milliseconds 500
    $log = [IO.File]::ReadAllText($logPath)
    Assert-XpetRelease ([regex]::Matches($log, 'Started client PID ').Count -eq 1 -and [regex]::Matches($log, 'Started owned backend PID ').Count -eq 1 -and $log.Contains('Already running; repeated launch ignored.')) 'Repeated launch preserved the original client/backend pair'
    Assert-XpetRelease (@(Get-OtherXpetClients).Count -eq 1 -and -not $client.HasExited -and -not $backend.HasExited) 'Exactly one desktop client remains after repeated launch'
    $accessibility = Read-XpetAccessibility $windows[0].Handle $client.Id
    Assert-XpetRelease ($accessibility.processId -eq $client.Id -and $accessibility.width -gt 0 -and $accessibility.height -gt 0) 'UI Automation read the verified client bounding rectangle before normal Quit (UUM-146676 regression)'
    $accessibility | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $testDirectory 'accessibility-read.json') -Encoding utf8
    if ($AutomaticExitSeconds -gt 0) {
        Assert-XpetRelease ($client.WaitForExit(($AutomaticExitSeconds+30)*1000)) 'Real client invoked its own normal Quit action after release verification'
    } elseif ($AwaitInteractiveClose) {
        Write-Host ('READY_FOR_UI_CHECK: real Xpet client PID ' + $client.Id + '. Close it through the app within 180 seconds.')
        Assert-XpetRelease ($client.WaitForExit(180000)) 'Real client closed through the interactive app window'
    } else {
        Assert-XpetRelease ([XpetReleaseSmoke.Native]::Close($windows[0].Handle, $client.Id)) 'Sent normal WM_CLOSE to the verified test client only'
    }
    Assert-XpetRelease ($client.WaitForExit(15000) -and $client.ExitCode -eq 0) 'Real client exited normally'
    Assert-XpetRelease ($launcher.WaitForExit(15000) -and $launcher.ExitCode -eq 0) 'Launcher completed normal client shutdown'
    Assert-XpetRelease ($backend.WaitForExit(3000) -and -not (Test-XpetPort $port)) 'Owned backend exited and released the isolated port'
    $log = [IO.File]::ReadAllText($logPath)
    Assert-XpetRelease ($log.Contains('Requested graceful backend shutdown.') -and $log.Contains('Owned backend exited gracefully.')) 'Authenticated graceful backend shutdown completed'
    if ($AutomaticExitSeconds -gt 0) {
        $playerLog = Join-Path $localLow 'Player.log'
        $diagnostic = [regex]::Match([IO.File]::ReadAllText($playerLog),'XPET_RUNTIME_DIAGNOSTICS (\{[^\r\n]+\})')
        Assert-XpetRelease ($diagnostic.Success) 'Packaged player wrote runtime verification diagnostics'
        $runtime = $diagnostic.Groups[1].Value | ConvertFrom-Json
        $report.Add('OBSERVATION: Packaged player Unity version=' + $runtime.unityVersion + '; app version=' + $runtime.version)
        Assert-XpetRelease ($runtime.version -ceq '0.6.0') 'Packaged player reports Xpet version 0.6.0'
        if ($ExpectedUnityVersion) {
            Assert-XpetRelease ($runtime.unityVersion -ceq $ExpectedUnityVersion) ('Packaged player uses expected Unity version ' + $ExpectedUnityVersion + ' (actual: ' + $runtime.unityVersion + ')')
        }
        Assert-XpetRelease ($runtime.codepage932 -and -not $runtime.development) 'Japanese VMD encoding works in the release player; Development Build is off'
        $report.Add('OBSERVATION: Unity runtime watermark visible=' + $runtime.watermarkVisible + '; license-related watermarks are reported, never patched out.')
        if ($runtime.watermarkVisible) {
            $report.Add('ATTENTION: Unity reports a visible license watermark. Confirm the active Unity license and rebuild; this test does not alter or hide the watermark.')
        }
        Copy-Item -LiteralPath $playerLog -Destination (Join-Path $testDirectory 'Player.log')
    }
    $motionsAfter = @(Get-XpetMotionManifest)
    Assert-XpetRelease (($motionsBefore | ConvertTo-Json -Depth 3 -Compress) -ceq ($motionsAfter | ConvertTo-Json -Depth 3 -Compress)) 'Existing imported motion files remained unchanged'
    $report.Add('LIMIT: Taskbar suppression is checked by native window styles, not a screenshot of Explorer. No model, chat, screenshot, microphone or speech action was requested.')
    $report.Add('LIMIT: --data-dir isolates backend settings/history/credentials and launcher logs only. Unity used this user''s Xpet preferences; registry deltas are restored below. Player logs are retained; previous logs were backed up. No user file is deleted or restored over.')
} catch {
    $failure = $_.Exception.Message
    $report.Add('FAILED: ' + $failure)
} finally {
    # Each handle was created by this test or pinned after verifying direct parent and exact path.
    # Killing our launcher closes its KILL_ON_JOB_CLOSE job. No name-based or global cleanup.
    foreach ($process in $tracked) {
        try { if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null } }
        catch { $report.Add('CLEANUP: ' + $_.Exception.Message); if (-not $failure) { $failure = 'Owned process cleanup failed.' } }
    }
    try { Restore-XpetPreferences } catch { $report.Add('RESTORE: ' + $_.Exception.Message); if (-not $failure) { $failure = 'Preference restoration did not complete.' } }
    foreach ($process in $tracked) { $process.Dispose() }
    $report | Set-Content -LiteralPath (Join-Path $testDirectory 'results.txt') -Encoding utf8
    Write-Host ('Real release smoke-test report: ' + $testDirectory)
}
if ($failure) { throw $failure }

