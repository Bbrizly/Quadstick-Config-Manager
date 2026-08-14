#!/usr/bin/env python3
"""Read every downloaded profile through qsf and write one index.

Two things come out of this that nothing else can give:

1. The bindings, already parsed by the app's own parser, so nothing here has a
   second opinion about what the file says.
2. What the DEVICE would refuse to load. These are published profiles by an
   expert of ten years, and some of them still carry rows the firmware drops
   without telling anyone. Counting them is the point, not a side effect.

    python3 agent/index_corpus.py agent/corpus/silas
"""
import hashlib
import json
import os
import re
import subprocess
import sys

QSF = "tools/qsf/bin/Debug/net8.0/qsf"

# Near-duplicate files for one game must never end up on both sides of a split:
# rebuilding "Cyberpunk 2077 PC KO" from "Cyberpunk 2077 PC QMP4" is copying,
# not personalising. Families are matched before any result is looked at.
FAMILIES = [
    ("call-of-duty", r"^(cod|call of duty)"),
    ("cyberpunk-2077", r"^cyberpunk"),
    ("gta", r"^gta"),
    ("far-cry", r"^far cry"),
    ("sniper-elite", r"^sniper elite"),
    ("tomb-raider", r"^tomb raider"),
    ("battlefield", r"^battlefield"),
    ("apex-legends", r"^apex"),
    ("red-dead", r"^red dead"),
    ("dead-space", r"^dead space"),
    ("flight-simulator", r"^(flight simulator|copy of flight simulator|fsx)"),
    ("deep-rock", r"^deeprock|^deep rock"),
    ("the-last-of-us", r"^the last of us"),
    ("assassins-creed", r"^assassin"),
    ("fortnite", r"fortnite"),
    ("warhammer-40k", r"^warhammer"),
    ("default-shooter", r"^(default shooter|shooter default)"),
    ("desktop-mouse", r"^(maus|monitor control|screen saver|my current preferences)"),
]


def family_of(game):
    low = game.lower().strip()
    for name, pattern in FAMILIES:
        if re.search(pattern, low):
            return name
    return re.sub(r"[^a-z0-9]+", "-", low).strip("-")


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def inspect(path):
    r = subprocess.run([QSF, "inspect", path], capture_output=True, text=True,
                       env={**os.environ, "DOTNET_ROOT": os.environ.get("DOTNET_ROOT", os.path.expanduser("~/.dotnet"))})
    if r.returncode != 0:
        return None, (r.stderr or r.stdout).strip()[:300]
    try:
        return json.loads(r.stdout)["profiles"][0], None
    except (json.JSONDecodeError, KeyError, IndexError) as e:
        return None, f"unreadable qsf output: {e}"


def main():
    corpus = sys.argv[1] if len(sys.argv) > 1 else "agent/corpus/silas"
    index = json.load(open(os.path.join(corpus, "index.json")))

    out, broken = [], []
    for entry in index["profiles"]:
        path = entry["file"]
        if not os.path.exists(path):
            continue
        profile, error = inspect(path)
        if profile is None:
            broken.append({"game": entry["game"], "error": error})
            print(f"  !! {entry['game']}: {error}", flush=True)
            continue

        modes = [m for m in profile["modes"] if m["type"] == "ProfileName"]
        errors = [i for i in profile["issues"] if i["severity"] == "error"]
        out.append({
            "game": entry["game"],
            "family": family_of(entry["game"]),
            "file": path,
            "sha256": sha256(path),
            "csvFileName": profile.get("csvFileName"),
            "title": profile.get("title"),
            "channel": entry.get("channel"),
            "modeCount": len(modes),
            "bindingCount": sum(len(m["bindings"]) for m in modes),
            "errorCount": len(errors),
            "warningCount": len(profile["issues"]) - len(errors),
            "skippedTabs": profile.get("skippedTabs", []),
            "modes": [{
                "number": m["number"], "name": m["name"], "label": m["label"],
                "bindings": [b for b in m["bindings"] if b["output"] or b["inputs"]],
            } for m in modes],
        })
        print(f"  {entry['game']}: {len(modes)} modes, "
              f"{out[-1]['bindingCount']} bindings, {len(errors)} errors", flush=True)

    dest = os.path.join(corpus, "bindings.json")
    with open(dest, "w") as f:
        json.dump({"author": index["author"], "source": index["source"],
                   "profiles": out, "unreadable": broken}, f)

    fams = {}
    for p in out:
        fams.setdefault(p["family"], []).append(p["game"])
    print(f"\n{len(out)} profiles indexed, {len(broken)} unreadable, {len(fams)} families")
    print(f"{sum(p['bindingCount'] for p in out)} bindings, "
          f"{sum(p['errorCount'] for p in out)} device level errors across the set")
    print(f"{sum(1 for p in out if p['errorCount'])} profiles carry at least one error")
    for name, games in sorted(fams.items(), key=lambda kv: -len(kv[1])):
        if len(games) > 1:
            print(f"  family {name}: {len(games)} files")
    print(f"written to {dest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
