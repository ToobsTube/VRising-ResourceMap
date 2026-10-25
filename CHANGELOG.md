# Changelog

## 1.0.0

First public release, as Prospector (files and commands use the name ResourceMap).

- Dumps every resource node on the server (ores, trees, rocks, plants) with position, what it gives, and what its spot can roll into, with odds.
- Records the game's own map projection so the viewer lines nodes up on the in-game map automatically.
- Chat commands (with VampireCommandFramework): `.rmap help`, `.rmap find`, `.rmap mark` (sets your map marker), `.rmap dump`, `.rmap reload`.
- Join message pointing players at `.rmap help`.
- Dumps run in the background without lagging the server, are written safely (never half-written files), and old dumps are cleaned up (`KeepDumps`).
- Compressed copy of each dump (`nodes-latest.json.gz`) and optional automatic posting to a Discord webhook.
- Ready-made config that is installed on first start and never overwrites existing settings.
