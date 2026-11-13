function Use-UnchangedComponents {
    param([string]$Root,[string]$PackageDirectory,[string]$GameDir,[switch]$RebuildStartup)
    $record = Get-Content -LiteralPath (Join-Path $PackageDirectory 'BUILD-RECORD.json') -Raw | ConvertFrom-Json
    foreach ($check in @(@{File='Assembly-CSharp.dll';Hash=$record.gameAssemblyHash},@{File='UnityEngine.CoreModule.dll';Hash=$record.gameCoreAssemblyHash})) {
        if ((Get-FileHash -LiteralPath (Join-Path $GameDir ('NuclearOption_Data\Managed\'+$check.File))).Hash -ne $check.Hash) { throw 'Cannot reuse components built for different game assemblies.' }
    }
    $version = [string]$record.packageVersion
    if ($version -notmatch '^\d+\.\d+\.\d+(?:-rc\.\d+)?$') { throw 'Invalid baseline package version.' }
    $archives = @{}
    try {
        foreach ($kind in @('FULL','SOURCE')) {
            $name = "KellysDOORMAN-$version-$kind.zip"
            $matches = @($record.artifacts | Where-Object { [IO.Path]::GetFileName($_.Path) -eq $name })
            $path = Join-Path $PackageDirectory $name
            if ($matches.Count -ne 1 -or (Get-FileHash -LiteralPath $path).Hash -ne $matches[0].Hash) { throw "Baseline archive checksum differs: $name" }
            $archives[$kind] = [IO.Compression.ZipFile]::OpenRead($path)
        }
        $components = @(
            @{Project='KellysDOORMAN.csproj'; Server=$true; Entry='BepInEx/plugins/KellysJOINCHECKServer.dll'; Target='bin/Release/KellysJOINCHECKServer/net472/KellysJOINCHECKServer.dll'},
            @{Project='startup/DoormanStartup.csproj'; Entry='BepInEx/patchers/KellysDOORMANStartup.dll'; Target='startup/bin/Release/net472/KellysDOORMANStartup.dll'},
            @{Project='restart/RestartHelper.csproj'; Entry='BepInEx/DOORMAN/KellysDOORMANRestart.exe'; Target='restart/bin/Release/net472/KellysDOORMANRestart.exe'}
        )
        if ($RebuildStartup) { $components = @($components | Where-Object { $_.Project -ne 'startup/DoormanStartup.csproj' }) }
        $basis = [IO.Path]::GetFullPath($Root).TrimEnd('\')+'\'
        foreach ($component in $components) {
            $project = Join-Path $Root $component.Project
            $directory = [IO.Path]::GetDirectoryName($project)
            [xml]$xml = Get-Content -LiteralPath $project -Raw
            $inputs = @($project)
            if (!$component.Server) {
                $inputs += @(Get-ChildItem -LiteralPath $directory -Filter '*.cs' -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | ForEach-Object { $_.FullName })
                $prefix = $directory.Substring($basis.Length).Replace('\','/')+'/'
                $inputs += @($archives.SOURCE.Entries | Where-Object { $_.FullName.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -and $_.FullName.EndsWith('.cs',[StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { Join-Path $Root $_.FullName })
            }
            foreach ($compile in $xml.Project.ItemGroup.Compile) {
                if (!$compile.Include -or ($component.Server -and $compile.Condition -match '!=')) { continue }
                if ($compile.Include -match '[*?]') { throw 'Reuse requires explicit source files.' }
                $inputs += [IO.Path]::GetFullPath((Join-Path $directory $compile.Include))
            }
            foreach ($input in ($inputs | Sort-Object -Unique)) {
                if (!$input.StartsWith($basis,[StringComparison]::OrdinalIgnoreCase)) { throw 'Component source is outside the candidate.' }
                $relative = $input.Substring($basis.Length).Replace('\','/')
                $entry = $archives.SOURCE.GetEntry($relative)
                if (!$entry) { throw "Baseline source is missing: $relative" }
                $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
                try { $hash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') } finally { $stream.Dispose();$sha.Dispose() }
                if ((Get-FileHash -LiteralPath $input).Hash -ne $hash) { throw "Component source changed; rebuild and ship its new binary: $relative" }
            }
        }
        # Source checks for all components finish before copying any of them.
        foreach ($component in $components) {
            $entry=$archives.FULL.GetEntry($component.Entry)
            if (!$entry) { throw "Baseline component is missing: $($component.Entry)" }
            $target=Join-Path $Root $component.Target
            $input=$entry.Open();$output=[IO.File]::Create($target)
            try { $input.CopyTo($output);$output.Flush($true) } finally { $input.Dispose();$output.Dispose() }
            [PSCustomObject]@{ Component=$component.Entry; BaselineVersion=$version; SHA256=(Get-FileHash -LiteralPath $target).Hash }
        }
    } finally { foreach ($archive in $archives.Values) { $archive.Dispose() } }
}
