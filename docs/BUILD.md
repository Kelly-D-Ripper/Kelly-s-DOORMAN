# Build

Install the .NET 8 SDK and Nuclear Option with BepInEx 5. Run:

```powershell
./Build-Package.ps1 -GameDir 'C:/path/to/Nuclear Option'
```

The script builds both .NET Framework 4.7.2 plugins, runs the regression/game-assembly checks, and produces FULL, CLIENT, SERVER and SOURCE ZIPs plus SHA256SUMS and a build record in `dist/`. An optional `-DotnetPath` selects a specific SDK executable. Game, Steam, Unity, BepInEx and Harmony assemblies are local build references and are excluded from downloads.

The FULL archive installs both components for a player who also hosts. The SERVER archive contains only the server plugin for dedicated hosts. The DLL names, plugin IDs, config filename and protocol remain those of JOINCHECK so upgrades do not create duplicate plugins or lose preferences. Both components report 1.0.0 internally; the GitHub release and tag are 1.0 and v1.0.

Client settings remain in `BepInEx/config/kelly.nuclearoption.joincheck.cfg`. FavouritesOnly is cleared when no favourites are saved. The server has no settings. Database impact: none. No database, mission, save, credential or other-plugin configuration migration.

The server companion retains the cached metadata implementation from Server 0.1.1. It has no gameplay/frame callback, timer, background thread or added network endpoint. Startup and metadata event work still have a cost; this is not a claim of zero overhead.

The client keeps one broad dedicated server-list request while the browser is open. Version and mission tag filtering happens locally before the game's normal server-rules/ping/full/empty/password checks. Hosted discovery remains native. Filter changes queue behind an active search. Direct joins and authentication retain their original paths. Closing the browser cancels outstanding queries and releases its request and callback.

[Steam's RefreshQuery](https://partner.steamgames.com/doc/api/ISteamMatchmakingServers#RefreshQuery) updates servers already in the list. Reopening the browser requests fresh membership. This avoids the repeated filtered-master-query failures reproduced with the installed Steamworks library.

Tests check logic and assembly contracts, not Unity rendering or real Steam multiplayer. A separate read-only probe of this release's BrowserServerList source returned 119 responding dedicated servers on each of three consecutive refreshes, then repeated successfully after disposing/reopening the session. Aaron confirmed the full 0.3.4 package works perfectly on 5 October 2026 and requested stable 1.0 publication. The 1.0.0 code changes only plugin version labels. [The game checklist](IN-GAME-TEST.md) remains available for future regression testing; its individual cases are not presented as separately recorded results. Rollback uses the retained previous client/server DLLs outside this source repository.
