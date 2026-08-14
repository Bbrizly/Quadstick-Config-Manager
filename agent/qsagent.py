#!/usr/bin/env python3
"""The agent: settle the bindings a person's own history cannot settle.

The deterministic pass (agent/predict.py) answers what it can from evidence and
stops at the rest. Those leftovers are this agent's whole job, and they are the
interesting ones: a control whose meaning changes from game to game, where the
history says "he has done it four different ways".

What the agent may do is deliberately small:
  - read what the game's controls mean, and what this person habitually does
  - look up a real device token instead of inventing one
  - propose a binding, with a reason naming its evidence
  - ASK, when the evidence does not decide it
  - finish, once

What it cannot do: write a cell directly, invent a token, or keep going. The
loop is bounded, the write path is qsf, and qsf refuses anything the device
would not read. No amount of confident wrong reasoning reaches the file.

    python3 agent/qsagent.py --plan plan.json --report decisions.json
    QSF_MODEL_MODE=replay python3 agent/qsagent.py ...   # demo, from cache only
"""
import argparse
import hashlib
import json
import os
import random
import ssl
import sys
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, "cache")
API = "https://api.anthropic.com/v1/messages"
MODEL = os.environ.get("QSF_MODEL", "claude-sonnet-5")
MODE = os.environ.get("QSF_MODEL_MODE", "auto")   # auto | replay | live

MAX_STEPS = 20            # the loop is bounded because a runaway loop on stage
MAX_REPAIRS = 2           # is worse than an unfinished profile
RETRY_STATUS = {408, 429, 500, 502, 503, 504, 529}


# ---- the model, with a cache the demo can run from ------------------------

def call_model(system, messages, tools, max_tokens=1500):
    """One turn. Cached by exact request, so a rehearsed run needs no network.

    The cache key covers the model, the system prompt, the whole conversation
    and the tool list. Anything that would change the answer changes the key,
    which is the point: a stale hit would be worse than a miss.
    """
    payload = {"model": MODEL, "max_tokens": max_tokens, "system": system,
               "messages": messages, "tools": tools}
    key = hashlib.sha256(json.dumps(payload, sort_keys=True).encode()).hexdigest()[:32]
    path = os.path.join(CACHE, key + ".json")

    if os.path.exists(path):
        with open(path) as f:
            return json.load(f), "cache"
    if MODE == "replay":
        raise SystemExit(
            f"replay mode and nothing cached for this request ({key}).\n"
            f"This exact conversation has not been recorded. Run once with "
            f"QSF_MODEL_MODE=live to record it, or check that the plan file "
            f"and the corpus are the ones the recording was made from.")

    api_key = os.environ.get("ANTHROPIC_API_KEY")
    if not api_key:
        raise SystemExit("no ANTHROPIC_API_KEY set, and nothing cached for this request.")

    body = json.dumps(payload).encode()
    request = urllib.request.Request(API, data=body, method="POST", headers={
        "x-api-key": api_key,
        "anthropic-version": "2023-06-01",
        "content-type": "application/json",
    })
    last = None
    for attempt in range(5):
        try:
            with urllib.request.urlopen(request, timeout=120,
                                        context=ssl.create_default_context()) as r:
                reply = json.loads(r.read())
            os.makedirs(CACHE, exist_ok=True)
            with open(path, "w") as f:
                json.dump(reply, f)
            return reply, "live"
        except urllib.error.HTTPError as e:
            last = f"HTTP {e.code}: {e.read()[:200].decode(errors='replace')}"
            if e.code not in RETRY_STATUS:
                raise SystemExit(f"the model refused this request. {last}")
        except (urllib.error.URLError, TimeoutError) as e:
            last = str(e)
        wait = min(2 ** attempt + random.random(), 20)
        print(f"    model call failed ({last}); retrying in {wait:.1f}s", flush=True)
        time.sleep(wait)
    raise SystemExit(f"the model did not answer after 5 tries. Last error: {last}")


# ---- what the agent is allowed to do -------------------------------------

TOOLS = [
    {
        "name": "read_habits",
        "description": (
            "What this person has bound a control to in their other profiles, ranked, "
            "with the game and row each option came from. Use this before proposing "
            "anything. A lopsided result is evidence; an even split is not."),
        "input_schema": {"type": "object", "properties": {
            "controls": {"type": "array", "items": {"type": "string"},
                         "description": "device output tokens, e.g. kb_left_shift"}},
            "required": ["controls"]},
    },
    {
        "name": "find_output",
        "description": (
            "Search the device's real vocabulary. The device matches names whole and "
            "case sensitively, so guessing a name is how a profile silently stops "
            "working: it is kb_left_shift, never kb_lshift."),
        "input_schema": {"type": "object", "properties": {
            "query": {"type": "string"}}, "required": ["query"]},
    },
    {
        "name": "propose_binding",
        "description": (
            "Bind one control. The reason is written into the record beside the row "
            "and shown to the user, so it must name the evidence it rests on, not "
            "restate the decision."),
        "input_schema": {"type": "object", "properties": {
            "output": {"type": "string"},
            "inputs": {"type": "array", "items": {"type": "string"}},
            "function": {"type": "string", "description": "normal, toggle, tap, repeat, ..."},
            "why": {"type": "string"},
            "confidence": {"type": "string", "enum": ["evidenced", "inferred"]}},
            "required": ["output", "inputs", "function", "why", "confidence"]},
    },
    {
        "name": "ask_user",
        "description": (
            "Stop and ask. Correct whenever the evidence does not decide it. An "
            "unasked question that turns into a wrong binding is worse than a "
            "question: this is a control someone plays and works through with their "
            "mouth. Offer their own past choices as the options."),
        "input_schema": {"type": "object", "properties": {
            "output": {"type": "string"},
            "question": {"type": "string"},
            "options": {"type": "array", "items": {"type": "string"}}},
            "required": ["output", "question", "options"]},
    },
    {
        "name": "finish",
        "description": "Done deciding. Call once, when every control has a proposal or a question.",
        "input_schema": {"type": "object", "properties": {
            "summary": {"type": "string"}}, "required": ["summary"]},
    },
]

SYSTEM = """You set up a QuadStick for someone. A QuadStick is a mouth-operated \
controller: sips, puffs, a lip sensor and a joystick, used by people who cannot use \
their hands. The profile you are helping build is how they will play, and for some of \
them work and talk.

You are finishing a job, not starting one. The controls handed to you are the ones this \
person's own history could NOT settle, because they have done them several different \
ways across their other games. That is the whole reason you are here.

How to decide one:
- Read what the control does in THIS game, and what they habitually do for that same
  job in other games. Those are two different facts and you need both.
- A binding is `evidenced` when their own profiles show them doing this for this job.
  It is `inferred` when you are reasoning across games. Say which. Do not dress one up
  as the other.
- If the evidence does not decide it, ASK. Asking is the right answer, not a failure.
  You are not scored on how few questions you ask. A wrong binding costs them a
  playthrough and a support call; a question costs them ten seconds.
- Never invent a token. Look it up. The device matches names case sensitively and
  whole, so a near miss is silently dead rather than wrong.
- Do not change something they did not ask you to change.

Work one control at a time. When each has a proposal or a question, call finish."""


def run_tool(name, args, ctx):
    """The tools are plain lookups over data the caller already has."""
    if name == "read_habits":
        out = {}
        for control in args["controls"][:12]:
            ranked = ctx["habits"].get(control, [])
            out[control] = [{
                "inputs": o["inputs"], "function": o["function"],
                "usedIn": f"{o['seenIn']} of {o['ofGames']} of their games "
                          f"({o['share']:.0%})",
                "example": o["evidence"],
            } for o in ranked[:4]] or "they have never bound this control"
        return out
    if name == "find_output":
        q = args["query"].lower().replace(" ", "_")
        hits = [o for o in ctx["outputs"] if q in o.lower()]
        return {"matches": sorted(hits)[:25],
                "note": "exact spelling and case, as the device reads it"}
    if name == "propose_binding":
        ctx["proposals"].append(args)
        return {"recorded": args["output"]}
    if name == "ask_user":
        ctx["questions"].append(args)
        return {"asked": args["output"], "note": "the person will answer this before anything is written"}
    if name == "finish":
        ctx["done"] = args["summary"]
        return {"ok": True}
    return {"error": f"no tool called {name}"}


def agent_loop(task, ctx, verbose=True):
    """Bounded. Every step is printed, because a loop nobody can see is a claim."""
    messages = [{"role": "user", "content": task}]
    for step in range(1, MAX_STEPS + 1):
        reply, origin = call_model(SYSTEM, messages, TOOLS)
        blocks = reply.get("content", [])
        messages.append({"role": "assistant", "content": blocks})

        said = "".join(b.get("text", "") for b in blocks if b.get("type") == "text").strip()
        if verbose and said:
            print(f"  [{step}] {said[:300]}", flush=True)

        calls = [b for b in blocks if b.get("type") == "tool_use"]
        if not calls:
            if verbose:
                print(f"  [{step}] stopped without acting ({origin})")
            return step, False

        results = []
        for call in calls:
            result = run_tool(call["name"], call["input"], ctx)
            if verbose:
                detail = call["input"].get("output") or call["input"].get("query") or ""
                print(f"  [{step}] {call['name']}({detail}) [{origin}]", flush=True)
            results.append({"type": "tool_result", "tool_use_id": call["id"],
                            "content": json.dumps(result)})
        messages.append({"role": "user", "content": results})

        if ctx.get("done"):
            if verbose:
                print(f"  [{step}] finished: {ctx['done'][:200]}")
            return step, True
    return MAX_STEPS, False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--plan", required=True,
                    help="JSON from predict.py --trace, plus the game's control meanings")
    ap.add_argument("--report", default=None)
    args = ap.parse_args()

    plan = json.load(open(args.plan))
    unresolved = [a["output"] for a in plan["asks"]]
    if not unresolved:
        print("nothing left ambiguous; the deterministic pass settled everything")
        return 0

    ctx = {"habits": plan["habits"], "outputs": plan["outputs"],
           "proposals": [], "questions": [], "done": None}

    meanings = plan.get("controlMeanings", {})
    task = (
        f"Game: {plan['game']}.\n\n"
        f"These {len(unresolved)} controls are unsettled. For each, what it does in this "
        f"game, where that is known:\n"
        + "\n".join(f"  {c}: {meanings.get(c, 'unknown, not in the sourced control list')}"
                    for c in unresolved)
        + "\n\nRead their habits, then settle each one or ask about it."
    )

    print(f"{len(unresolved)} controls the evidence did not settle. Model: {MODEL}, mode: {MODE}")
    steps, finished = agent_loop(task, ctx)
    print(f"\n{steps} steps, {'finished' if finished else 'hit the step budget'}: "
          f"{len(ctx['proposals'])} proposed, {len(ctx['questions'])} to ask")

    report = {"game": plan["game"], "steps": steps, "finished": finished,
              "proposals": ctx["proposals"], "questions": ctx["questions"],
              "summary": ctx["done"]}
    if args.report:
        with open(args.report, "w") as f:
            json.dump(report, f, indent=2)
        print(f"written to {args.report}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
