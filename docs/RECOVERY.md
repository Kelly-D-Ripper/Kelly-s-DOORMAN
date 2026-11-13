# Break glass, get your mods back

Missing Mods button? Stuck with a server's mod selection? Close Nuclear Option completely. If a DOORMAN restart is still running, end `KellysDOORMANRestart.exe` in Task Manager first. Clicking the status window's X only hides it. Alternatively, let the restart finish, then close the game.

In your game's `BepInEx/config` folder, rename these files if present:

- `doorman-mods.json` to `doorman-mods.json.disabled`
- `doorman-server-setup.json` to `doorman-server-setup.json.disabled`
- `doorman-disabled-restore.json` to `doorman-disabled-restore.json.disabled`

Deleting these works too. Renaming keeps rollback copies. Start normally through Steam. DOORMAN now allows all installed plugins/content and cancels its server return and pending NOMM restoration. The `.bak` files are not loaded automatically.

Keep `kelly.nuclearoption.joincheck.cfg`, other mod configs, maps and `BepInEx/plugins/DOORMAN-Lists`. This resets DOORMAN's active selection and Auto match preference, keeping favourites and saved lists.

DOORMAN's own disabling leaves files in place. The separate **Delete addon** action permanently removes only the confirmed files on the next startup. Settings and saved update backups are kept. NOMM moves disabled mods to `BepInEx/disabledPlugins`; deleting a selection cannot return those files. Enable them in NOMM with the game closed. DOORMAN can discover supported disabled packages and restore required ones intact on an agreed restart. Conflicting destinations or changed bytes block the move. Close NOMM during DOORMAN setup/restart.

Changed your mind about a deletion before restarting? With the game closed, rename `BepInEx/cache/doorman-removal.json` if its `State` is `pending`. If its state is `moving` or `finalising`, leave the receipt and `BepInEx/cache/DOORMAN-removal-files` alone and ask for help; they are needed to finish an interrupted deletion.

Still cannot boot? With the game closed, move `BepInEx/patchers/KellysDOORMANStartup.dll` and `BepInEx/plugins/KellysJOINCHECK.dll` outside BepInEx, then start normally. This bypasses DOORMAN while retaining a copy. Queued DOORMAN updates will wait until its startup helper is restored. Keep the BepInEx log for diagnosis. Reinstall the complete CLIENT or FULL package when ready.

Problem with a DOORMAN self-update? Close the game, rename `BepInEx/cache/DOORMAN-self-update` to `DOORMAN-self-update.disabled`, then install the complete CLIENT or FULL package. The renamed folder keeps the previous client backup if one was made.

Map update problem? DOORMAN keeps previous map files and receipts under `BepInEx/cache/DOORMAN-map-updates` and Ironjaw files under `BepInEx/cache/DOORMAN-ironjaw-updates`. Wake Flight Support backups are under `BepInEx/cache/DOORMAN-updates`. Keep these folders and ask for help with the receipts before moving files; they record the original names and checksums.

Apparently even the doorman needs an emergency exit.

Windows showing an unrecognised-app warning for the restart helper? Close that prompt and choose **Restart manually** in DOORMAN. It verifies and saves your setup, then closes Nuclear Option without running the EXE. Wait for Steam to show **Play**, then start normally. Mods apply during startup and a saved server page returns after loading. Windows protection stays enabled.

Game still opens? After a setup or restart error, press **Copy report** on the server checklist and paste it with your bug report. F8 reopens the last checklist. It includes the latest failure, loaded mod versions, disabled selections, queued updates and recent DOORMAN events. Local user-folder paths are masked; passwords and full configs are not copied. A short restart-helper log excerpt survives restarting. Other event history lasts for the current game session.
