# Kelly's DOORMAN

Nuclear Option says "Incorrect version" and leaves you to develop psychic powers. DOORMAN tells you what's missing, wrong or extra, and helps you sort it out.

Apparently that needed a mod.

[Download DOORMAN](https://github.com/Kelly-D-Ripper/Kelly-s-DOORMAN/releases)

## What does it do?

### Find a server

- **Check mods:** See which mods you need to install, change or remove before joining. Failed joins get a report too.
- **Show incompatible servers:** Find servers the regular browser hides, including dedicated servers. Other browser filters still apply.
- **Favourites:** Star your usual servers and use **Only favourites** to find them again.
- **Copy report:** Send the host a readable report when things go wrong. Press **F8** to reopen the last checklist.

### Manage your mods

- **Mods menu:** Enable and disable installed mods without rummaging through folders.
- **Setup mods:** Select a server and let DOORMAN prepare a matching set from the mods you already have.
- **Restart and return:** If a restart is needed, DOORMAN can bring you back to the selected server page once the game has finished loading.
- **Saved mod lists:** Save sets for single player or different servers. Share the list files from `BepInEx/plugins/DOORMAN-Lists`.
- **Maps:** Browse installed custom maps and their previews.
- **Previews and prerequisites:** See mod descriptions, images and links, plus warnings when something a mod needs is missing.
- **GitHub updates:** Check supported mods for updates and install them when available.
- **Install mod:** Paste a GitHub repository, release or release-asset link into the Mods menu. DOORMAN will download and install it when the package can be verified and handled safely.

### Server compatibility

- Servers running **DOORMAN's server companion** can provide extra compatibility information, including exact file checks for supported gameplay mods.
- Servers using **NOSMR** can provide a reported modpack list. DOORMAN can download supported versions from that list and give the host a useful report if something doesn't add up.
- Optional client UI mods are kept outside the known gameplay checks.

A NOSMR list is supplied by the host and may be out of date. If it says one thing and the server is running another, someone has a little admin to do.

## Install

1. Install **BepInEx 5** and **Blueprinter** separately.
2. Close Nuclear Option.
3. Download and extract the **FULL** ZIP into your Nuclear Option folder. Replace old DOORMAN files and keep your configs.
4. Start the game through Steam.

**FULL** includes the client and hosting companion. You can have both installed if you join other servers and occasionally host your own.

## A few things worth knowing

- Some mod changes need a restart. DOORMAN will tell you when.
- GitHub installs need a supported package and a SHA-256 checksum from GitHub. If a release has several downloads, paste the link to the specific file.
- Complex packages and downloads without a checksum need manual installation. DOORMAN does not automatically overwrite an existing mod.
- Showing an incompatible server doesn't magically make you compatible. Nice try.
- If the restart helper is blocked, choose **Restart manually**, wait for Steam to show **Play**, then start the game normally.
- If the Mods button disappears or you need your original selection back, see the **RECOVERY** guide included in the download.

## For modders

The download includes a **DEVELOPER** guide for adding descriptions, images, links and prerequisites to mods and maps. DOORMAN can also use available NOMNOM information when a mod doesn't supply its own preview.
