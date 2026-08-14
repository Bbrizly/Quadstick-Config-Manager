#!/usr/bin/env bash
# Set up a QuadStick for one game, start to finish, in one command.
#
#     agent/setup.sh cyberpunk-2077
#     agent/setup.sh cyberpunk-2077 --replay      # from the recording, no network
#
# It builds what their own profiles already answer, asks about the rest, waits
# for them to approve it, writes it, and opens it in QuadStick Config Manager.
# --hold-out hides that game's real profiles first, so the result can be checked
# against what they actually built.
set -uo pipefail
cd "$(dirname "$0")/.."
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

GAME="${1:-}"
[ -z "$GAME" ] && { echo "usage: agent/setup.sh <game-slug> [--replay] [--hold-out]"; exit 2; }
shift

CHART="agent/charts/$GAME.json"
CONTROLS="agent/eval/controls-${GAME%%-*}.json"
[ -f "$CHART" ] || { echo "no control chart at $CHART. Charts live in agent/charts/."; exit 2; }
[ -f "$CONTROLS" ] || { echo "no control list at $CONTROLS."; exit 2; }

HOLD=() ; MODE="auto" ; WORK="${TMPDIR:-/tmp}/qs-$GAME"
for arg in "$@"; do
  case "$arg" in
    --replay)  MODE="replay" ;;
    --hold-out) HOLD=(--exclude-family "$GAME") ;;
    *) echo "unknown option $arg"; exit 2 ;;
  esac
done
mkdir -p "$WORK"

dotnet build tools/qsf/qsf.csproj -v q --nologo >/dev/null || exit 1

echo
echo "== what their own profiles already answer =="
python3 agent/predict.py --controls "$CONTROLS" --chart "$CHART" "${HOLD[@]}" \
  --out "$WORK/profile.csv" --trace "$WORK/plan.json" || exit 1

echo
echo "== what the evidence could not settle =="
QSF_MODEL_MODE="$MODE" python3 -u agent/qsagent.py \
  --plan "$WORK/plan.json" --report "$WORK/decisions.json" || exit 1

echo
python3 agent/finalize.py --profile "$WORK/profile.csv" \
  --decisions "$WORK/decisions.json" --interactive --open
