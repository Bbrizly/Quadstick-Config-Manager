#!/usr/bin/env python3
"""The same run as the app shows, in a terminal.

agent/run.py is the one orchestration. The app draws its events as cards; this
prints them as lines and reads answers from the keyboard. Two front ends, one
pipeline, so nothing can be true in one and not the other.

    python3 agent/terminal.py --game "Hollow Knight Silksong"
    python3 agent/terminal.py --edit mine.csv --request "make sprint a hard puff"
"""
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


def ask(prompt, valid):
    """Read one answer. Anything unrecognised is asked again, never assumed."""
    while True:
        try:
            said = input(prompt).strip().lower()
        except EOFError:
            return "s"
        if said in valid:
            return said
        print(f"   please answer one of: {', '.join(valid)}")


def main():
    run = subprocess.Popen(
        [sys.executable, "-u", os.path.join(HERE, "run.py"), *sys.argv[1:]],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, bufsize=1)

    def reply(**answer):
        run.stdin.write(json.dumps(answer) + "\n")
        run.stdin.flush()

    for line in run.stdout:
        try:
            e = json.loads(line)
        except json.JSONDecodeError:
            print(line, end="")
            continue
        kind = e["event"]

        if kind == "run":
            print(f"mode {e['mode']}: {e['says']} ({e['model']} via {e['backend']})")
        elif kind == "stage":
            print(f"\n== {e['title']} ==")
        elif kind == "tool":
            print(f"  {e['title']}" + (f": {e['subtitle']}" if e.get("subtitle") else ""))
        elif kind == "tool_done":
            if e.get("summary"):
                took = f" in {e['ms'] / 1000:.1f}s" if e.get("ms") else ""
                where = f" [{e['origin']}]" if e.get("origin") else ""
                print(f"     {e['state']}{took}{where}: {e['summary']}")
        elif kind == "note":
            print(f"  {e['text']}")
        elif kind == "rows":
            print(f"\n{e['title']}: {len(e['rows'])}")
            for r in e["rows"]:
                print(f"   {r['output']:24} {' + '.join(r['inputs'])}, {r['function']}")
                print(f"   {'':24} {r.get('why', '')[:120]}")
        elif kind == "question":
            print(f"\n{e['question']}")
            print(f"   ({e['output']})")
            for n, o in enumerate(e["options"], 1):
                bound = ' + '.join(o["inputs"]) + f", {o['function']}" if o["inputs"] \
                    else "leaves it unbound"
                print(f"     {n}. {o['label']}")
                print(f"        {bound}")
            said = ask("   which one, or s to leave it alone: ",
                       [str(n) for n in range(1, len(e["options"]) + 1)] + ["s"])
            reply(id=e["id"], choice=None if said == "s" else int(said) - 1)
        elif kind == "confirm":
            print(f"\nAbout to write {len(e['rows'])} bindings into "
                  f"{os.path.basename(e['profile'])}:\n")
            for r in e["rows"]:
                was = f"   was {r['was']}" if r.get("was") else ""
                print(f"   {r['output']:24} {' + '.join(r['inputs'])}, {r['function']}{was}")
            left = [o["output"] for o in e.get("open", [])] + list(e.get("untouched", []))
            if left:
                print(f"\n   {len(left)} stay unbound and are left exactly as they are: "
                      f"{', '.join(left)}")
            reply(id=e["id"], write=ask("\nwrite it? [y/n]: ", ["y", "n"]) == "y")
        elif kind == "done":
            print(f"\n{e['written']} written to {e['profile']}: "
                  f"{e['errors']} errors, {e['warnings']} warnings")
            for i in e.get("issues", [])[:5]:
                print(f"   {i['severity']}: {i['cell']} {i['message'][:90]}")
            for c in (e.get("diff") or {}).get("changes", []):
                print(f"   {c['cell']}: {c['from']} -> {c['to']}")
        elif kind == "failed":
            print(f"\nStopped. Nothing was written.\n{e['message']}")

    return run.wait()


if __name__ == "__main__":
    sys.exit(main())
