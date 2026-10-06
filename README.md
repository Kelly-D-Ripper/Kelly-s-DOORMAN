# Kelly's DOORMAN

"Incorrect version" is apparently an instruction now. DOORMAN supplies the missing information.

**1.3.4 release candidate.** The published [1.0 release](https://github.com/Kelly-D-Ripper/Kelly-s-DOORMAN/releases/tag/v1.0) remains available.

Close Nuclear Option, extract **FULL** into its game folder and overwrite. Keep your config. Both helpers are required. Requires BepInEx 5 and Blueprinter 2.0.1 for content switching. FULL includes both; CLIENT omits the server companion. Upgrading an earlier test build? Remove its bundled `BepInEx/plugins/DOORMAN-Previews/aryx` folder once. Keep your actual mods.

- **Mods** on the main menu: tick your packs, press **Reload mods**, go fly.
- **Saved lists**: name your ticks, load them later. Share JSON files from `BepInEx/plugins/DOORMAN-Lists`. Organised chaos.
- **Maps**: browse `.nomap` details and previews. Folder changes appear while open; new builds need restarting.
- Supported content switches from the menus without restarting. New files and ordinary code plugin changes still need a restart. Computers remain inconvenient.
- Select a server, click **Setup mods**, then **Join server**. **Auto match mods when joining a server** does setup when you press Join.
- **Proceed** restarts with a Windows status box, waits for Steam and loading, then returns to that server. Vanilla servers leave Blueprinter out. DOORMAN and unrelated client plugins stay enabled.
- Developers can supply JSON/images or embed them in their DLL.
- No preview manifest? DOORMAN borrows available details from NOMNOM. Custom manifests win. Missing files, wrong versions and different game builds still need fixing.
- GitHub checks run on a mod's page and cache for six hours. **Update** downloads it; restart installs it with a backup.
- **Show incompatible servers**, favourites, **Only favourites**, compatibility checklists, **Copy report** and **F8** still do their jobs.

Hashed server lists need DOORMAN Server. The server DLL remains 1.0.0. Dedicated hosts can keep the existing SERVER download.

Developers: [mod previews](docs/MOD-PREVIEWS.md), [map previews](docs/MAP-PREVIEWS.md). [Saved lists](docs/MOD-LISTS.md). [Server setup](docs/SERVER-SETUP.md). [Recovery](docs/RECOVERY.md). [Testing](docs/MODS-TEST.md).
