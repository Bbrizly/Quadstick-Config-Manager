#!/usr/bin/env bash
# The whole path, end to end, with no model and no network: hold a game out,
# rebuild it from the rest, refuse a bad batch, then apply a good one.
# This is also the demo's dry run. Run: agent/pipeline_check.sh
set -uo pipefail
cd "$(dirname "$0")/.."
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
fails=0

check() {
  if [ "$2" = "$3" ]; then printf 'ok   %s\n' "$1"
  else printf 'FAIL %s\n       want: %s\n       got:  %s\n' "$1" "$2" "$3"; fails=$((fails + 1)); fi
}

dotnet build tools/qsf/qsf.csproj -v q --nologo >/dev/null || exit 1

python3 agent/predict.py --controls agent/eval/controls-cyberpunk.json \
  --exclude-family cyberpunk-2077 --chart agent/charts/cyberpunk-2077.json \
  --out "$TMP/p.csv" --trace "$TMP/plan.json" > "$TMP/predict.log" 2>&1
check "a held-out game rebuilds end to end" "0" "$?"
check "the profile the device would load has no errors" "0" \
  "$(tools/qsf/bin/Debug/net8.0/qsf validate "$TMP/p.csv" | python3 -c 'import json,sys; print(json.load(sys.stdin)["errors"])')"

# Every binding written has to say where it came from. A row nobody can trace
# is a guess wearing a fact's clothes.
python3 - "$TMP/plan.json" <<'PY' > "$TMP/prov.txt"
import json, sys
plan = json.load(open(sys.argv[1]))
traced = sum(1 for d in plan["decided"] if d.get("evidence") and d.get("ofGames"))
print(traced, len(plan["decided"]), len(plan["asks"]))
PY
read -r traced decided asked < "$TMP/prov.txt"
check "every written binding cites a profile and row" "$decided" "$traced"
check "it declined rather than guessing at some" "yes" "$([ "$asked" -gt 0 ] && echo yes || echo no)"

# A batch holding one invented token must leave the profile exactly as it was.
cp "$TMP/p.csv" "$TMP/refused.csv"
cat > "$TMP/bad.json" <<'EOF'
{"game":"t","steps":1,"finished":true,"questions":[],"summary":"",
 "proposals":[{"output":"kb_left_shift","inputs":["mp_triple_puff"],"function":"normal",
               "confidence":"inferred","why":"fine"},
              {"output":"kb_lshift","inputs":["lip"],"function":"normal",
               "confidence":"inferred","why":"invented, never looked up"}]}
EOF
python3 agent/finalize.py --profile "$TMP/refused.csv" --decisions "$TMP/bad.json" >/dev/null 2>&1
check "a batch with an invented token is refused" "1" "$?"
check "and the profile is byte for byte unchanged" "same" \
  "$(cmp -s "$TMP/p.csv" "$TMP/refused.csv" && echo same || echo changed)"
check "no half-built file is left behind" "absent" \
  "$([ -f "$TMP/refused.csv.building" ] && echo present || echo absent)"

cat > "$TMP/good.json" <<'EOF'
{"game":"t","steps":1,"finished":true,"summary":"",
 "questions":[{"output":"kb_c","question":"hold or toggle?","options":["toggle","hold"]}],
 "proposals":[{"output":"kb_left_shift","inputs":["mp_triple_puff"],"function":"normal",
               "confidence":"inferred","why":"Cyberpunk holds Shift to sprint"}]}
EOF
cp "$TMP/p.csv" "$TMP/good.csv"
python3 agent/finalize.py --profile "$TMP/good.csv" --decisions "$TMP/good.json" > "$TMP/fin.log" 2>&1
check "a good batch applies and validates" "0" "$?"
check "an unanswered question is left open, not filled in" "yes" \
  "$(grep -q 'still open' "$TMP/fin.log" && echo yes || echo no)"
check "the reason is written beside the binding" "yes" \
  "$(grep -q 'Shift to sprint' "$TMP/good.csv" && echo yes || echo no)"

# Answering a question is only worth anything if the answer can be bound as
# shown, and if "leave it alone" really leaves it alone.
cat > "$TMP/ask.json" <<'EOF'
{"game":"t","steps":1,"finished":true,"summary":"","proposals":[],
 "questions":[
  {"output":"kb_c","question":"hold or toggle?","options":[
    {"inputs":["mp_left_sip"],"function":"toggle","label":"Left sip, toggle - 42% of profiles"}]},
  {"output":"kb_z","question":"no evidence at all here","options":[
    {"inputs":[],"function":"normal","label":"Leave this unbound for now"}]}]}
EOF
cp "$TMP/p.csv" "$TMP/asked.csv"
printf '1\n1\ny\n' | python3 agent/finalize.py --profile "$TMP/asked.csv" \
  --decisions "$TMP/ask.json" --interactive > "$TMP/ask.log" 2>&1
check "an answer is bound exactly as it was shown" "yes" \
  "$(grep -q 'mp_left_sip' "$TMP/asked.csv" && echo yes || echo no)"
check "choosing leave-unbound writes no row" \
  "$(grep -o 'kb_z' "$TMP/p.csv" | wc -l | tr -d ' ')" \
  "$(grep -o 'kb_z' "$TMP/asked.csv" | wc -l | tr -d ' ')"
check "and it is reported as still open" "yes" \
  "$(grep -q 'still open' "$TMP/ask.log" && echo yes || echo no)"

# --- the ways a wrong value used to reach the file ------------------------

# Timings the person set are part of what they chose. Reading their history
# with the parameters stripped off turned "tap 200" into "tap".
python3 - <<'PY' > "$TMP/params.txt"
import sys; sys.path.insert(0, "agent")
import predict
one = {"family": "f", "game": "g", "modes": [{"name": "m", "bindings": [
    {"output": "kb_c", "function": "delay_on 500 16000",
     "inputs": ["mp_left_sip"], "row": 4}]}]}
print(predict.habits([one])["kb_c"][0]["function"])
PY
check "the timing they set survives being read back" "delay_on 500 16000" "$(cat "$TMP/params.txt")"

# A dead even split is a question. It used to be settled by whichever profile
# the corpus happened to list first.
python3 - <<'PY' > "$TMP/tie.txt"
import sys; sys.path.insert(0, "agent")
import predict
ranked = {"kb_c": [{"inputs": ["a"], "function": "normal", "seenIn": 1, "ofGames": 2, "share": 0.5,
                    "evidence": "A"},
                   {"inputs": ["b"], "function": "normal", "seenIn": 1, "ofGames": 2, "share": 0.5,
                    "evidence": "B"}]}
decided, asks = predict.predict(["kb_c"], ranked, 0.5)
print(len(decided), len(asks))
PY
check "a 50/50 habit is asked about, not picked" "0 1" "$(cat "$TMP/tie.txt")"

# A control the research would not commit to has to reach the person.
python3 - <<'PY' > "$TMP/disputed.txt"
import sys; sys.path.insert(0, "agent")
import predict
ranked = {"kb_c": [{"inputs": ["a"], "function": "normal", "seenIn": 9, "ofGames": 9,
                    "share": 1.0, "evidence": "A"}]}
decided, asks = predict.predict(["kb_c"], ranked, 0.5, disputed={"kb_c": ["aim", "brake"]})
print(len(decided), len(asks))
PY
check "a disputed control is asked about even with a settled habit" "0 1" "$(cat "$TMP/disputed.txt")"

# The agent may only answer the controls it was handed, once each, and never
# with nothing to trigger them.
python3 - <<'PY' > "$TMP/guard.txt"
import sys; sys.path.insert(0, "agent")
import qsagent
ctx = qsagent.new_context({"habits": {}, "outputs": []}, ["kb_c"])
def call(**args):
    return qsagent.run_tool("propose_binding", {"function": "normal", "why": "w",
                                                "confidence": "inferred", **args}, ctx)
uninvited = call(output="left_trigger", inputs=["lip"])
empty = call(output="kb_c", inputs=[])
first = call(output="kb_c", inputs=["mp_left_sip"])
again = call(output="kb_c", inputs=["lip"])
early = qsagent.run_tool("finish", {"summary": "done"},
                         qsagent.new_context({"habits": {}, "outputs": []}, ["kb_c", "kb_v"]))
print("error" in uninvited, "error" in empty, "recorded" in first,
      "error" in again, "error" in early, len(ctx["proposals"]))
PY
check "a control nobody asked about is refused, and so is finishing early" \
  "True True True True True 1" "$(cat "$TMP/guard.txt")"

# The device reads function parameters as whole numbers, so a generated row
# that says "tap banana" would run as "tap 0" without saying so.
cp "$TMP/p.csv" "$TMP/banana.csv"
cat > "$TMP/banana.json" <<'EOF'
{"game":"t","steps":1,"finished":true,"questions":[],"summary":"",
 "proposals":[{"output":"kb_left_shift","inputs":["mp_triple_puff"],"function":"tap banana",
               "confidence":"inferred","why":"a parameter the device cannot read"}]}
EOF
python3 agent/finalize.py --profile "$TMP/banana.csv" --decisions "$TMP/banana.json" >/dev/null 2>&1
check "a parameter the device would read as something else is refused" "1" "$?"
check "and that profile is byte for byte unchanged" "same" \
  "$(cmp -s "$TMP/p.csv" "$TMP/banana.csv" && echo same || echo changed)"

# An answer is taken exactly as given. A missing function used to become
# "normal", which is a device setting nobody typed.
cp "$TMP/p.csv" "$TMP/partial.csv"
echo '{"kb_c": {"inputs": ["mp_left_sip"]}}' > "$TMP/partial-answers.json"
python3 agent/finalize.py --profile "$TMP/partial.csv" --decisions "$TMP/ask.json" \
  --answers "$TMP/partial-answers.json" >/dev/null 2>&1
check "an answer with no function is refused, not filled in" "1" "$?"

# Nothing the agent never reached may pass in silence.
cat > "$TMP/left.json" <<'EOF'
{"game":"t","steps":20,"finished":false,"summary":"","proposals":[],"questions":[],
 "untouched":["kb_v","kb_space"]}
EOF
cp "$TMP/p.csv" "$TMP/left.csv"
python3 agent/finalize.py --profile "$TMP/left.csv" --decisions "$TMP/left.json" > "$TMP/left.log" 2>&1
check "controls the agent never reached are named, not passed over" "yes" \
  "$(grep -q 'never reached' "$TMP/left.log" && echo yes || echo no)"

# --- changing a profile that already exists -------------------------------

# An edit touches the row it was asked about and carries the rest of that row
# through exactly as they left it.
python3 - <<'PY' > "$TMP/edit.txt"
import sys; sys.path.insert(0, "agent")
import run
ctx = {"rows": [{"row": 7, "mode": "m", "output": "kb_left_shift",
                 "function": "delay_on 500 16000", "inputs": ["mp_triple_puff"]}],
       "changes": [], "questions": [], "settled": set(), "unresolved": set(),
       "inputs": ["right_puff"], "outputs": ["kb_left_shift"], "done": None,
       "required": {t["name"]: t["input_schema"].get("required", []) for t in run.EDIT_TOOLS}}
missing = run.edit_tool("change_row", {"row": 99, "inputs": ["right_puff"], "why": "w"}, ctx)
same = run.edit_tool("change_row", {"row": 7, "inputs": ["mp_triple_puff"], "why": "w"}, ctx)
ok = run.edit_tool("change_row", {"row": 7, "inputs": ["right_puff"], "why": "harder puff"}, ctx)
kept = ctx["changes"][0]
nothing = run.edit_tool("finish", {"summary": "s"},
                        {**ctx, "changes": [], "questions": []})
print("error" in missing, "error" in same, "changed" in ok,
      kept["function"], "|", kept["output"], "|", len(ctx["changes"]), "error" in nothing)
PY
check "an edit changes one row and leaves the rest of it alone" \
  "True True True delay_on 500 16000 | kb_left_shift | 1 True" "$(cat "$TMP/edit.txt")"

# --- the one process the app talks to -------------------------------------

# Approval is the literal true. "false" as a string, 1 and {} are all truthy
# in Python, and every one of them would authorise a write nobody approved.
python3 - <<'PY' > "$TMP/approve.txt"
import sys; sys.path.insert(0, "agent")
import run
print(*(run.approved({"id": "c1", "write": v})
        for v in (True, "false", 1, {}, None)))
PY
check "only a real yes authorises a write" "True False False False False" "$(cat "$TMP/approve.txt")"

# What they take off the list arrives as positions, and anything that is not a
# real position is not one. True is an int in Python, and skip:[true] would
# otherwise take the first row off a list nobody unticked.
python3 - <<'PY' > "$TMP/skip.txt"
import sys; sys.path.insert(0, "agent")
import run
rows = [{"output": "a"}, {"output": "b"}, {"output": "c"}]
print(sorted(run.taken_off({"skip": [0, 2]}, rows)),
      sorted(run.taken_off({"skip": [True, "1", 9, -1, None, 1]}, rows)),
      sorted(run.taken_off({"skip": "all"}, rows)),
      sorted(run.taken_off({}, rows)))
PY
check "a row is taken off by position, and only by a real one" \
  "[0, 2] [1] [] []" "$(cat "$TMP/skip.txt")"

# A blank line is not a sentence, and spending a model call on one would be a
# round of changes nobody asked for.
python3 - <<'PY' > "$TMP/spoken.txt"
import sys; sys.path.insert(0, "agent")
import run
print(repr(run.spoken({"say": "  make it a hard puff "})),
      repr(run.spoken({"say": "   "})), repr(run.spoken({"say": 5})),
      repr(run.spoken({})))
PY
check "only a real sentence starts another round" \
  "'make it a hard puff' '' '' ''" "$(cat "$TMP/spoken.txt")"

# The revise turn is over a list where every row already has an answer, so
# finishing without changing anything is a real answer. Finishing without
# saying what happened is not.
python3 - <<'PY' > "$TMP/revise.txt"
import sys; sys.path.insert(0, "agent")
import run
ctx = {"habits": {}, "outputs": ["kb_c"], "inputs": ["right_puff", "right_puff_soft"],
       "unresolved": {"kb_c"}, "settled": set(), "proposals": [], "questions": [],
       "unbound": [], "done": None}
blank = run.revise_tool("finish", {"summary": "  "}, ctx)
nothing = run.revise_tool("finish", {"summary": "they meant one of two rows"}, ctx)
found = run.revise_tool("find_input", {"query": "right puff"}, ctx)
# Nobody is waiting to be asked at this point: they are looking at the list.
asked = run.revise_tool("ask_user", {"output": "kb_c", "question": "which?",
                                     "options": []}, ctx)
print("error" in blank, ctx["done"] is not None, found["matches"],
      "error" in asked, len(ctx["questions"]))
PY
check "a revise turn may finish having changed nothing, but not in silence" \
  "True True ['right_puff', 'right_puff_soft'] True 0" "$(cat "$TMP/revise.txt")"

# A row they take off the list has to come out of the working copy too. The
# deterministic pass has already written its answer in there, so leaving it off
# the list alone would write the very thing they declined.
cp "$TMP/p.csv" "$TMP/declined.csv"
python3 - "$TMP/declined.csv" <<'PY' > "$TMP/unbind.txt"
import sys; sys.path.insert(0, "agent")
import run
before = run.finalize.qsf("inspect", sys.argv[1])
rows = [b["output"] for pf in before["profiles"] for m in pf["modes"] for b in m["bindings"]]
take = {rows[3], rows[9]}
took = run.unbind(sys.argv[1], take)
after = run.finalize.qsf("inspect", sys.argv[1])
still = [b["output"] for pf in after["profiles"] for m in pf["modes"] for b in m["bindings"]]
print(took, sorted(take & set(still)), len(rows) - len(still))
PY
check "a declined row is taken out of the file, not just off the list" \
  "2 [] 2" "$(cat "$TMP/unbind.txt")"

# The whole confirm, driven the way the window drives it, over a profile the
# deterministic pass really built: untick a row that is already in the working
# copy, write the rest, and check the file rather than the report.
cp "$TMP/p.csv" "$TMP/bf-work.csv"
python3 - "$TMP/bf-work.csv" "$TMP/backforth.csv" <<'PY' > "$TMP/backforth.txt" 2>&1
import contextlib, io, json, sys; sys.path.insert(0, "agent")
import run
work, out = sys.argv[1], sys.argv[2]
have = [b for pf in run.finalize.qsf("inspect", work)["profiles"]
        for m in pf["modes"][:1] for b in m["bindings"] if b["inputs"]][:3]
shown = [{"output": b["output"], "inputs": b["inputs"], "function": b["function"],
          "confidence": "evidenced", "why": "the published profiles do this",
          "action": ""} for b in have]
sys.stdin = io.StringIO(json.dumps({"id": "c1", "write": True, "skip": [0]}) + "\n")
said = io.StringIO()
with contextlib.redirect_stdout(said):
    run.confirm_and_write(work, [], [], [], out=out, shown=shown, fresh=True)
events = [json.loads(line) for line in said.getvalue().splitlines()]
done = next(e for e in events if e["event"] == "done")
offered = next(e for e in events if e["event"] == "confirm")
print(have[0]["output"], done["written"], offered["canSay"],
      [d["output"] for d in done["declined"]])
PY
read -r took wrote offered gone < "$TMP/backforth.txt"
check "the written count is the list minus what they took off" "2" "$wrote"
check "and the run names what they declined" "['$took']" "$gone"
check "a run with no rework on offer never offers one" "False" "$offered"
check "the row they took off is not in the file at all" "0" \
  "$(grep -c "^$took," "$TMP/backforth.csv" || true)"
check "the profile it wrote is one the device would load" "0" \
  "$(tools/qsf/bin/Debug/net8.0/qsf validate "$TMP/backforth.csv" | python3 -c 'import json,sys; print(json.load(sys.stdin)["errors"])')"

# Saying something goes round again and shows the whole list a second time,
# under a second id, and only the second answer writes anything.
cp "$TMP/p.csv" "$TMP/say-work.csv"
python3 - "$TMP/say-work.csv" "$TMP/said.csv" <<'PY' > "$TMP/said.txt" 2>&1
import contextlib, io, json, sys; sys.path.insert(0, "agent")
import run
work, out = sys.argv[1], sys.argv[2]
have = [b for pf in run.finalize.qsf("inspect", work)["profiles"]
        for m in pf["modes"][:1] for b in m["bindings"] if b["inputs"]][:2]
shown = [{"output": b["output"], "inputs": b["inputs"], "function": b["function"],
          "confidence": "evidenced", "why": "w", "action": ""} for b in have]
def rework(said, shown, settled, left):
    return [dict(r, why=said) for r in shown], settled, left
sys.stdin = io.StringIO(
    json.dumps({"id": "c1", "say": "make the first one a hard puff"}) + "\n"
    + json.dumps({"id": "c2", "write": True}) + "\n")
said = io.StringIO()
with contextlib.redirect_stdout(said):
    run.confirm_and_write(work, [], [], [], out=out, shown=shown, fresh=True, rework=rework)
events = [json.loads(line) for line in said.getvalue().splitlines()]
asks = [e for e in events if e["event"] == "confirm"]
done = next(e for e in events if e["event"] == "done")
print(len(asks), [a["id"] for a in asks], [a["canSay"] for a in asks],
      asks[1]["rows"][0]["why"], "|", done["written"])
PY
check "saying something shows the whole list again before anything is written" \
  "2 ['c1', 'c2'] [True, True] make the first one a hard puff | 2" "$(cat "$TMP/said.txt")"

# Two front ends, one pipeline. Whatever the window can do to the list, the
# terminal can do to the same list, or one of them is showing a run the other
# one cannot.
python3 - <<'PY' > "$TMP/term.txt"
import contextlib, io, sys; sys.path.insert(0, "agent")
import terminal
card = {"id": "c1", "profile": "/tmp/g.csv", "canSay": True, "open": [], "untouched": [],
        "rows": [{"output": "kb_a", "inputs": ["lip"], "function": "normal"},
                 {"output": "kb_b", "inputs": ["right_puff"], "function": "toggle"},
                 {"output": "kb_c", "inputs": ["mp_left_sip"], "function": "tap 200"}]}
def answered(typed, **over):
    sys.stdin = io.StringIO(typed)
    with contextlib.redirect_stdout(io.StringIO()) as out:
        said = terminal.decide({**card, **over})
    return said, out.getvalue()
took, _ = answered("1\n3\ny\n")
back, _ = answered("2\n2\ny\n")
# Every row off, so y is refused and asked again; putting one back writes that one.
every, refused = answered("1\n2\n3\ny\n3\ny\n")
spoke, _ = answered("s\nmake sprint a hard puff\n")
quiet, _ = answered("")                          # a closed keyboard writes nothing
plain, _ = answered("s\ns\nn\n", canSay=False)   # s is not on offer without it
print(took, back, every["skip"] == [0, 1], "every row is taken off" in refused)
print(spoke, quiet, plain)
PY
check "the terminal can take rows off the list too" \
  "{'id': 'c1', 'write': True, 'skip': [0, 2]} {'id': 'c1', 'write': True, 'skip': []} True True" \
  "$(sed -n 1p "$TMP/term.txt")"
check "and can say something, and a closed keyboard writes nothing" \
  "{'id': 'c1', 'say': 'make sprint a hard puff'} {'id': 'c1', 'write': False} {'id': 'c1', 'write': False}" \
  "$(sed -n 2p "$TMP/term.txt")"

# The rounds are bounded, and the last one says so rather than quietly ignoring
# what they typed.
cp "$TMP/p.csv" "$TMP/loop-work.csv"
python3 - "$TMP/loop-work.csv" "$TMP/loop.csv" <<'PY' > "$TMP/loop.txt" 2>&1
import contextlib, io, json, sys; sys.path.insert(0, "agent")
import run
work, out = sys.argv[1], sys.argv[2]
shown = [{"output": "kb_c", "inputs": ["mp_left_sip"], "function": "normal",
          "confidence": "evidenced", "why": "w", "action": ""}]
sys.stdin = io.StringIO("".join(
    json.dumps({"id": f"c{n}", "say": "again"}) + "\n" for n in range(1, 20)))
said = io.StringIO()
with contextlib.redirect_stdout(said):
    try:
        run.confirm_and_write(work, [], [], [], out=out, shown=shown, fresh=True,
                              rework=lambda s, a, b, c: (a, b, c))
        why = "wrote it anyway"
    except run.Stopped as stopped:
        why = str(stopped)
events = [json.loads(line) for line in said.getvalue().splitlines()]
asks = [e["id"] for e in events if e["event"] == "confirm"]
print(len(asks), asks[-1], [e["canSay"] for e in events if e["event"] == "confirm"][-1],
      "|", "not applied" in why, __import__("os").path.exists(out))
PY
check "the rounds are bounded, and the last one says so instead of writing" \
  "5 c5 False | True False" "$(cat "$TMP/loop.txt")"

# Starting a run must not write anything. The deterministic pass used to build
# straight into the destination, so opening the window wrote a file.
rm -f "$TMP/never.csv"
printf '' | python3 agent/run.py --game "cyberpunk 2077" --hold-out --replay \
  --out "$TMP/never.csv" > "$TMP/never.log" 2>&1
check "a run nobody answered writes nothing at all" "absent" \
  "$([ -f "$TMP/never.csv" ] && echo present || echo absent)"
check "and it says why it stopped" "yes" \
  "$(grep -q '"event": "failed"' "$TMP/never.log" && echo yes || echo no)"

# The default name never lands on a profile that already exists.
touch "$TMP/taken.csv"
python3 - "$TMP/taken.csv" <<'PY' > /dev/null 2> "$TMP/free.err"
import sys; sys.path.insert(0, "agent")
import run
picked = run.free_name(sys.argv[1])
sys.stderr.write(str(picked.endswith("taken-2.csv")))
PY
check "a name already in use is never written over" "True" "$(cat "$TMP/free.err")"

[ "$fails" -eq 0 ] && echo "the whole pipeline holds" || echo "$fails check(s) failed"
exit "$fails"
