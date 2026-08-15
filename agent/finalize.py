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


def ask(prompt, valid):
    """Read one answer. Anything unrecognised is asked again rather than assumed."""
    while True:
        try:
            said = input(prompt).strip().lower()
        except EOFError:
            return "s"
        if said in valid:
            return said
        print(f"   please answer one of: {', '.join(valid)}")


def interview(report):
    """Put the agent's questions to the person, and take their answers as final.

    Every option carries the device tokens it will be bound to, so what they
    choose is written exactly as it was shown to them. Nothing is filled in for
    a question they skip. This is read aloud by some of the people using it, so
    it is numbered plain text and never signals anything by colour or position
    alone.
    """
    answers, questions = {}, report.get("questions", [])
    if report.get("proposals"):
        print(f"\n{len(report['proposals'])} controls it settled from your own profiles:\n")
        for p in report["proposals"]:
            print(f"   {p['output']:18} {' + '.join(p['inputs'])}, {p.get('function', 'normal')}")
            print(f"   {'':18} because {p['why'][:150]}\n")

    if questions:
        print(f"{len(questions)} it will not guess at. Answer, or press s to leave one unbound.\n")
    for n, q in enumerate(questions, 1):
        usable = [o for o in q["options"] if isinstance(o, dict)]
        print(f"[{n} of {len(questions)}] {q['output']}")
        print(f"   {q['question']}")
        for i, o in enumerate(usable, 1):
            print(f"     {i}. {o['label']}")
        for o in q["options"]:
            if not isinstance(o, dict):
                print(f"     -  {o}   (recorded before options carried tokens, cannot be applied)")
        if not usable:
            print("   nothing here can be bound as written, so it is being left alone.\n")
            continue
        choice = ask("   which one, or s to skip: ",
                     [str(i) for i in range(1, len(usable) + 1)] + ["s"])
        if choice == "s":
            print("   left unbound.\n")
            continue
        picked = usable[int(choice) - 1]
        # An option with nothing to trigger it is the agent offering to leave the
        # control alone, which is what its label says. Writing it would put an
        # output in the file that nothing can ever fire.
        if not picked["inputs"]:
            print("   left unbound.\n")
            continue
        answers[q["output"]] = {"inputs": picked["inputs"], "function": picked["function"]}
        print(f"   {q['output']} -> {' + '.join(picked['inputs'])}, {picked['function']}\n")
    return answers


def open_rows(profile, fresh=False):
    """Row number of every output in the first mode that a binding may land on.

    A row with an input in it is somebody's binding, so editing an existing
    profile only ever takes the empty ones. A profile this run built a minute ago
    out of the template has nobody's work in it, and its stock rows
    (left_joy_up=up, increment_mode=right_sip) are exactly the ones the answers
    are meant to replace, so `fresh` takes those too.
    """
    read = qsf("inspect", profile)
    spare = {}
    for pf in read["profiles"]:
        for mode in pf["modes"][:1]:
            for b in mode["bindings"]:
                if (fresh or not b["inputs"]) and b["output"] not in spare:
                    spare[b["output"]] = b["row"]
        break
    return spare


def apply_settled(profile, settled, out, log=print, fresh=False):
    """Turn approved proposals into rows, or change nothing at all.

    Two passes, because a row has to exist before it can be bound, so both run
    on a copy: a refusal in the second pass would otherwise leave the empty rows
    the first one made. The copy only becomes the profile once every binding has
    been accepted AND the result validates, so a file that would load wrong on
    the device never replaces one that loaded right.
    """
    work = out + ".building"
    spare = open_rows(profile, fresh)
    try:
        # A new profile arrives with a blank row per output already in it, so
        # adding one for every proposal wrote a second dpad_N under the first and
        # left the file twice the size with each output bound in two places. A
        # row that already carries an input is somebody's, and is never reused.
        # Claimed as they are handed out, so two proposals for one output do not
        # both land on the same row and quietly become one binding.
        taken = [spare.pop(p["output"], None) for p in settled]
        rows_ops = [{"op": "add_row", "mode": 0, "why": f"a row for {p['output']}"}
                    for p, row in zip(settled, taken) if row is None]
        made = qsf("apply", "--from", profile, "--ops", write_ops(rows_ops, "rows"),
                   "--out", work, ok=(0, 1))
        if made["rejected"]:
            log(f"could not make the rows: {made['rejected'][:2]}")
            return {"ok": False, "error": "the rows could not be made",
                    "rejected": made["rejected"]}
        fresh = iter(a["detail"]["row"] for a in made["applied"] if a["op"] == "add_row")
        rows = [row if row is not None else next(fresh) for row in taken]

        bind_ops = [{"op": "set_binding", "row": row, "output": p["output"],
                     "function": p.get("function", "normal"), "inputs": p["inputs"],
                     "why": f"[{p.get('confidence', 'inferred')}] {p['why']}",
                     # 180 characters of prose in a spreadsheet cell is a wall
                     # nobody reads. Say where it came from, in a glance.
                     "note": p["why"][:70]}
                    for row, p in zip(rows, settled)]
        result = qsf("apply", "--from", work, "--ops", write_ops(bind_ops, "bind"),
                     "--out", work, ok=(0, 1))

        # `ok` is every op accepted and no errors in the result, and qsf writes
        # nothing otherwise. Both halves matter: a refused binding and a result
        # the device would read wrong are equally reasons to leave the profile
        # exactly as it was.
        if not result["ok"]:
            log(f"nothing was written, and {out} is exactly as it was.")
            for r in result["rejected"]:
                log(f"   {r['reason']}\n     it wanted this because: {r['why'][:120]}")
            for i in result["issues"]:
                if i["severity"] == "Error":
                    log(f"   {i['cell']}: {i['message'][:90]}")
            return {"ok": False, "error": "the result was refused before it was written",
                    "rejected": result["rejected"], "validation": result}
        os.replace(work, out)
    finally:
        if os.path.exists(work):
            os.remove(work)
    return {"ok": True, "validation": result, "written": len(settled)}


def report_open(unanswered, untouched=()):
    """Say which controls are still unbound. Silence here is the bug this file
    exists to avoid: an unanswered question is a control that does nothing."""
    if unanswered:
        print(f"\n{len(unanswered)} controls are still open, and were left alone:")
        for q in unanswered:
            print(f"   {q['output']}: {q['question']}")
            for option in q.get("options") or []:
                print(f"      - {option['label'] if isinstance(option, dict) else option}")
    if untouched:
        print(f"\n{len(untouched)} controls the agent never reached, so they are "
              f"unbound and nothing was asked about them:")
        for output in untouched:
            print(f"   {output}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--profile", required=True, help="what predict.py wrote")
    ap.add_argument("--decisions", required=True, help="what qsagent.py reported")
    ap.add_argument("--answers", help='{"kb_c": {"inputs": [...], "function": "toggle"}}')
    ap.add_argument("--interactive", action="store_true",
                    help="put the agent's questions to the person at the keyboard")
    ap.add_argument("--out", help="defaults to editing the profile in place")
    ap.add_argument("--open", action="store_true", help="open the result in QuadStick Config Manager")
    args = ap.parse_args()

    out = args.out or args.profile
    report = json.load(open(args.decisions))
    answers = json.load(open(args.answers)) if args.answers else {}
    if args.interactive:
        answers.update(interview(report))

    settled = list(report["proposals"])
    asked = {q["output"] for q in report["questions"]}
    # A question the person answered is theirs, and it outranks any proposal.
    # Every field is taken exactly as given. An answer missing its function used
    # to become `normal`, which is a device setting nobody typed, and an answer
    # naming a control nobody asked about used to be written anyway.
    for output, choice in answers.items():
        if output not in asked:
            raise SystemExit(f"the answers name {output}, which the agent never asked "
                             f"about. Nothing was written.")
        if "function" not in choice or "inputs" not in choice:
            raise SystemExit(f"the answer for {output} has no "
                             f"{'function' if 'function' not in choice else 'inputs'}. "
                             f"Nothing is guessed here, so nothing was written.")
        settled = [p for p in settled if p["output"] != output]
        settled.append({"output": output, "inputs": choice["inputs"],
                        "function": choice["function"],
                        "confidence": "chosen by the user",
                        "why": "they answered the question about this control"})

    unanswered = [q for q in report["questions"] if q["output"] not in answers]
    if not settled:
        # "Nothing to apply" on its own read as a clean run. It is not one when
        # the agent asked questions nobody answered: those controls are still
        # unbound, and saying so is the whole job.
        print(f"nothing to apply: the agent settled nothing on its own.")
        report_open(unanswered, report.get("untouched") or [])
        return 0

    # Nothing is written until they say so. The list they approve is the list
    # that gets written, and it is shown in full rather than summarised.
    if args.interactive:
        print(f"\nAbout to write {len(settled)} bindings into {os.path.basename(out)}:\n")
        for p in settled:
            print(f"   {p['output']:18} {' + '.join(p['inputs'])}, {p.get('function', 'normal')}"
                  f"   [{p.get('confidence', 'inferred')}]")
        if unanswered:
            print(f"\n   {len(unanswered)} left unbound, untouched: "
                  f"{', '.join(q['output'] for q in unanswered)}")
        if ask("\nwrite it? [y/n]: ", ["y", "n"]) != "y":
            print("nothing written, the profile is exactly as it was")
            return 0

    done = apply_settled(args.profile, settled, out)
    if not done["ok"]:
        return 1

    check = done["validation"]
    print(f"{len(settled)} bindings applied to {out}")
    print(f"validation: {check['errors']} errors, {check['warnings']} warnings")
    for i in check["issues"][:5]:
        print(f"   {i['severity']}: {i['cell']} {i['message'][:90]}")

    report_open(unanswered, report.get("untouched") or [])

    if args.open:
        # The person's own app, on the file that was just built. Opening is as
        # far as this goes: nothing installs anything onto a device from here.
        built = os.path.join(os.path.dirname(HERE), "dist/QuadStick Config Manager.app")
        app = built if os.path.isdir(built) else "QuadStick Config Manager"
        done = subprocess.run(["open", "-a", app, out], capture_output=True, text=True)
        if done.returncode == 0:
            print(f"\nopened {out} in QuadStick Config Manager")
        else:
            print(f"\ncould not open QuadStick Config Manager ({done.stderr.strip()[:120]}).\n"
                  f"The profile is written and valid at {out}; run `make package` to build the app.")
    return 0 if check["errors"] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
