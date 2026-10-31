# QudBridge

A [Caves of Qud](https://www.cavesofqud.com/) mod that exposes the game over a local HTTP API, so
an external program, such as an LLM agent, can read what the player sees and play the game.

It only reports what the player can see: the map within sight (and remembered terrain), visible
creatures and items, inventory, messages, popups and menus. Actions go through the game's own
commands and screens, so they take time and get refused the same way they would for a player.

```
$ curl -s http://127.0.0.1:15600/state?view=4
{"screen":"Stage","turn":55,"player":{"name":"...","hp":42,"maxHp":42,"level":5, ...},
 "map":{"w":80,"h":25,"x0":7,"y0":18,"rows":["..&..a...",...]},
 "creatures":[{"key":"a","name":"Issachari rifler","x":11,"y":23,"attitude":"neutral", ...}], ...}

$ curl -s -X POST http://127.0.0.1:15600/action -d '{"type":"move","dir":"N"}'
{"result":"ok","log":[...],"state":{...}}
```

## What it covers

- **State** (`GET /state`): player stats and effects, a symbol map of the zone (or the world map),
  creatures, items and features, inventory and equipment with stable item ids, recent messages.
- **Actions** (`POST /action`): move, attack, fire, throw, reload, abilities, wait, rest, travel,
  auto-explore, stairs, items (eat, drink, equip, apply and any other inventory action), pick up,
  take from containers, talk, look, tinker, and spending skill, attribute and mutation points.
- **Prompts**: popups, conversations, target and direction pickers, item lists, trade and container
  screens, books, terminals, number input and game over. They come back as a `prompt` and are
  answered with `choose`.
- **Character** (`GET /character`, `GET /tinkering`): attributes, mutations, the skill tree with
  costs and requirements, bits and known recipes.

An action returns once the game has settled: the player's next turn, a prompt that needs an answer,
or a screen the API can't read. The full reference, with examples, is in [API.md](API.md).

## Install

1. Copy the contents of `src/` into a folder named `QudBridge` in the game's Mods folder
   (on Windows: `%USERPROFILE%\AppData\LocalLow\Freehold Games\CavesOfQud\Mods\QudBridge\`).
   From WSL, `./deploy.sh` does this (edit `MODS` in it for your Windows user name).
2. Start the game and enable the mod. The game compiles mods at launch, so restart it after
   every change. Compile errors show up in `Player.log` as `MODERROR`.
3. To keep playing through the API while the game window is in the background, turn on
   **Options → App Settings → Keep Caves of Qud active in background** (needs "Show advanced
   options") and set **Display → Frame rate** to a number such as 60 instead of VSync.

The server listens on `http://localhost:15600` (loopback only) from mod load until the game quits.
`curl -s http://127.0.0.1:15600/` is a health check. From WSL use `127.0.0.1` rather than
`localhost` (see the top of API.md for why).

## Repository

- `src/`: the mod. `StateBuilder.cs` builds `/state`, `ActionHandler.cs` runs actions,
  `PopupBridge.cs`, `ScreenBridge.cs` and `TradeBridge.cs` read and answer the game's screens,
  `BridgeServer.cs` is the HTTP server.
- `API.md`: the API reference.
- `QUDBRIDGE.md`: developer notes: architecture, the dev loop, game internals the bridge depends
  on, and what has been verified in-game.
- `tools/`: small test clients (`bridge.sh`, `walkto.py`).
- `QudBridge.csproj`: for type-checking against the game's assemblies (`dotnet build`; set
  `GameDir` to your install). The game itself compiles `src/` directly.

## Status

Version 0.1.0. Built and tested against the Steam release of Caves of Qud on Windows. Not yet
tested in-game: multi-page books, terminals, the number screen and tinkering with recipes.
