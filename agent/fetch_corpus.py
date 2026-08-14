#!/usr/bin/env python3
"""Pull one author's public profiles out of the official community catalog.

The catalog is the same list the app's Community Profiles window shows, and
these are the files their author published for other people to use. One
author's set is what makes personalisation testable: hold one profile back,
rebuild it from the rest, and compare against what they actually made.

    python3 agent/fetch_corpus.py "Silas P." agent/corpus/silas
"""
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request

CATALOG = "https://bvhbml89uymwxubx.quadstick.com"
EXPORT = "https://docs.google.com/spreadsheets/d/{id}/export?format=xlsx"
UA = {"User-Agent": "quadstick-agent-corpus/1.0"}


def get(url, tries=4):
    """One fetch, with the backoff a shared Google endpoint needs."""
    for attempt in range(tries):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=60) as r:
                return r.read()
        except (urllib.error.URLError, urllib.error.HTTPError, TimeoutError) as e:
            code = getattr(e, "code", None)
            if code in (400, 401, 403, 404):        # not going to get better
                raise
            if attempt == tries - 1:
                raise
            time.sleep(2 ** attempt)
    raise RuntimeError("unreachable")


def safe(name):
    return re.sub(r"[^A-Za-z0-9._-]+", "-", name).strip("-") or "profile"


def main():
    author = sys.argv[1] if len(sys.argv) > 1 else "Silas P."
    outdir = sys.argv[2] if len(sys.argv) > 2 else "agent/corpus/silas"
    os.makedirs(outdir, exist_ok=True)

    rows = json.loads(get(CATALOG))[0]
    mine = [r for r in rows if r[0].startswith(author)]
    print(f"{len(mine)} of {len(rows)} catalog profiles are by {author}", flush=True)

    index, failed = [], []
    for i, row in enumerate(mine, 1):
        title, sheet_id, csv_name = row[0], row[1], row[2]
        game = title.split("/", 1)[1] if "/" in title else title
        path = os.path.join(outdir, safe(game) + ".xlsx")
        entry = {"game": game, "csvFileName": csv_name.strip(),
                 "sheetId": sheet_id, "channel": row[4], "file": path}
        if os.path.exists(path) and os.path.getsize(path) > 0:
            index.append(entry)
            continue
        try:
            body = get(EXPORT.format(id=sheet_id))
            with open(path, "wb") as f:
                f.write(body)
            index.append(entry)
            print(f"  [{i}/{len(mine)}] {game} ({len(body)} bytes)", flush=True)
        except Exception as e:                       # noqa: BLE001 - report, keep going
            failed.append({"game": game, "error": str(e)})
            print(f"  [{i}/{len(mine)}] {game} FAILED: {e}", flush=True)
        time.sleep(0.4)                              # be a good guest

    with open(os.path.join(outdir, "index.json"), "w") as f:
        json.dump({"author": author, "source": CATALOG,
                   "profiles": index, "failed": failed}, f, indent=2)
    print(f"got {len(index)}, failed {len(failed)}; index.json written", flush=True)
    return 1 if not index else 0


if __name__ == "__main__":
    sys.exit(main())
