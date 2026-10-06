# DOORMAN 1.3.4 release audit

Scope: client, startup and restart helper 1.3.4. The SERVER ZIP and DLL remain the exact published 1.0.0 bytes. No game/server installation or restart is performed by the audit.

Aaron confirmed the 1.3.3 FULL package worked in game, including saved lists, Maps, scrollbars, a mod update and restarting back to a server, on 6 October 2026.

The final audit repairs:

- Retained update history cannot consume active-queue capacity or hide new updates. Pending work is limited explicitly; backups remain.
- JSON writes validate adjacent temporary paths. Cached NOMNOM images are validated and corrupt entries recover through atomic replacement.
- Startup dependency drift keeps the disabled-plugin exclusions. Native BepInEx still handles missing hard dependencies.
- Repeating server setup retains the normal-selection restoration flag.
- A cancelled or failed restart leaves the previous normal profile and server-return ticket intact; the selection commits after helper readiness.
- Imported absent-disabled content IDs survive blocked or cancelled apply attempts. Server inventory rejects stale source metadata except verified queued updates.
- New mod/map selections start at the top; async redraws retain position. Long Mods status messages have a scrollbar.
- A capped map registry reports incomplete state instead of claiming omitted maps failed to load.

Build-Package.ps1 records the completed regression count, game/loader/binary hashes and per-member ZIP verification in dist/1.3.4/BUILD-RECORD.json. SOURCE is rebuilt separately for portability. Symlink-dependent checks may be skipped when Windows cannot create their fixtures; test output records that limitation.

Before publishing, smoke-test the exact 1.3.4 FULL ZIP:

1. Open Mods and Multiplayer. Check preview changes and the new status scrollbar at your usual resolution.
2. Change a plugin selection, request restart, cancel, then reopen Mods and apply again. Check that the intended selection survives and no restart occurs on cancellation.
3. Setup the same server twice, then return to the main menu. Check that the normal content set returns. Perform one restart/setup/return and actual Join.
4. With the game closed, follow RECOVERY.md using disposable selections. Confirm previously disabled packs return and favourites/saved lists remain.

These are final-fix runtime gates, not claimed results. Longer multiplayer, scaling and performance stress cases remain in MODS-TEST.md. No numerical zero-overhead claim is made.

Database impact: none. Configuration impact: existing configs, favourites and saved lists preserved; normal selection, temporary server ticket, caches and update receipts retain their documented formats. Recovery resets only the two explicitly named active-selection JSON files. No server settings change.
