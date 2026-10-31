# QudBridge

A Caves of Qud mod that exposes game state and player actions over a localhost HTTP API, so an external program (an LLM agent harness) can play the game. This repo is the game-side adapter only. The harness is a separate project.

## Environment

- Development happens in WSL (Linux). The game runs on Windows.
- Networking: until 2026-10-30 WSL used **NAT** (no `.wslconfig`), so `localhost` in WSL did *not* reach Windows `localhost` and the bridge had to be hit with Windows' curl: `/mnt/c/Windows/System32/curl.exe -s http://localhost:15600/`. The user then switched to **mirrored** networking (`C:\Users\Jake\.wslconfig`: `[wsl2]` / `networkingMode=mirrored`). Check with `wslinfo --networking-mode` (verified `mirrored` on 2026-10-30). **From WSL use `http://127.0.0.1:15600/`, not `localhost`**: Mono's `HttpListener` resolves the `localhost` prefix to `::1` and binds it as a separate socket; WSL's `localhost` is IPv4-only and mirrored mode refuses `::1`, so WSL's request lands on the `127.0.0.1` socket, whose only prefix is `127.0.0.1`, and gets `400 Bad Request (Invalid host)`. `tools/` scripts use `127.0.0.1` and pick plain `curl` when it works, else `curl.exe`.
- .NET SDK 10 is installed in WSL at `~/.dotnet`. `ilspycmd` is installed as a global dotnet tool, but 11.x is a native build needing glibc 2.38 (WSL is Ubuntu 22.04, glibc 2.35) and won't run. Use 9.1 instead, installed at `~/.local/share/ilspycmd9/ilspycmd`; run it with `DOTNET_ROLL_FORWARD=Major` since it targets .NET 8.
- Windows user: `Jake`.

## Paths

Verify these exist before relying on them. Ask if any are wrong.

- Game install: `/mnt/c/Program Files (x86)/Steam/steamapps/common/Caves of Qud/`
- Game assemblies: `<game install>/CoQ_Data/Managed/` (main one: `Assembly-CSharp.dll`)
- Mods folder (where the game loads mods from): `/mnt/c/Users/Jake/AppData/LocalLow/Freehold Games/CavesOfQud/Mods/`
- Decompiled source output: `~/qud-src`
- This project: `~/projects/QudBridge`
- Logs (all under `/mnt/c/Users/Jake/AppData/LocalLow/Freehold Games/CavesOfQud/`):
  - `build_log.txt` — mod compilation (load order, compile errors). Previous run: `build_log.txt.prev`.
  - `Player.log` — Unity log (exceptions, `Debug.Log`, mod directory scan). Previous run: `Player-prev.log`.
  - `game_log.txt` — game's own logger (rotated to `game_log.1..3.txt`).

## How Qud mods work

- A mod is a folder in the Mods folder containing `.cs` files and an optional `manifest.json`.
- The game compiles the mod's `.cs` files itself at startup. There is no build step for the mod.
- Harmony is bundled with the game and can patch game methods.
- The in-game compiler is RoslynCSharp on Roslyn 3.11, so mods are **C# 9 max** (no file-scoped namespaces, global usings, etc.). Mods reference every loaded game assembly (`XRL/ModManager.cs` `GetCompilerService`), including `System.dll` and `Newtonsoft.Json.dll`. The `.csproj` pins `LangVersion` 9 to match.
- The game reads only from the Windows Mods folder. WSL symlinks do not work from the Windows side, so files must be copied there.
- Compile errors and log output appear in the game's log files. Find where these are (likely `Player.log` under `AppData/LocalLow/Freehold Games/CavesOfQud/` and/or a mod build log) and document the path in this file.

## Project layout

```
QudBridge/
  QUDBRIDGE.md        this file
  src/                mod source (.cs files) + manifest.json — this is what gets deployed
  QudBridge.csproj    editor-only: references game DLLs for autocomplete/type checking; the game ignores it
  deploy.sh           copies src/ into the Mods folder as Mods/QudBridge/
  API.md              every endpoint, with request/response examples
  tools/              manual test clients (not deployed): bridge.sh, walkto.py
```

## Tasks, in order

Do one milestone at a time. After each, stop and report what was done and how to verify it in-game.

### 0. Decompile
```bash
DOTNET_ROLL_FORWARD=Major ~/.local/share/ilspycmd9/ilspycmd -p -o ~/qud-src "/mnt/c/Program Files (x86)/Steam/steamapps/common/Caves of Qud/CoQ_Data/Managed/Assembly-CSharp.dll"
```
The decompiled source is the API documentation. Use it to answer every question about game internals. Do not guess class or method names: find them in `~/qud-src` and cite file paths.

### 1. Project scaffolding
- Create `QudBridge.csproj` referencing `Assembly-CSharp.dll`, `UnityEngine.CoreModule.dll`, Harmony (`0Harmony.dll` if present in `Managed/`), and any other assemblies needed. Target a framework compatible with the game's assemblies. `dotnet build` must succeed as a type check.
- Create `deploy.sh`.
- Create `src/manifest.json`.

### 2. Hello world mod
- Find in the decompiled source how mods hook into game startup / game load (look for attributes or interfaces the game scans for in mod assemblies).
- Write a minimal mod that logs a message on load and shows a message in the in-game message log once a game is loaded.
- Deploy, have me launch the game, and confirm it works.

### 3. HTTP server
- Start an `HttpListener` (or a raw `TcpListener` if `HttpListener` isn't available in the game's runtime) on a background thread, on a fixed localhost port (pick one, e.g. 15600).
- `GET /` returns `{"status":"ok","mod":"QudBridge","version":"..."}`.
- Server must start once, survive returning to the main menu and loading another save, and shut down cleanly.

### 4. Main-thread dispatch
- Game state must not be read or modified from the HTTP thread.
- Implement a queue: HTTP handlers enqueue a work item and wait for its result. The game thread drains the queue. Find a suitable per-frame or per-turn hook in the decompiled source.
- Include a timeout so requests don't hang forever.

### 5. Read-only state: `GET /state`
Return JSON with:
- Player: name, HP/max HP, key stats, level/XP, position (zone + x,y), status effects
- Visible map around the player: a grid of cells with terrain/walls, and objects on them
- Visible creatures: name, position, hostility, HP if known
- Items on the ground nearby
- Inventory and equipped items
- Last N message log lines
- Current game mode / screen (normal play vs. a popup or menu being open)

Keep the JSON compact. It will be fed to an LLM.

### 6. Actions: `POST /action`
Start with: move (8 directions), wait, attack in a direction, pick up, use stairs. Then eat/drink, equip/unequip.
- Find how the game executes player commands internally (the path a keypress takes after key mapping) and call that, rather than simulating raw key input.
- Each action returns the result plus the new state, or an error explaining why it failed.

### 7. Popups and menus
Expose the currently open popup/menu (title, text, options) in `/state`, and an action to choose an option. This includes conversations, trade, and confirmation prompts. Expect this to be the largest chunk of work.

## Conventions

- Keep all mod code in `src/`.
- Document every endpoint in `API.md` with request/response examples.
- When something in the game's internals is unclear, show me the relevant decompiled code rather than assuming.

## Status and handoff (2026-10-30)

All seven milestones are done and committed on `master`; the API is documented in `API.md`.
`5e45379` (popup grace-period change) verified in-game 2026-10-30: a conversation choice leading
back to an identical node returns in ~0.02 s. Settle now also waits out auto-actions (see Gotchas).

### Dev loop

1. Edit `src/`, type-check with `~/.dotnet/dotnet build -nologo -v q` (must be 0 errors).
2. `./deploy.sh`, then the user restarts the game (mods compile only at launch) and loads a save.
3. Test with `tools/bridge.sh state` / `tools/bridge.sh act '{"type":"move","dir":"N"}'` / `tools/walkto.py X Y`.
4. Check `Player.log` for `MODERROR` / `QudBridge` lines (log output is buffered; lines can appear late).
5. Commit when verified (the user asks for commits; stay on `master`).

Required game settings (otherwise requests time out while the game window is in the background):
**Options → App Settings → Keep Caves of Qud active in background** (needs "Show advanced
options") and **Display → Frame rate** set to a number (e.g. 60), not VSync (VSync in the
background pins a CPU core). Test character since 2026-10-30: "Iusar Ganuginus", a level-1 true kin warrior (long sword, shield,
  Issachar rifle) in the Joppa outskirts. The first one, Mamower, died testing game over.

### Architecture (src/)

- `QudBridge.cs`: mod entry. `[ModSensitiveCacheInit]` starts the server and registers turn hooks;
  `[CallAfterGameLoaded]` / `[PlayerMutator]` post the "QudBridge active" message.
- `BridgeServer.cs`: `HttpListener` on `localhost`/`127.0.0.1:15600`, thread-pool handlers, routing,
  JSON (nulls omitted), error mapping. Cheap off-thread checks (`The.Game.Running`, current view
  name) make "no game" / "not in normal play" fail fast instead of timing out.
- `GameThread.cs`: run work on the core thread via the game's own `GameManager.gameQueue`, drained in
  `XRLCore.RenderBase` during the player-turn loop. Sets `Keyboard.KeyEvent` to wake `IdleWait`,
  resets it afterwards. `QueuedWork<T>`: run-once, cancel-if-not-started timeouts (shared with UiThread).
- `UiThread.cs`: same over `GameManager.uiQueue` (Unity main thread, drained every frame). Needed
  because modern popups/screens block the core thread (`Popup.cs:899`, `.Result` on UI tasks).
- `FocusPatch.cs`: Harmony prefixes on `GameManager.OnApplicationFocus/OnApplicationPause` so losing
  window focus doesn't put the turn loop to sleep (`XRLCore.cs:756`).
- `StateBuilder.cs`: `/state` snapshot (player, map with own symbol set, creatures, items, features,
  inventory, equipment, messages; world-map terrain mode). `Clean()` = own thread-safe markup
  stripper (the game's `ColorUtility.StripFormatting` shares a static StringBuilder) + stat glyphs
  spelled out. Never reads `GameObject.ID` (it assigns one on first read).
- `ActionHandler.cs`: `/action`. Commands are injected as `Keyboard.PushMouseEvent("Command:CmdX")`
  (same as controller buttons / ability bar) and run through `PlayerTurn`'s command switch; item
  actions via `EquipmentAPI.GetInventoryActions` + `InventoryAction.Process`. `Settle()` waits for:
  begin-player-turn / end-of-command hooks, a game-thread "player ready" probe, a prompt (UI-thread
  probe), or an unreadable screen ("blocked"). `talk` auto-answers the direction prompt.
- `PopupBridge.cs`: prompts on the UI thread: `PopupMessage` (popups, conversations, number/text
  input), the direction picker (answered by pushing keypad keys, as the on-screen control panel
  does), and `PickGameObjectScreen` (item picker). `TradeBridge.cs`: `TradeScreen`.
- `Advancement.cs`: `GET /character` and the `learn` / `raise` / `mutate` actions. Status screens are
  unreadable, so it repeats their checks (`SelectNode`, `BuyStat`, `ShowMutationPopup`) without popups;
  `mutate` calls `MutationsAPI.BuyRandomMutation`, whose choice popup comes back as a prompt.
- `Abilities.cs`: ability list (as `Qud.UI/AbilityBar.cs` builds it; don't filter on `Visible`, it's off
  by default) and the `ability` action (`Command:<ability command>`, like the ability bar).
- `MessageLog.cs`: numbered copy of message-log lines from `XRLCore.RegisterNewMessageLogEntryCallback`,
  so a request can report the lines it caused even while the core thread is blocked.

### Gotchas learned the hard way

- Mono `HttpListener`: `Close()` leaves `IsListening` true (accept loop must exit on
  `ObjectDisposedException`); a bodyless POST gets `411` before the handler runs.
- zsh here has `noclobber` (use `>|`), `rm` is aliased to `rm -i` (use `command rm -f`), quote globs
  in `grep --include='*.cs'`.
- Mods are C# 9 (Roslyn 3.11); everything in `Managed/` is referenced.
- The low-HP warning popup ("Your health has dropped below 40%!") is shown at the top of PlayerTurn's
  loop, *after* the begin-turn callbacks (XRLCore.cs:693 vs 767), so the begin-turn hook skips turns
  while `XRLCore.HPWarning` is set (verified 2026-10-30: the action returns the warning as a prompt; before
  the fix it returned `ok` and the next request got a 504).
- Quickness > 100 means the player sometimes acts twice per game turn, so `turns: 0` is normal.
- `PlayerTurn` (and the begin-player-turn callback) runs for every step of an auto-action ("take all",
  auto-explore, walking), so the begin-turn hook ignores turns while `AutoAct.IsActive()`; otherwise
  Settle returns after the first step with a half-done state.

### What still needs work (priority order)

1. ~~Verify `5e45379` and mirrored networking~~: done 2026-10-30 (use `127.0.0.1` from WSL, see Environment).
2. ~~Death / game over~~: done 2026-10-30 (src/GameOver.cs; death popup -> `gameover` prompt -> `ended`,
   then `lastGame` in no-game errors). Verified in-game except the glyph cleanup and slimmer `lastGame`.
   Starting a new character from the main menu isn't possible through the API.
3. Combat: verified 2026-10-30, ranged (dragonflies, croc; bleeding, swoop attacks) and melee (`attack`
   on a glowfish, counterattacks in the same response). Walking into a neutral swaps places; neither
   `attack` nor `fire` on a neutral asked "really attack?". Still untested: move-to-attack a hostile,
   interruptions of auto-actions by hostiles.
4. Untested paths: `choose {"dir":...}` on a direction prompt directly, `getfrom` on a container
   (Joppa chests are villagers' property, so taking is stealing), trade `offer`, storing into a container.
5. Ranged combat: `fire {x,y}` and `reload` done (verified 2026-10-30; target passed straight to
   `Combat.FireMissileWeapon`, no picker). Still missing: `throw`, and abilities that open `PickTarget`.
6. Abilities: `abilities` in `/state` and the `ability` action (src/Abilities.cs) done, verified 2026-10-30
   (stance switch, Shield Slam with/without `dir`, cooldown error). Still missing: answering cell-target
   pickers (`target` prompt, cancel only; PickTarget's PointerOver/LeftClick mouse events look usable).
7. Travel helpers: auto-explore (`CmdAutoExplore`), move-to point, `CmdWaitUntilHealed`.
8. Level-up: popup readable; `points` in `/state`, `GET /character`, `learn` / `raise` / `mutate`
   (src/Advancement.cs; copies the status screens' checks). Verified 2026-10-30: learn power, raise
   mutation rank, all error cases. Untested: `raise` attribute with AP, `mutate` with 4 MP (choice popup).
9. Other unreadable screens: books, terminals, tinkering, gamepad `AskNumberScreen`.
10. Look/examine descriptions of creatures and items.
11. `/state` size (~5 KB, mostly the 80x25 map): an option for a window around the player.
12. Out-of-view objects aren't listed (remembered cells show terrain only).
13. Inventory indices shift when inventory changes; stable IDs would need `GameObject.ID` (writes the save).
14. The milestone-2 "QudBridge active" message clutters `messages`.
15. Settle's "player ready" probe can fire slightly before all creatures finish their turn in edge cases.
