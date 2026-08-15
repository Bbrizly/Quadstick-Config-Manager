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


def ask(prompt, valid, eof="s"):
    """Read one answer. Anything unrecognised is asked again, never assumed.

    A closed keyboard is not an answer, so `eof` says what that means here. At a
    question it means leave the control alone; over the list it means write
    nothing, which is the same outcome as closing the window.
    """
    while True:
        try:
            said = input(prompt).strip().lower()
        except EOFError:
            return eof
        if said in valid:
            return said
        print(f"   please answer one of: {', '.join(valid)}")


def named(row):
    """One row as a line, the way the window says it."""
    was = f"   was {row['was']}" if row.get("was") else ""
    return (f"{row['output']:24} {' + '.join(row['inputs'])}, "
            f"{row.get('function') or 'normal'}{was}")


def decide(e, say=input):
    """The same three ways out the window offers, at a keyboard.

    A list you can only take whole or leave whole is not something anybody can
    steer, and one row you disagree with should not cost the whole run.
    """
    off = set()
    rows = e["rows"]
    while True:
        print(f"\nAbout to write {len(rows) - len(off)} of {len(rows)} bindings into "
              f"{os.path.basename(e['profile'])}:\n")
        for n, r in enumerate(rows, 1):
            # The word carries it. There is no colour in here and there is not
            # meant to be one anywhere else either.
            print(f"   {n:3}. {named(r)}"
                  + ("      TAKEN OFF, nothing will be written for it"
                     if n - 1 in off else ""))
        left = [o["output"] for o in e.get("open", [])] \
            + [u["output"] for u in e.get("left", [])] + list(e.get("untouched", []))
        if left:
            print(f"\n   {len(left)} stay unbound and are left exactly as they are: "
                  f"{', '.join(left)}")

        choices = ["y", "n"] + [str(n) for n in range(1, len(rows) + 1)]
        if e.get("canSay"):
            choices.append("s")
        answer = ask("\nwrite it? y, n, or a number to take that one off"
                     + (", or s to say what to change" if e.get("canSay") else "")
                     + ": ", choices, eof="n")
        if answer == "y":
            if len(off) == len(rows):
                print("   every row is taken off, so there is nothing to write.")
                continue
            return {"id": e["id"], "write": True, "skip": sorted(off)}
        if answer == "n":
            return {"id": e["id"], "write": False}
        if answer == "s":
            try:
                said = say("   what should change: ").strip()
            except EOFError:
                said = ""
            if not said:
                print("   nothing typed, so nothing was asked for.")
                continue
            return {"id": e["id"], "say": said}
        at = int(answer) - 1
        off.symmetric_difference_update({at})
        print(f"   {rows[at]['output']} "
              + ("taken off the list." if at in off else "put back on the list."))


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
        elif kind == "tally":
            rest = max(0, e["of"] - e["answered"] - e["asking"])
            print(f"\n{e['of']} controls this game uses: {e['answered']} answered from his "
                  f"own profiles, {e['asking']} the evidence cannot settle, "
                  f"{rest} the chart does not cover")
        elif kind == "map":
            # The window draws this on the device. There is no device to draw on
            # here, so it says what it holds rather than passing over it.
            print(f"\n{len(e['rows'])} controls worked out for {e['game']}, "
                  f"{len(e.get('open', []))} still to ask you, "
                  f"{len(e.get('left', []))} left unbound on purpose")
        elif kind == "confirm":
            reply(**decide(e))
        elif kind == "done":
            print(f"\n{e['written']} written to {e['profile']}: "
                  f"{e['errors']} errors, {e['warnings']} warnings")
            for i in e.get("issues", [])[:5]:
                print(f"   {i['severity']}: {i['cell']} {i['message'][:90]}")
            for c in (e.get("diff") or {}).get("changes", []):
                print(f"   {c['cell']}: {c['from']} -> {c['to']}")
            # A row they took off is not a row that was never there.
            if e.get("declined"):
                print(f"   {len(e['declined'])} you took off the list, so they are "
                      f"unbound: {', '.join(d['output'] for d in e['declined'])}")
            for u in e.get("left", []):
                print(f"   {u['output']} was left unbound: {u['why']}")
            for q in e.get("open", []):
                print(f"   {q['output']} is still open: {q['question']}")
        elif kind == "failed":
            print(f"\nStopped. Nothing was written.\n{e['message']}")
        else:
            # Anything this version does not know is still something the run
            # said, and dropping it is the front end deciding what a person is
            # allowed to know.
            print(line, end="")

    return run.wait()


if __name__ == "__main__":
    sys.exit(main())
