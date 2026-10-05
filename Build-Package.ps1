param(
    [string]$GameDir = $(if ($env:NUCLEAR_OPTION_DIR) { $env:NUCLEAR_OPTION_DIR } else { 'C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option' }),
    [string]$DotnetPath = ''
)
$ErrorActionPreference = 'Stop'
$version = '1.0.0'
$serverVersion = '1.0.0'
$workspace = Split-Path $PSScriptRoot -Parent
if (!$DotnetPath) {
    $bundled = Join-Path $workspace '.tools\dotnet-sdk\dotnet.exe'
    $DotnetPath = if (Test-Path -LiteralPath $bundled) { $bundled } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
$cache = Join-Path $workspace '.tools\nuget-packages'
if (Test-Path -LiteralPath $cache) { $env:NUGET_PACKAGES = $cache }
$evidence = Join-Path $PSScriptRoot 'evidence'
$dist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force -Path $evidence,$dist | Out-Null
$project = Join-Path $PSScriptRoot 'KellysDOORMAN.csproj'
$client = Join-Path $PSScriptRoot 'bin\Release\KellysJOINCHECK\net472\KellysJOINCHECK.dll'
$server = Join-Path $PSScriptRoot 'bin\Release\KellysJOINCHECKServer\net472\KellysJOINCHECKServer.dll'
& $DotnetPath build $project -c Release "-p:GameDir=$GameDir" --nologo -v:q 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'client-build.txt')
if ($LASTEXITCODE -ne 0) { throw 'Client build failed.' }
& $DotnetPath build $project -c Release -p:ServerBuild=true "-p:GameDir=$GameDir" --nologo -v:q 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'server-build.txt')
if ($LASTEXITCODE -ne 0) { throw 'Server build failed.' }
$testLog = Join-Path $evidence 'test-results.txt'
& $DotnetPath run --project (Join-Path $PSScriptRoot 'tests\Tests.csproj') -c Release "-p:GameDir=$GameDir" -- $GameDir $client $server 2>&1 | Tee-Object -FilePath $testLog
if ($LASTEXITCODE -ne 0) { throw 'Regression checks failed.' }
$testMatch = [regex]::Match((Get-Content -LiteralPath $testLog -Raw),'PASS: (\d+) regression')
if (!$testMatch.Success) { throw 'Missing test completion record.' }
foreach ($path in @('README.md','RELEASE-NOTES.md')) {
    $text = Get-Content -LiteralPath (Join-Path $PSScriptRoot $path) -Raw
    if ($text.Contains([char]0x2014)) { throw "Em dash found in $path" }
}
if ([regex]::Matches((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Raw),'\S+').Count -gt 300) { throw 'README exceeds the short public format.' }
. (Join-Path $PSScriptRoot 'build-tools\New-CrossPlatformZip.ps1')
$artifacts = @()
foreach ($role in @(
    @{ Name='FULL'; Dlls=@($client,$server) },
    @{ Name='CLIENT'; Dlls=@($client) },
    @{ Name='SERVER'; Dlls=@($server) }
)) {
    $package = Join-Path $dist "KellysDOORMAN-$version-$($role.Name)"
    $plugins = Join-Path $package 'BepInEx\plugins'
    New-Item -ItemType Directory -Force -Path $plugins | Out-Null
    Copy-Item -LiteralPath $role.Dlls -Destination $plugins -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md'),(Join-Path $PSScriptRoot 'LICENSE') -Destination $package -Force
    $zipPath = $package+'.zip'
    New-CrossPlatformZip -SourceDirectory $package -DestinationPath $zipPath
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $expected = @('README.md','LICENSE')+@($role.Dlls | ForEach-Object { 'BepInEx/plugins/'+[IO.Path]::GetFileName($_) })
        if (Compare-Object ($zip.Entries.FullName | Sort-Object) ($expected | Sort-Object)) { throw "Unexpected $($role.Name) package contents." }
        foreach ($dll in $role.Dlls) {
            $stream = $zip.GetEntry('BepInEx/plugins/'+[IO.Path]::GetFileName($dll)).Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $inZip = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') }
            finally { $sha.Dispose(); $stream.Dispose() }
            if ($inZip -ne (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash) { throw 'Packaged DLL differs from the tested build.' }
        }
    } finally { $zip.Dispose() }
    $artifacts += Get-FileHash -LiteralPath $zipPath -Algorithm SHA256 | Select-Object Path,Hash
    Write-Output "Verified $($role.Name) package: $zipPath"
}
$source = Join-Path $dist "KellysDOORMAN-$version-SOURCE"
New-Item -ItemType Directory -Force -Path $source | Out-Null
$sourceFiles = @('README.md','LICENSE','.gitignore','.gitattributes','KellysDOORMAN.csproj','Build-Package.ps1','RELEASE-NOTES.md','RELEASE-PLAN.json','build-tools/New-CrossPlatformZip.ps1','tests/Tests.csproj','tests/Program.cs','tests/NativeBrowserLayout.json','docs/BUILD.md','docs/IN-GAME-TEST.md')
$sourceFiles += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' -File | ForEach-Object { 'src/'+$_.Name })
foreach ($relative in $sourceFiles) {
    $destination = Join-Path $source $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $relative) -Destination $destination -Force
}
$sourceZip = $source+'.zip'
New-CrossPlatformZip -SourceDirectory $source -DestinationPath $sourceZip
$zip = [IO.Compression.ZipFile]::OpenRead($sourceZip)
try {
    if (Compare-Object ($zip.Entries.FullName | Sort-Object) ($sourceFiles | Sort-Object)) { throw 'Unexpected source package contents.' }
    foreach ($entry in $zip.Entries) {
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $fileText = [IO.File]::ReadAllText((Join-Path $PSScriptRoot $entry.FullName))
        if ($text -ne $fileText) { throw "Source package differs: $($entry.FullName)" }
    }
} finally { $zip.Dispose() }
$artifacts += Get-FileHash -LiteralPath $sourceZip -Algorithm SHA256 | Select-Object Path,Hash
$artifacts | ForEach-Object { $_.Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($_.Path) } | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
$record = [ordered]@{
    product = "Kelly's DOORMAN"
    version = $version
    clientVersion = $version
    serverVersion = $serverVersion
    serverArtifactReused = $false
    protocol = 1
    builtUtc = [DateTime]::UtcNow.ToString('o')
    artifacts = $artifacts
    dlls = @(Get-FileHash -LiteralPath $client,$server -Algorithm SHA256 | Select-Object Path,Hash)
    gameAssemblyHash = (Get-FileHash -LiteralPath (Join-Path $GameDir 'NuclearOption_Data\Managed\Assembly-CSharp.dll') -Algorithm SHA256).Hash
    databaseImpact = 'none'
    configurationImpact = 'Legacy IDs, config filename and existing preferences retained; FavouritesOnly clears when saved count is zero; no new keys or server settings'
    regressionChecks = [int]$testMatch.Groups[1].Value
    installed = $false
    published = $false
    remotelyStaged = $false
    inGameAcceptance = 'Aaron confirmed the full 0.3.4 package works perfectly on 5 October 2026 and requested stable 1.0 publication; 1.0.0 changes plugin version labels only'
}
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $dist 'BUILD-RECORD.json') -Encoding utf8
Write-Output "Verified SOURCE package: $sourceZip"
