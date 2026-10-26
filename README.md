# Prospector for V Rising

A **server plugin** (BepInEx) that knows where the resources really are on your V Rising server: every ore vein, tree, rock and plant, what each spot can turn into, and **the odds for each spot**. Community maps tell you where sulfur *can* appear. Prospector tells you that a given spot is 2% sulfur and 91% stone, and which spots are worth the trip.

It runs on the server, so whoever runs the server installs it. Players then use it through chat commands, and through map dumps the server owner shares.

- **Chat commands:** `.rmap find copper` gives the nearest copper around you. `.rmap mark copper` puts your map marker on it, so the game draws a path there.
- **Map viewer:** open a dump at **https://toobstube.com/vrising-map/** (or `viewer/index.html` from this repo). Pick a resource and see where it is now (dots) and where it can roll (rings, bigger = more likely).
- **Sharing:** dumps can be posted to a Discord channel automatically, which also works on hosted servers with no ports or FTP.

Files and commands use the name *ResourceMap* (`ResourceMap.dll`, `.rmap`).

> **Made with AI assistance:** much of the code and documentation was written with Claude Code (Anthropic's AI coding tool), directed and tested by the author on a live V Rising server.

## Install

Needs [BepInExPack V Rising](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/) on the **dedicated server**. For the chat commands, also install [VampireCommandFramework](https://thunderstore.io/c/v-rising/p/deca/VampireCommandFramework/) (optional: without it, everything except the commands still works).

**Manual (GitHub / Nexus zip):** extract the zip into the server's main folder (the one with `VRisingServer.exe`). It contains:

```
BepInEx/plugins/ResourceMap/ResourceMap.dll
BepInEx/plugins/ResourceMap/Prospector.cfg.template
```

**Thunderstore / r2modman:** install ResourceMap for your server profile. Its dependencies are installed for you.

**Single player / host & play:** these games run the dedicated server in the background, so Prospector works there too. With the Thunderstore app or r2modman, install [ServerLaunchFix](https://thunderstore.io/c/v-rising/p/Mythic/ServerLaunchFix/) alongside Prospector and VampireCommandFramework, so the background server loads your mods. The first launch can time out while BepInEx builds its files; just try again. Notes:

- In single player you're not admin by default. Use `adminauth` in the console, or set `FindAdminOnly = false`.
- Prospector's settings show up in the mod manager's **Config editor** after your first single-player start: edit **`BepInEx/config/Prospector.cfg`**.
- Dumps go to `BepInEx/ResourceMap` in your profile folder (*Settings → Browse profile folder*), as compressed `.json.gz` files only, so they don't clutter the Config editor. The viewer opens them directly.

On the first start, the bundled template (`Prospector.cfg.template`, next to the DLL) is copied to `BepInEx/config/Prospector.cfg`. Edit the template **before** the first start if you want your settings from the beginning; after that, edit the file in `BepInEx/config`. Updates never overwrite your settings: the template is only used when no config exists yet. (Before 1.0.1 the config was called `community.vrising.resourcemap.cfg`; it's renamed to `Prospector.cfg` automatically, with your settings.)

About a minute after the world loads, the server log shows `Wrote N resource nodes to …/BepInEx/ResourceMap/nodes-….json`. That file (or `nodes-latest.json.gz`, the compressed copy) is what you open in the viewer.

> **Tip (Windows servers):** don't leave the server's console window in "Select" mode (its title starts with "Select" after you click in it). Windows pauses the server while it's in that mode, which looks like lag or connection drops. Press Esc to resume, or turn off *QuickEdit Mode* in the console's Defaults.

## Chat commands

Typed in normal game chat. They need VampireCommandFramework on the server. Every player can type `.rmap help` to see the commands *they* can use with the server's current settings. Players are told about it when they join; see `WelcomeMessage`.

| Command | Who | What it does |
|---|---|---|
| `.rmap help` | everyone | Lists the ResourceMap commands you can use. |
| `.rmap find <resource> [range]` | admins, or everyone if `FindAdminOnly = false` | Nearest harvestable node of that kind around you, with distance, direction and coordinates. Matches names like `copper`, `sulfur`, `iron`, `ghost crystal`, `stone`, `blood rose`. |
| `.rmap mark <resource> [range]` | same as `find` | Like `find`, then puts your map marker on that node, so the game draws its guide path to it. Replaces any marker you had. |
| `.rmap dump` | admins | Write a fresh dump now. It runs in the background without lagging the server, and you get a message when it's done. |
| `.rmap reload` | admins | Reload the config file without restarting. |

### Using with VRoles (or another permission mod)

[VRoles](https://thunderstore.io/c/v-rising/p/odjit/VRoles/) lets you give commands to roles instead of just admins. To let only a role (say `trusted`) use `find` and `mark`:

1. Set `FindAdminOnly = false` in the config, so Prospector's own setting doesn't block them.
2. In game, as admin:
   ```
   .role disallow rmap.find
   .role disallow rmap.mark
   .role allow rmap.find trusted
   .role allow rmap.mark trusted
   ```

`.rmap help` follows the same permission checks as the commands themselves, so each player only sees what they can really use, with VRoles or without it. VRoles is optional; Prospector doesn't need it.

The server pauses areas with no players nearby, and spots there only catch up (and may re-roll into something else) when someone gets close. So `find` and the "there now" view are most accurate around players.

## Settings

`BepInEx/config/Prospector.cfg`. Apply changes with `.rmap reload` (everything except `DelaySeconds`, which only matters at server start).

| Setting | Default | What it does |
|---|---|---|
| `Commands.WelcomeMessage` | Type .rmap help to see the resource map commands. | Private message each player gets about 10 seconds after joining. Empty = off. Only sent when the chat commands are available. |
| `Commands.FindEnabled` | true | Allow `.rmap find` and `.rmap mark`. |
| `Commands.FindAdminOnly` | true | Only admins can use `find`/`mark`. Set to false to let every player use them. |
| `Commands.FindMaxRange` | 300 | How far (meters) `find`/`mark` search around the player. 0 = no limit. |
| `Commands.FindCooldownSeconds` | 10 | Wait between searches for non-admins. |
| `Discord.WebhookUrl` | *(empty)* | Discord webhook link. When set, each dump's compressed copy is posted to that channel (see below). |
| `Discord.PostAutomaticDumps` | true | Also post startup and `RepeatMinutes` dumps. When false, only `.rmap dump` posts. |
| `Dump.DelaySeconds` | 60 | Wait this long after the world loads before the first dump. |
| `Dump.RepeatMinutes` | 0 | Dump again every N minutes. 0 = once per server start (plus `.rmap dump`). |
| `Dump.IncludeDisabled` | true | Include nodes the game has switched off for now, e.g. in an inactive area (flagged as `disabled`). |
| `Output.Folder` | *(empty)* | Where dumps are written. Empty = `BepInEx/ResourceMap`. A network share works, written as `//nas/ResourceMap` (forward slashes are safest in BepInEx config files). |
| `Output.KeepDumps` | 10 | Keep only the newest N timestamped dumps; older ones are deleted after each dump. 0 = keep all. `nodes-latest.json` and `.gz` are always kept. |

**Network share:** the Windows account that runs the server needs to be able to log in to it. Store that login once with `cmdkey /add:NAS-NAME /user:USERNAME /pass` while signed in as that account, and check it with `dir \\NAS-NAME\ShareName`.

## Sharing dumps (Discord)

Every dump also writes `nodes-latest.json.gz`, a compressed copy about 1/15 the size (roughly 4 MB). The viewer opens it directly, so it's the file to share.

To have the server post it to a Discord channel automatically:

1. In Discord, open the channel's settings: *Integrations → Webhooks → New Webhook → Copy Webhook URL*. This needs the "Manage Webhooks" permission.
2. Paste it into `Discord.WebhookUrl`, then run `.rmap reload`.

Players download the newest file from the channel and drop it into the viewer. They don't need access to the server or its files. Treat the webhook link like a password: anyone who has it can post to that channel. If it leaks, delete the webhook in Discord and make a new one.

## Using the viewer

Open **https://toobstube.com/vrising-map/**, or `viewer/index.html` from this repo. Load a dump (or drag it onto the map). The file is read in your browser and never uploaded anywhere.

- **Find:** pick a resource. Filled dots are where it is **now**. Rings are spots that **can roll** it, and bigger, brighter rings are more likely. Hover a spot for its exact odds and what's there now.
- **Chance filter:** hide the long shots ("25% or better", "50% or better", ...) to see the spots worth farming.
- **More options:** compare two dumps, browse every node by type, choose a map image.

The hosted viewer includes the game's world map. To run `viewer/index.html` locally with the map, extract it once from your own game install:

```bash
pip install UnityPy
python tools/extract_map.py "C:/Program Files (x86)/Steam/steamapps/common/VRising"
```

That writes `viewer/map.jpg`, which the viewer loads and lines up automatically using the map projection stored in each dump.

## Building from source

Needs the .NET SDK (6 or newer).

1. Copy the `BepInEx/interop` folder from a server running the target game version into `lib/interop/` (so the DLLs end up in `lib/interop/interop/`). They are generated from the game, so they always match its version. They are game code, so they're not in this repo.
2. `dotnet build plugin -c Release`. The DLL is in `plugin/bin/Release/net6.0/ResourceMap.dll`.
3. `python tools/make_release.py` builds the release zips into `dist/`.

To build against a different interop folder: `dotnet build plugin -c Release -p:InteropDir=path\to\interop`.

## Dump format

```json
{
  "plugin": "ResourceMap 1.0.0",
  "gameVersion": "...",
  "dumpedAt": "2026-10-21T05:00:00-05:00",
  "count": 12345,
  "nodes": [
    { "prefab": "...", "guid": 123, "x": -1500.2, "y": 0, "z": -900.4,
      "source": "randomizedSpawnChain", "disabled": true,
      "spot": 4521, "chain": 3, "stage": 0, "spawner": 1, "spawnerSpot": 8870, "changeIn": -3600,
      "yields": [ { "item": "Item_Ingredient_Mineral_CopperOre", "amount": 20 } ] }
  ],
  "mapZones": [
    { "mapType": "MainMap", "startChunk": [2, 5], "chunkSize": [19, 19],
      "projection": [[0.000658, 0, 0, 0], [0, 0, -0.02, 0], [0, 0.000658, 0, 0], [0.8947, 0.5789, -1, 1]] }
  ],
  "chains": [
    { "name": "...", "main": 0, "stages": [ { "name": "Main", "prefab": "..." }, { "name": "Depleted", "afterSeconds": 1800 } ] }
  ],
  "spawners": [
    { "name": "...", "options": [ { "chainName": "...", "chain": 3, "chance": 0.6 }, { "chainName": "...", "chain": 4, "chance": 0.4 } ] }
  ]
}
```

`x`/`z` are the horizontal world coordinates (north is +z), `y` is height. `chain` and `spawner` index into the `chains` and `spawners` lists; `spot` / `spawnerSpot` identify the physical spot. `changeIn` is seconds until the spot's next automatic change (negative = overdue, waiting for a player to come near). `mapZones` is the game's world-to-map projection (column-major 4×4; texture coordinates run -1..1, +z up). Fields are left out when they don't apply.

## License

MIT. See [LICENSE](LICENSE). V Rising and its art belong to Stunlock Studios; this project isn't affiliated with them.
