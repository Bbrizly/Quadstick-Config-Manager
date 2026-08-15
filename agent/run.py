#!/usr/bin/env python3
"""The one process QuadStick Config Manager talks to.

Everything the terminal pipeline does, driven as a single run that narrates
itself. One JSON object per line on stdout, one per line back on stdin. The app
turns those lines into cards; nothing here knows or cares that it is being
watched, and running it in a terminal shows exactly the same events.

    python3 agent/run.py --game "Hollow Knight Silksong" --out /tmp/hk.csv
    python3 agent/run.py --edit mine.csv --request "make sprint a hard puff"

The events are the whole protocol:

    stage      a new phase started
    tool       something is happening now, with what it was given
    tool_done  how it ended: ok, warn or failed, and what it found
    note       a plain sentence
    rows       bindings settled so far, with the evidence for each
    question   the run stops here until a line comes back on stdin
    confirm    nothing is written until a line comes back saying so
    done       what was written, and what was deliberately left alone
    failed     why it stopped

Answers arrive as {"id": "q1", "choice": 0} or {"id": "q1", "choice": null} to
leave a control alone, and {"id": "c1", "write": true} to approve the write.
A closed pipe before the confirm means nothing was written, which is the same
outcome as saying no.
"""
import argparse
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import finalize                                            # noqa: E402
import predict                                             # noqa: E402
import qsagent                                             # noqa: E402
import research                                            # noqa: E402

CHARTS = os.path.join(HERE, "charts")


def emit(event, **fields):
    sys.stdout.write(json.dumps({"event": event, **fields}) + "\n")
    sys.stdout.flush()


def listen(want):
    """One reply from whoever is driving. A closed pipe ends the run.

    Ending on EOF rather than carrying on is the point: the only things this
    ever blocks for are a person's answer and a person's approval, and going
    ahead without either would be inventing one.
    """
    while True:
        line = sys.stdin.readline()
        if not line:
            raise Stopped("the window closed before answering, so nothing was written")
        try:
            said = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(said, dict) and said.get("id") == want:
            return said


class Stopped(Exception):
    """Ended on purpose, with a sentence for the person."""


# ---- the model's own calls, as something a person can read ----------------

def words(value):
    """A list of names as one phrase, whatever shape it actually arrived in."""
    if isinstance(value, list):
        return " + ".join(str(v) for v in value)
    return "" if value is None else str(value)


def title_for(name, args):
    """What a call is doing, from the call alone, before any answer exists.

    Split out from the result so a card can be put up the moment the call is
    made. Waiting for the answer to name the card is what made every step
    appear and finish in the same instant.
    """
    args = args if isinstance(args, dict) else {}
    name = str(name)
    if name == "WebSearch":
        return (f"Searching the web for “{args.get('query', '')}”", "reading what comes back")
    if name == "WebFetch":
        return (f"Reading {short_url(args.get('url', ''))}", "the actual control page")
    if name == "read_habits":
        controls = args.get("controls") or []
        return (f"Reading his habit for {len(controls)} "
                f"control{'' if len(controls) == 1 else 's'}",
                ", ".join(str(c) for c in controls[:6]))
    if name == "find_output":
        return (f"Looking up the device's name for “{args.get('query', '')}”", "")
    if name == "find_input":
        return (f"Looking up the device's input for “{args.get('query', '')}”", "")
    if name == "read_profile":
        asked = args.get("match") or ""
        return (f"Reading the profile for “{asked}”" if asked else "Reading the whole profile", "")
    if name == "propose_binding":
        return (f"Settling {args.get('output', '')}",
                f"{words(args.get('inputs'))}, {args.get('function', '')}"
                f"  [{args.get('confidence', 'inferred')}]")
    if name == "ask_user":
        return (f"Deciding to ask about {args.get('output', '')}", args.get("question", ""))
    if name == "change_row":
        return (f"Changing row {args.get('row', '')}",
                f"{args.get('output', '')}, {args.get('function', '')}, "
                f"{words(args.get('inputs'))}")
    if name == "finish":
        return ("Finishing", args.get("summary", ""))
    return (name, "")


def short_url(url):
    """A page as somebody would say it, not as a query string."""
    text = str(url or "")
    for cut in ("https://", "http://", "www."):
        text = text.removeprefix(cut)
    return text[:70] + ("…" if len(text) > 70 else "")


def describe(name, args, result):
    """One finished tool call as a title, a subtitle and how it went.

    Everything here is defensive on purpose. This runs on what a model sent,
    and a card that cannot be built is a step the person never sees.
    """
    args = args if isinstance(args, dict) else {}
    result = result if isinstance(result, dict) else {}
    name = str(name)
    failed = "error" in result
    if name == "read_habits":
        controls = args.get("controls") or []
        return (f"Read his habit for {len(controls)} control{'' if len(controls) == 1 else 's'}",
                ", ".join(str(c) for c in controls[:6]), failed)
    if name == "find_output":
        found = result.get("matches") or []
        return (f"Looked up the device's name for “{args.get('query', '')}”",
                f"{len(found)} match{'' if len(found) == 1 else 'es'}: "
                f"{', '.join(found[:5])}", failed)
    if name == "propose_binding":
        return (f"Settled {args.get('output', '')}",
                f"{words(args.get('inputs'))}, {args.get('function', '')}"
                f"  [{args.get('confidence', 'inferred')}]", failed)
    if name == "ask_user":
        return (f"Will ask about {args.get('output', '')}",
                args.get("question", ""), failed)
    if name == "finish":
        return ("Finished deciding", args.get("summary", ""), failed)
    if name == "read_profile":
        asked = args.get("match") or ""
        shown = len(result.get("rows") or [])
        return (f"Read the profile for “{asked}”" if asked else "Read the whole profile",
                f"{shown} of {result.get('of', 0)} rows", failed)
    if name == "find_input":
        found = result.get("matches") or []
        return (f"Looked up the device's input for “{args.get('query', '')}”",
                f"{len(found)} match{'' if len(found) == 1 else 'es'}: "
                f"{', '.join(found[:5])}", failed)
    if name == "change_row":
        was = result.get("from", "")
        return (f"Changed row {args.get('row', '')}",
                f"{args.get('output', '')}, {args.get('function', '')}, "
                f"{words(args.get('inputs'))}" + (f"   was {was}" if was else ""), failed)
    return (name, "", failed)


def watcher(label="Working out what to do next", prefix="m", origin=None):
    """Turn a loop's steps into events, one card each, as they happen.

    Two cards per model turn, not one: the wait for the model is itself a step,
    and it is the long one. Leaving it out meant the window sat blank for the
    whole time anything was actually happening, then filled up at once, which
    reads as a recording rather than as work.
    """
    seen = [0]
    open_ids = {}

    def start(key, name, args, origin=None):
        seen[0] += 1
        ident = f"{prefix}{seen[0]}"
        open_ids[key] = (ident, name, args)
        title, subtitle = title_for(name, args)
        emit("tool", id=ident, title=title, subtitle=subtitle, detail=args,
             state="running", origin=origin)
        return ident

    def on_event(kind, **fields):
        if kind == "thinking":
            seen[0] += 1
            open_ids["think"] = (f"{prefix}{seen[0]}", "think", None)
            emit("tool", id=f"{prefix}{seen[0]}", title=label,
                 subtitle=f"step {fields.get('step', '')}", state="running")
        elif kind == "thought":
            ident = (open_ids.pop("think", None) or (None,))[0]
            if ident:
                emit("tool_done", id=ident, state="ok",
                     summary="decided what to do", ms=fields.get("ms"),
                     origin=fields.get("origin"))
        elif kind == "said" and fields.get("text"):
            # The researcher's answer IS a JSON chart, and it arrives as the last
            # thing it says. Printing it as a sentence put a wall of raw JSON in
            # the middle of the transcript; the card under it already reports
            # what the chart turned out to contain.
            said = fields["text"].strip()
            if not said.startswith(("{", "[", "```")):
                emit("note", text=said)
        elif kind in ("stalled", "budget"):
            emit("note", text=fields.get("text", ""))
        elif kind == "tool":
            key = fields.get("id") or (fields.get("step"), fields.get("index"))
            start(key, fields.get("name", "a step"), fields.get("input"),
                  fields.get("origin"))
        elif kind == "tool_result":
            key = fields.get("id") or (fields.get("step"), fields.get("index"))
            ident, name, args = open_ids.pop(key, (None, None, None))
            result = fields.get("result")
            if ident is None:
                # A result for a call nobody announced is still a call it made,
                # so it gets its own card rather than being dropped.
                ident = start(key, fields.get("name", "a step"), fields.get("input"),
                              fields.get("origin"))
                open_ids.pop(key, None)
                name, args = fields.get("name"), fields.get("input")
            if result is None:
                # The web tools stream a summary line, not a result object.
                emit("tool_done", id=ident, state="failed" if fields.get("failed") else "ok",
                     summary=fields.get("summary", ""), ms=fields.get("ms"),
                     origin=fields.get("origin") or origin)
                return
            title, subtitle, failed = describe(name, args, result)
            emit("tool_done", id=ident, state="failed" if failed else "ok",
                 summary=result.get("error", "") if failed and isinstance(result, dict)
                         else subtitle,
                 detail=result, ms=fields.get("ms"), origin="local")
    return on_event


# ---- setting a game up from nothing ---------------------------------------

def chart_for(game, replay, live=False):
    """The game's controls, researched if nobody has charted it yet."""
    slug = research.slugify(game)
    path = os.path.join(CHARTS, slug + ".json")
    if os.path.exists(path) and not live:
        chart = json.load(open(path))
        emit("tool", id="chart", title=f"Read the control chart for {chart.get('game', game)}",
             subtitle=f"{len(chart.get('controls') or {})} controls, checked in already",
             state="running", detail={"path": path})
        emit("tool_done", id="chart", state="ok", origin="cache", ms=0,
             summary=f"{len(chart.get('controls') or {})} controls, "
                     f"{len(chart.get('disputed') or {})} the sources disagree about",
             detail={"source": chart.get("source"), "confidence": chart.get("confidence")})
        return path, chart

    emit("tool", id="chart", title=f"Nobody has charted {game}, so reading how it is controlled",
         subtitle="searching the web and reading the control pages", state="running",
         detail={"game": game})
    began = time.time()
    done = research.build_chart(game, out=path,
                                mode="replay" if replay else "live" if live else "auto",
                                log=lambda *_: None,
                                # Every search and every page it reads becomes its own
                                # card while it happens. These are the only calls in the
                                # whole run that touch the outside world, so they are
                                # the ones a person most needs to see it make.
                                on_event=watcher(prefix="w", origin="live"))
    took = int((time.time() - began) * 1000)
    if not done["ok"]:
        emit("tool_done", id="chart", state="failed",
             summary="nothing came back that the device could use", detail=done.get("dropped"))
        raise Stopped(f"nothing usable was found about how {game} is controlled, "
                      f"so no profile was built.")
    emit("tool_done", id="chart", state="warn" if done["dropped"] else "ok",
         ms=took, origin=done.get("origin"),
         summary=f"{done['kept']} controls the device knows, {done['disputed']} the sources "
                 f"disagree about, {len(done['dropped'])} dropped",
         detail={"source": done["source"], "dropped": done["dropped"],
                 "confidence": done["chart"]["confidence"], "read": done["origin"]})
    return done["path"], done["chart"]


def create(args, work):
    """Everything up to the point a person is asked to approve it.

    The deterministic profile is built at `work`, never at the destination.
    Building straight into the destination meant that starting a run wrote a
    file, which is a write nobody approved.
    """
    game = args.game
    emit("stage", key="research", title=f"How {game} is controlled")
    chart_path, chart = chart_for(game, args.replay, args.live)

    emit("stage", key="history", title="What his own profiles already answer")
    emit("tool", id="predict", title="Matched this game against every profile he has built",
         subtitle="no model involved, evidence only", state="running",
         detail={"corpus": args.corpus, "heldOut": args.hold_out})
    began = time.time()
    built = predict.build(work, corpus=args.corpus, chart=chart_path,
                          exclude_family=research.slugify(game) if args.hold_out else None,
                          log=lambda *_: None)
    took = int((time.time() - began) * 1000)
    if not built["ok"]:
        emit("tool_done", id="predict", state="failed", summary=built["error"], detail=built)
        raise Stopped(built["error"])
    plan = built["plan"]
    check = built["validation"]
    emit("tool_done", id="predict", state="ok", ms=took, origin="local",
         summary=f"{len(plan['decided'])} of {len(built['spec']['controls'])} answered from "
                 f"his own profiles, {len(plan['asks'])} the evidence will not settle",
         # The whole apply record is thousands of lines and the same facts are
         # already in the rows below, so what travels is what a person would
         # actually read: what the device thinks of the file so far.
         detail={"errors": check["errors"], "warnings": check["warnings"],
                 "issues": [{"severity": i["severity"], "cell": i["cell"],
                             "message": i["message"]} for i in check["issues"][:8]]})
    emit("rows", title="Settled from his own profiles", rows=[
        {"output": d["output"], "inputs": d["inputs"], "function": d["function"],
         "confidence": "evidenced",
         "why": f"{d['seenIn']} of {d['ofGames']} of his profiles do this "
                f"({d['share']:.0%}); nearest example {d['evidence']}"}
        for d in plan["decided"]])

    if not plan["asks"]:
        emit("note", text="his own profiles settled every control this game needs.")
        return qsagent.new_context(plan, []), plan

    emit("stage", key="agent", title="What the evidence could not settle")
    ctx = qsagent.new_context(plan, [a["output"] for a in plan["asks"]])
    meanings = plan.get("controlMeanings", {})
    task = (f"Game: {plan['game']}.\n\nThese {len(plan['asks'])} controls are unsettled. For "
            f"each, what it does in this game, where that is known:\n"
            + "\n".join(f"  {a['output']}: "
                        f"{meanings.get(a['output'], 'unknown, not in the sourced control list')}"
                        for a in plan["asks"])
            + "\n\nRead their habits, then settle each one or ask about it.")
    qsagent.agent_loop(task, ctx, verbose=False,
                       on_event=watcher("Working out the ones his profiles cannot settle"))
    return ctx, plan


# ---- changing a profile that already exists -------------------------------

EDIT_TOOLS = [
    dict(qsagent.TOOLS[1]),                                # find_output, unchanged
    {
        "name": "read_profile",
        "description": ("The rows this profile already has, so a change lands on the "
                        "right one. Read before changing anything."),
        "input_schema": {"type": "object", "properties": {
            "match": {"type": "string", "description":
                      "an output name, an input name, or part of one. Empty for all."}},
            "required": ["match"]},
    },
    {
        # Outputs and inputs are two separate lists on the device. An edit is
        # almost always about what triggers a row, so without this the only
        # lookup available searches the wrong half and finds nothing.
        "name": "find_input",
        "description": ("Search the sips, puffs, lip and joystick positions the device "
                        "knows. A soft input and a hard one are different names: "
                        "right_puff is a hard puff, right_puff_soft is a light one."),
        "input_schema": {"type": "object", "properties": {
            "query": {"type": "string"}}, "required": ["query"]},
    },
    {
        "name": "change_row",
        "description": ("Change one row that already exists. Only the fields you give are "
                        "changed. Everything else on that row stays exactly as they left it."),
        "input_schema": {"type": "object", "properties": {
            "row": {"type": "integer"},
            "output": {"type": "string"}, "function": {"type": "string"},
            "inputs": {"type": "array", "items": {"type": "string"}},
            "why": {"type": "string"}}, "required": ["row", "why"]},
    },
    dict(qsagent.TOOLS[3]),                                # ask_user, unchanged
    dict(qsagent.TOOLS[4]),                                # finish, unchanged
]

EDIT_SYSTEM = """You change one QuadStick profile, exactly as far as you are asked to.

A QuadStick is a mouth-operated controller used by people who cannot use their hands. \
This profile is one they already play with. Every row in it is a decision they made.

The rules, in order:
- Change only what they asked for. A row you were not asked about is not yours to
  tidy, rename, reorder or improve. This is the whole job.
- Read the profile first and change the row that is actually there. Never guess a
  row number.
- Never invent a token. Look it up. The device matches names whole and case
  sensitively, so a near miss is silently dead rather than wrong.
- If what they asked for could mean two different rows or two different bindings,
  ASK. A question costs them ten seconds; a wrong change costs them a session.
- Say what you changed and why, in plain words, for each row."""


def edit_tool(name, args, ctx):
    if not isinstance(args, dict):
        return {"error": f"{name} was called with {type(args).__name__}, not an object."}
    missing = [k for k in ctx["required"].get(name, []) if k not in args]
    if missing:
        return {"error": f"{name} needs {', '.join(missing)}, which you left out."}
    if name == "read_profile":
        match = (args.get("match") or "").lower()
        rows = [r for r in ctx["rows"]
                if not match or match in json.dumps(r).lower()]
        return {"rows": rows[:60], "of": len(ctx["rows"]),
                "note": "row numbers here are the ones to change"}
    if name == "find_input":
        q = (args["query"] or "").lower().replace(" ", "_")
        hits = [i for i in ctx["inputs"] if q in i.lower()]
        return {"matches": sorted(hits)[:25],
                "note": "exact spelling and case, as the device reads it"}
    if name == "change_row":
        row = args["row"]
        current = next((r for r in ctx["rows"] if r["row"] == row), None)
        if current is None:
            return {"error": f"this profile has no row {row}. Read it first and use a "
                             f"row number it actually has."}
        # Only the named fields move. Everything else on the row is theirs and
        # is carried through untouched, which is why the current row is read
        # here rather than rebuilt from what the model happens to remember.
        change = {"row": row,
                  "output": args.get("output", current["output"]),
                  "function": args.get("function", current["function"]),
                  "inputs": args.get("inputs", current["inputs"]),
                  "why": args["why"], "was": current}
        if (change["output"], change["function"], change["inputs"]) == \
           (current["output"], current["function"], current["inputs"]):
            return {"error": f"row {row} already says exactly that, so there is nothing "
                             f"to change. Either change something or finish."}
        ctx["changes"] = [c for c in ctx["changes"] if c["row"] != row] + [change]
        ctx["settled"].add(str(row))
        return {"changed": row, "from": f"{current['output']}, {current['function']}, "
                                        f"{' + '.join(current['inputs'])}"}
    if name == "ask_user":
        return qsagent.run_tool(name, args, ctx)
    if name == "finish":
        if not ctx["changes"] and not ctx["questions"]:
            return {"error": "nothing was changed and nothing was asked, so there is "
                             "nothing to finish. Say what you could not do instead."}
        ctx["done"] = args["summary"]
        return {"ok": True}
    return qsagent.run_tool(name, args, ctx)


def rows_of(path):
    """Every binding row in the profile, as the device reads it."""
    read = finalize.qsf("inspect", path)
    if not isinstance(read, dict) or not isinstance(read.get("profiles"), list):
        raise Stopped(f"{os.path.basename(path)} could not be read as a profile, "
                      f"so nothing was changed.")
    rows = []
    for profile in read["profiles"]:
        for mode in profile.get("modes") or []:
            for b in mode.get("bindings") or []:
                rows.append({"row": b["row"], "mode": mode.get("name", ""),
                             "output": b["output"], "function": b.get("function") or "normal",
                             "inputs": b.get("inputs") or []})
    return rows


def edit(args):
    emit("stage", key="read", title="What this profile says now")
    emit("tool", id="read", title=f"Read {os.path.basename(args.edit)}",
         subtitle="every row, as the device reads it", state="running",
         detail={"path": args.edit})
    try:
        rows = rows_of(args.edit)
    except Stopped:
        emit("tool_done", id="read", state="failed",
             summary="this file could not be read as a profile")
        raise
    emit("tool_done", id="read", state="ok", summary=f"{len(rows)} bindings",
         detail={"rows": rows[:12]})

    emit("stage", key="agent", title=f"“{args.request}”")
    vocab = finalize.qsf("vocab")
    ctx = {"rows": rows, "changes": [], "questions": [], "settled": set(),
           "unresolved": set(), "done": None, "habits": {},
           "inputs": sorted(set(vocab["inputs"]) | set(vocab["legacyInputs"])),
           "outputs": sorted(set(vocab["outputs"]["ps3"]) | set(vocab["outputs"]["xbox"])),
           "required": {t["name"]: t["input_schema"].get("required", []) for t in EDIT_TOOLS}}
    # ask_user checks membership of these two, and in an edit any row is fair
    # game, so the question path is opened to whatever it needs to ask about.
    ctx["unresolved"] = {r["output"] for r in rows} | {"", None}
    task = (f"This is their profile. They asked for exactly this and nothing more:\n\n"
            f"  “{args.request}”\n\n"
            f"Read the profile, find the row or rows that means, and change only those.")
    qsagent.agent_loop(task, ctx, verbose=False,
                       on_event=watcher("Working out which row they mean"),
                       system=EDIT_SYSTEM, tools=EDIT_TOOLS, runner=edit_tool)
    return ctx


def apply_changes(path, changes, out, committed=None):
    """Every change as one qsf batch, on a copy, all of them or none.

    `committed` is set the instant the new file replaces the old one, so a
    failure after that point cannot be reported as "nothing was written".
    """
    ops = [{"op": "set_binding", "row": c["row"], "output": c["output"],
            "function": c["function"], "inputs": c["inputs"], "why": c["why"]}
           for c in changes]
    work = out + ".building"
    try:
        import shutil
        shutil.copyfile(path, work)
        result = finalize.qsf("apply", "--from", work,
                              "--ops", finalize.write_ops(ops, "edit"), "--out", work, ok=(0, 1))
        if not result["ok"]:
            return {"ok": False, "rejected": result["rejected"], "validation": result}
        # The cell level difference between what they had and what they would
        # have. This is the thing they are being asked to approve, so it is
        # read off the two files rather than described from the ops.
        changed = finalize.qsf("diff", path, work, ok=(0, 1))
        os.replace(work, out)
        if committed is not None:
            committed.append(out)
    finally:
        if os.path.exists(work):
            os.remove(work)
    return {"ok": True, "validation": result, "diff": changed}


# ---- asking, approving, writing -------------------------------------------

def interview(questions):
    """Each question, one at a time, and nothing filled in for a skipped one."""
    answers = {}
    for n, q in enumerate(questions, 1):
        ident = f"q{n}"
        emit("question", id=ident, output=q["output"], question=q["question"],
             options=q["options"])
        said = listen(ident)
        choice = said.get("choice")
        if not isinstance(choice, int) or not 0 <= choice < len(q["options"]):
            emit("note", text=f"{q['output']} was left alone.")
            continue
        picked = q["options"][choice]
        # An option with nothing to trigger it is the offer to leave the control
        # alone, which is what its label says. Writing it would put an output in
        # the file that nothing can ever fire.
        if not picked["inputs"]:
            emit("note", text=f"{q['output']} was left unbound.")
            continue
        answers[q["output"]] = {"inputs": picked["inputs"], "function": picked["function"]}
    return answers


def approved(ident):
    """Exactly the boolean true, and nothing that merely looks like it.

    "false" as a string, 1, and an empty object are all truthy in Python, and
    each of them would authorise a write nobody approved.
    """
    return listen(ident).get("write") is True


def confirm_and_write(profile, settled, open_questions, untouched, out=None, shown=None):
    """Show the whole list, write it only if they say so.

    `shown` is what the person is approving, which for a new profile is every
    row it will contain. `settled` is only the part still to be applied on top
    of what the deterministic pass already put in the working copy.
    """
    out = out or profile
    emit("confirm", id="c1", profile=out,
         rows=shown if shown is not None else settled,
         open=[{"output": q["output"], "question": q["question"]} for q in open_questions],
         untouched=list(untouched))
    if not approved("c1"):
        raise Stopped("nothing was written, and the profile is exactly as it was")

    done = finalize.apply_settled(profile, settled, out, log=lambda text: emit("note", text=text))
    if not done["ok"]:
        emit("failed", message=f"{done['error']}, so nothing was written and the profile "
                               f"is exactly as it was", detail=done)
        return False
    check = done["validation"]
    emit("done", profile=out, written=done["written"],
         errors=check["errors"], warnings=check["warnings"],
         issues=[{"severity": i["severity"], "cell": i["cell"], "message": i["message"]}
                 for i in check["issues"][:12]],
         open=[{"output": q["output"], "question": q["question"]} for q in open_questions],
         untouched=list(untouched))
    return True


def free_name(path):
    """A name nothing is using yet.

    Setting up a game they have set up before must not quietly overwrite the
    profile they already tuned. Only the default name goes through here; a path
    they asked for by name is theirs and is used as given.
    """
    if not os.path.exists(path):
        return path
    stem, ext = os.path.splitext(path)
    for n in range(2, 200):
        candidate = f"{stem}-{n}{ext}"
        if not os.path.exists(candidate):
            emit("note", text=f"{os.path.basename(path)} already exists and was left "
                              f"alone, so this one is {os.path.basename(candidate)}.")
            return candidate
    raise Stopped(f"there are already 200 files named like {os.path.basename(path)}. "
                  f"Move some of them, or say where to write with --out.")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", help="set a game up from nothing")
    ap.add_argument("--edit", help="a profile that already exists")
    ap.add_argument("--request", help="what to change about it, in their words")
    ap.add_argument("--out", help="where to write. Defaults beside the profile.")
    ap.add_argument("--corpus", default=predict.CORPUS)
    ap.add_argument("--hold-out", action="store_true",
                    help="hide this game's own profiles first, so the result can be checked")
    ap.add_argument("--replay", action="store_true", help="from the recording, no network")
    ap.add_argument("--live", action="store_true",
                    help="ask the model even where an answer is already recorded")
    args = ap.parse_args()

    if args.replay and args.live:
        emit("failed", message="give either --replay or --live, not both")
        return 2
    mode = "replay" if args.replay else "live" if args.live else "auto"
    os.environ["QSF_MODEL_MODE"] = mode
    qsagent.MODE = mode
    # What a run is allowed to do is said before it does anything. A run that
    # finished in a second because every answer was already on disk looks
    # exactly like a run that made it all up, and only this line tells them apart.
    emit("run", mode=mode, model=qsagent.MODEL, backend=qsagent.BACKEND,
         says={"auto": "asks the model, but uses a recorded answer where one exists",
               "live": "asks the model every time, nothing replayed",
               "replay": "from the recording only, no network"}[mode])
    if not args.game and not (args.edit and args.request):
        emit("failed", message="give either --game, or --edit with --request")
        return 2

    committed = []
    try:
        if args.edit:
            args.out = args.out or args.edit
            ctx = edit(args)
            answers = interview(ctx["questions"])
            # A question left unanswered takes its row back off the list. The
            # model may have changed a row and then asked which row was meant;
            # writing its first guess after they declined to confirm it is
            # filling in an unanswered question.
            for q in ctx["questions"]:
                if q["output"] in answers:
                    continue
                dropped = [c for c in ctx["changes"] if c["was"]["output"] == q["output"]]
                if dropped:
                    ctx["changes"] = [c for c in ctx["changes"] if c not in dropped]
                    emit("note", text=f"{q['output']} was left alone, so the change to "
                                      f"row {dropped[0]['row']} was dropped.")
            # An answered question is a change too, and it outranks anything the
            # model proposed for that row.
            for output, choice in answers.items():
                row = next((r["row"] for r in ctx["rows"] if r["output"] == output), None)
                if row is None:
                    continue
                ctx["changes"] = [c for c in ctx["changes"] if c["row"] != row] + [{
                    "row": row, "output": output, "inputs": choice["inputs"],
                    "function": choice["function"], "why": "they answered the question about this row",
                    "was": next(r for r in ctx["rows"] if r["row"] == row)}]
            if not ctx["changes"]:
                emit("done", profile=args.edit, written=0, errors=0, warnings=0,
                     issues=[], open=[], untouched=[])
                emit("note", text="nothing was changed.")
                return 0
            emit("confirm", id="c1", profile=args.out, rows=[
                {"output": c["output"], "inputs": c["inputs"], "function": c["function"],
                 "why": c["why"], "confidence": "asked for",
                 "was": f"{c['was']['output']}, {c['was']['function']}, "
                        f"{' + '.join(c['was']['inputs'])}", "row": c["row"]}
                for c in ctx["changes"]], open=[], untouched=[])
            if not approved("c1"):
                raise Stopped("nothing was written, and the profile is exactly as it was")
            done = apply_changes(args.edit, ctx["changes"], args.out, committed)
            if not done["ok"]:
                emit("failed", message="the change was refused, so the profile is exactly "
                                       "as it was", detail=done)
                return 1
            emit("done", profile=args.out, written=len(ctx["changes"]),
                 errors=done["validation"]["errors"], warnings=done["validation"]["warnings"],
                 issues=[], open=[], untouched=[], diff=done["diff"])
            return 0

        args.out = args.out or free_name(os.path.join(
            os.path.expanduser("~/Documents"), research.slugify(args.game) + ".csv"))
        work = os.path.join(os.environ.get("TMPDIR", "/tmp"),
                            f"qs-building-{os.getpid()}.csv")
        try:
            ctx, plan = create(args, work)
            result = qsagent.brief(ctx)
            answers = interview(result["questions"])
            asked = {q["output"] for q in result["questions"]}
            # A proposal for a control that was also asked about is dropped
            # unless they answered it. The agent cannot do both any more, but a
            # profile is not the place to rely on that: an unanswered question
            # must never come out of this as a written row.
            settled = [p for p in result["proposals"]
                       if p["output"] not in asked and p["output"] not in answers]
            settled += [{"output": output, "inputs": choice["inputs"],
                         "function": choice["function"], "confidence": "chosen by them",
                         "why": "they answered the question about this control"}
                        for output, choice in answers.items()]
            still_open = [q for q in result["questions"] if q["output"] not in answers]
            already = [{"output": d["output"], "inputs": d["inputs"], "function": d["function"],
                        "confidence": "evidenced",
                        "why": f"{d['seenIn']} of {d['ofGames']} of his profiles do this "
                               f"({d['share']:.0%}); nearest example {d['evidence']}"}
                       for d in plan["decided"]]
            confirm_and_write(work, settled, still_open, result["untouched"],
                              out=args.out, shown=already + settled)
        finally:
            if os.path.exists(work):
                os.remove(work)
        return 0
    except Stopped as why:
        emit("failed", message=str(why), wrote=committed or None)
        return 1
    # SystemExit is how the pieces below report a condition they cannot carry
    # on from, and it is not an Exception, so without this the window would go
    # quiet and the message would land on a stderr nobody is reading.
    except SystemExit as why:
        emit("failed", message=str(why) or "the run stopped", wrote=committed or None)
        return 1
    except Exception as crash:                             # noqa: BLE001
        # A traceback on the app's stderr is a run that ended with the window
        # showing nothing. Whatever broke, it says so in the stream first.
        emit("failed", message=f"{type(crash).__name__}: {crash}", wrote=committed or None)
        return 1


if __name__ == "__main__":
    sys.exit(main())
