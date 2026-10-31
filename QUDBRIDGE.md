# QudBridge

A Caves of Qud mod that exposes game state and player actions over a localhost HTTP API, so an external program (an LLM agent harness) can play the game. This repo is the game-side adapter only. The harness is a separate project.

## Environment

- Development happens in WSL (Linux). The game runs on Windows.
- WSL uses **NAT** networking (no `.wslconfig`), so `localhost` in WSL does *not* reach Windows `localhost`. Hit the bridge from WSL with Windows' curl: `/mnt/c/Windows/System32/curl.exe -s http://localhost:<port>/`. The harness will need to run on Windows, use `curl.exe`, or WSL must be switched to `networkingMode=mirrored`.
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
