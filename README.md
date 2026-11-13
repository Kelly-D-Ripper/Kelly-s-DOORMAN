# Kelly's DOORMAN 1.4.0-rc.36

A server browser and mod manager for Nuclear Option. Because "incorrect version" is a diagnosis.

## Install

1. Install BepInEx 5 and Blueprinter. Neither is included.
2. Close the game. Extract this ZIP into your Nuclear Option folder and replace old DOORMAN files. Keep your configs.
3. Launch through Steam. Remove duplicate JOINCHECK DLLs. Keep both helpers.

FULL includes client and hosting support. CLIENT is for players.

## Use

- **Mods** manages maps, saved lists, updates and prerequisites. **Install mod / map** accepts supported GitHub links.
- Select mods and choose **Delete addon** for each. Restart once to remove the confirmed files. Settings and saved update backups are kept.
- Select a mod and press **Versions** to restore a saved copy.
- The main menu flags available updates, with a button for DOORMAN. Updates that change its helpers need a manual install.
- Maps lists NOCustomMaps and NOMapLoader files. **Setup mods** can fetch supported maps and loaders, including Wake and Ironjaw; unfamiliar packages may need manual installation. Ironjaw's server map name does not identify its version.
- Show incompatible servers, save favourites and use **Setup mods** before joining. Supported mods download automatically. Proceed to restart; existing files are backed up.
- **Setup mods** also uses NOSMR's reported gameplay list, which may be outdated and cannot verify exact files. Optional utilities stay untouched.
- **Copy report** supplies the host with mod and download details. Exact file checks need a compatible DOORMAN server. Loading errors still matter.
- Press F8 for the last checklist. Saved lists are in `BepInEx/plugins/DOORMAN-Lists`.

Restart blocked? Choose **Restart manually**, then launch through Steam once it shows Play.

On Proton, choose **Restart manually** and reopen through Steam. Proton support is experimental.

[Recovery](docs/RECOVERY.md) | [Modder instructions](docs/DEVELOPER.md)
