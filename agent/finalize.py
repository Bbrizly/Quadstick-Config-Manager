#!/usr/bin/env python3
"""Put the agent's decisions into the profile, and nothing else into it.

This is the only place a model's output turns into cells, and it is deliberately
dull. Each proposal becomes one qsf op carrying its own reason. qsf refuses any
op naming a token the device does not know, and refuses the whole batch if one
op is bad, so a confidently wrong answer produces a rejection to read rather
than a file to load. A question the agent asked is left as a question: nothing
unanswered is filled in.

    python3 agent/finalize.py --profile new.csv --decisions decisions.json \\
                              --answers answers.json --open
"""
import argparse
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
QSF = os.path.join(os.path.dirname(HERE), "tools/qsf/bin/Debug/net8.0/qsf")


def qsf(*args, ok=(0,)):
    r = subprocess.run([QSF, *args], capture_output=True, text=True,
                       env={**os.environ, "DOTNET_ROOT": os.environ.get(
                           "DOTNET_ROOT", os.path.expanduser("~/.dotnet"))})
    if r.returncode not in ok:
        raise RuntimeError(f"qsf {args[0]} failed: {(r.stderr or r.stdout)[:400]}")
    return json.loads(r.stdout)


def write_ops(ops, tag):
    path = os.path.join(os.environ.get("TMPDIR", "/tmp"), f"qsf-{tag}-{os.getpid()}.json")
    with open(path, "w") as f:
        json.dump(ops, f)
    return path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--profile", required=True, help="what predict.py wrote")
    ap.add_argument("--decisions", required=True, help="what qsagent.py reported")
    ap.add_argument("--answers", help='{"kb_c": {"inputs": [...], "function": "toggle"}}')
    ap.add_argument("--out", help="defaults to editing the profile in place")
    ap.add_argument("--open", action="store_true", help="open the result in QuadStick Config Manager")
    args = ap.parse_args()

    out = args.out or args.profile
    report = json.load(open(args.decisions))
    answers = json.load(open(args.answers)) if args.answers else {}

    settled = list(report["proposals"])
    # A question the person answered is theirs, and it outranks any proposal.
    for output, choice in answers.items():
        settled = [p for p in settled if p["output"] != output]
        settled.append({"output": output, "inputs": choice["inputs"],
                        "function": choice.get("function", "normal"),
                        "confidence": "chosen by the user",
                        "why": "they answered the question about this control"})

    unanswered = [q for q in report["questions"] if q["output"] not in answers]
    if not settled:
        print("nothing to apply")
        return 0

    # Rows have to be made before they can be bound, which is two passes, which
    # means a refusal in the second one would otherwise leave the empty rows the
    # first one made. So both passes run on a copy, and the copy only becomes
    # the profile once every binding has been accepted.
    work = out + ".building"
    try:
        rows_ops = [{"op": "add_row", "mode": 0, "why": f"a row for {p['output']}"} for p in settled]
        made = qsf("apply", "--from", args.profile, "--ops", write_ops(rows_ops, "rows"),
                   "--out", work, ok=(0, 1))
        if made["rejected"]:
            print("could not make the rows:", made["rejected"][:2])
            return 1
        rows = [a["detail"]["row"] for a in made["applied"] if a["op"] == "add_row"]

        bind_ops = [{"op": "set_binding", "row": row, "output": p["output"],
                     "function": p.get("function", "normal"), "inputs": p["inputs"],
                     "why": f"[{p.get('confidence', 'inferred')}] {p['why']}",
                     "note": f"{p.get('confidence', 'inferred')}: {p['why'][:180]}"}
                    for row, p in zip(rows, settled)]
        result = qsf("apply", "--from", work, "--ops", write_ops(bind_ops, "bind"),
                     "--out", work, ok=(0, 1))

        if result["rejected"]:
            print(f"{len(result['rejected'])} of the agent's bindings were refused, "
                  f"so the profile was left exactly as it was:")
            for r in result["rejected"]:
                print(f"   {r['reason']}\n     it wanted this because: {r['why'][:120]}")
            return 1
        os.replace(work, out)
    finally:
        if os.path.exists(work):
            os.remove(work)

    check = qsf("validate", out, ok=(0, 1))
    print(f"{len(settled)} bindings applied to {out}")
    print(f"validation: {check['errors']} errors, {check['warnings']} warnings")
    for i in check["issues"][:5]:
        print(f"   {i['severity']}: {i['cell']} {i['message'][:90]}")

    if unanswered:
        print(f"\n{len(unanswered)} controls are still open, and were left alone:")
        for q in unanswered:
            print(f"   {q['output']}: {q['question']}")
            for option in q["options"]:
                print(f"      - {option}")

    if args.open:
        # The person's own app, on the file that was just built. Opening is as
        # far as this goes: nothing installs anything onto a device from here.
        subprocess.run(["open", "-a", "QuadStick Config Manager", out], check=False)
        print(f"\nopened {out} in QuadStick Config Manager")
    return 0 if check["errors"] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
