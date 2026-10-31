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

## Errors

| Status | When | Body |
|---|---|---|
| 404 | Unknown path | `{"error":"not found","path":"/nope"}` |
| 405 | Wrong method on a known path | `{"error":"method not allowed"}` |
| 500 | Unhandled exception in a handler | `{"error":"<exception message>"}` |
