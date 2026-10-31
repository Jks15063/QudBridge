# QudBridge HTTP API

Base URL: `http://localhost:15600` (loopback only; also reachable as `http://127.0.0.1:15600`).

From WSL (NAT networking), use Windows' curl: `/mnt/c/Windows/System32/curl.exe`.

POST requests must send a `Content-Length` (use `-d ''` for an empty body); Mono's `HttpListener`
rejects bodyless POSTs with `411 Length Required` before the mod sees them.

All responses are compact JSON (`Content-Type: application/json; charset=utf-8`). Errors use
`{"error":"<message>", ...}` with a matching HTTP status.

The server starts when the game finishes loading mods and keeps running across main menu,
new games and save loads. It stops when the game quits.

## `GET /`

Health check. Does not touch game state, so it works from the main menu.

```
$ curl -s http://localhost:15600/
{"status":"ok","mod":"QudBridge","version":"0.1.0"}
```

`version` comes from `manifest.json`.

## Game-thread requests

Endpoints that read or change game state run their work on the game's core thread and wait up
to 5 s for it to be picked up (another 5 s if it has started but not finished). The game thread only
takes work at certain points: during normal play while waiting for your input, and inside popups,
trade, inventory and the save picker. It does **not** take work on the main menu / character
creation, or in menus that don't pump the game queue.

To work while the game window is in the background, enable **Options → App Settings → Keep Caves
of Qud active in background** (needs "Show advanced options"). The mod also ignores window
focus loss while the server is running (otherwise the game sleeps until refocused).
Also set **Display → Frame rate** to a number (e.g. 60) rather than the default `VSync`: in the
background VSync doesn't throttle and Unity's main thread pins a CPU core.

In those cases the request fails with `504` instead of hanging. A request that times out before it
starts is cancelled and never runs.

## `GET /debug/gamethread`

Diagnostic: runs a trivial read on the game thread and reports where it ran and how long dispatch took.

```
$ curl -s http://localhost:15600/debug/gamethread
{"thread":"XRLCore Thread","isCoreThread":true,"gameRunning":true,"turns":1234,"player":"Mehmet","ms":18}
```

## `GET /state`

Snapshot of everything the player can currently see. Read-only (never changes the game).

Query parameters:

| Param | Default | Meaning |
|---|---|---|
| `messages` | `15` | Number of most recent message-log lines to include (`0` for none). |
| `legend` | off | `legend=1` adds `map.legend` (the symbol table below). |

```
$ curl -s 'http://localhost:15600/state?messages=3'
{"screen":"Stage","turn":80,"time":"Harvest Dawn",
 "player":{"name":"Mamower","hp":18,"maxHp":18,"level":1,"xp":0,"xpNext":220,
  "stats":{"str":16,"agi":22,"tou":18,"int":17,"wil":16,"ego":16,"av":1,"dv":12,"qn":115,"ms":100},
  "food":"Sated","water":"Quenched","weight":39,"maxWeight":240,
  "zone":{"id":"JoppaWorld.11.22.1.1.10","name":"Joppa","worldMap":false},"x":37,"y":16,"effects":[]},
 "map":{"w":80,"h":25,"rows":["<25 strings of 80 chars>"]},
 "creatures":[{"key":"a","name":"Mehmet","x":42,"y":19,"dist":5,"attitude":"neutral","health":"Perfect"}],
 "items":[{"name":"vinecord","x":35,"y":24,"dist":8}],
 "features":[{"name":"chest","x":30,"y":17},{"name":"table","x":69,"y":18}],
 "inventory":[{"i":0,"name":"waterskin [empty]"},{"i":6,"name":"lead slug x251 PV4 DMG1d2"}],
 "equipped":[{"slot":"Body","name":"woven tunic AV1 DV2"},{"slot":"Right Missile Weapon","name":"chrome revolver PV7 DMG1d6 [lead slug x6]"}],
 "messages":["You pass by a door.","You pass by a chest.","You pass by a door."]}
```

(Wrapped and abbreviated here; the real response is a single line.) Fields whose value would be
`null` are omitted.

- `screen`: the game's current view (`GameManager.CurrentGameView`). `"Stage"` is normal play; other
  values are menus/popups (e.g. `PopupMessage`, `ModernTrade`, `Looker`).
- `player.hp`/`maxHp`: exact. `stats`: attributes plus `av`/`dv`, `qn` (quickness), `ms` (move speed).
  `effects`: active effect descriptions, with remaining turns in parentheses when finite.
- Coordinates are zone cells, `x` 0–79 left→right, `y` 0–24 top→bottom. `dist` is in moves
  (diagonals count as 1).
- `creatures`: visible creatures, nearest first (max 52). `key` is the letter used on the map.
  `attitude` is `hostile`, `friendly` (allied or your follower) or `neutral`. `health` is what the
  game tells the player: a wound level like `"Wounded"`, or exact `"hp / max"` only with scanning.
- `items`: visible items on the ground, nearest first (max 40).
- `features`: visible furniture, containers and solid objects (max 40).
- `inventory`: carried items; `i` is the index in this list. `equipped`: one entry per equipped
  item, labelled by its (first) body slot.
- Item names spell out stat glyphs: `PV` penetration, `DMG` damage, `AV` armor, `DV` dodge.
- `messages`: oldest first, colour codes removed; a line may contain `\n`.

### Map symbols

"In view" means in line of sight and lit. Remembered cells only show terrain.

| Char | Meaning |
|---|---|
| `@` | you |
| `a`–`z`, `A`–`Z` | creature (see `creatures[].key`) |
| `?` | creature beyond the first 52 |
| `.` | floor, in view |
| `,` | floor, remembered (not in view) |
| ` ` | unexplored |
| `#` | wall |
| `+` / `'` | closed / open door |
| `<` / `>` | stairs up / down |
| `~` | liquid |
| `*` | item(s) |
| `&` | furniture / container / other solid object |

Returns `409 {"error":"no game in progress (screen: MainMenu)"}` when no game is loaded.

## Errors

| Status | When | Body |
|---|---|---|
| 400 | Bad query parameter | `{"error":"messages must be a non-negative integer"}` |
| 404 | Unknown path | `{"error":"not found","path":"/nope"}` |
| 405 | Wrong method on a known path | `{"error":"method not allowed"}` |
| 409 | Endpoint needs a game in progress | `{"error":"no game in progress (screen: MainMenu)"}` |
| 500 | Unhandled exception in a handler | `{"error":"<exception message>"}` |
| 503 | Game thread hasn't started yet | `{"error":"game thread not started"}` |
| 504 | Game thread didn't take or finish the request in time | `{"error":"game thread did not pick up the request within 5000ms (...)"}` |
