param(
    [string]$GameDir = $(if ($env:NUCLEAR_OPTION_DIR) { $env:NUCLEAR_OPTION_DIR } else { 'C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option' }),
    [string]$DotnetPath = '',
    [string]$ServerArchive = ''
)
$ErrorActionPreference = 'Stop'
$version = '1.3.4'
$packageVersion = '1.3.4'
$workspace = Split-Path $PSScriptRoot -Parent
if (!$DotnetPath) {
    $bundled = Join-Path $workspace '.tools\dotnet-sdk\dotnet.exe'
    $DotnetPath = if (Test-Path -LiteralPath $bundled) { $bundled } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
if (!$ServerArchive) { $ServerArchive = Join-Path $PSScriptRoot 'dist\KellysDOORMAN-1.0.0-SERVER.zip' }
if (!(Test-Path -LiteralPath $ServerArchive)) { throw 'Supply the published 1.0 SERVER ZIP with -ServerArchive. This candidate reuses that release instead of rebuilding the server.' }
if ((Get-FileHash -LiteralPath $ServerArchive).Hash -ne '3FCF98043365EAB1814843BF31A8FB3C5FA83710F8A088E90EFCD787905D6B1A') { throw 'SERVER archive does not match the published 1.0 package.' }
$cache = Join-Path $workspace '.tools\nuget-packages'
if (Test-Path -LiteralPath $cache) { $env:NUGET_PACKAGES = $cache }
$evidence = Join-Path $PSScriptRoot "evidence\$packageVersion"
$dist = Join-Path $PSScriptRoot "dist\$packageVersion"
New-Item -ItemType Directory -Force -Path $evidence,$dist | Out-Null
$client = Join-Path $PSScriptRoot 'bin\Release\KellysJOINCHECK\net472\KellysJOINCHECK.dll'
$startup = Join-Path $PSScriptRoot 'startup\bin\Release\net472\KellysDOORMANStartup.dll'
$restart = Join-Path $PSScriptRoot 'restart\bin\Release\net472\KellysDOORMANRestart.exe'
$server = Join-Path $dist 'reused-server\KellysJOINCHECKServer.dll'
New-Item -ItemType Directory -Force -Path (Split-Path $server -Parent) | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($ServerArchive)
try {
    $inputStream = $archive.GetEntry('BepInEx/plugins/KellysJOINCHECKServer.dll').Open()
    $outputStream = [IO.File]::Create($server)
    try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose(); $inputStream.Dispose() }
} finally { $archive.Dispose() }
if ((Get-FileHash -LiteralPath $server).Hash -ne '5ADB2B67797C2BD002ED7F499CB4A20A577E04979545EB8F9CE3359BF8FD1E6F') { throw 'Released server DLL failed its digest check.' }
foreach ($build in @(
    @{ Project='KellysDOORMAN.csproj'; Log='client-build.txt' },
    @{ Project='startup\DoormanStartup.csproj'; Log='startup-build.txt' },
    @{ Project='restart\RestartHelper.csproj'; Log='restart-build.txt' }
)) {
    & $DotnetPath build (Join-Path $PSScriptRoot $build.Project) -c Release "-p:GameDir=$GameDir" --nologo -v:q 2>&1 | Tee-Object -FilePath (Join-Path $evidence $build.Log)
    if ($LASTEXITCODE -ne 0) { throw "$($build.Project) failed." }
}
$testLog = Join-Path $evidence 'test-results.txt'
& $DotnetPath run --project (Join-Path $PSScriptRoot 'tests\Tests.csproj') -c Release "-p:GameDir=$GameDir" -- $GameDir $client $server $startup $restart 2>&1 | Tee-Object -FilePath $testLog
if ($LASTEXITCODE -ne 0) { throw 'Regression checks failed.' }
$match = [regex]::Match((Get-Content -LiteralPath $testLog -Raw),'PASS: (\d+) regression')
if (!$match.Success) { throw 'Tests did not complete successfully.' }
foreach ($file in @('README.md','docs/MOD-PREVIEWS.md','docs/MAP-PREVIEWS.md','docs/MOD-LISTS.md','docs/MODS-TEST.md','docs/MODS-HANDOFF.md','docs/UPDATES.md','docs/SERVER-SETUP.md')) {
    if ((Get-Content -LiteralPath (Join-Path $PSScriptRoot $file) -Raw).Contains([char]0x2014)) { throw "Em dash in $file" }
}
if ([regex]::Matches((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'docs/MOD-PREVIEWS.md') -Raw),'\S+').Count -gt 350) { throw 'Modder guide exceeds 350 words.' }
if ([regex]::Matches((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'docs/MAP-PREVIEWS.md') -Raw),'\S+').Count -gt 220) { throw 'Map preview guide exceeds 220 words.' }
if ([regex]::Matches((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Raw),'\S+').Count -gt 300) { throw 'README exceeds 300 words.' }
. (Join-Path $PSScriptRoot 'build-tools\New-CrossPlatformZip.ps1')
function New-VerifiedPackage {
    param([string]$Name,[array]$Files)
    $folder = Join-Path $dist $Name
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    foreach ($file in $Files) {
        $target = Join-Path $folder $file.To
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Copy-Item -LiteralPath $file.From -Destination $target -Force
    }
    $zipPath = $folder+'.zip'
    New-CrossPlatformZip -SourceDirectory $folder -DestinationPath $zipPath
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        if (Compare-Object ($zip.Entries.FullName | Sort-Object) ($Files.To | Sort-Object)) { throw "Unexpected package member in $Name." }
        if ($zip.Entries.FullName | Where-Object { $_ -match '(?i)(^|/)(previews|DOORMAN-Previews)/|\.doorman\.json$|\.(png|jpe?g)$' -and $_ -ne 'metadata/client/doorman.png' }) { throw "Sample preview/image files must not be shipped in $Name." }
        foreach ($file in $Files) {
            $stream = $zip.GetEntry($file.To).Open(); $sha = [Security.Cryptography.SHA256]::Create()
            try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') } finally { $sha.Dispose(); $stream.Dispose() }
            if ($actual -ne (Get-FileHash -LiteralPath $file.From).Hash) { throw "Packaged bytes differ: $($file.To)" }
        }
    } finally { $zip.Dispose() }
    Write-Host "Verified $Name"
    return (Get-FileHash -LiteralPath $zipPath | Select-Object Path,Hash)
}
$docs = @('docs/RECOVERY.md','docs/RELEASE-1.3.4.md','docs/RELEASE-AUDIT.md','README.md','LICENSE','docs/MOD-PREVIEWS.md','docs/MAP-PREVIEWS.md','docs/MOD-LISTS.md','docs/MODS-TEST.md','docs/MODS-HANDOFF.md','docs/UPDATES.md','docs/SERVER-SETUP.md')
$common = @($docs | ForEach-Object { @{ From=(Join-Path $PSScriptRoot $_); To=$_ } })
$common += @{ From=$client; To='BepInEx/plugins/KellysJOINCHECK.dll' }
$common += @{ From=$startup; To='BepInEx/patchers/KellysDOORMANStartup.dll' }
$common += @{ From=$restart; To='BepInEx/DOORMAN/KellysDOORMANRestart.exe' }
$artifacts = @()
$artifacts += New-VerifiedPackage "KellysDOORMAN-$packageVersion-FULL" ($common+@{ From=$server; To='BepInEx/plugins/KellysJOINCHECKServer.dll' })
$artifacts += New-VerifiedPackage "KellysDOORMAN-$packageVersion-CLIENT" $common
$serverCopy = Join-Path $dist 'KellysDOORMAN-1.0.0-SERVER.zip'
Copy-Item -LiteralPath $ServerArchive -Destination $serverCopy -Force
if ((Get-FileHash -LiteralPath $serverCopy).Hash -ne (Get-FileHash -LiteralPath $ServerArchive).Hash) { throw 'SERVER copy changed.' }
$artifacts += Get-FileHash -LiteralPath $serverCopy | Select-Object Path,Hash
$sourceNames = @('README.md','LICENSE','.gitignore','.gitattributes','KellysDOORMAN.csproj','Build-Package.ps1','build-tools/New-CrossPlatformZip.ps1','tests/Tests.csproj','tests/Program.cs','tests/ModChecks.cs','tests/UpdateChecks.cs','tests/StartupChecks.cs','tests/NativeBrowserLayout.json','startup/DoormanStartup.csproj','startup/DoormanStartupPatcher.cs','startup/DoormanStartupPatch.cs','startup/DoormanRuntimeHooks.cs','docs/BUILD.md','docs/IN-GAME-TEST.md','docs/MOD-PREVIEWS.md','docs/MODS-TEST.md','docs/MODS-HANDOFF.md','docs/UPDATES.md')
$sourceNames += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' -File | ForEach-Object { 'src/'+$_.Name })
$sourceNames += @('restart/RestartHelper.csproj','restart/Program.cs','restart/StatusWindow.cs','restart/RestartLaunchPolicy.cs','restart/SteamSessionState.cs','restart/RestartProcessFamily.cs','restart/DesktopRestartBroker.cs','tests/WorkflowChecks.cs','tests/RestartChecks.cs','docs/SERVER-SETUP.md')
$sourceNames += @('startup/DoormanRuntimeHost.cs','build-tools/Test-DetachedRestart.ps1')
$sourceNames += @('tests/PreviewChecks.cs')
$sourceNames += @('tests/SavedListChecks.cs','tests/InstalledContentChecks.cs','docs/MOD-LISTS.md')
$sourceNames += @('tests/MapFilesTests.cs','tests/MapBridgeChecks.cs','docs/MAP-PREVIEWS.md')
$sourceNames += @('metadata/client/doorman.json','tests/DoormanManifestChecks.cs')
$sourceNames += @('tests/ScrollbarChecks.cs','docs/RECOVERY.md','docs/RELEASE-1.3.4.md','docs/RELEASE-AUDIT.md')
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'metadata/client/doorman.png')) { $sourceNames += 'metadata/client/doorman.png' }
$sourceFiles = @($sourceNames | ForEach-Object { @{ From=(Join-Path $PSScriptRoot $_); To=$_ } })
$artifacts += New-VerifiedPackage "KellysDOORMAN-$packageVersion-SOURCE" $sourceFiles
$artifacts | ForEach-Object { $_.Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($_.Path) } | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
$record = [ordered]@{
    product="Kelly's DOORMAN"; packageVersion=$packageVersion; clientVersion=$version; serverVersion='1.0.0'
    serverArtifactReused=$true; protocol=1; builtUtc=[DateTime]::UtcNow.ToString('o')
    artifacts=$artifacts; dlls=@(Get-FileHash -LiteralPath $client,$startup,$server,$restart | Select-Object Path,Hash)
    gameAssemblyHash=(Get-FileHash -LiteralPath (Join-Path $GameDir 'NuclearOption_Data\Managed\Assembly-CSharp.dll')).Hash
    gameCoreAssemblyHash=(Get-FileHash -LiteralPath (Join-Path $GameDir 'NuclearOption_Data\Managed\UnityEngine.CoreModule.dll')).Hash
    blueprinterHash=(Get-FileHash -LiteralPath (Join-Path $GameDir 'BepInEx\plugins\Blueprinter_2.0.1.dll')).Hash
    databaseImpact='none'
    configurationImpact='Maps view is read-only with a scoped OS watcher and no persisted settings or cache. Existing configs/favourites preserved; no server settings changed. Explicit saved-list actions create portable JSON and backups in BepInEx/plugins/DOORMAN-Lists and a local hash-bound content receipt in BepInEx/cache/doorman-content-inventory.json. Loading uses the existing normal profile/reload/restart transaction. Return tickets and other caches retain their documented behavior. Explicit ZIP updates may install plugin-bound JSON/images under BepInEx/plugins/DOORMAN-Metadata with per-file queue hashes/backups; legacy DLL-only queues remain readable. No bundled sample previews/images or player list files.'
    regressionChecks=[int]$match.Groups[1].Value
    installed=$false; published=$false; remotelyStaged=$false
    priorInGameObservation='Aaron confirmed all 1.3.3 features worked in game on 6 October 2026: saved lists, Maps, scrollbars, a mod update and restart back to a server. The 1.3.4 audit fixes need the focused smoke check in docs/RELEASE-AUDIT.md; no 1.3.4 installation or restart was performed by this build.'
    detachedLaunchProbe=if(Test-Path -LiteralPath (Join-Path $evidence 'explorer-parent-probe.json')) {Get-Content -LiteralPath (Join-Path $evidence 'explorer-parent-probe.json') -Raw | ConvertFrom-Json} else {$null}
    inGameAcceptance='1.3.3 feature acceptance confirmed by Aaron; 1.3.4 focused smoke check pending. Broader stress coverage remains unrecorded: all ten permanent scrollbar tracks/thumbs, wheel/drag/track clicks, wrapping, nested views and scaling; Maps tab scaling/default chart/embedded assets, add/change/remove notices, unsupported loader, async close/refresh and watcher cleanup; 1.3.4 saved-list UI/save/load/share, typing/scaling, missing/version warnings, dependency conflicts, cached disabled wrapper recovery and cancelled restarts; embedded previews on disabled wrappers, sidecar ZIP update and raw-DLL transition, manual overrides, interrupted per-file recovery; cancellation recovery restores Mods controls after closing/reopening during verification/bootstrap; repeated restart and broker timeout/plugin disposal; Steam session-release wait and bounded retry fallback, late/manual launch without duplicate dispatch; disabled content/wrapper images and custom plugin-only previews; Windows status UI/focus/DPI/cleanup; ordinary launch restores the saved profile and preserves unrelated client plugins; downloaded F-16 update through restart and actual Join; vanilla/modded/passworded joins, repeated A -> B -> A hot reload networking, NOMNOM fallback, clean install/upgrade and busy-mission runtime performance'
}
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $dist 'BUILD-RECORD.json') -Encoding utf8
Write-Output "Candidate packages: $dist"
