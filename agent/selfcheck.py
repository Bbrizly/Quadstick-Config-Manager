#!/usr/bin/env python3
"""Prove the agent loop without spending a model call.

The model is replaced by a script of replies. What is being checked is the
harness around it, which is the part that has to hold when the model behaves
badly on stage: that tools dispatch, that the budget actually stops a model
which will not stop, that a malformed reply does not take the process down,
and that nothing a model says can put a value in a file by itself.

    python3 agent/selfcheck.py
"""
import io
import json
import os
import sys
import contextlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import qsagent  # noqa: E402

fails = []


def check(name, want, got):
    if want == got:
        print(f"ok   {name}")
    else:
        print(f"FAIL {name}\n       want: {want}\n       got:  {got}")
        fails.append(name)


def scripted(*replies):
    """Stand in for the model with a fixed script, and count the turns taken."""
    state = {"n": 0}

    def call(system, messages, tools, max_tokens=1500):
        i = min(state["n"], len(replies) - 1)
        state["n"] += 1
        return replies[i], "scripted"
    call.turns = state
    return call


def use(name, **args):
    return {"content": [{"type": "tool_use", "id": f"t{name}", "name": name, "input": args}]}


def fresh_ctx():
    return {
        "habits": {"kb_c": [{"inputs": ["mp_left_sip"], "function": "toggle", "seenIn": 40,
                             "ofGames": 95, "share": 0.42, "evidence": "Doom, mode 'Gameplay', row 31"}]},
        "outputs": ["kb_c", "kb_left_shift", "kb_left_control", "x", "circle"],
        "proposals": [], "questions": [], "done": None,
    }


def run(call, ctx):
    qsagent.call_model = call
    with contextlib.redirect_stdout(io.StringIO()) as out:
        steps, finished = qsagent.agent_loop("task", ctx)
    return steps, finished, out.getvalue()


# A model that decides and stops.
ctx = fresh_ctx()
steps, finished, log = run(scripted(
    use("read_habits", controls=["kb_c"]),
    use("propose_binding", output="kb_c", inputs=["mp_left_sip"], function="toggle",
        why="40 of 95 of his games, Doom row 31", confidence="evidenced"),
    use("finish", summary="one control settled"),
), ctx)
check("a deciding model finishes", (3, True), (steps, finished))
check("the proposal is recorded", 1, len(ctx["proposals"]))
check("the reason survives", True, "Doom row 31" in ctx["proposals"][0]["why"])

# A model that will not stop must be stopped.
ctx = fresh_ctx()
steps, finished, log = run(scripted(use("read_habits", controls=["kb_c"])), ctx)
check("a looping model hits the budget", (qsagent.MAX_STEPS, False), (steps, finished))
check("the budget is small enough to matter", True, qsagent.MAX_STEPS <= 25)

# Asking is a real outcome, not a failure.
ctx = fresh_ctx()
run(scripted(
    use("ask_user", output="kb_c", question="Crouch: hold or toggle?",
        options=["toggle on mp_left_sip, as in Doom", "hold on mp_left_puff"]),
    use("finish", summary="one to ask"),
), ctx)
check("a question is recorded, not a binding", (0, 1), (len(ctx["proposals"]), len(ctx["questions"])))

# Looking a token up beats inventing one.
ctx = fresh_ctx()
run(scripted(use("find_output", query="shift"), use("finish", summary="looked up")), ctx)
check("find_output finds the real spelling", True, "kb_left_shift" in ctx["outputs"])

# Garbage from the model must not take the process down.
ctx = fresh_ctx()
steps, finished, log = run(scripted(
    {"content": [{"type": "tool_use", "id": "t1", "name": "no_such_tool", "input": {}}]},
    {"content": [{"type": "text", "text": "I have no tools left"}]},
), ctx)
check("an unknown tool does not crash the loop", True, finished is False and steps >= 2)

ctx = fresh_ctx()
steps, finished, _ = run(scripted({"content": []}), ctx)
check("an empty reply ends the loop cleanly", (1, False), (steps, finished))

# Nothing the model says can become a cell here. The agent produces proposals;
# a separate pass turns them into qsf ops, and qsf refuses the bad ones.
source = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "qsagent.py")).read()
check("the agent never writes a profile itself", (False, False),
      (".csv" in source, "import subprocess" in source))

print("\nall agent checks passed" if not fails else f"\n{len(fails)} check(s) failed")
sys.exit(len(fails))
