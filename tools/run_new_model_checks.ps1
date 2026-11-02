param([string]$ReportDirectory = 'new-models-native')
$ErrorActionPreference = 'Stop'
$petWorkspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$qaExecutable = Join-Path $petWorkspace 'pet-frontend\pet-frontend\Builds\BehaviorQA\Xpet.QA.exe'
$qaIdentity = @(Get-Content -LiteralPath (Join-Path (Split-Path $qaExecutable) 'Xpet.QA_Data\app.info'))
if ($qaIdentity.Count -lt 2 -or $qaIdentity[0] -ne 'DesktopPetQA') { throw 'Build the isolated QA client before running model checks.' }
$qaSession = Get-Content -LiteralPath (Join-Path $petWorkspace '.artifacts\behavior-qa-session.json') -Raw | ConvertFrom-Json
$qaData = [IO.Path]::GetFullPath($qaSession.data)
$qaReport = [IO.Path]::GetFullPath((Join-Path $petWorkspace ('verification\' + $ReportDirectory)))
if (-not $qaReport.StartsWith((Join-Path $petWorkspace 'verification\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Reports must stay inside the verification directory.' }
$qaUrl = 'http://127.0.0.1:' + $qaSession.port
$qaHealth = Invoke-RestMethod ($qaUrl + '/health') -TimeoutSec 3
if ($qaHealth.app -ne 'desktop-pet-backend-qa') { throw 'An isolated backend is required.' }
$qaArguments = @('--backend-base-url', $qaUrl, '--xpet-library-root', $qaData, '--xpet-isolated-libraries',
    '--xpet-new-model-checks', '--xpet-model-source-root', (Join-Path $petWorkspace 'pet-models'),
    '--xpet-new-model-report-dir', $qaReport, '-logFile', ($qaReport + '-player.log'))
$qaProcess = Start-Process -FilePath $qaExecutable -ArgumentList $qaArguments -WorkingDirectory (Split-Path $qaExecutable) -WindowStyle Hidden -PassThru
if (-not $qaProcess.WaitForExit(90000)) { $qaProcess.Kill(); throw 'The owned model QA client timed out.' }
Get-Content -LiteralPath (Join-Path $qaReport 'results.txt')
exit $qaProcess.ExitCode
