# Developer guide

Give your mod a description, picture and links. No DOORMAN dependency or second version number to forget.

## Mod previews

Create `MyMod.doorman.json`:

```json
{
  "plugin": "your.bepinex.plugin.id",
  "name": "My Mod",
  "author": "You",
  "description": "What it adds.",
  "image": "preview.png",
  "links": [
    { "label": "GitHub", "url": "https://github.com/you/MyMod" },
    { "label": "Discord", "url": "https://discord.gg/your-invite" }
  ]
}
```

Copy `plugin` from your `BepInPlugin` attribute. Keep it stable. DOORMAN reads the installed version itself. Author, image and links are optional.

Prefer one folder per mod:

```text
BepInEx/plugins/MyMod/
    MyMod.dll
    MyMod.doorman.json
    preview.png
```

The JSON's `image` must match the image filename, not the DLL filename. `preview.png` is perfectly fine beside `MyMod.dll`.

Release those files together as a ZIP. Flat ZIPs work; you do not need to recreate the whole `BepInEx/plugins` tree inside the download.

For a standalone `.nobp`, identify its bundle instead:

```json
{
  "bundle": "actual_internal_bundle_id",
  "name": "My Content Pack",
  "image": "preview.png"
}
```

Package the `.nobp` instead of the DLL. Its preview works; installation and updates remain manual. Ordinary plugin previews should use `plugin` without `bundle` so content inherits through its installed wrapper.

Prefer everything inside your DLL? Embed the same files in its `.csproj`:

```xml
<ItemGroup>
  <EmbeddedResource Include="MyMod.doorman.json" LogicalName="doorman.json" />
  <EmbeddedResource Include="preview.png" LogicalName="preview.png" />
</ItemGroup>
```

Build normally and release the DLL. The JSON's `image` must match the image resource's logical name. In a single-plugin DLL, `plugin` and `name` may be omitted; DOORMAN reads its metadata.

Use PNG/JPEG, at most 12 MiB, 16 megapixels and 8192 pixels per side. A 1024px picture is plenty. Local custom JSON wins over downloaded previews, then embedded previews. NOMNOM supplies missing cards.

## GitHub updates

Use release tags matching the actual mod version, such as `v1.2.3`. A GitHub link supplies the repository. To select a particular download, add:

```json
"update": { "repository": "you/MyMod", "asset": "MyMod_*.zip" }
```

Automatic updates replace one plugin DLL and its matching preview files after restarting. Extra dependencies and multi-component packages need their own installation procedure. DOORMAN itself requires the complete CLIENT or FULL package, including both helpers.

## Map previews

Keep the map's existing `map.json`, author and version. Add an optional Unity TextAsset named `doorman.json`:

```json
{
  "map": "your-existing-map-id",
  "description": "What players are about to crash into.",
  "image": "preview.png",
  "links": [
    { "label": "GitHub", "url": "https://github.com/you/YourMap" }
  ]
}
```

`map` must exactly match `mapId` in `map.json`. Import the picture as a texture with Read/Write disabled. Include both assets in your existing `AssetBundleBuild.assetNames` array before building the `.nomap`. Use a unique image filename or its full bundle asset path.

Omit `image` to use the registered chart. Without preview JSON, DOORMAN still shows the normal map details and chart. Keep `.nomap` as an ordinary AssetBundle, without ZIP wrapping or appended metadata.

Rebuilding changes its hash. Give servers and players identical files, and update mission references to the new `cm.<mapId>.<hash8>` key. A pretty picture still counts as a new build.

## Build DOORMAN

Use the .NET 8 SDK and an installed Nuclear Option with BepInEx 5 and Blueprinter 2.0.1. Download the published v1.0 SERVER ZIP first, then run:

```powershell
./Build-Package.ps1 -GameDir 'C:/path/to/Nuclear Option' -ServerArchive 'C:/downloads/KellysDOORMAN-1.0.0-SERVER.zip'
```

Use `-DotnetPath` to select an SDK executable. The script builds the client and both helpers, runs regression/game-assembly checks, reuses the hash-verified server DLL, and writes packages plus SHA256SUMS to `dist/1.3.5`. It does not install anything or restart the game. Unity rendering and real multiplayer still need in-game checks.
