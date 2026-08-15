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
    tally      how many controls the game has, how many are answered, how many are not
    rows       bindings settled so far, with the evidence for each
    map        the whole profile before anyone is asked anything: every control,
               the game's word for it, and what it is bound to
    question   the run stops here until a line comes back on stdin
    confirm    nothing is written until a line comes back saying so
    done       what was written, and what was deliberately left alone
    failed     why it stopped

Answers arrive as {"id": "q1", "choice": 0} or {"id": "q1", "choice": null} to
leave a control alone. The confirm takes three, because a list you can only
accept whole or refuse whole is not something anybody can steer:

    {"id": "c1", "write": true}                 write the list as it stands
    {"id": "c1", "write": true, "skip": [3, 7]} write it without those rows
    {"id": "c1", "say": "sprint should be a hard puff"}
                                                change it, then show it again

`skip` holds positions in the row list that confirm sent, not names, because
one output can be on two rows and a name would be ambiguous exactly where it
matters. `say` starts another round: the confirm that comes back is c2, then
c3, and it carries `canSay` so the front end never offers a round the run will
not take. A closed pipe before the confirm means nothing was written, which is
the same outcome as saying no.
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
        return (f"Reading the habit for {len(controls)} "
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
    if name == "leave_unbound":
        return (f"Leaving {args.get('output', '')} unbound", args.get("why", ""))
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
        return (f"Read the habit for {len(controls)} control{'' if len(controls) == 1 else 's'}",
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
    if name == "leave_unbound":
        return (f"Left {args.get('output', '')} unbound",
                args.get("why", ""), failed)
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

def chart_for(game, replay, again=False):
    """The game's controls, researched if nobody has charted it yet."""
    slug = research.slugify(game)
    path = os.path.join(CHARTS, slug + ".json")
    # How a game is controlled is a fact about the game, not an answer about the
    # person, so a chart that exists is reused even on a live run. Reading the
    # same twenty pages every launch cost three and a half minutes and told
    # nobody anything new. --research is there for when a game has patched.
    #
    # A replay of a game this tool researched goes back through the researcher
    # instead of the file, so the offline run, the one for when the room has no
    # wifi, still shows the searches and the page reads.
    if replay and research.recorded_for(game):
        pass
    elif os.path.exists(path) and not again:
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
                                mode="replay" if replay else "live",
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
    emit("stage", key="research", title=f"How {game} is controlled",
         why="A binding can only be right if the game's own controls are right, so those "
             "are read first, from the game's own pages.")
    chart_path, chart = chart_for(game, args.replay, args.research)

    emit("stage", key="history", title="What the published profiles already answer",
         why="A control he has bound the same way across years of profiles is already "
             "answered. No model is asked, and neither are you.")
    emit("tool", id="predict", title="Matched this game against every published profile",
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
    # The template ships with "Left Joystick" in the mode name cell, which is the
    # label for the joystick columns, not a name. Left alone, every profile this
    # tool makes is called Left Joystick in the app and on the device.
    finalize.qsf("apply", "--from", work, "--out", work,
                 "--ops", finalize.write_ops(
                     [{"op": "rename_mode", "mode": 0, "name": "Gameplay",
                       "why": "the template's placeholder is a column label"}], "name"),
                 ok=(0, 1))
    plan = built["plan"]
    # The chart's own word for each output, carried to the write so the rows can
    # say "Jump" rather than "kb_space". Column L only, which the device ignores.
    plan["controls"] = chart.get("controls") or {}
    check = built["validation"]
    emit("tool_done", id="predict", state="ok", ms=took, origin="local",
         summary=f"{len(plan['decided'])} of {len(built['spec']['controls'])} answered from "
                 f"the published profiles, {len(plan['asks'])} the evidence will not settle",
         # The whole apply record is thousands of lines and the same facts are
         # already in the rows below, so what travels is what a person would
         # actually read: what the device thinks of the file so far.
         detail={"errors": check["errors"], "warnings": check["warnings"],
                 "issues": [{"severity": i["severity"], "cell": i["cell"],
                             "message": i["message"]} for i in check["issues"][:8]]})
    # The same three numbers the run is about, said once, in one place, before
    # the list of rows. "138 controls, 34 answered, 6 that need you" is the whole
    # shape of the job, and reading it off a wall of cards is not the same thing.
    emit("tally", of=len(built["spec"]["controls"]),
         answered=len(plan["decided"]), asking=len(plan["asks"]))
    emit("rows", title="Settled from the published profiles", rows=[
        {"output": d["output"], "inputs": d["inputs"], "function": d["function"],
         "confidence": "evidenced",
         "why": f"{d['seenIn']} of {d['ofGames']} of the published profiles do this "
                f"({d['share']:.0%}); nearest example {d['evidence']}"}
        for d in plan["decided"]])

    if not plan["asks"]:
        emit("note", text="The published profiles settled every control this game needs.")
        return qsagent.new_context(plan, []), plan

    emit("stage", key="agent", title="What the evidence could not settle",
         why="Only the controls the published profiles cannot answer go to the model. These "
             "are the ones you may be asked about.")
    ctx = qsagent.new_context(plan, [a["output"] for a in plan["asks"]])
    meanings = plan.get("controlMeanings", {})
    # Everything it would have fetched, handed over up front: what each control
    # does in this game, and every way they have bound it before. Fetching these
    # a control at a time was most of the run.
    unsettled = {
        a["output"]: {
            "inThisGame": meanings.get(a["output"],
                                       "unknown, not in the sourced control list"),
            "theyHaveDone": qsagent.habits_for(plan["habits"], a["output"]),
        } for a in plan["asks"]}
    task = (f"Game: {plan['game']}.\n\nThese {len(plan['asks'])} controls are unsettled. "
            f"For each: what it does in this game, and every way they have bound it "
            f"before, ranked.\n\n" + json.dumps(unsettled, indent=1)
            + "\n\nSettle or ask about all of them in this reply, then call finish.")
    qsagent.agent_loop(task, ctx, verbose=False, tools=qsagent.SETUP_TOOLS,
                       on_event=watcher("Working out the ones the published profiles cannot settle"))
    return ctx, plan


# ---- changing a profile that already exists -------------------------------

# Outputs and inputs are two separate lists on the device. Anything changing a
# row that already exists is almost always about what triggers it, so without
# this the only lookup available searches the wrong half and finds nothing.
FIND_INPUT = {
    "name": "find_input",
    "description": ("Search the sips, puffs, lip and joystick positions the device "
                    "knows. A soft input and a hard one are different names: "
                    "right_puff is a hard puff, right_puff_soft is a light one."),
    "input_schema": {"type": "object", "properties": {
        "query": {"type": "string"}}, "required": ["query"]},
}


EDIT_TOOLS = [
    qsagent.tool("find_output"),
    {
        "name": "read_profile",
        "description": ("The rows this profile already has, so a change lands on the "
                        "right one. Read before changing anything."),
        "input_schema": {"type": "object", "properties": {
            "match": {"type": "string", "description":
                      "an output name, an input name, or part of one. Empty for all."}},
            "required": ["match"]},
    },
    FIND_INPUT,
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
    qsagent.tool("ask_user"),
    qsagent.tool("finish"),
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

def word_for(plan, output):
    """The game's own word for one control, trimmed the way column L trims it.

    The guide and the file have to say the same thing. If the profile ends up
    with "Move Madeline left" in column L, a walkthrough that calls it something
    else is teaching them a name their own file does not use.
    """
    said = ((plan.get("controls") or {}).get(output) or {}).get("action", "")
    return finalize.action_name(said) if said else ""


def with_words(plan, rows):
    """Every row carrying the game's word for it, and whether the game calls it
    critical. Both come off the chart, so neither is the window's invention."""
    controls = plan.get("controls") or {}
    return [{**r, "action": word_for(plan, r["output"]),
             "critical": bool((controls.get(r["output"]) or {}).get("critical"))}
            for r in rows]


def show_map(plan, rows, questions, unbound, untouched):
    """The whole profile as a picture, before a single question is asked.

    This is the event the window draws the device from: every control it worked
    out, on the part of the device it lands on, in the game's words. It goes out
    before the interview on purpose. Being asked "sprint: hold or toggle?" makes
    a different kind of sense once you have already seen where everything else
    on your mouthpiece went.
    """
    emit("map", game=plan["game"], rows=with_words(plan, rows),
         open=[{"output": q["output"], "action": word_for(plan, q["output"]),
                "question": q["question"]} for q in questions],
         left=[{"output": u["output"], "action": word_for(plan, u["output"]),
                "why": u["why"]} for u in unbound],
         untouched=list(untouched))


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


def approved(said):
    """Exactly the boolean true, and nothing that merely looks like it.

    "false" as a string, 1, and an empty object are all truthy in Python, and
    each of them would authorise a write nobody approved.
    """
    return said.get("write") is True


def spoken(said):
    """What they typed about the list, or nothing. A blank line is not a
    sentence, and treating one as a request would spend a model call on it."""
    text = said.get("say")
    return text.strip() if isinstance(text, str) else ""


def taken_off(said, rows):
    """The rows they unticked, as positions in the list they were shown.

    Positions, not names: one output can sit on two rows, and a name would be
    ambiguous in exactly the case where getting it wrong writes a binding they
    took off the list.
    """
    asked = said.get("skip")
    if not isinstance(asked, list):
        return set()
    return {n for n in asked if isinstance(n, int) and not isinstance(n, bool)
            and 0 <= n < len(rows)}


# How many times one run will go round on their say-so. Past this it is a
# conversation about a file that still does not exist, and the change box over
# a written profile is the better place for the next one.
MAX_ROUNDS = 5

def revise_tools():
    """The setup tools, said again for a turn that is not setting anything up.

    Three of them arrive describing a job where every control still needs an
    answer, and left as they are they tell a model to account for rows nobody
    asked it about. The schemas are the same; only what they are for changed.
    """
    said = {
        "propose_binding": ("Replace one row on the list they are looking at. The reason "
                            "is shown to them beside the row, so it says what their "
                            "sentence asked for, not that you decided something."),
        "leave_unbound": ("Take one row off the list, so nothing is written for that "
                          "control and they are told it was left. Use this when what "
                          "they said was to drop it, never to skip a row you find hard."),
        "finish": ("Done. Call once, with a summary of what you changed and why, or of "
                   "what you could not tell and so did not change."),
    }
    tools = [qsagent.tool("find_output"), FIND_INPUT, qsagent.tool("propose_binding"),
             qsagent.tool("leave_unbound"), qsagent.tool("finish")]
    return [{**t, "description": said.get(t["name"], t["description"])} for t in tools]


REVISE_TOOLS = revise_tools()

REVISE_SYSTEM = """They have just been shown a whole profile, and nothing has been \
written yet. They said something about it. You change what that sentence asks for, and \
nothing else.

A QuadStick is a mouth-operated controller: sips, puffs, a lip sensor and a joystick, \
used by people who cannot use their hands. This profile is how they will play, and for \
some of them work and talk.

The rules, in order:
- Change only the rows their sentence is about. Every other row on that list is staying
  exactly as it is, and re-proposing one you were not asked about is a change nobody
  asked for.
- propose_binding replaces a row on the list. leave_unbound takes one off it.
- Never invent a token. Look it up. The device matches names whole and case sensitively,
  so a near miss is silently dead rather than wrong.
- If their sentence could mean two different rows, change nothing and say which two in
  your summary. They see the whole list again either way, so saying you could not tell
  costs them one sentence and a wrong guess costs them a session.
- Do it in one reply, then call finish saying what you changed and why."""


def revise_tool(name, args, ctx):
    """The setup tools, over the list they were just shown.

    finish is not the setup one. There every control had to end with an answer,
    so finishing early was refused; here every row already has one, and a model
    made to account for all of them would re-propose rows nobody asked about.
    Finishing with nothing changed is a real answer and is reported as one.
    """
    # A tool it was not given is refused here rather than left to run_tool, which
    # knows ask_user and would record a question nobody is ever going to be
    # asked. A question that reaches no one is the quietest way to say nothing.
    if name not in {t["name"] for t in REVISE_TOOLS}:
        return {"error": f"{name} is not one of the tools for this. You have "
                         f"{', '.join(t['name'] for t in REVISE_TOOLS)}. There is nobody "
                         f"to ask at this point: they are looking at the list right now."}
    if name == "find_input":
        if not isinstance(args, dict) or not isinstance(args.get("query"), str):
            return {"error": "find_input takes a query, as a string."}
        wanted = args["query"].lower().replace(" ", "_")
        return {"matches": sorted(i for i in ctx["inputs"] if wanted in i.lower())[:25],
                "note": "exact spelling and case, as the device reads it"}
    if name == "finish":
        if not isinstance(args, dict) or not str(args.get("summary") or "").strip():
            return {"error": "finish needs a summary: what you changed, or that you "
                             "changed nothing and why."}
        ctx["done"] = args["summary"]
        return {"ok": True}
    return qsagent.run_tool(name, args, ctx)


def unbind(profile, outputs):
    """Take rows back out of the working copy.

    A row they decline is not just a row left off the list. The deterministic
    pass has already written its answer into the copy, so dropping it from the
    list alone would write the very thing they took off it.

    Deleting a row renumbers everything under it, so these go in descending
    order and the numbers read before the first delete stay true.
    """
    if not outputs:
        return 0
    read = finalize.qsf("inspect", profile)
    rows = sorted((b["row"] for pf in read.get("profiles") or []
                   for mode in pf.get("modes") or []
                   for b in mode.get("bindings") or [] if b["output"] in outputs),
                  reverse=True)
    if not rows:
        return 0
    ops = finalize.write_ops([{"op": "delete_row", "row": row,
                               "why": "they took this one off the list"} for row in rows],
                             "decline")
    done = finalize.qsf("apply", "--from", profile, "--ops", ops, "--out", profile, ok=(0, 1))
    if not done["ok"]:
        raise Stopped("a control you took off the list could not be removed from the "
                      "profile, so nothing was written at all")
    return len(rows)


def reworker(plan, work):
    """A turn over the list they were just shown, in their own words.

    Bound to the plan and the working copy so the confirm loop can stay one
    thing that shows a list and reads a reply, whichever run it belongs to.
    """
    def rework(said, shown, settled, left):
        vocab = finalize.qsf("vocab")
        ctx = {"habits": plan["habits"], "outputs": plan["outputs"],
               "inputs": sorted(set(vocab["inputs"]) | set(vocab["legacyInputs"])),
               # Every row on the list is fair game, and so is anything being
               # left off it: "actually bind crouch after all" is a sentence.
               "unresolved": ({r["output"] for r in shown}
                              | {u["output"] for u in left}),
               "settled": set(), "proposals": [], "questions": [], "unbound": [],
               "done": None}
        listed = "\n".join(
            f"  {r['output']}  {r.get('action') or ''}"
            f"  ->  {' + '.join(r['inputs'])}, {r.get('function') or 'normal'}"
            for r in shown)
        off = "\n".join(f"  {u['output']}  {u.get('action') or ''}  ->  nothing, on purpose"
                        for u in left)
        task = (f"Game: {plan['game']}. This is the profile they have just been shown. "
                f"Nothing has been written.\n\n{listed}\n"
                + (f"\nLeft off it:\n{off}\n" if off else "")
                + f"\nThey said, about it:\n\n  “{said}”\n\n"
                f"Change only what that asks for, then finish.")
        qsagent.agent_loop(task, ctx, verbose=False, system=REVISE_SYSTEM,
                           tools=REVISE_TOOLS, runner=revise_tool,
                           on_event=watcher("Working out what they asked to change"))
        changed = {p["output"]: p for p in ctx["proposals"]}
        gone = {u["output"] for u in ctx["unbound"]}
        if not changed and not gone:
            emit("note", text=(ctx["done"] or "Nothing on the list changed.")
                 + " Say it another way, or write it as it is and change it in the editor.")
            return shown, settled, left

        def redo(rows):
            return [{**r, **changed[r["output"]], "confidence": "you asked for this"}
                    if r["output"] in changed else r
                    for r in rows if r["output"] not in gone]

        was_shown = {r["output"] for r in shown}
        added = with_words(plan, [{**p, "confidence": "you asked for this"}
                                  for p in ctx["proposals"] if p["output"] not in was_shown])
        # One entry per control, so a control left off twice is not listed twice
        # and one that is being bound after all stops being listed as left.
        off = {u["output"]: u for u in left if u["output"] not in changed}
        off.update({u["output"]: u for u in ctx["unbound"]})
        # A row taken off the list comes out of the working copy in the same
        # breath, for the same reason a declined one does.
        unbind(work, gone)
        return redo(shown) + added, redo(settled) + added, list(off.values())
    return rework


def confirm_and_write(profile, settled, open_questions, untouched, out=None, shown=None,
                      fresh=False, controls=None, left=(), rework=None):
    """Show the whole list, take back what they decline, write what is left.

    `shown` is what the person is approving, which for a new profile is every
    row it will contain. `settled` is only the part still to be applied on top
    of what the deterministic pass already put in the working copy.

    Three ways out, because yes-or-nothing over fifty rows is not something a
    person can steer: write it, write it without the rows they unticked, or say
    what is wrong in their own words and see the whole list again.
    """
    out = out or profile
    shown = list(shown if shown is not None else settled)
    settled, left = list(settled), list(left)
    declined = []

    for round_ in range(1, MAX_ROUNDS + 1):
        # Whether another round is on offer travels with the list, so nothing
        # can offer a change this run has already decided it will not make.
        again = rework is not None and round_ < MAX_ROUNDS
        emit("confirm", id=f"c{round_}", profile=out, rows=shown, canSay=again,
             open=[{"output": q["output"], "question": q["question"]} for q in open_questions],
             left=[{"output": u["output"], "why": u["why"]} for u in left],
             untouched=list(untouched))
        said = listen(f"c{round_}")

        if spoken(said) and again:
            emit("stage", key="rework", title=f"“{spoken(said)}”",
                 why="Nothing has been written. This changes what is on the list, and then "
                     "shows you the whole list again.")
            shown, settled, left = rework(spoken(said), shown, settled, left)
            continue
        if spoken(said):
            raise Stopped(f"“{spoken(said)}” was not applied: that is as many rounds of "
                          f"changes as one run makes. Nothing was written, and the profile "
                          f"is exactly as it was. Set it up again, or write it and ask for "
                          f"the change from the editor.")
        if not approved(said):
            raise Stopped("nothing was written, and the profile is exactly as it was")

        off = taken_off(said, shown)
        declined = [shown[n] for n in sorted(off)]
        if declined:
            gone = {r["output"] for r in declined}
            shown = [r for n, r in enumerate(shown) if n not in off]
            settled = [r for r in settled if r["output"] not in gone]
            # Off the list is not enough. The deterministic pass has already put
            # its answer in the working copy, so a row they unticked has to come
            # out of the file too or it is written anyway. By name here, not by
            # position, because that is what the file is keyed on, and on this
            # path one control is one row: the two passes that fill this list
            # both refuse a second answer for a control that already has one.
            unbind(profile, gone)
            emit("note", text=f"{len(declined)} you took off the list, so "
                              f"{'it is' if len(declined) == 1 else 'they are'} left "
                              f"unbound: {', '.join(sorted(gone))}.")
        if not shown:
            raise Stopped("you took every row off the list, so nothing was written and the "
                          "profile is exactly as it was")
        break

    done = finalize.apply_settled(profile, settled, out, fresh=fresh, controls=controls,
                                  log=lambda text: emit("note", text=text))
    if not done["ok"]:
        emit("failed", message=f"{done['error']}, so nothing was written and the profile "
                               f"is exactly as it was", detail=done)
        return False
    check = done["validation"]
    # What they approved and what they are told was written have to be the same
    # number. `settled` is only the second pass; the file holds every row shown.
    emit("done", profile=out, written=len(shown),
         errors=check["errors"], warnings=check["warnings"],
         issues=[{"severity": i["severity"], "cell": i["cell"], "message": i["message"]}
                 for i in check["issues"][:12]],
         open=[{"output": q["output"], "question": q["question"]} for q in open_questions],
         left=[{"output": u["output"], "why": u["why"]} for u in left],
         # A row they took off is not a row that was never there. It is named
         # here for the same reason an unanswered question is.
         declined=[{"output": r["output"], "action": r.get("action") or ""}
                   for r in declined],
         named=done.get("named", 0),
         untouched=list(untouched))
    return True


def edit_run(args, committed):
    """Work out the change they asked for, show it, and write it if they say so.

    The same three ways out as a new profile. Saying something here starts the
    request again in their new words rather than reworking a list, because
    nothing has been written and their own profile is still the thing being
    changed, so a fresh sentence about it is the whole job again.
    """
    request = args.request
    for round_ in range(1, MAX_ROUNDS + 1):
        args.request = request
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
                 issues=[], open=[], untouched=[], declined=[])
            emit("note", text="nothing was changed.")
            return 0

        again = round_ < MAX_ROUNDS
        shown = [{"output": c["output"], "inputs": c["inputs"], "function": c["function"],
                  "why": c["why"], "confidence": "asked for",
                  "was": f"{c['was']['output']}, {c['was']['function']}, "
                         f"{' + '.join(c['was']['inputs'])}", "row": c["row"]}
                 for c in ctx["changes"]]
        emit("confirm", id=f"c{round_}", profile=args.out, rows=shown, canSay=again,
             open=[], untouched=[])
        said = listen(f"c{round_}")

        if spoken(said) and again:
            request = spoken(said)
            emit("note", text="Nothing has been written, so this starts again from your "
                              "profile exactly as it is now.")
            continue
        if spoken(said):
            raise Stopped(f"“{spoken(said)}” was not applied: that is as many rounds of "
                          f"changes as one run makes. Nothing was written, and the profile "
                          f"is exactly as it was.")
        if not approved(said):
            raise Stopped("nothing was written, and the profile is exactly as it was")

        off = taken_off(said, shown)
        if off:
            # Positions in the list they were shown, and that list is built from
            # ctx["changes"] in order, so the two line up row for row.
            emit("note", text=f"{len(off)} you took off the list, and "
                              f"{'that row is' if len(off) == 1 else 'those rows are'} "
                              f"exactly as they were: "
                              f"{', '.join(sorted(shown[n]['output'] for n in off))}.")
            ctx["changes"] = [c for n, c in enumerate(ctx["changes"]) if n not in off]
        if not ctx["changes"]:
            raise Stopped("you took every change off the list, so nothing was written and "
                          "the profile is exactly as it was")

        done = apply_changes(args.edit, ctx["changes"], args.out, committed)
        if not done["ok"]:
            emit("failed", message="the change was refused, so the profile is exactly "
                                   "as it was", detail=done)
            return 1
        emit("done", profile=args.out, written=len(ctx["changes"]),
             errors=done["validation"]["errors"], warnings=done["validation"]["warnings"],
             issues=[], open=[], untouched=[],
             declined=[{"output": shown[n]["output"], "action": ""} for n in sorted(off)],
             diff=done["diff"])
        return 0


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
    ap.add_argument("--research", action="store_true",
                    help="read the web about the game again, even if it is charted")
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
               "live": "asks the model for every binding. A game already charted is not read again",
               "replay": "from the recording only, no network"}[mode])
    if not args.game and not (args.edit and args.request):
        emit("failed", message="give either --game, or --edit with --request")
        return 2

    committed = []
    try:
        if args.edit:
            args.out = args.out or args.edit
            return edit_run(args, committed)

        args.out = args.out or free_name(os.path.join(
            os.path.expanduser("~/Documents"), research.slugify(args.game) + ".csv"))
        work = os.path.join(os.environ.get("TMPDIR", "/tmp"),
                            f"qs-building-{os.getpid()}.csv")
        try:
            ctx, plan = create(args, work)
            result = qsagent.brief(ctx)
            asked = {q["output"] for q in result["questions"]}
            # A proposal for a control that was also asked about is dropped
            # unless they answered it. The agent cannot do both any more, but a
            # profile is not the place to rely on that: an unanswered question
            # must never come out of this as a written row.
            proposed = [p for p in result["proposals"] if p["output"] not in asked]
            already = [{"output": d["output"], "inputs": d["inputs"], "function": d["function"],
                        "confidence": "evidenced",
                        "why": f"{d['seenIn']} of {d['ofGames']} of the published profiles do this "
                               f"({d['share']:.0%}); nearest example {d['evidence']}"}
                       for d in plan["decided"]]
            show_map(plan, already + proposed, result["questions"],
                     result.get("unbound", ()), result["untouched"])
            answers = interview(result["questions"])
            settled = proposed + [
                {"output": output, "inputs": choice["inputs"],
                 "function": choice["function"], "confidence": "chosen by them",
                 "why": "they answered the question about this control"}
                for output, choice in answers.items()]
            still_open = [q for q in result["questions"] if q["output"] not in answers]
            confirm_and_write(work, settled, still_open, result["untouched"],
                              out=args.out, shown=with_words(plan, already + settled),
                              fresh=True, controls=plan.get("controls"),
                              left=result.get("unbound", ()),
                              rework=reworker(plan, work))
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
