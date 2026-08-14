#!/usr/bin/env bash
# Regression net for qsf. Every case here is a bug that was real once.
# Run: tools/qsf/selfcheck.sh
set -uo pipefail
cd "$(dirname "$0")/../.."
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

QSF=tools/qsf/bin/Debug/net8.0/qsf
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
fails=0

check() { # name expected actual
  local want=${2%$'\r'} got=${3%$'\r'}
  if [ "$want" = "$got" ]; then printf 'ok   %s\n' "$1"
  else printf 'FAIL %s\n       want: %s\n       got:  %s\n' "$1" "$want" "$got"; fails=$((fails + 1)); fi
}

dotnet build tools/qsf/qsf.csproj -v q --nologo >/dev/null || exit 1

# A file already in device shape keeps its line numbers, so a row number the
# agent quotes back as evidence points at the line a human would count to.
read -r shift row <<<"$($QSF inspect tests/QuadStick.Format.Tests/corpus/device-style.csv |
  python3 -c 'import json,sys
p = json.load(sys.stdin)["profiles"][0]
print(p["rowsInsertedForDevice"], p["modes"][0]["bindings"][0]["row"])')"
check "a device-shape file is not moved" "0" "$shift"
first=$(grep -n . tests/QuadStick.Format.Tests/corpus/device-style.csv | sed -n "${row}p" | cut -d: -f1)
check "inspect row numbers match file lines" "$row" "$first"

# A file the device could not read as written gains rows, and every row below
# moves. Silent is the failure mode that matters, so the shift is reported.
shift=$($QSF inspect tests/QuadStick.Format.Tests/corpus/gta-mode1.csv |
  python3 -c 'import json,sys; print(json.load(sys.stdin)["profiles"][0]["rowsInsertedForDevice"])')
check "a headerless file reports the rows it gained" "1" "$shift"

# The template arrives with no version header. It used to gain one at write
# time, moving every row down one AFTER the ops had been placed, so a binding
# aimed at row 7 was written to row 8.
echo '[{"op":"set_cell","row":7,"col":10,"value":"MARK"}]' > "$TMP/mark.json"
$QSF apply --template t.csv --ops "$TMP/mark.json" --out "$TMP/mark.csv" >/dev/null
check "template op lands on the row it named" "MARK" "$(sed -n '7p' "$TMP/mark.csv" | cut -d, -f11)"

# A token the device does not know must never reach a file. All-or-nothing:
# one bad op and nothing is written at all.
cat > "$TMP/bad.json" <<'EOF'
[{"op":"set_binding","row":5,"output":"x","function":"normal","inputs":["lip"]},
 {"op":"set_binding","row":6,"output":"jump","function":"normal","inputs":["lip"]}]
EOF
$QSF apply --template t.csv --ops "$TMP/bad.json" --out "$TMP/never.csv" >/dev/null
check "unknown output is refused" "1" "$?"
check "a refused batch writes nothing" "absent" "$([ -f "$TMP/never.csv" ] && echo present || echo absent)"

for pair in "input:lip_typo" "function:hold"; do
  field=${pair%%:*}; value=${pair#*:}
  if [ "$field" = input ]; then
    printf '[{"op":"set_binding","row":5,"output":"x","function":"normal","inputs":["%s"]}]' "$value" > "$TMP/o.json"
  else
    printf '[{"op":"set_binding","row":5,"output":"x","function":"%s","inputs":["lip"]}]' "$value" > "$TMP/o.json"
  fi
  $QSF apply --template t.csv --ops "$TMP/o.json" --out "$TMP/n.csv" >/dev/null
  check "unknown $field is refused" "1" "$?"
done

# A binding written over a sheet's keyword, file name or label row leaves a
# file that still parses and a mode the device drops.
printf '[{"op":"set_binding","row":3,"output":"x","function":"normal","inputs":["lip"]}]' > "$TMP/hdr.json"
$QSF apply --template t.csv --ops "$TMP/hdr.json" --out "$TMP/n.csv" >/dev/null
check "binding on a sheet header row is refused" "1" "$?"

# A new mode has no rows. add_row must hand back the row it really made.
cat > "$TMP/mode.json" <<'EOF'
[{"op":"add_mode","name":"Driving"},{"op":"add_row","mode":1}]
EOF
newrow=$($QSF apply --template t.csv --ops "$TMP/mode.json" --out "$TMP/mode.csv" |
         python3 -c 'import json,sys; print(json.load(sys.stdin)["applied"][1]["detail"]["row"])')
check "add_row returns a row that exists" "normal" "$(sed -n "${newrow}p" "$TMP/mode.csv" | cut -d, -f2)"

# The write must not move rows out from under the numbers just reported.
shift_at_write=$($QSF apply --template t.csv --ops "$TMP/mode.json" --out "$TMP/mode.csv" |
                 python3 -c 'import json,sys; print(json.load(sys.stdin)["rowsInsertedAtWrite"])')
check "write moves no rows" "0" "$shift_at_write"

$QSF validate tests/QuadStick.Format.Tests/corpus/device-style.csv >/dev/null
check "a real profile validates clean" "0" "$?"

# The firmware's keyword table still holds these names; only the validation
# endpoint dropped them. Refusing one tells somebody to change a name their
# device answers to. This author uses lip_soft 317 times.
printf '[{"op":"set_binding","row":5,"output":"x","function":"normal","inputs":["lip_soft"]}]' > "$TMP/legacy.json"
$QSF apply --template t.csv --ops "$TMP/legacy.json" --out "$TMP/legacy.csv" >/dev/null
check "a legacy input the device knows is accepted" "0" "$?"

# A settings row's column C is the value, not an input name.
printf '[{"op":"set_binding","row":5,"output":"mouse_speed","function":"normal","inputs":["550"]}]' > "$TMP/pref.json"
$QSF apply --template t.csv --ops "$TMP/pref.json" --out "$TMP/pref.csv" >/dev/null
check "a preference value is not read as an input" "0" "$?"
check "the value reaches the file" "550" "$(sed -n '5p' "$TMP/pref.csv" | cut -d, -f3)"


[ "$fails" -eq 0 ] && echo "all qsf checks passed" || echo "$fails check(s) failed"
exit "$fails"
