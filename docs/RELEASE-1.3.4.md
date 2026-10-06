"Incorrect version" remains a rubbish instruction. DOORMAN now helps you fix it, because apparently reading a mod list was too much admin.

- **Native Mods menu:** enable or disable installed mods. **Reload mods** switches supported content from the menus; code changes ask for a restart.
- **Setup mods, then Join server:** match the selected server using mods already installed. **Auto match mods when joining a server** does the setup when you press Join. Missing mods and wrong versions are explained, rather than guessed away.
- **Restart and return:** **Proceed** configures the selection, shows a Windows status box, waits for Steam and game loading, then brings you back to the selected server page. DOORMAN and unrelated client plugins stay enabled.
- **Saved lists:** save sets for single player or different servers. Share their JSON files from `BepInEx/plugins/DOORMAN-Lists`. Organisation, tragically.
- **Mod pages:** descriptions, previews, GitHub and social links. Developers can use a tiny JSON/image pair or embed them in a DLL. Available NOMNOM details fill in when no preview is supplied.
- **GitHub updates:** cached checks when you open a mod page. Supported DLL updates download on click and install on restart, keeping backups. Complex packages and loaders use the release page.
- **Maps tab:** browse installed `.nomap` details, charts and optional embedded previews. Folder changes appear while the tab is open; new map builds need a restart.
- **Browser tools:** show incompatible hosted and dedicated servers, favourites first, Only favourites, readable mismatch checklists, Copy report and F8 to reopen the last diagnosis.
- **Visible scrollbars:** lists, previews, reports and status messages. No more guessing whether there is another paragraph hiding downstairs.
- **Recovery:** reset two local JSON files to restore everything DOORMAN disabled, even if its menu disappears. See [RECOVERY.md](https://github.com/Kelly-D-Ripper/Kelly-s-DOORMAN/blob/codex/release-1.3.4/docs/RECOVERY.md).

**Most players want FULL.** Close the game, extract it into your Nuclear Option folder, overwrite the old files and restart. Keep your config. Install the entire package so the client, startup helper and Windows restart helper match. Requires BepInEx 5; content switching supports Blueprinter 2.0.1. Upgrading an old test build? Remove only its old `BepInEx/plugins/DOORMAN-Previews/aryx` sample folder.

CLIENT is enough for joining. FULL also includes the hosting companion. Dedicated hosts can keep SERVER 1.0.0; it is unchanged. Exact lists behind hashed server versions need that companion. Showing a server does not override its compatibility or password checks. Update checks and map monitoring run when their pages are used, with no new server gameplay loop.

SOURCE contains buildable sources and checks. SHA256SUMS contains the download hashes. No database migration or server configuration change.
