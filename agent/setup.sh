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
[ -z "$GAME" ] && { echo "usage: agent/setup.sh \"<game or app>\" [--replay] [--hold-out]"; exit 2; }
shift

# Whatever they typed, reduced to a filename. "Elden Ring" and "elden-ring"
# have to land on the same chart or the same game gets researched twice.
SLUG=$(printf '%s' "$GAME" | tr '[:upper:]' '[:lower:]' | sed 's/[^a-z0-9]\{1,\}/-/g; s/^-//; s/-$//')
CHART="agent/charts/$SLUG.json"
WORK="${TMPDIR:-/tmp}/qs-$SLUG"

HOLD=() ; MODE="auto" ; RESEARCH=()
for arg in "$@"; do
  case "$arg" in
    --replay)  MODE="replay"; RESEARCH=(--replay) ;;
    --hold-out) HOLD=(--exclude-family "$SLUG") ;;
    *) echo "unknown option $arg"; exit 2 ;;
  esac
done
mkdir -p "$WORK"

dotnet build tools/qsf/qsf.csproj -v q --nologo >/dev/null || exit 1

if [ ! -f "$CHART" ]; then
  echo
  echo "== nobody has charted $GAME, so reading how it is controlled =="
  python3 agent/research.py "$GAME" ${RESEARCH[@]+"${RESEARCH[@]}"} --out "$CHART" || exit 1
fi

# A hand written control list wins when one exists, because a person checked it.
CONTROLS_ARG=()
for candidate in "agent/eval/controls-$SLUG.json" "agent/eval/controls-${SLUG%%-*}.json"; do
  [ -f "$candidate" ] && { CONTROLS_ARG=(--controls "$candidate"); break; }
done

echo
echo "== what their own profiles already answer =="
python3 agent/predict.py ${CONTROLS_ARG[@]+"${CONTROLS_ARG[@]}"} --chart "$CHART" ${HOLD[@]+"${HOLD[@]}"} \
  --out "$WORK/profile.csv" --trace "$WORK/plan.json" || exit 1

echo
echo "== what the evidence could not settle =="
QSF_MODEL_MODE="$MODE" python3 -u agent/qsagent.py \
  --plan "$WORK/plan.json" --report "$WORK/decisions.json" || exit 1

echo
python3 agent/finalize.py --profile "$WORK/profile.csv" \
  --decisions "$WORK/decisions.json" --interactive --open
