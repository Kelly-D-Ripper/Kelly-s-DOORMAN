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

Package the `.nobp` instead of the DLL. Supported GitHub releases can be installed when checksums and content inventory are verified. Updates and unsupported layouts need manual installation. Plugin previews should use `plugin` without `bundle` so content inherits through its wrapper.

Prefer everything inside your DLL? Embed the same files in its `.csproj`:

```xml
<ItemGroup>
  <EmbeddedResource Include="MyMod.doorman.json" LogicalName="doorman.json" />
  <EmbeddedResource Include="preview.png" LogicalName="preview.png" />
</ItemGroup>
```

Build normally and release the DLL. The JSON's `image` must match the image resource's logical name. In a single-plugin DLL, `plugin` and `name` may be omitted; DOORMAN reads its metadata.

Use PNG/JPEG, at most 12 MiB, 16 megapixels, 8192px per side. A 1024px picture is plenty. Local JSON wins over downloaded, then embedded previews. NOMNOM supplies missing cards.

NOMM-disabled packages need no extra manifest. DOORMAN reads DLL/plugin and bundle identities, keeps the package intact and restores selected prerequisites on restart. Keep dependency IDs accurate.

## Prerequisites

Optionally add this to either mod or map JSON:

```json
"requisites": [
  {
    "plugin": "com.javoski.custommaps",
    "name": "NOCustomMaps",
    "repository": "BatongMaster/NOCustomMaps"
  }
]
```

Use the required mod's BepInPlugin ID, or `bundle` for a content ID. Optional `minimumVersion` sets a numeric minimum; `asset` selects a GitHub filename/pattern. Up to 16. BepInEx hard dependencies and the NOCustomMaps requirement for `.nomap` files are automatic. Missing/disabled dependencies appear red. GitHub installs support one plugin DLL and previews, staged until restart; other packages use their release page.

## GitHub updates

Use release tags matching the actual mod version, such as `v1.2.3`. A GitHub link supplies the repository. To select a particular download, add:

```json
"update": { "repository": "you/MyMod", "asset": "MyMod_*.zip" }
```

Automatic updates replace one plugin DLL and its matching preview files after restarting. Install prerequisites separately; multi-component packages need manual installation. DOORMAN itself requires the complete CLIENT or FULL package, including both helpers.

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

Use .NET 8, Nuclear Option, BepInEx 5 and Blueprinter 2.0.1. Run:

```powershell
./Build-Package.ps1 -GameDir 'C:/path/to/Nuclear Option'
```

`-DotnetPath` selects an SDK; `-BlueprinterPath` selects a validation DLL. Packages go into `dist/1.4.0-rc.19`. NOSMR lists are reported hints, not proof. Pending assets remain diagnostics. Exact checks cover content and known gameplay DLLs; optional client UIs stay enabled. Test in game.
