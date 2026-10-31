#!/usr/bin/env python3
"""Walk the player next to cell (X, Y) in the current zone, one POST /action move at a time.

    tools/walkto.py X Y

BFS over GET /state's map: walkable = floor/remembered floor/open door/items; furniture ('&') is
allowed only as the final cell. A test helper, not a real pathfinder (no doors opened, no liquids).
"""
import json, subprocess, sys
from collections import deque

BASE = "http://127.0.0.1:15600"
def pick_curl():
    try:
        subprocess.run(["curl", "-sf", "-m", "1", BASE + "/"], check=True, capture_output=True)
        return "curl"
    except Exception:
        return "/mnt/c/Windows/System32/curl.exe"
CURL = pick_curl()

def get_state():
    out = subprocess.run([CURL, "-s", BASE + "/state?messages=0"], capture_output=True, text=True).stdout
    return json.loads(out)

def act(body):
    out = subprocess.run([CURL, "-s", "-X", "POST", BASE + "/action", "-d", json.dumps(body)], capture_output=True, text=True).stdout
    return json.loads(out)

DIRS = {"N": (0, -1), "S": (0, 1), "E": (1, 0), "W": (-1, 0), "NE": (1, -1), "NW": (-1, -1), "SE": (1, 1), "SW": (-1, 1)}

def main():
    tx, ty = int(sys.argv[1]), int(sys.argv[2])
    for _ in range(200):
        st = get_state()
        if "player" not in st:
            print("not in normal play:", st); return
        p = st["player"]; x, y = p["x"], p["y"]; rows = st["map"]["rows"]
        if max(abs(x - tx), abs(y - ty)) <= 1:
            print("\narrived next to target at", x, y); return
        prev = {(x, y): None}; q = deque([(x, y)]); goal = None
        while q:
            cx, cy = q.popleft()
            if max(abs(cx - tx), abs(cy - ty)) <= 1 and (cx, cy) != (tx, ty):
                goal = (cx, cy); break
            for dx, dy in DIRS.values():
                nx, ny = cx + dx, cy + dy
                if not (0 <= nx < 80 and 0 <= ny < 25) or (nx, ny) in prev:
                    continue
                c = rows[ny][nx]
                final = max(abs(nx - tx), abs(ny - ty)) <= 1 and c == "&"
                if c in ".,'*" or final:
                    prev[(nx, ny)] = (cx, cy); q.append((nx, ny))
        if goal is None:
            print("no path from", x, y); return
        c = goal
        while prev[c] != (x, y):
            c = prev[c]
        d = next(k for k, (dx, dy) in DIRS.items() if (x + dx, y + dy) == c)
        r = act({"type": "move", "dir": d})
        if r.get("result") != "ok":
            print("\nmove", d, "->", r); return
        print(d, end=" ", flush=True)

if __name__ == "__main__":
    main()
