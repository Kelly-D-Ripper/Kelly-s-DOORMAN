# Kelly's DOORMAN

"Incorrect version" is apparently an instruction now. DOORMAN tells you what needs fixing.

## Install

1. Install BepInEx 5. Content switching supports Blueprinter 2.0.1; neither is bundled.
2. Download **FULL** from [Releases](https://github.com/Kelly-D-Ripper/Kelly-s-DOORMAN/releases).
3. Close Nuclear Option. Extract into its game folder, overwrite the old DOORMAN files and restart. Keep your config.

Install the whole package, including the startup and Windows restart helpers. Half a mod is still half a mod. Remove old JOINCHECK copies from other folders if upgrading.

**CLIENT** is enough for joining. **FULL** also includes the hosting companion. Dedicated hosts use **SERVER 1.0.0**, which is unchanged. You can keep both installed.

## Use

- Open **Mods** from the main menu to enable/disable mods, save lists, view maps and check for GitHub updates.
- Press **Reload mods** to apply supported content changes. Code changes need a restart.
- Pick a server, press **Setup mods**, then **Join server**. Setup uses installed mods; missing or incorrect versions are listed. **Proceed** restarts when needed and returns to that server page.
- Use **Show incompatible servers**, favourites and **Only favourites** in the browser. F8 reopens the last diagnosis; **Copy report** shares it.
- Share saved-list JSON files from `BepInEx/plugins/DOORMAN-Lists`. Copy received files there and reopen Saved lists. Organisation, tragically.

Something broke? [Recovery](docs/RECOVERY.md). Making a mod? [Developer instructions](docs/DEVELOPER.md).
