# Give your mod a face

Write this once. Reuse it every release. No DOORMAN dependency, version field or paperwork subscription.

Create `MyMod.doorman.json` and a PNG/JPEG:

```json
{
  "plugin": "your.bepinex.plugin.id",
  "name": "My Mod",
  "description": "What it adds.",
  "image": "MyMod.png",
  "links": [
    { "label": "GitHub", "url": "https://github.com/you/MyMod" },
    { "label": "Discord", "url": "https://discord.gg/your-invite" }
  ]
}
```

Copy `plugin` from your `BepInPlugin` attribute. `author` is optional. Keep that ID stable; DOORMAN reads the installed version itself.

## Option 1: JSON and image beside the mod

Put these in one folder under `BepInEx/plugins`:
```text
MyMod.dll
MyMod.doorman.json
MyMod.png
```
Make sure to match the .png file name with the line "image": "MyMod.png" in the JSON

Release those three files as one ZIP. For automatic updates, use only the plugin ID, without a bundle field. No nested BepInEx folder structure is required. DOORMAN's updater carries the matching JSON and image through the restart.

For a standalone `.nobp`, replace `plugin` with "bundle": "actual_internal_bundle_id"` and package the bundle instead of the DLL. Its preview works; its update installation remains manual.
#
# To avoid clutter i recommend releasing your mods in a folder; so the BepInEx/Plugins folder contains one folder per mod:

BepInEx/plugins/FS41/
    FS41.dll
    FS41.doorman.json
    preview.png

## Option 2: everything inside the DLL

Use the same JSON/image. Add this inside your `.csproj`:

```xml
<ItemGroup>
  <EmbeddedResource Include="MyMod.doorman.json" LogicalName="doorman.json" />
  <EmbeddedResource Include="MyMod.png" LogicalName="MyMod.png" />
</ItemGroup>
```

Build normally. Upload just the DLL. Future builds automatically include the preview. In a single-plugin DLL, `plugin` and `name` may be omitted; DOORMAN reads them from its metadata.

Images: PNG/JPEG, maximum 12 MiB and 16 megapixels. Links and images are optional. Local custom JSON overrides downloaded previews, which override embedded previews; NOMNOM supplies missing cards.

Use release tags matching your actual mod version, such as `v1.2.3`. If a release has several downloads, add `"update": { "repository": "you/MyMod", "asset": "MyMod_*.dll" }` to select one. Edit your preview only when its details change.
