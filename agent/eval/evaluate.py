#!/usr/bin/env python3
"""Leave-one-family-out evaluation of the baselines, per agent/eval/manifest.json.

The question is narrow on purpose: given every profile this author has made
EXCEPT the ones for this game family, and given only the list of output tokens
the held-out profile binds, what would we bind each one to, and how often is
that what he actually did?

    python3 agent/eval/evaluate.py                 # every family
    python3 agent/eval/evaluate.py --family gta    # one family, verbosely
"""
import argparse
import collections
import json
import os
import statistics
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
MANIFEST = json.load(open(os.path.join(HERE, "manifest.json")))
CORPUS = os.path.join(os.path.dirname(HERE), "corpus", "silas", "bindings.json")

RIG_TOKENS = set(MANIFEST["binding_classes"]["rig"]["tokens"])
RIG_PREFIXES = tuple(MANIFEST["binding_classes"]["rig"]["prefixes"])


def is_rig(output):
    return output in RIG_TOKENS or output.startswith(RIG_PREFIXES)


def bindings_of(profile):
    """One profile as {output token: (inputs tuple, function keyword, whole function)}.

    First binding wins when a profile binds one output in several modes: the
    question asked is what this author reaches for, not every place he put it.

    Both forms of the function are kept because they measure different things.
    The keyword alone is what the registered numbers were scored on. The whole
    string includes the timings he set, and `tap 200` and `tap 500` are not the
    same binding to the person playing through them, so `with timings` is the
    stricter and more honest column. Neither replaces the other here: the
    registered one stays exactly as registered.
    """
    out = {}
    for mode in profile["modes"]:
        for b in mode["bindings"]:
            if not b["output"] or not b["inputs"]:
                continue
            whole = b["function"] or ""
            out.setdefault(b["output"], (tuple(b["inputs"]), whole.split()[0] if whole else "", whole))
    return out


# ---- the two baselines the agent has to beat -----------------------------

def global_baseline(train):
    """His most frequent answer for each output token, across every other game."""
    tally = collections.defaultdict(collections.Counter)
    for p in train:
        for output, value in bindings_of(p).items():
            tally[output][value] += 1
    return {output: c.most_common(1)[0][0] for output, c in tally.items()}


def nearest_baseline(train, wanted):
    """Copy the one training profile that binds the most of the same outputs."""
    best, best_overlap = None, -1
    for p in train:
        b = bindings_of(p)
        overlap = len(wanted & b.keys())
        if overlap > best_overlap:
            best, best_overlap = b, overlap
    return best or {}


# ---- scoring --------------------------------------------------------------

def score(reference, prediction):
    """Compare a prediction against what the author actually built."""
    buckets = {"gameplay": collections.Counter(), "rig": collections.Counter()}
    for output, truth in reference.items():
        cls = "rig" if is_rig(output) else "gameplay"
        b = buckets[cls]
        b["total"] += 1
        guess = prediction.get(output)
        if guess is None:
            b["abstained"] += 1
            continue
        b["answered"] += 1
        if guess[0] == truth[0]:
            b["inputs"] += 1
            if guess[1] == truth[1]:
                b["exact"] += 1
                if guess[2] == truth[2]:
                    b["withTimings"] += 1
    return buckets


def rate(bucket, key):
    return bucket[key] / bucket["total"] if bucket["total"] else 0.0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--family")
    ap.add_argument("--corpus", default=CORPUS)
    args = ap.parse_args()

    profiles = json.load(open(args.corpus))["profiles"]
    families = sorted({p["family"] for p in profiles})
    if args.family:
        if args.family not in families:
            print(f"no family '{args.family}'. have: {', '.join(families[:12])}...")
            return 2
        families = [args.family]

    results = []
    for family in families:
        held = [p for p in profiles if p["family"] == family]
        train = [p for p in profiles if p["family"] != family]
        if not train:
            continue
        for profile in held:
            reference = bindings_of(profile)
            if not reference:
                continue
            wanted = set(reference)
            # Neither baseline is shown a single cell of the held-out file.
            # It contributes only the list of tokens we are asked about.
            preds = {
                "global": {k: v for k, v in global_baseline(train).items() if k in wanted},
                "nearest": {k: v for k, v in nearest_baseline(train, wanted).items() if k in wanted},
            }
            for name, prediction in preds.items():
                results.append({"family": family, "game": profile["game"],
                                "baseline": name, "buckets": score(reference, prediction)})

    # Macro average by family, so one 550-binding profile cannot carry a number.
    print(f"leave-one-family-out over {len(families)} families, "
          f"{len({r['game'] for r in results})} profiles\n")
    header = (f"{'baseline':<10}{'gameplay exact':>16}{'with timings':>14}"
              f"{'gameplay inputs':>17}{'coverage':>10}{'rig exact':>11}")
    print(header)
    print("-" * len(header))
    for name in ("global", "nearest"):
        per_family = collections.defaultdict(list)
        for r in (x for x in results if x["baseline"] == name):
            per_family[r["family"]].append(r["buckets"])
        def macro(cls, key):
            vals = []
            for buckets in per_family.values():
                fam = [rate(b[cls], key) for b in buckets if b[cls]["total"]]
                if fam:
                    vals.append(statistics.mean(fam))
            return statistics.mean(vals) if vals else 0.0
        print(f"{name:<10}{macro('gameplay','exact'):>15.1%}{macro('gameplay','withTimings'):>14.1%}"
              f"{macro('gameplay','inputs'):>17.1%}"
              f"{macro('gameplay','answered'):>10.1%}{macro('rig','exact'):>11.1%}")
    print("\n'gameplay exact' is the registered measure: same inputs, same function name.\n"
          "'with timings' is the same rows scored with the function's parameters too,\n"
          "so `tap 200` and `tap 500` count as different. It is the stricter number.")

    if args.family:
        print()
        for r in results:
            g = r["buckets"]["gameplay"]
            print(f"  {r['baseline']:<9}{r['game']:<34} "
                  f"gameplay {g['exact']}/{g['total']} exact, {g['inputs']}/{g['total']} inputs right")

    out = os.path.join(HERE, "results.json")
    with open(out, "w") as f:
        json.dump({"manifest": MANIFEST["registered"],
                   "results": [{**r, "buckets": {k: dict(v) for k, v in r["buckets"].items()}}
                               for r in results]}, f, indent=2)
    print(f"\nper-profile results written to {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
