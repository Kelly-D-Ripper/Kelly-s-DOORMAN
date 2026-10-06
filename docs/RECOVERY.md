# Break glass, get your mods back

Missing Mods button? Stuck with a server's mod selection? Close Nuclear Option completely, including any DOORMAN restart/status window.

In your game's `BepInEx/config` folder, rename these files if present:

- `doorman-mods.json` to `doorman-mods.json.disabled`
- `doorman-server-setup.json` to `doorman-server-setup.json.disabled`

Deleting both works too. Renaming keeps a rollback copy. Start the game normally through Steam. DOORMAN now has no saved exclusions or server-return selection, so it allows all installed plugins and content packs. It never moved or deleted their DLLs or `.nobp` files. The `.bak` files are not loaded automatically.

Keep `kelly.nuclearoption.joincheck.cfg`, your other mod configs, maps and `BepInEx/plugins/DOORMAN-Lists`. This reset clears DOORMAN's active selection and Auto match preference, not your favourites or saved lists. Mods disabled by another manager, missing files, broken DLLs and missing dependencies still need their own fixes.

Still cannot boot? With the game closed, move `BepInEx/patchers/KellysDOORMANStartup.dll` and `BepInEx/plugins/KellysJOINCHECK.dll` outside BepInEx, then start normally. This bypasses DOORMAN while retaining a copy. Queued DOORMAN updates will wait until its startup helper is restored. Keep the BepInEx log for diagnosis. Reinstall the complete CLIENT or FULL package when ready.

Apparently even the doorman needs an emergency exit.
