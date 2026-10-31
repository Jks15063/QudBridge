#!/usr/bin/env bash
# Tiny client for the QudBridge API, for manual testing from WSL.
#   tools/bridge.sh state [query]      GET /state (e.g. 'messages=3&legend=1'), pretty-printed
#   tools/bridge.sh act '<json>'       POST /action, pretty-printed with the state blob summarised
#   tools/bridge.sh raw <path>         GET any path, raw
# Uses plain curl if WSL can reach the game's localhost (mirrored networking), else Windows' curl.exe.
set -euo pipefail
BASE="http://localhost:15600"
if curl -s -m 1 -o /dev/null "$BASE/" 2>/dev/null; then CURL=curl; else CURL=/mnt/c/Windows/System32/curl.exe; fi

case "${1:-}" in
  state)
    "$CURL" -s -m 30 "$BASE/state${2:+?$2}" | python3 -m json.tool --no-ensure-ascii ;;
  act)
    out=$("$CURL" -s -m 30 -w '\n%{http_code} %{time_total}s' -X POST "$BASE/action" -d "$2")
    echo ">> $2  -> HTTP $(tail -1 <<<"$out")"
    head -n -1 <<<"$out" | python3 -c '
import json, sys
d = json.load(sys.stdin)
st = d.pop("state", None)
print(json.dumps(d, indent=1, ensure_ascii=False))
if st:
    p = st["player"]
    print("   [state] screen", st["screen"], "zone", p["zone"]["name"], "pos", p["x"], p["y"], "turn", st["turn"], "hp", p["hp"])
' ;;
  raw)
    "$CURL" -s -m 30 "$BASE${2:-/}"; echo ;;
  *)
    sed -n 2,6p "$0"; exit 1 ;;
esac
