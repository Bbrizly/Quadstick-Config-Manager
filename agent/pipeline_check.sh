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

[ "$fails" -eq 0 ] && echo "the whole pipeline holds" || echo "$fails check(s) failed"
exit "$fails"
