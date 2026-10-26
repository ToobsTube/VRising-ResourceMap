# Changelog

## 1.0.1

- `.rmap help` now follows the same permission checks as the commands themselves, including permission mods such as VRoles, so players only see commands they can really use.
- Single player / host & play with ServerLaunchFix: settings and dumps now live in the profile's normal `BepInEx` folder, so the mod manager's Config editor shows Prospector's settings. Dumps there are written compressed only (`.json.gz`), so they don't fill the editor's file list, and the config template ships as `.cfg.template` so only the real config is listed.
- The config is now called `Prospector.cfg`, so it's easy to find. An existing `community.vrising.resourcemap.cfg` is renamed automatically on first start, keeping all settings.
- README: single player / host & play, how to use Prospector with VRoles, and a note that much of the code was written with AI assistance (Claude Code).

## 1.0.0

First public release, as Prospector (files and commands use the name ResourceMap).

- Dumps every resource node on the server (ores, trees, rocks, plants) with position, what it gives, and what its spot can roll into, with odds.
- Records the game's own map projection so the viewer lines nodes up on the in-game map automatically.
- Chat commands (with VampireCommandFramework): `.rmap help`, `.rmap find`, `.rmap mark` (sets your map marker), `.rmap dump`, `.rmap reload`.
- Join message pointing players at `.rmap help`.
- Dumps run in the background without lagging the server, are written safely (never half-written files), and old dumps are cleaned up (`KeepDumps`).
- Compressed copy of each dump (`nodes-latest.json.gz`) and optional automatic posting to a Discord webhook.
- Ready-made config that is installed on first start and never overwrites existing settings.
