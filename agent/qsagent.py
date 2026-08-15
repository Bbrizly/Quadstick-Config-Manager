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

The model is reached through the Claude Code CLI, which is already signed in,
so this needs no API key. Set ANTHROPIC_API_KEY to use the API instead.

    python3 agent/qsagent.py --plan plan.json --report decisions.json
    QSF_MODEL_MODE=replay python3 agent/qsagent.py ...   # demo, from cache only
"""
import argparse
import hashlib
import json
import os
import random
import shutil
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, "cache")
API = "https://api.anthropic.com/v1/messages"
CLAUDE_BIN = os.environ.get("QSF_CLAUDE_BIN") or shutil.which("claude") or "claude"
MODEL = os.environ.get("QSF_MODEL", "claude-sonnet-5")
MODE = os.environ.get("QSF_MODEL_MODE", "auto")   # auto | replay | live

# Two ways to reach the model, and no key is the normal case. The Claude Code
# CLI is already signed in to a subscription, so `claude -p` is the default and
# an ANTHROPIC_API_KEY is only an alternative, never a requirement.
BACKEND = os.environ.get("QSF_MODEL_BACKEND") or (
    "api" if os.environ.get("ANTHROPIC_API_KEY") else "cli")

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
               "messages": messages, "tools": tools, "backend": BACKEND}
    if BACKEND == "cli":
        # The reply protocol is part of the request. Replaying an answer given
        # under a different one would be a stale hit wearing a fresh answer's
        # clothes, which is the one thing this cache must not do.
        payload["protocol"] = PROTOCOL
    key = hashlib.sha256(json.dumps(payload, sort_keys=True).encode()).hexdigest()[:32]
    path = os.path.join(CACHE, key + ".json")

    # live asks the model even when the answer is already recorded, which is how
    # a recording gets refreshed and how anyone can check that the cache is not
    # what is doing the thinking.
    if os.path.exists(path) and MODE != "live":
        with open(path) as f:
            recorded = json.load(f)
        # A recording is a file on disk, so it can be truncated, hand edited or
        # left over from an older shape. Reading one that is not a reply at all
        # would crash the loop, or worse, look like a model that said nothing.
        if not isinstance(recorded, dict) or not isinstance(recorded.get("content"), list):
            raise SystemExit(
                f"the recorded answer at {path} is not a model reply.\n"
                f"Delete that file and run again, or run with QSF_MODEL_MODE=live "
                f"to record it fresh.")
        return recorded, "cache"
    if MODE == "replay":
        raise SystemExit(
            f"replay mode and nothing cached for this request ({key}).\n"
            f"This exact conversation has not been recorded. Run once with "
            f"QSF_MODEL_MODE=live to record it, or check that the plan file "
            f"and the corpus are the ones the recording was made from.")

    if BACKEND == "cli":
        reply = call_cli(system, messages, tools)
        record(path, reply)
        return reply, "live"

    api_key = os.environ.get("ANTHROPIC_API_KEY")
    if not api_key:
        raise SystemExit("QSF_MODEL_BACKEND=api was asked for, but no ANTHROPIC_API_KEY is set.")

    body = json.dumps({k: v for k, v in payload.items() if k != "backend"}).encode()
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
            record(path, reply)
            return reply, "live"
        except urllib.error.HTTPError as e:
            last = f"HTTP {e.code}: {e.read()[:200].decode(errors='replace')}"
            if e.code not in RETRY_STATUS:
                raise SystemExit(f"the model refused this request. {last}")
        except (urllib.error.URLError, TimeoutError) as e:
            last = str(e)
        wait = min(2 ** attempt + random.random(), 20)
        print(f"    model call failed ({last}); retrying in {wait:.1f}s",
              file=sys.stderr, flush=True)
        time.sleep(wait)
    raise SystemExit(f"the model did not answer after 5 tries. Last error: {last}")


def record(path, reply):
    os.makedirs(CACHE, exist_ok=True)
    with open(path, "w") as f:
        json.dump(reply, f)


# ---- the same model, reached through the signed-in CLI --------------------
#
# `claude -p` has no tool-schema argument, so the tools are described in the
# system prompt and the reply is constrained with --json-schema to one call at
# a time. What comes back is reshaped into the API's own tool_use block, so the
# loop below cannot tell the two backends apart and neither can its tests.

def render(messages):
    """Flatten the conversation into the transcript the CLI is given."""
    lines = []
    for message in messages:
        content = message["content"]
        if isinstance(content, str):
            lines.append(content)
            continue
        for block in content:
            kind = block.get("type")
            if kind == "text":
                lines.append(block["text"])
            elif kind == "tool_use":
                lines.append(f"\nYou called {block['name']}({json.dumps(block['input'])})")
            elif kind == "tool_result":
                lines.append(f"It returned: {block['content']}")
    return "\n".join(lines)


def tool_manual(tools):
    parts = ["The tools you may call. Each reply calls exactly one of them."]
    for tool in tools:
        parts.append(f"\n{tool['name']}\n  {tool['description']}\n"
                     f"  input: {json.dumps(tool['input_schema'])}")
    return "\n".join(parts)


# One reply carries every call the model is ready to make. Not a nicety: 21
# unsettled controls cannot be settled one per reply inside a 20 step budget,
# and every reply is a fresh process. Batching is also what the API backend
# does natively, so the two stay the same shape.
#
# The contract is stated here rather than through --json-schema on purpose.
# That flag makes the CLI answer in prose first and reformat afterwards, which
# measured at 131s and 11,500 output tokens for a step that costs 2.6s and 144
# this way. Same JSON either way.
PROTOCOL = """

REPLY FORMAT. Your entire reply is one JSON object and nothing else. No prose \
around it, no markdown fence:
{"calls": [{"tool": "<name>", "input": {...}}, ...]}
Put every call you are making now in that list."""


def cli_system(system, tools):
    return system + "\n\n" + tool_manual(tools) + PROTOCOL


def call_cli(system, messages, tools):
    command = [
        CLAUDE_BIN, "-p", render(messages) + (
            "\n\nDecide what to do now. Settle or ask about as many controls as "
            "you can in this one reply."),
        "--model", MODEL,
        "--output-format", "json",
        "--system-prompt", cli_system(system, tools),
        # The CLI's own tools, skills and MCP servers are not this agent's tools,
        # and leaving them on costs 40k tokens of context it must not act on.
        "--tools", "",
        "--disable-slash-commands",
        "--strict-mcp-config", "--mcp-config", '{"mcpServers":{}}',
        # Every reply is a fresh process, so this machine's hooks, plugins and
        # CLAUDE.md would otherwise load into each one. The agent's context has
        # to be only what this file put there.
        "--safe-mode",
        "--no-session-persistence",
    ]
    last = None
    for attempt in range(4):
        try:
            # DEVNULL because the person's answers may be arriving on stdin;
            # the model must never eat a keystroke meant for them.
            done = subprocess.run(command, capture_output=True, text=True,
                                  timeout=900, stdin=subprocess.DEVNULL)
            if done.returncode == 0:
                answer = json.loads(done.stdout)
                if not answer.get("is_error"):
                    return shape(answer)
                last = str(answer.get("result"))[:200]
            else:
                last = (done.stderr or done.stdout)[:200]
        except (subprocess.TimeoutExpired, json.JSONDecodeError, ValueError) as e:
            last = str(e)[:200]
        wait = min(2 ** attempt + random.random(), 20)
        print(f"    the CLI did not answer ({last}); retrying in {wait:.1f}s",
              file=sys.stderr, flush=True)
        time.sleep(wait)
    raise SystemExit(
        f"`claude -p` did not answer after 4 tries. Last error: {last}\n"
        f"Check that {CLAUDE_BIN} runs and is signed in.")


def shape(answer):
    """Turn one CLI reply into the API's message shape, or into plain text.

    A reply that names no tool becomes a text block, which stops the loop
    cleanly. Guessing at what a malformed answer meant is how a wrong binding
    gets written, so nothing is guessed here.
    """
    reply = answer.get("structured_output")
    if not isinstance(reply, dict):
        text = (answer.get("result") or "").strip()
        if text.startswith("```"):           # asked for no fence, sometimes fenced anyway
            text = text.split("```")[1].removeprefix("json").strip()
        try:
            reply = json.loads(text)
        except (json.JSONDecodeError, TypeError):
            reply = None
    calls = reply.get("calls") if isinstance(reply, dict) else None
    blocks = [{"type": "tool_use", "id": f"{answer.get('uuid', 'cli')}-{i}",
               "name": c["tool"], "input": c.get("input") or {}}
              for i, c in enumerate(calls or []) if isinstance(c, dict) and c.get("tool")]
    if not blocks:
        return {"content": [{"type": "text", "text": str(answer.get("result", ""))[:2000]}]}
    return {"content": blocks}


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
            "mouth. Offer their own past choices as the options. Every option is "
            "bound exactly as you write it, so it carries real device tokens, not a "
            "description of them, and the label is what gets read aloud to them."),
        "input_schema": {"type": "object", "properties": {
            "output": {"type": "string"},
            "question": {"type": "string", "description": "plain words, no device jargon"},
            "options": {"type": "array", "description":
                        "each one ready to bind as it stands, best first", "items": {
                            "type": "object", "properties": {
                                "inputs": {"type": "array", "items": {"type": "string"}},
                                "function": {"type": "string"},
                                "label": {"type": "string", "description":
                                          "how they will hear it read out, with the "
                                          "share and the game and row it came from"}},
                            "required": ["inputs", "function", "label"]}}},
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

Read habits first, then settle or ask about every control you can in each reply.
When each one has a proposal or a question, call finish."""


REQUIRED = {t["name"]: t["input_schema"].get("required", []) for t in TOOLS}


# The shapes the write path depends on. A model that sends a string where a
# list belongs would otherwise be recorded as a proposal and then read one
# character at a time, so `inputs: "mp_left_sip"` becomes eleven inputs.
SHAPES = {"controls": list, "inputs": list, "output": str, "function": str,
          "query": str, "why": str, "summary": str, "question": str, "options": list}


def run_tool(name, args, ctx):
    """The tools are plain lookups over data the caller already has."""
    if not isinstance(args, dict):
        return {"error": f"{name} was called with {type(args).__name__}, not an object. "
                         f"Call it again with a JSON object of arguments."}
    # A half-filled call is sent back to be redone rather than half-recorded. A
    # proposal missing its reason would otherwise reach the file with no reason.
    missing = [k for k in REQUIRED.get(name, []) if k not in args]
    if missing:
        return {"error": f"{name} needs {', '.join(missing)}, which you left out. "
                         f"Call it again with all of them."}
    wrong = [f"{k} must be a {SHAPES[k].__name__}, not a {type(v).__name__}"
             for k, v in args.items() if k in SHAPES and not isinstance(v, SHAPES[k])]
    if wrong:
        return {"error": f"{name}: {'; '.join(wrong)}. Call it again."}
    if name == "read_habits":
        out = {}
        for control in args["controls"][:12]:
            if not isinstance(control, str):
                return {"error": "read_habits takes a list of output names as strings."}
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
        if not all(isinstance(i, str) for i in args["inputs"]):
            return {"error": "every input must be a device input name, as a string."}
        # A binding with nothing to trigger it is a row the device reads and can
        # never fire. If it should be left alone, leave it out of the report.
        if not args["inputs"]:
            return {"error": f"{args['output']} was proposed with no inputs, which "
                             f"would write a control nothing can trigger. To leave it "
                             f"alone, do not propose it at all."}
        # The job is the controls handed over. A proposal for anything else is a
        # change nobody asked for, and it would reach the file the same way a
        # real one does.
        if args["output"] not in ctx["unresolved"]:
            return {"error": f"{args['output']} is not one of the controls you were "
                             f"asked to settle, so it was not recorded. Only these are "
                             f"open: {', '.join(sorted(ctx['unresolved']))[:400]}"}
        if args["output"] in ctx["settled"]:
            return {"error": f"{args['output']} already has an answer. Two answers for "
                             f"one control would write two rows that fight each other."}
        ctx["settled"].add(args["output"])
        ctx["proposals"].append(args)
        return {"recorded": args["output"]}
    if name == "ask_user":
        # An option is only worth offering if it can be bound exactly as shown.
        # One that cannot is dropped here, with the reason, rather than reaching
        # a person as a choice that quietly does nothing when they pick it.
        good, bad = [], []
        for o in args["options"]:
            if (isinstance(o, dict) and isinstance(o.get("inputs"), list)
                    and all(isinstance(i, str) for i in o["inputs"])
                    and isinstance(o.get("label"), str)):
                good.append(o)
            else:
                bad.append(o)
        args["options"] = good
        if bad:
            return {"error": f"{len(bad)} of those options could not be bound as written, "
                             f"so they were dropped. Each option needs inputs (a list of "
                             f"device input names), function, and label. Ask again with "
                             f"options that carry real tokens."}
        if args["output"] not in ctx["unresolved"]:
            return {"error": f"{args['output']} is not one of the controls you were asked "
                             f"to settle, so nothing was asked about it."}
        if args["output"] in ctx["settled"]:
            return {"error": f"{args['output']} already has an answer."}
        ctx["settled"].add(args["output"])
        ctx["questions"].append(args)
        return {"asked": args["output"], "note": "the person will answer this before anything is written"}
    if name == "finish":
        # Finishing with controls still untouched used to end the run reporting
        # only what it happened to record, and the rest were never mentioned
        # again. They are named back here so the run cannot end by forgetting.
        left = sorted(ctx["unresolved"] - ctx["settled"])
        if left:
            return {"error": f"{len(left)} controls still have neither a proposal nor a "
                             f"question: {', '.join(left)[:600]}. Settle or ask about "
                             f"each one, then finish."}
        ctx["done"] = args["summary"]
        return {"ok": True}
    return {"error": f"no tool called {name}"}


def new_context(plan, unresolved):
    """Everything the tools read, plus the two sets that keep the agent honest:
    which controls it was asked about, and which it has answered."""
    return {"habits": plan["habits"], "outputs": plan["outputs"],
            "unresolved": set(unresolved), "settled": set(),
            "proposals": [], "questions": [], "done": None}


def brief(ctx):
    """What the run actually produced, including what it never touched."""
    return {"proposals": ctx["proposals"], "questions": ctx["questions"],
            "untouched": sorted(ctx["unresolved"] - ctx["settled"]),
            "summary": ctx["done"]}


def agent_loop(task, ctx, verbose=True, on_event=None,
               system=None, tools=None, runner=None):
    """Bounded. Every step is printed, because a loop nobody can see is a claim.

    on_event gets the same steps as a structured record, which is how the app
    shows each call as it happens instead of after the fact. system/tools/runner
    are the seam the edit agent uses: same bounded loop, same cache, same write
    refusals, a different set of things it may do.
    """
    say = on_event or (lambda *_, **__: None)
    system = system or SYSTEM
    tools = tools or TOOLS
    runner = runner or run_tool
    messages = [{"role": "user", "content": task}]
    for step in range(1, MAX_STEPS + 1):
        say("thinking", step=step)
        began = time.time()
        reply, origin = call_model(system, messages, tools)
        say("thought", step=step, origin=origin, ms=int((time.time() - began) * 1000))
        content = reply.get("content", []) if isinstance(reply, dict) else []
        # Only blocks that are objects are read. One that is not would crash the
        # reader below on its way to being refused, which turns a bad answer into
        # a dead run instead of a reported one.
        blocks = [b for b in content if isinstance(b, dict)]
        messages.append({"role": "assistant", "content": blocks})

        said = "".join(b.get("text", "") for b in blocks if b.get("type") == "text").strip()
        if verbose and said:
            print(f"  [{step}] {said[:300]}", flush=True)
        if said:
            say("said", step=step, text=said[:600], origin=origin)

        calls = [b for b in blocks if b.get("type") == "tool_use"]
        if not calls:
            if verbose:
                print(f"  [{step}] stopped without acting ({origin})")
            say("stalled", step=step, text=said[:600] or "it answered with nothing to do")
            return step, False

        results = []
        for i, call in enumerate(calls):
            # A block with no input at all is still a call it made, so it goes
            # through run_tool and comes back as the same missing-argument
            # error the model already knows how to fix.
            given = call.get("input")
            # Announced before it runs, not after. Announcing a call once its
            # answer was already in hand meant the card appeared and settled in
            # the same instant, so the window never showed anything working.
            say("tool", step=step, index=i, name=call["name"], input=given, origin=origin)
            began = time.time()
            result = runner(call["name"], given, ctx)
            if verbose:
                detail = (given.get("output") or given.get("query") or ""
                          if isinstance(given, dict) else "")
                print(f"  [{step}] {call['name']}({detail}) [{origin}]", flush=True)
            say("tool_result", step=step, index=i, name=call["name"], input=given,
                result=result, origin=origin, ms=int((time.time() - began) * 1000))
            results.append({"type": "tool_result", "tool_use_id": call["id"],
                            "content": json.dumps(result)})
        messages.append({"role": "user", "content": results})

        if ctx.get("done"):
            if verbose:
                print(f"  [{step}] finished: {ctx['done'][:200]}")
            return step, True
    say("budget", step=MAX_STEPS,
        text=f"it used all {MAX_STEPS} steps without finishing")
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

    ctx = new_context(plan, unresolved)

    meanings = plan.get("controlMeanings", {})
    task = (
        f"Game: {plan['game']}.\n\n"
        f"These {len(unresolved)} controls are unsettled. For each, what it does in this "
        f"game, where that is known:\n"
        + "\n".join(f"  {c}: {meanings.get(c, 'unknown, not in the sourced control list')}"
                    for c in unresolved)
        + "\n\nRead their habits, then settle each one or ask about it."
    )

    print(f"{len(unresolved)} controls the evidence did not settle. "
          f"Model: {MODEL} via {BACKEND}, mode: {MODE}")
    steps, finished = agent_loop(task, ctx)
    result = brief(ctx)
    print(f"\n{steps} steps, {'finished' if finished else 'hit the step budget'}: "
          f"{len(result['proposals'])} proposed, {len(result['questions'])} to ask")
    # A control the agent never reached is not a control that is fine. It is
    # named here and carried into the report so the write step can say so too.
    if result["untouched"]:
        print(f"{len(result['untouched'])} it never got to, and they are still unbound: "
              f"{', '.join(result['untouched'])}")

    report = {"game": plan["game"], "steps": steps, "finished": finished, **result}
    if args.report:
        with open(args.report, "w") as f:
            json.dump(report, f, indent=2)
        print(f"written to {args.report}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
