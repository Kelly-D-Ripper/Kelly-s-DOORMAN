# Give your map a face

Keep your existing `map.json`, including its author and version. DOORMAN reads those already. No second version number to forget.

Add an optional `doorman.json` Unity TextAsset:

```json
{
  "map": "your-existing-map-id",
  "description": "What players are about to crash into.",
  "image": "preview.png",
  "links": [
    { "label": "GitHub", "url": "https://github.com/you/YourMap" },
    { "label": "Discord", "url": "https://discord.gg/your-invite" }
  ]
}
```

`map` must exactly match `mapId` in `map.json`. A wrong ID gets ignored, because apparently naming things is hard.

Import a PNG into the same Unity project as a texture, with Read/Write disabled. Include both `doorman.json` and that PNG in your existing `AssetBundleBuild.assetNames` array before building the `.nomap`. Use a unique filename or its full bundle asset path. A 1024px picture is plenty.

You can point `image` at your existing chart PNG instead. Omit `image` to use the map's registered chart automatically. Omit the whole preview JSON and DOORMAN still shows the normal map name, author, version and chart.

Keep `.nomap` as a normal AssetBundle. Do not wrap it in ZIP or append metadata bytes.

Rebuilding changes the map hash. Release identical files to the server and players, and update mission map references to the new `cm.<mapId>.<hash8>` key. Adding a pretty picture is still a new map build.
