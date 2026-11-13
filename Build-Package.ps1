param(
    [string]$GameDir = $(if ($env:NUCLEAR_OPTION_DIR) { $env:NUCLEAR_OPTION_DIR } else { 'C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option' }),
    [string]$DotnetPath = '',
    [string]$OutputDir = '',
    [string]$BlueprinterPath = '',
    [string]$ReuseUnchangedFrom = '',
    [switch]$RebuildStartup
)
$ErrorActionPreference = 'Stop'
$version = '1.4.0'
$serverVersion = '1.1.0'
$packageVersion = '1.4.0-rc.36'
if (!(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\SelfUpdateRelease.cs') -Raw).Contains('Installed="'+$packageVersion+'"')) { throw 'Set SelfUpdateRelease.Installed to the package version before building.' }
if (!$BlueprinterPath) { $BlueprinterPath = Join-Path $GameDir 'BepInEx\plugins\Blueprinter_2.0.1.dll' }
$BlueprinterPath = [IO.Path]::GetFullPath($BlueprinterPath)
if (!(Test-Path -LiteralPath $BlueprinterPath -PathType Leaf)) { throw 'Blueprinter validation DLL missing. Pass -BlueprinterPath with a Blueprinter 2.0.1 DLL; it does not need to be installed in the game.' }
$workspace = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (!$DotnetPath) {
    $bundled = Join-Path $workspace '.tools\dotnet-sdk\dotnet.exe'
    $DotnetPath = if (Test-Path -LiteralPath $bundled) { $bundled } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
$cache = Join-Path $workspace '.tools\nuget-packages'
if (Test-Path -LiteralPath $cache) { $env:NUGET_PACKAGES = $cache }
$evidence = Join-Path $PSScriptRoot "evidence\$packageVersion"
$dist = if ($OutputDir) { [IO.Path]::GetFullPath($OutputDir) } else { Join-Path $PSScriptRoot "dist\$packageVersion" }
New-Item -ItemType Directory -Force -Path $evidence,$dist | Out-Null
$client = Join-Path $PSScriptRoot 'bin\Release\KellysJOINCHECK\net472\KellysJOINCHECK.dll'
$startup = Join-Path $PSScriptRoot 'startup\bin\Release\net472\KellysDOORMANStartup.dll'
$restart = Join-Path $PSScriptRoot 'restart\bin\Release\net472\KellysDOORMANRestart.exe'
$server = Join-Path $PSScriptRoot 'bin\Release\KellysJOINCHECKServer\net472\KellysJOINCHECKServer.dll'
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($build in @(
    @{ Project='KellysDOORMAN.csproj'; Log='client-build.txt' },
    @{ Project='KellysDOORMAN.csproj'; Log='server-build.txt'; Server=$true },
    @{ Project='startup\DoormanStartup.csproj'; Log='startup-build.txt' },
    @{ Project='restart\RestartHelper.csproj'; Log='restart-build.txt' }
)) {
    $buildArgs = @('build',(Join-Path $PSScriptRoot $build.Project),'-c','Release',"-p:GameDir=$GameDir",'--nologo','-v:q')
    if ($build.Server) { $buildArgs += '-p:ServerBuild=true' }
    & $DotnetPath @buildArgs 2>&1 | Tee-Object -FilePath (Join-Path $evidence $build.Log)
    if ($LASTEXITCODE -ne 0) { throw "$($build.Project) failed." }
}
$reusedComponents = @()
if ($ReuseUnchangedFrom) {
    . (Join-Path $PSScriptRoot 'build-tools\Use-UnchangedComponents.ps1')
    $reusedComponents = @(Use-UnchangedComponents -Root $PSScriptRoot -PackageDirectory ([IO.Path]::GetFullPath($ReuseUnchangedFrom)) -GameDir $GameDir -RebuildStartup:$RebuildStartup)
}
$testLog = Join-Path $evidence 'test-results.txt'
& $DotnetPath run --project (Join-Path $PSScriptRoot 'tests\Tests.csproj') -c Release "-p:GameDir=$GameDir" -- $GameDir $client $server $startup $restart $BlueprinterPath 2>&1 | Tee-Object -FilePath $testLog
if ($LASTEXITCODE -ne 0) { throw 'Regression checks failed.' }
$match = [regex]::Match((Get-Content -LiteralPath $testLog -Raw),'PASS: (\d+) regression')
if (!$match.Success) { throw 'Tests did not complete successfully.' }
foreach ($file in @('README.md','docs/RECOVERY.md','docs/DEVELOPER.md')) {
    if ((Get-Content -LiteralPath (Join-Path $PSScriptRoot $file) -Raw).Contains([char]0x2014)) { throw "Em dash in $file" }
}
if ([regex]::Matches((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'docs/DEVELOPER.md') -Raw),'\S+').Count -gt 650) { throw 'Developer guide exceeds 650 words.' }
if ([regex]::Matches((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Raw),'\S+').Count -gt 300) { throw 'README exceeds 300 words.' }
. (Join-Path $PSScriptRoot 'build-tools\New-CrossPlatformZip.ps1')
function New-VerifiedPackage {
    param([string]$Name,[array]$Files)
    $folder = Join-Path $dist ($Name+'.stage-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    foreach ($file in $Files) {
        $target = Join-Path $folder $file.To
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Copy-Item -LiteralPath $file.From -Destination $target -Force
    }
    $zipPath = Join-Path $dist ($Name+'.zip')
    New-CrossPlatformZip -SourceDirectory $folder -DestinationPath $zipPath
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        if (Compare-Object ($zip.Entries.FullName | Sort-Object) ($Files.To | Sort-Object)) { throw "Unexpected package member in $Name." }
        $expectedGuides = @('README.md','docs/RECOVERY.md','docs/DEVELOPER.md')
        if (Compare-Object ($zip.Entries.FullName | Where-Object { $_ -like '*.md' } | Sort-Object) ($expectedGuides | Sort-Object)) { throw "Package must contain exactly three guides: $Name." }
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
$docs = @('README.md','docs/RECOVERY.md','docs/DEVELOPER.md','LICENSE')
$common = @($docs | ForEach-Object { @{ From=(Join-Path $PSScriptRoot $_); To=$_ } })
$common += @{ From=$client; To='BepInEx/plugins/KellysJOINCHECK.dll' }
$common += @{ From=$startup; To='BepInEx/patchers/KellysDOORMANStartup.dll' }
$common += @{ From=$restart; To='BepInEx/DOORMAN/KellysDOORMANRestart.exe' }
$artifacts = @()
$artifacts += New-VerifiedPackage "KellysDOORMAN-$packageVersion-FULL" ($common+@{ From=$server; To='BepInEx/plugins/KellysJOINCHECKServer.dll' })
$artifacts += New-VerifiedPackage "KellysDOORMAN-$packageVersion-CLIENT" $common
$artifacts += New-VerifiedPackage "KellysDOORMAN-$serverVersion-rc.36-SERVER" (($common | Where-Object { $_.To -in @('README.md','docs/RECOVERY.md','docs/DEVELOPER.md','LICENSE') })+@{ From=$server; To='BepInEx/plugins/KellysJOINCHECKServer.dll' })
$sourceNames = @('README.md','LICENSE','.gitignore','.gitattributes','KellysDOORMAN.csproj','Build-Package.ps1','build-tools/New-CrossPlatformZip.ps1','tests/Tests.csproj','tests/Program.cs','tests/ModChecks.cs','tests/UpdateChecks.cs','tests/StartupChecks.cs','tests/NativeBrowserLayout.json','startup/DoormanStartup.csproj','startup/DoormanStartupPatcher.cs','startup/DoormanStartupPatch.cs','startup/DoormanRuntimeHooks.cs','docs/RECOVERY.md','docs/DEVELOPER.md')
$sourceNames += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' -File | ForEach-Object { 'src/'+$_.Name })
$sourceNames += @('restart/RestartHelper.csproj','restart/Program.cs','restart/StatusWindow.cs','restart/RestartLaunchPolicy.cs','restart/SteamSessionState.cs','restart/RestartProcessFamily.cs','restart/DesktopRestartBroker.cs','tests/WorkflowChecks.cs','tests/RestartChecks.cs')
$sourceNames += @('startup/DoormanRuntimeHost.cs','build-tools/Test-DetachedRestart.ps1')
$sourceNames += 'build-tools/Use-UnchangedComponents.ps1'
$sourceNames += @('tests/PreviewChecks.cs')
$sourceNames += @('tests/SavedListChecks.cs','tests/InstalledContentChecks.cs')
$sourceNames += @('tests/MapFilesTests.cs','tests/MapBridgeChecks.cs')
$sourceNames += @('tests/ServerMapUpdateChecks.cs')
$sourceNames += @('tests/IronjawUpdateChecks.cs')
$sourceNames += @('metadata/client/doorman.json','tests/DoormanManifestChecks.cs')
$sourceNames += @('tests/RequisiteChecks.cs','tests/RequisiteSelectionChecks.cs','tests/RequisiteUiChecks.cs')
$sourceNames += @('tests/ContentSourceChecks.cs')
$sourceNames += @('tests/OpaqueCompatibilityChecks.cs')
$sourceNames += @('tests/RestartLaunchFailureChecks.cs','tests/ManualRestartChecks.cs')
$sourceNames += @('tests/StartupDiagnosticsChecks.cs','tests/ServerCompatibilityPluginChecks.cs')
$sourceNames += @('tests/NommDisabledWorkflowChecks.cs')
$sourceNames += @('tests/DisabledModFileChecks.cs','tests/DisabledRestoreQueueChecks.cs','tests/DisabledContentChecks.cs','tests/NommDisabledCatalogChecks.cs','tests/NommDisabledSelectionChecks.cs')
$sourceNames += @('tests/SupportReportChecks.cs','tests/SupportCaptureChecks.cs')
$sourceNames += @('tests/ScrollbarChecks.cs','tests/ModMenuChecks.cs')
$sourceNames += @('tests/GameplayFingerprintChecks.cs','tests/GameplayFingerprintSetupChecks.cs','tests/GameplayFingerprintIntegrationChecks.cs','tests/GameplayFingerprintFileChecks.cs')
$sourceNames += @('tests/ShipyardSetupChecks.cs')
$sourceNames += @('tests/ServerDownloadResolverChecks.cs')
$sourceNames += @('tests/AssetLoadingDiagnosticsChecks.cs','tests/MapPackageChecks.cs','tests/SelfUpdatePackageChecks.cs','tests/SelfUpdateTransactionChecks.cs')
$sourceNames += @('tests/StateWriteSafetyChecks.cs')
$sourceNames += @('tests/StartupUpdatePreflightChecks.cs')
$sourceNames += @('tests/StandaloneBundleUpdateChecks.cs')
$sourceNames += @('tests/AutoSetupBatchChecks.cs')
$sourceNames += @('tests/AddonRemovalChecks.cs')
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'metadata/client/doorman.png')) { $sourceNames += 'metadata/client/doorman.png' }
$sourceFiles = @($sourceNames | ForEach-Object { @{ From=(Join-Path $PSScriptRoot $_); To=$_ } })
$artifacts += New-VerifiedPackage "KellysDOORMAN-$packageVersion-SOURCE" $sourceFiles
$artifacts | ForEach-Object { $_.Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($_.Path) } | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
$record = [ordered]@{
    product="Kelly's DOORMAN"; packageVersion=$packageVersion; clientVersion=$version; serverVersion=$serverVersion
    serverArtifactReused=($reusedComponents.Count -gt 0); reusedComponents=$reusedComponents; protocol=1; gameplayFingerprintProtocol=3; gameplayFingerprintScope='audited-v2'; builtUtc=[DateTime]::UtcNow.ToString('o')
    artifacts=$artifacts; dlls=@(Get-FileHash -LiteralPath $client,$startup,$server,$restart | Select-Object Path,Hash)
    restartHelperSignature=(Get-AuthenticodeSignature -LiteralPath $restart).Status.ToString()
    gameAssemblyHash=(Get-FileHash -LiteralPath (Join-Path $GameDir 'NuclearOption_Data\Managed\Assembly-CSharp.dll')).Hash
    gameCoreAssemblyHash=(Get-FileHash -LiteralPath (Join-Path $GameDir 'NuclearOption_Data\Managed\UnityEngine.CoreModule.dll')).Hash
    blueprinterHash=(Get-FileHash -LiteralPath $BlueprinterPath).Hash
    blueprinterValidationPath=$BlueprinterPath
    databaseImpact='none'
    candidateConfigurationImpact='Opt-in server map setup queues verified loaders and map files for restart; replaced files retain rollback copies. Ironjaw may install its bundled missions and create its own configs according to that mod. Existing DOORMAN configs and saved lists remain intact. Database impact: none. No live installation, server restart or publication.'
    candidateRegression='rc.36: audited Su-30 content and plugin identities resolve the catalogue download and existing installation. Exact fingerprint checks remain required. Retains RC35 menu and footer fixes. Protocol 3 and audited-v2 unchanged; live acceptance recorded separately.'
    candidateInGameAcceptance='Automated checks run during build. In-game, live download and multiplayer acceptance are recorded separately. The build itself does not install, restart or publish anything.'
    configurationImpact='Existing configs and saved lists are preserved. The opt-in self-update adds a receipt and client backup under BepInEx/cache. Database impact is none.'
    regressionChecks=[int]$match.Groups[1].Value
    installed=$false; published=$false; remotelyStaged=$false
    detachedLaunchProbe=if(Test-Path -LiteralPath (Join-Path $evidence 'explorer-parent-probe.json')) {Get-Content -LiteralPath (Join-Path $evidence 'explorer-parent-probe.json') -Raw | ConvertFrom-Json} else {$null}
}
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $dist 'BUILD-RECORD.json') -Encoding utf8
Write-Output "Candidate packages: $dist"

