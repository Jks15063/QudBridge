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

## Errors

| Status | When | Body |
|---|---|---|
| 404 | Unknown path | `{"error":"not found","path":"/nope"}` |
| 405 | Wrong method on a known path | `{"error":"method not allowed"}` |
| 500 | Unhandled exception in a handler | `{"error":"<exception message>"}` |
| 503 | Game thread hasn't started yet | `{"error":"game thread not started"}` |
| 504 | Game thread didn't take or finish the request in time | `{"error":"game thread did not pick up the request within 5000ms (...)"}` |
