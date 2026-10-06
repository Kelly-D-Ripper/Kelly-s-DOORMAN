# Saved mod lists

In **Mods**, tick the mods you want, open **Saved lists**, type a name and press **Save selection**. Save records your ticks, including changes you have not reloaded yet. Using an existing name asks before replacing its list.

Select a list and press **Load list**. DOORMAN selects its content packs and recorded plugin choices, then uses the usual Reload mods flow. Supported content reloads from the menu; plugin or unavailable content changes ask for a restart. Cancel keeps the running game as it is. Missing required mods block loading. Different installed versions or game versions ask before loading with what you actually have.

Content packs absent from the list are left out. Unlisted client plugins retain their selection. DOORMAN and Blueprinter remain protected. Dependencies are checked before changes apply. This is a single-player selection, not a way around server compatibility checks.

Saved sets retain a local receipt for previously discovered content. Unchanged installed files can be recognised even after their supporting plugin is disabled; enabling them needs a restart. For a newly imported set whose disabled content has never been discovered on this PC, enable its supporting plugins and restart once, then load the list. DOORMAN does not guess bundle IDs from filenames.

## Share or back up

List files live in:

```text
BepInEx/plugins/DOORMAN-Lists/
    My-planes-12345678.doorman-list.json
```

Send that JSON file to another player, or copy it elsewhere for backup. They put it directly in their own **DOORMAN-Lists** folder and reopen Saved lists. Names, stable mod IDs, versions and ticks travel together. Mods, configs, download links and machine paths do not. Everyone still installs their own mods.

**Delete** removes a list after confirmation and leaves a `.bak` recovery file. Replacements also keep `.bak`. To restore one, copy it back with its original `.doorman-list.json` ending, then reopen Saved lists. Keep recovered filenames distinct to avoid replacing another list.

Files are read only when you open or change lists. There is no folder watcher or gameplay scan. Invalid files are skipped with a reason in the BepInEx log. Limits: 128 lists, 512 entries per list and 64 KiB per file.
