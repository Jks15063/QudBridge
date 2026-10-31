# QudBridge HTTP API

Base URL: `http://localhost:15600` (loopback only; also reachable as `http://127.0.0.1:15600`).

From WSL: with mirrored networking plain `curl http://127.0.0.1:15600/` works, but `localhost` returns `400 Bad Request (Invalid host)` (Mono binds the `localhost` prefix to `::1` only, which WSL can't reach). With NAT networking use Windows' curl, `/mnt/c/Windows/System32/curl.exe`. `tools/bridge.sh` picks whichever works. Clients on Windows itself can use either name.

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
  `points`: unspent `sp` (skill), `ap` (attribute), `mp` (mutation) points; see [`GET /character`](#get-character).
  `abilities`: what the ability bar shows, e.g. `{"name":"Shield Slam","cooldown":38,"attack":true}`,
  `{"name":"Night Vision","on":true}`. `cooldown` (rounds) only while cooling down; `on` only for
  toggles; `why` when unusable for another reason. Use with the `ability` action.
  `effects`: active effect descriptions, with remaining turns in parentheses when finite.
- Coordinates are zone cells, `x` 0–79 left→right, `y` 0–24 top→bottom. `dist` is in moves
  (diagonals count as 1).
- `creatures`: visible creatures, nearest first (max 52). `key` is the letter used on the map.
  `attitude` is `hostile`, `friendly` (allied or your follower) or `neutral`. `health` is what the
  game tells the player: a wound level like `"Wounded"`, or exact `"hp / max"` only with scanning.
  `flying: true` (omitted otherwise) means you can't hit it in melee unless you fly too; use `fire`.
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

### World map

When `player.zone.worldMap` is `true`, `map.rows` shows terrain instead: each distinct terrain name
the game shows (regions like `salt marsh` or `River Svy`, and places like `Joppa`) gets its own
symbol, assigned most-common-first, so symbols can differ between responses. `map.legend` is
therefore always included on the world map, and `@` names the tile you're on. `creatures`, `items`
and `features` are empty there.

```
"map":{"w":80,"h":25,"legend":{"@":"you (on Joppa)"," ":"unexplored",".":"jungle",",":"salt dunes",
  ":":"mountains","~":"River Svy","&":"salt marsh","*":"Joppa","1":"Red Rock", ...},
 "rows":[",,,,,,,,,\"\"5'::::::...", ...]}
```

Use `stairs` `down` to enter the zone you're standing on.

Returns `409 {"error":"no game in progress (screen: MainMenu)"}` when no game is loaded.

## `GET /character`

Character advancement: unspent points, base attributes, mutations and the skill tree. Spend
points with the `learn`, `raise` and `mutate` actions. The game only spends points from its
status screens, which this API can't read, so `learn` and `raise` repeat those screens' checks
(`XRL.UI/SkillsAndPowersScreen.cs` `SelectNode`, `XRL.UI/StatusScreen.cs` `BuyStat` /
`ShowMutationPopup`) and skip their "Are you sure?" popups. `mutate` uses the game's own code.

`?details=1` adds `desc` to skills, powers and mutations.

```
$ curl -s http://127.0.0.1:15600/character
{"points":{"sp":78,"ap":0,"mp":1},
 "attributes":{"Strength":16,"Agility":22,"Toughness":18,"Intelligence":17,"Willpower":16,"Ego":16},
 "canMutate":true,
 "mutations":[{"name":"Beak","rank":1,"baseRank":1},{"name":"Wings","rank":1,"baseRank":1,"canRaise":true}, ...],
 "skills":[{"name":"Axe","cost":50,"canLearn":true,"powers":[{"name":"Cleave","cost":...,
   "why":"requires the Axe skill (50 SP) first"}, ...]},
  {"name":"Pistol","cost":100,"owned":true,"powers":[{"name":"Steady Hand","cost":0,"owned":true},
   {"name":"Sling and Run","cost":50,"canLearn":true}, ...]}, ...]}
```

`owned` / `canLearn` / `canRaise` are present only when true. `requires` (on anything not owned)
lists all requirements whether met or not, as the skills screen does: attribute minimums
(`"Strength 23"`; `" or "` between alternatives), `needs <skill or mutation>`, `not with <...>`.
Example (level-2 true kin, 44 SP, Strength 22):
`{"name":"Improved Aggressive Stance","cost":100,"why":"costs 100 SP; you have 44; needs Strength 23 (you have 22)","requires":"Strength 23"}`.
`why` lists every reason it can't be learned now, e.g.
`"costs 100 SP; you have 44; needs Strength 23 (you have 22)"` (also: needs its skill first,
initiation only).
Powers with cost `0` come free with their skill. `rank` is the mutation's effective rank, `baseRank`
the bought one. `canMutate` is false for creatures that can't buy mutations (e.g. true kin).

```
$ curl -s -X POST http://127.0.0.1:15600/action -d '{"type":"learn","name":"Sling and Run"}'
{"result":"ok","turns":0,"log":["You learn Sling and Run for 50 SP."],"state":{...}}
$ curl -s -X POST http://127.0.0.1:15600/action -d '{"type":"learn","name":"Dead Shot"}'
{"error":"costs 200 SP; you have 28"}
```

## `POST /action`

Performs one player action, waits until it is the player's turn again (other creatures have acted),
and returns what happened plus the new state. Only one action runs at a time.

Body: a JSON object with a `type` and type-specific fields.

| `type` | Fields | Game command |
|---|---|---|
| `move` | `dir` | `CmdMove<dir>` (moving into a hostile attacks it, as in-game) |
| `attack` | `dir` | `CmdAttack<dir>`. `409` if there's nothing there you can hit (`"nothing to attack there"`, or a flying creature: `"can't reach ... it is flying"`); the game itself would do nothing silently |
| `fire` | `x`, `y` | fires your equipped missile weapons at that cell (`Combat.FireMissileWeapon` with the target, so the game's targeting screen never opens). Every ready weapon fires. `409` with no missile weapon or `"you need to reload"`. Misses often log nothing; the target's `health` in `state` shows the effect |
| `throw` | `x`, `y` | throws what's in the Thrown Weapon slot at that cell (`Combat.ThrowWeapon` with the target, no targeting screen). The next one of a stack is equipped automatically ("You equip the torch."). Throws can fall short. `409` if the slot is empty: put something there with `item` verb `"equip (manual)"` and choose the "Thrown Weapon" option |
| `reload` | — | `CmdReload`: reloads missile weapons from inventory ammo |
| `ability` | `name`, optional `dir` | uses an ability by its `name` from `state.player.abilities` (or its command), as the ability bar does. If it asks "which direction?" it's answered with `dir`; without `dir` that comes back as a `direction` prompt. `409` with the game's message while cooling down ("You must wait 38 rounds before using Shield Slam."). Abilities that pick a cell return a `target` prompt that can only be cancelled for now |
| `learn` | `name` | buys a skill or power (name or class from `GET /character`) for its SP cost. Instant, no time passes |
| `raise` | `attribute` or `mutation` | spends 1 AP on an attribute (`"Strength"` or `"str"`), or 1 MP on a mutation's rank |
| `mutate` | — | spends 4 MP on a new mutation: returns the game's "Choose a mutation" `prompt`; answer with `choose {"option": i}` |
| `wait` | optional `turns` | `CmdWait` (one turn); with `turns`, waits that many as "Wait N Turns" does (refused with a popup if hostiles are in view) |
| `travel` | `x`, `y` | walks to that cell of this zone by the game's pathing, as clicking the map does. Unexplored cells are fine (it explores its way there). No path: a popup "You cannot find a path to your destination." |
| `travel` | `edge`: `N` / `S` / `E` / `W` | walks to that edge of the zone (`CmdMoveToEdge`; its direction prompt is answered for you). Another `move` off the edge enters the next zone |
| `explore` | — | `CmdAutoExplore`. When the zone is done: popup "There doesn't seem to be anywhere else to explore." Not on the world map |
| `rest` | — | `CmdWaitUntilHealed`; refused with a popup naming them if hostiles are in view |
| `stop` | — | interrupts a running `travel`/`explore`/`rest`/`wait turns`. Doesn't wait for the action lock: send it while the request that started the walk is still waiting. Returns `{"result":"stopped","log":["You stop moving because you told it to."],"state":...}`; the original request then returns `ok` with the same line. `409` if nothing is running |
| `pickup` | — | `CmdGet` (pick up what's on your cell) |
| `stairs` | `dir`: `up` / `down` | `CmdMoveU` / `CmdMoveD`. `down` requires stairs down on your cell (or the world map); `up` off stairs on the surface goes to the world map, as `<` does in-game. |
| `eat` / `drink` / `equip` / `unequip` | `item` or `slot` | the item's inventory action (`equip` = "equip (auto)", `unequip` = "remove") |
| `item` | `item` or `slot`, `verb` | any inventory action of that item, by name or menu text (`"drop"`, `"apply"`, `"read"`, ...) |
| `getfrom` | `dir` | `CmdGetFrom<dir>`: take from a container (e.g. a chest) in that direction |
| `talk` | `dir` | `CmdTalk`; the "which direction?" prompt is answered with `dir` for you |
| `look` | `x`, `y` | what the looker shows for each visible object on that cell (no time passes): returns `look`, a list in the looker's order (creatures first, then terrain and items) of `{name, feeling, difficulty, health, description}`. `feeling` (`Hostile`/`Neutral`/`Friendly`) and `difficulty` (the game's rating relative to you: `Average`, `Tough`, ...) are for creatures. A creature's `description` lists its equipment and physical features. Looking can note a secret or observation, so the result may be a `prompt` (still with `look`). `409` `"you can't see that cell"` or `"there's nothing there to look at"`. For inventory or equipped items use `item` with verb `"look"` (a popup with the item's description and stats) |
| `choose` | see [Prompts](#prompts) | answers the open popup/menu |

`dir` is one of `N S E W NE NW SE SW`. `item` is an index from `state.inventory[].i`; `slot` is a
label from `state.equipped[].slot` (e.g. `"Right Hand"`).

```
$ curl -s -X POST http://localhost:15600/action -d '{"type":"move","dir":"N"}'
{"result":"ok","turns":1,"log":["You pass by a door."],"state":{ ...same as GET /state... }}
```

`result` is one of:

- `ok`: it's the player's turn again. Includes `turns`, `log` and `state`.
  - `turns`: game turns that passed. Can be `0` even for a real action: with quickness above 100
    the player sometimes acts twice in one game turn. Actions that take no time (unequip, bumping a
    wall, attacking an empty cell) also give `0`.
  - `log`: message-log lines added since the request started, including failure messages.
  - `state`: `GET /state` with the default 15 messages.
- `prompt`: the game is waiting on a popup or menu. Includes `screen`, `log` and `prompt` (see
  below); answer it with `choose`. The action itself has often already happened and the popup is
  its result message (eating, drinking).
- `blocked`: the game is on a screen this API can't read (e.g. the inventory screen); `screen` names it.
- `timeout`: nothing settled within 15 s (120 s for `travel`, `explore`, `rest` and `wait` with
  `turns`). If an auto-action is still running the error says so; `stop` interrupts it.

`travel`, `explore`, `rest` and `wait` with `turns` run the game's auto-actions and return once the
whole thing is over: arrived, explored, healed, or interrupted. The game interrupts them when hostiles
come into view; the reason is in `log` (`"You stop exploring because ..."`). They are fast:
exploring a whole surface zone took about 5 s (40-60 turns).

In `log`, runs of the same line are merged: `"You pass by a watervine. (x23)"`. If a long action
logged more than the last 200 lines, the first entry says how many earlier ones were dropped.

Unknown verbs list what's available:

```
$ curl -s -X POST http://localhost:15600/action -d '{"type":"item","item":4,"verb":"juggle"}'
{"error":"no \"juggle\" action for beetle jerky [3 servings]","available":["drop","equip (auto)","equip (manual)","add notes","mark important","look","eat","show effects"]}
```

Rejections (nothing happens in-game):

| Status | When |
|---|---|
| 400 | Bad JSON, unknown `type`/`dir`/`verb`, bad `item`/`slot` |
| 409 | No game, not in normal play (`screen` isn't `Stage`; only `choose` is allowed then), not the player's turn, another action in progress, `choose` with nothing open, or the action isn't possible ("no stairs down here", "cannot eat ... right now") |

## Prompts

While a popup or menu is open the game thread is waiting on it, so `GET /state` returns the prompt
instead of the game state:

```
$ curl -s http://localhost:15600/state
{"screen":"PopupMessage","prompt":{"kind":"popup","title":"Select a destination","text":"",
 "options":[{"i":0,"text":"Current location (C)"},{"i":1,"text":"Center (C)"}],
 "buttons":[{"i":0,"text":"[Esc] Cancel","command":"Cancel"}]}}
```

It returns `409 {"error":"the game is on a screen this API can't read yet","screen":...}` for other
screens. Answer with `POST /action {"type":"choose", ...}`; it waits like any action and returns
`ok` (back to normal play) or the next `prompt` (e.g. the next line of a conversation).

### `kind: "popup"`

Message boxes, yes/no questions, option lists, conversations, item action menus, and number/text
prompts. Fields: `title` (the speaker, in conversations), `text`, `options`, `buttons`, and `input`
(`{"value":"251","numeric":true}`) when it asks for a value. `anyKey: true` means it has no
options or buttons and closes on any key. `id` is the game's popup ID when set
(`"Conversation:Tam, dromad merchant"`).

| `choose` with | Does |
|---|---|
| `"option": i` | picks `options[i]` (conversation reply, menu entry) |
| `"button": i` or `"button": "Cancel"` | presses a button, by index, `command`, or end of its text |
| `"text": "1"` | types into `input` and submits (e.g. "How many do you want to drop?") |
| nothing | dismisses an `anyKey` popup |

```
$ curl -s -X POST http://localhost:15600/action -d '{"type":"talk","dir":"E"}'
{"result":"prompt","screen":"PopupMessage","log":[],"prompt":{"kind":"popup","id":"Conversation:W4Lbreaker",
 "title":"W4Lbreaker","text":"Greetings, organic. ...","options":[{"i":0,"text":"Yes, please help!"}, ...,
 {"i":5,"text":"Oh. Well I should be going. [End]"}],"buttons":[{"i":0,"text":"Look","command":"Look"},
 {"i":1,"text":"[Tab] Trade","command":"trade"},{"i":2,"text":"[Esc] Cancel","command":"Cancel"}]}}
$ curl -s -X POST http://localhost:15600/action -d '{"type":"choose","option":5}'
{"result":"ok","turns":0,"log":[],"state":{...}}
```

### `kind: "target"`

A cell picker (abilities like Charge, the game's "Move to Square"). Answer with the cell:

```
{"type":"choose","x":60,"y":18}
```

or cancel with `{"button":"cancel"}`. The game's own checks still apply: a cell it won't accept
("You may only select a visible square!") comes back as a popup, then the picker again. Pickers
with `mode: "PickField"` (several cells) can only be cancelled. `fire` and `throw` take their
target directly and never show this.

### `kind: "direction"`

"Which direction?" (e.g. after a command that targets an adjacent cell). `text` is the game's label.
Answer with `"dir"`: one of the eight directions, `"self"`, or `"cancel"`.

### `kind: "pick"`

The item list shown when picking up from a cell with several items, or opening a container.
`options` are the items; `buttons` is a subset of `take all`, `store`, `cancel`.

```
{"kind":"pick","title":"Select an item","options":[{"i":0,"text":"beetle jerky [1 serving]"},
 {"i":1,"text":"waterskin [empty]"}],"buttons":["take all","cancel"]}
```

`{"option": i}` selects an item, which opens its action menu as a `popup` (choose `get`); the
list then reopens with what's left. `{"button": "take all"}` takes everything: from a cell the game
does this as an auto-action (one item per step); the response waits for the whole auto-action, so
its `log` and `state` include every "You take ..." line.

### `kind: "trade"`

The trade screen (a merchant's "Let's trade", or storing into a container: `mode` `trade` /
`container`).

```
{"kind":"trade","mode":"trade","trader":"Tam, dromad merchant","traderDrams":171,"yourDrams":30,
 "theirs":[{"i":2,"name":"lead slug x37 PV4 DMG1d2","count":37,"price":0.03,"selected":1}, ...],
 "yours":[{"i":3,"name":"beetle jerky [1 serving]","count":1,"price":0.35,"selected":0}, ...],
 "totals":{"buy":0.03,"sell":0.0,"youPay":1},"buttons":["offer","cancel"]}
```

`price` is per item in drams of water. `totals.youPay` is the drams the offer costs you (negative:
they pay you).

| `choose` with | Does |
|---|---|
| `"side": "theirs"`/`"yours"`, `"item": i`, `"count": n` | sets how many of that item to trade (clamped to the stack; trading away an important item asks first) |
| `"button": "offer"` | offers the trade (may raise confirmation popups) |
| `"button": "cancel"` | leaves the trade screen |

### Death and `kind: "gameover"`

When the player dies, the action that caused it returns the game's death popup as an ordinary
`popup` prompt (`id` `"DeathMessage"`, text `"You died.\n\nYou bled to death because of ..."`).
Dismissing it with `choose` returns the end screen:

```
{"result":"prompt","screen":"GameSummaryScreen","log":[],"prompt":{"kind":"gameover","name":"Mamower",
 "cause":"You bled to death because of a bloody wet glowfish [swimming].",
 "details":"Game summary for Mamower\n\nThis game ended on ...\nYou were level 2.\nYou scored 88 points.\n...",
 "buttons":["exit"]}}
```

`GET /state` returns the same prompt with `"gameOver":true`. `details` is the game's summary (score,
chronology, final messages; a few KB). In Classic mode the save is already deleted at this point.
`choose` (any body, e.g. `{"button":"exit"}`) closes it, and the game goes to the main menu:

```
{"result":"ended","screen":"MainMenu","log":[],"lastGame":{"name":"Mamower","cause":"You bled to death ..."}}
```

After that, game requests fail with `409` and say how the last game ended (until the game is relaunched):

```
{"error":"no game in progress (screen: MainMenu)","gameOver":true,"lastGame":{"name":"Mamower","cause":"..."}}
```

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
