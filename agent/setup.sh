#!/usr/bin/env bash
# Set up a QuadStick for one game, start to finish, in one command.
#
#     agent/setup.sh "Hollow Knight Silksong"
#     agent/setup.sh "Elden Ring" --replay        # from the recording, no network
#     agent/setup.sh --edit mine.csv "make sprint a hard puff"
#
# This is the terminal front end. The app's window runs the same agent/run.py
# and shows the same events as cards, so there is one pipeline and not two.
set -uo pipefail
cd "$(dirname "$0")/.."
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

dotnet build tools/qsf/qsf.csproj -v q --nologo >/dev/null || exit 1

if [ "${1:-}" = "--edit" ]; then
  [ $# -ge 3 ] || { echo 'usage: agent/setup.sh --edit <profile.csv> "<what to change>" [--replay]'; exit 2; }
  PROFILE="$2"; REQUEST="$3"; shift 3
  exec python3 -u agent/terminal.py --edit "$PROFILE" --request "$REQUEST" "$@"
fi

GAME="${1:-}"
[ -z "$GAME" ] && { echo 'usage: agent/setup.sh "<game or app>" [--replay] [--hold-out]'; exit 2; }
shift
exec python3 -u agent/terminal.py --game "$GAME" "$@"
