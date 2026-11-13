param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
[string]$report = Join-Path $Root 'evidence\1.2.7-localtest.1\explorer-parent-probe.json'
[string]$helper = Join-Path $Root 'restart\bin\Release\net472\KellysDOORMANRestart.exe'
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $Root 'bin\Release\KellysJOINCHECK\net472\KellysJOINCHECK.dll'))
$launcher = $assembly.GetType('KellysJOINCHECK.WindowsRestartLauncher',$true).GetMethod('Start',[Reflection.BindingFlags]'Static,NonPublic')
[string]$arguments = '--probe-parent "' + $report + '"'
[object[]]$parameters = @([string]$helper,[string]$arguments,[string][IO.Path]::GetDirectoryName($helper))
$launcher.Invoke($null,$parameters)
$clock = [Diagnostics.Stopwatch]::StartNew()
while (!(Test-Path -LiteralPath $report) -and $clock.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 200 }
if (!(Test-Path -LiteralPath $report)) { throw 'Detached test process did not write its report.' }
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if ($result.parentPid -ne $result.desktopPid -or !$result.steamContextCleared) { throw 'Detached parent/environment check failed.' }
$result | ConvertTo-Json -Compress
