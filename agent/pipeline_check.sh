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

[ "$fails" -eq 0 ] && echo "the whole pipeline holds" || echo "$fails check(s) failed"
exit "$fails"
