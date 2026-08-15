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
            # The whole function, parameters and all. Keeping only the name
            # turned `delay_on 500 16000` into `delay_on` and `tap 200` into
            # `tap`, which is the device timing they set, deleted on their
            # behalf. Two different timings are two different habits and they
            # count separately, which is why this asks more often than it used
            # to. Asking is the correct outcome there.
            key = (tuple(b["inputs"]), b["function"])
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


def predict(controls, ranked, ask_below, disputed=()):
    """One prediction per control the game needs, or an honest abstention."""
    decided, asks = [], []
    for output in controls:
        options = ranked.get(output)
        def ask(why):
            asks.append({"output": output, "why": why, "options": [
                {"inputs": o["inputs"], "function": o["function"],
                 "share": round(o["share"], 3)} for o in (options or [])[:3]]})

        # A control the sources disagreed about is a question no matter how
        # settled this person's own habit is. Their habit says what they like
        # pressing; it does not say which of two jobs this control does here.
        if output in disputed:
            ask("the sources disagree about what this control does in this game")
            continue
        if not options:
            ask("this author has never bound this control")
            continue
        best = options[0]
        # `<=`, not `<`. At exactly the threshold the old code settled it, so a
        # dead even two-way split was decided by whichever profile the corpus
        # happened to list first.
        tied = len(options) > 1 and options[1]["seenIn"] == best["seenIn"]
        if best["share"] <= ask_below or tied:
            ask(f"his habit is split: {best['share']:.0%} across {best['ofGames']} games"
                + (", with two he does equally often" if tied else ""))
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
    chart = json.load(open(chart_path))
    # Disputed controls are in the list too. Leaving them out meant a control
    # the research would not commit to was never bound and never mentioned,
    # which reads exactly like a game that does not have that control.
    wanted = list(chart["controls"])
    wanted += [c for c in (chart.get("disputed") or {}) if c not in wanted]
    seen = {}
    for p in profiles:
        for output in {b["output"] for m in p["modes"] for b in m["bindings"]}:
            seen[output] = seen.get(output, 0) + 1
    rig = [o for o, n in seen.items() if n >= floor * len(profiles) and o not in wanted]
    return wanted + sorted(rig)


def build(out, corpus=CORPUS, controls=None, chart=None, exclude_family=None,
          ask_below=0.5, trace=None, log=print):
    """The whole deterministic pass as one call, so the CLI and the app share it.

    Returns what happened rather than printing it and exiting, because the app
    has to show every one of these numbers and cannot read them back out of
    prose. `ok` false always carries the reason with it.
    """
    profiles = json.load(open(corpus))["profiles"]
    train = [p for p in profiles if p["family"] != exclude_family]
    if controls:
        spec = json.load(open(controls))
    elif chart:
        loaded = json.load(open(chart))
        spec = {"game": loaded.get("game", "unknown"),
                "csvFileName": loaded.get("family", "profile") + ".csv",
                "controls": controls_for(chart, train)}
    else:
        raise ValueError("give either controls or a chart, so there is a list of "
                         "controls to build.")
    if exclude_family:
        log(f"holding out family '{exclude_family}': "
            f"{len(profiles) - len(train)} profiles withheld, {len(train)} left to learn from")

    ranked = habits(train)
    loaded_chart = load_chart(chart)
    decided, asks = predict(spec["controls"], ranked, ask_below, loaded_chart["disputed"])
    log(f"{len(spec['controls'])} controls this game needs: "
        f"{len(decided)} answered from his own profiles, {len(asks)} need him")

    # Rows have to be made before they can be filled, and only the row add_row
    # reports back is safe to write to, so this runs in two passes.
    # Both passes run on a copy. A refusal in the second one would otherwise
    # leave behind the empty rows the first one made.
    # The half-built copy is removed on every way out, including the ones that
    # raise. A leftover .building beside a real profile is a file somebody will
    # eventually open, and it is not a profile.
    work = out + ".building"
    try:
        ops = [{"op": "set_filename", "name": spec["csvFileName"], "why": "the name the device shows"}]
        ops += [{"op": "add_row", "mode": 0, "why": f"a row for {d['output']}"} for d in decided]
        result, _ = qsf("apply", "--template", spec["csvFileName"], "--ops", write_tmp(ops),
                        "--out", work, ok=(0, 1))
        if not result["ok"]:
            log(f"could not make the rows: {result['rejected'][:2]}")
            return {"ok": False, "error": "the rows could not be made",
                    "rejected": result["rejected"], "spec": spec}
        rows = [a["detail"]["row"] for a in result["applied"] if a["op"] == "add_row"]

        ops = [{"op": "set_binding", "row": row, "output": d["output"],
                "function": d["function"], "inputs": d["inputs"],
                "why": f"{d['seenIn']} of {d['ofGames']} of his profiles bind {d['output']} this way "
                       f"({d['share']:.0%}); nearest example {d['evidence']}",
                # Column K is a spreadsheet cell somebody reads at a glance. The
                # mode and row it came from filled eighty characters and answered
                # nothing; how many of their own profiles agree is the whole point.
                "note": f"{d['seenIn']} of your {d['ofGames']} profiles do this"}
               for row, d in zip(rows, decided)]
        result, _ = qsf("apply", "--from", work, "--ops", write_tmp(ops), "--out", work, ok=(0, 1))

        # qsf writes nothing unless every op was accepted AND the result has no
        # errors, so `ok` is the whole gate. Reading only `rejected` meant a
        # result that failed validation left the first pass's empty rows in
        # place and they were promoted as if they were the finished profile.
        if not result["ok"]:
            log(f"nothing was written. {len(result['rejected'])} bindings refused, "
                f"{result['errors']} errors in the result:")
            for r in result["rejected"][:5]:
                log("    " + r["reason"])
            for i in result["issues"][:5]:
                if i["severity"] == "Error":
                    log(f"    {i['cell']}: {i['message'][:90]}")
            return {"ok": False, "error": "the profile was refused before it was written",
                    "rejected": result["rejected"], "validation": result, "spec": spec}
        os.replace(work, out)
    finally:
        if os.path.exists(work):
            os.remove(work)
    # The apply result already carries the validation of exactly what was
    # written, so re-reading the file to ask again would only add a way for the
    # two answers to differ.
    check = result
    log(f"wrote {out}: {check['errors']} errors, {check['warnings']} warnings")

    # The plan is also the agent's brief: what was settled and on what evidence,
    # what was not, and what this game's controls actually do.
    vocab, _ = qsf("vocab")
    plan = {
        "game": spec["game"], "csvFileName": spec["csvFileName"],
        "heldOut": exclude_family, "profile": out,
        "decided": decided, "asks": asks,
        "habits": {a["output"]: ranked.get(a["output"], []) for a in asks},
        "controlMeanings": loaded_chart["meanings"],
        "disputed": loaded_chart["disputed"],
        "outputs": sorted(set(vocab["outputs"]["ps3"]) | set(vocab["outputs"]["xbox"])),
        "applied": result["applied"], "validation": check,
    }
    if trace:
        with open(trace, "w") as f:
            json.dump(plan, f, indent=2)
        log(f"evidence for every row written to {trace}")
    if asks:
        log(f"\n{len(asks)} it will not guess at:")
        for a in asks[:6]:
            log(f"   {a['output']}: {a['why']}")
    return {"ok": True, "spec": spec, "decided": decided, "asks": asks,
            "plan": plan, "validation": check}


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

    try:
        done = build(args.out, corpus=args.corpus, controls=args.controls,
                     chart=args.chart, exclude_family=args.exclude_family,
                     ask_below=args.ask_below, trace=args.trace)
    except ValueError as e:
        raise SystemExit(str(e))
    return 0 if done["ok"] else 1


_tmp_count = [0]


def write_tmp(ops):
    _tmp_count[0] += 1
    path = os.path.join(os.environ.get("TMPDIR", "/tmp"), f"qsf-ops-{os.getpid()}-{_tmp_count[0]}.json")
    with open(path, "w") as f:
        json.dump(ops, f)
    return path


if __name__ == "__main__":
    sys.exit(main())
