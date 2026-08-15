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


def fresh_ctx(unresolved=("kb_c",)):
    plan = {
        "habits": {"kb_c": [{"inputs": ["mp_left_sip"], "function": "toggle", "seenIn": 40,
                             "ofGames": 95, "share": 0.42, "evidence": "Doom, mode 'Gameplay', row 31"}]},
        "outputs": ["kb_c", "kb_left_shift", "kb_left_control", "x", "circle"],
    }
    return qsagent.new_context(plan, unresolved)


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
    use("ask_user", output="kb_c", question="Crouch: hold or toggle?", options=[
        {"inputs": ["mp_left_sip"], "function": "toggle", "label": "Left sip, toggle, as in Doom"},
        {"inputs": ["mp_left_puff"], "function": "normal", "label": "Left puff, held"}]),
    use("finish", summary="one to ask"),
), ctx)
check("a question is recorded, not a binding", (0, 1), (len(ctx["proposals"]), len(ctx["questions"])))

# An option written as a sentence cannot be bound as written, so offering one
# to a person would be offering a choice that does nothing when they pick it.
ctx = fresh_ctx()
run(scripted(
    use("ask_user", output="kb_c", question="Crouch: hold or toggle?",
        options=["toggle on mp_left_sip, as in Doom", "hold on mp_left_puff"]),
    {"content": [{"type": "text", "text": "stopping"}]},
), ctx)
check("an option that cannot be bound is refused, not offered", 0, len(ctx["questions"]))

# The controls it was handed are the job. Anything else is a change nobody
# asked for, and it would reach the file exactly like a real one.
ctx = fresh_ctx()
run(scripted(
    use("propose_binding", output="kb_left_control", inputs=["lip"], function="normal",
        why="while I am here", confidence="inferred"),
    {"content": [{"type": "text", "text": "stopping"}]},
), ctx)
check("a control nobody asked about is not recorded", 0, len(ctx["proposals"]))

# Finishing with controls still untouched used to end the run reporting only
# what it happened to record.
ctx = fresh_ctx(unresolved=("kb_c", "kb_left_shift"))
steps, finished, _ = run(scripted(
    use("propose_binding", output="kb_c", inputs=["mp_left_sip"], function="toggle",
        why="40 of 95 of his games, Doom row 31", confidence="evidenced"),
    use("finish", summary="close enough"),
    use("finish", summary="close enough"),
), ctx)
check("it cannot finish while a control is untouched", False, finished)
check("and the one it never reached is named", ["kb_left_shift"], qsagent.brief(ctx)["untouched"])

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

# A tool call missing an argument is sent back, not half-recorded. The CLI
# backend cannot constrain each tool's arguments, so this is what stands in.
ctx = fresh_ctx()
run(scripted(
    use("propose_binding", output="kb_c", inputs=["mp_left_sip"], function="toggle"),
    use("finish", summary="gave up"),
), ctx)
check("a proposal with no reason is refused, not recorded", 0, len(ctx["proposals"]))

# Nothing the model says can become a cell here. The agent produces proposals;
# a separate pass turns them into qsf ops, and qsf refuses the bad ones. The one
# process it does start is the model itself, and that is checked by running it.
source = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "qsagent.py")).read()
check("the agent never names a profile file", False, ".csv" in source)

seen = {}


def fake_run(command, **kwargs):
    seen["command"] = command
    return type("R", (), {"returncode": 0, "stdout": json.dumps({
        "is_error": False, "uuid": "u1", "result": "{}",
        "structured_output": {"calls": [{"tool": "finish", "input": {"summary": "done"}}]}}), "stderr": ""})


qsagent.subprocess.run = fake_run
reply = qsagent.call_cli("sys", [{"role": "user", "content": "task"}], qsagent.TOOLS)
check("the only process the agent starts is the model", True,
      seen["command"][0] == qsagent.CLAUDE_BIN and "-p" in seen["command"])
check("and it hands that process no file to touch", (True, False),
      ("--tools" in seen["command"], any(".csv" in str(a) for a in seen["command"])))
check("a structured reply becomes a real tool call", ("tool_use", "finish"),
      (reply["content"][0]["type"], reply["content"][0]["name"]))

# A reply naming no tool must stop the loop rather than be guessed at.
check("an unparseable reply becomes text, not a binding", "text",
      qsagent.shape({"result": "I am not sure what to do"})["content"][0]["type"])


# ---- what the run lets a person watch -------------------------------------
#
# The window can only show what the pipeline says out loud. Two things had to be
# proved here rather than by eye: that a call is announced before it runs, and
# that the web calls the researcher makes come out one at a time while they
# happen. Both were wrong for real, and both looked fine from the outside: every
# card appeared and finished in the same instant, and a run that spent two
# minutes reading the web showed one motionless box the whole time.

import research  # noqa: E402

order = []
qsagent.call_model = lambda *a, **k: ({"content": [
    {"type": "tool_use", "id": "c1", "name": "read_habits",
     "input": {"controls": ["kb_w"]}}]}, "scripted")


def slow(name, args, ctx):
    order.append("ran")
    return {"habits": {}}


watched = qsagent.new_context({"habits": {}, "outputs": {}}, ["kb_w"])
qsagent.agent_loop("t", watched, verbose=False, runner=slow,
                   on_event=lambda kind, **f: order.append(kind))
check("a call is announced before it runs, not after",
      ["thinking", "thought", "tool", "ran", "tool_result"], order[:5])

heard = []
lines = [
    {"type": "assistant", "message": {"content": [
        {"type": "tool_use", "id": "w1", "name": "WebSearch",
         "input": {"query": "Celeste controls"}}]}},
    {"type": "user", "message": {"content": [
        {"type": "tool_result", "tool_use_id": "w1",
         "content": "Web search results for query: x\nLinks: [{\"url\":\"a\"}]"}]}},
    {"type": "assistant", "message": {"content": [
        {"type": "tool_use", "id": "w2", "name": "WebFetch",
         "input": {"url": "https://pcgamingwiki.com/wiki/Celeste"}}]}},
    {"type": "result", "is_error": False, "result": '{"game": "Celeste"}'},
]
answer, recorded_steps = research.stream_cli(
    [sys.executable, "-c",
     "import sys\nfor l in %r: sys.stdout.write(l + chr(10))" % [json.dumps(l) for l in lines]],
    lambda kind, **f: heard.append((kind, f.get("name") or f.get("summary") or "")))
check("every web call the researcher makes is reported as it happens",
      [("tool", "WebSearch"), ("tool_result", "1 results"), ("tool", "WebFetch")], heard)
check("and the chart it read out is what comes back", '{"game": "Celeste"}', answer)
# The offline run is the one to fall back on when the network is not there, so
# it has to show the same searches rather than a silent gap where they were.
check("what it did is kept, so a replay can show the same work",
      [("tool", "WebSearch"), ("tool_result", None), ("tool", "WebFetch")],
      [(s["kind"], s.get("name")) for s in recorded_steps])

# A researcher that fails must say so, not hand back half a chart.
try:
    research.stream_cli([sys.executable, "-c", "import sys; sys.exit(3)"], lambda *a, **k: None)
    check("a failed research call stops the run", "raised", "returned")
except SystemExit as stopped:
    check("a failed research call stops the run", True, "research call failed" in str(stopped))

print("\nall agent checks passed" if not fails else f"\n{len(fails)} check(s) failed")
sys.exit(len(fails))
