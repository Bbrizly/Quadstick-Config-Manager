#!/usr/bin/env python3
"""Build a profile for a game this author has never played, from the ones he has.

No model is involved here. This is the floor the agent has to beat, and it is
also the whole write path: predict, turn each prediction into a qsf op that
carries its own evidence, let qsf refuse anything the device would not read,
and validate what comes out.

    python3 agent/predict.py --controls agent/eval/controls-example.json \\
                             --out /tmp/newgame.csv
"""
import argparse
import collections
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
QSF = os.path.join(ROOT, "tools/qsf/bin/Debug/net8.0/qsf")
CORPUS = os.path.join(HERE, "corpus", "silas", "bindings.json")


def qsf(*args, ok=(0,)):
    r = subprocess.run([QSF, *args], capture_output=True, text=True,
                       env={**os.environ, "DOTNET_ROOT": os.environ.get("DOTNET_ROOT", os.path.expanduser("~/.dotnet"))})
    if r.returncode not in ok:
        raise RuntimeError(f"qsf {args[0]} failed: {(r.stderr or r.stdout)[:400]}")
    return json.loads(r.stdout), r.returncode


def bindings_of(profile):
    out = {}
    for mode in profile["modes"]:
        for b in mode["bindings"]:
            if not b["output"] or not b["inputs"]:
                continue
            out.setdefault(b["output"], {
                "inputs": b["inputs"],
                "function": b["function"] or "normal",
                "game": profile["game"], "row": b["row"], "mode": mode["name"],
            })
    return out


def habits(train):
    """What this author reaches for, per output token, with the evidence.

    Kept as a ranked list rather than a single answer, because how lopsided
    that list is IS the confidence, and the agent needs to see it to know when
    to stop and ask rather than guess.
    """
    tally = collections.defaultdict(collections.Counter)
    where = collections.defaultdict(dict)
    for p in train:
        for output, b in bindings_of(p).items():
            key = (tuple(b["inputs"]), b["function"].split()[0])
            tally[output][key] += 1
            where[output].setdefault(key, b)
    out = {}
    for output, counter in tally.items():
        total = sum(counter.values())
        ranked = []
        for key, n in counter.most_common(4):
            src = where[output][key]
            ranked.append({
                "inputs": list(key[0]), "function": key[1],
                "seenIn": n, "ofGames": total, "share": n / total,
                "evidence": f"{src['game']}, mode '{src['mode']}', row {src['row']}",
            })
        out[output] = ranked
    return out


def predict(controls, ranked, ask_below):
    """One prediction per control the game needs, or an honest abstention."""
    decided, asks = [], []
    for output in controls:
        options = ranked.get(output)
        if not options:
            asks.append({"output": output, "why": "this author has never bound this control",
                         "options": []})
            continue
        best = options[0]
        if best["share"] < ask_below:
            asks.append({
                "output": output,
                "why": f"his habit is split: {best['share']:.0%} across {best['ofGames']} games",
                "options": [{"inputs": o["inputs"], "function": o["function"],
                             "share": round(o["share"], 3)} for o in options[:3]],
            })
            continue
        decided.append({"output": output, **best})
    return decided, asks


def load_chart(path):
    """A chart says what a control does in this game, and says when it does not know.

    Disputed entries are kept apart from settled ones on purpose. A control two
    sources disagree about is a question for the person, not a coin toss.
    """
    if not path:
        return {"meanings": {}, "disputed": {}}
    chart = json.load(open(path))
    # A hand written chart lists the candidates it is torn between; a researched
    # one says in a sentence why it would not commit. Both are a reason to ask.
    disputed = {}
    for name, why in (chart.get("disputed") or {}).items():
        disputed[name] = why["candidates"] if isinstance(why, dict) and "candidates" in why \
            else ([why] if isinstance(why, str) else why)
    return {
        "meanings": {k: (v["action"] if isinstance(v, dict) else v)
                     for k, v in chart["controls"].items()},
        "disputed": disputed,
        "source": chart.get("source"),
    }


def controls_for(chart_path, profiles, floor=0.6):
    """What a profile for this game needs: its controls, plus their own rig.

    The game's chart says which buttons the game has. It says nothing about how
    this person drives a mouse, switches modes or tunes their joystick, and that
    part is theirs and nearly identical in every profile they have ever built.
    So the rig is taken from what they actually do rather than asked about.
    """
    wanted = list(json.load(open(chart_path))["controls"])
    seen = {}
    for p in profiles:
        for output in {b["output"] for m in p["modes"] for b in m["bindings"]}:
            seen[output] = seen.get(output, 0) + 1
    rig = [o for o, n in seen.items() if n >= floor * len(profiles) and o not in wanted]
    return wanted + sorted(rig)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--controls", default=None,
                    help="JSON: {game, csvFileName, controls: [output token, ...]}. "
                         "Left out, the list is taken from the chart plus their own rig.")
    ap.add_argument("--out", required=True)
    ap.add_argument("--corpus", default=CORPUS)
    ap.add_argument("--exclude-family", default=None,
                    help="hold a family out, so a prediction cannot read its own answer")
    ap.add_argument("--ask-below", type=float, default=0.5,
                    help="below this share of his games agreeing, ask instead of guess")
    ap.add_argument("--chart", default=None,
                    help="a sourced control chart for the target game")
    ap.add_argument("--trace", default=None)
    args = ap.parse_args()

    profiles = json.load(open(args.corpus))["profiles"]
    train = [p for p in profiles if p["family"] != args.exclude_family]
    if args.controls:
        spec = json.load(open(args.controls))
    elif args.chart:
        chart = json.load(open(args.chart))
        spec = {"game": chart.get("game", "unknown"),
                "csvFileName": chart.get("family", "profile") + ".csv",
                "controls": controls_for(args.chart, train)}
    else:
        raise SystemExit("give either --controls or --chart, so there is a list of "
                         "controls to build.")
    if args.exclude_family:
        print(f"holding out family '{args.exclude_family}': "
              f"{len(profiles) - len(train)} profiles withheld, {len(train)} left to learn from")

    ranked = habits(train)
    decided, asks = predict(spec["controls"], ranked, args.ask_below)
    print(f"{len(spec['controls'])} controls this game needs: "
          f"{len(decided)} answered from his own profiles, {len(asks)} need him")

    # Rows have to be made before they can be filled, and only the row add_row
    # reports back is safe to write to, so this runs in two passes.
    # Both passes run on a copy. A refusal in the second one would otherwise
    # leave behind the empty rows the first one made.
    work = args.out + ".building"
    ops = [{"op": "set_filename", "name": spec["csvFileName"], "why": "the name the device shows"}]
    ops += [{"op": "add_row", "mode": 0, "why": f"a row for {d['output']}"} for d in decided]
    result, _ = qsf("apply", "--template", spec["csvFileName"], "--ops", write_tmp(ops),
                    "--out", work, ok=(0, 1))
    if not result["ok"]:
        print("could not make the rows:", result["rejected"][:2])
        return 1
    rows = [a["detail"]["row"] for a in result["applied"] if a["op"] == "add_row"]

    ops = [{"op": "set_binding", "row": row, "output": d["output"],
            "function": d["function"], "inputs": d["inputs"],
            "why": f"{d['seenIn']} of {d['ofGames']} of his profiles bind {d['output']} this way "
                   f"({d['share']:.0%}); nearest example {d['evidence']}",
            "note": f"from {d['evidence']} ({d['seenIn']}/{d['ofGames']} of his games)"}
           for row, d in zip(rows, decided)]
    result, _ = qsf("apply", "--from", work, "--ops", write_tmp(ops), "--out", work, ok=(0, 1))

    if result["rejected"]:
        print(f"{len(result['rejected'])} bindings refused before they reached the file:")
        for r in result["rejected"][:5]:
            print("   ", r["reason"])
        os.remove(work)
        return 1
    os.replace(work, args.out)

    check, _ = qsf("validate", args.out, ok=(0, 1))
    print(f"wrote {args.out}: {check['errors']} errors, {check['warnings']} warnings")

    if args.trace:
        # The trace is also the agent's brief: what was settled and on what
        # evidence, what was not, and what this game's controls actually do.
        chart = load_chart(args.chart)
        vocab, _ = qsf("vocab")
        with open(args.trace, "w") as f:
            json.dump({
                "game": spec["game"], "csvFileName": spec["csvFileName"],
                "heldOut": args.exclude_family, "profile": args.out,
                "decided": decided, "asks": asks,
                "habits": {a["output"]: ranked.get(a["output"], []) for a in asks},
                "controlMeanings": chart["meanings"],
                "disputed": chart["disputed"],
                "outputs": sorted(set(vocab["outputs"]["ps3"]) | set(vocab["outputs"]["xbox"])),
                "applied": result["applied"], "validation": check,
            }, f, indent=2)
        print(f"evidence for every row written to {args.trace}")
    if asks:
        print(f"\n{len(asks)} it will not guess at:")
        for a in asks[:6]:
            print(f"   {a['output']}: {a['why']}")
    return 0


_tmp_count = [0]


def write_tmp(ops):
    _tmp_count[0] += 1
    path = os.path.join(os.environ.get("TMPDIR", "/tmp"), f"qsf-ops-{os.getpid()}-{_tmp_count[0]}.json")
    with open(path, "w") as f:
        json.dump(ops, f)
    return path


if __name__ == "__main__":
    sys.exit(main())
