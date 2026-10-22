#!/bin/sh
# Self-driving smoke test of the real app on this Mac (`pnpm e2e:mac`).
#
# Builds the UI in Vite mode `e2e` and the shell with the `e2e` cargo feature,
# then launches the real binary with HOME pointed at a throwaway folder. The
# WebView walks the UI by itself, writes a report and quits. Nothing needs
# Accessibility or Screen Recording access, and the real profiles, settings
# and rescue draft are never read or written.
#
#   QCM_E2E_RUNS=2        runs, each in a fresh HOME (default 2)
#   QCM_E2E_SKIP_BUILD=1  reuse the last e2e build
#   CARGO_TARGET_DIR      where cargo builds (default ./target)
set -eu

ROOT=$(cd "$(dirname "$0")/.." && pwd)
TARGET=${CARGO_TARGET_DIR:-$ROOT/target}
RUNS=${QCM_E2E_RUNS:-2}
BIN=$TARGET/release/qcm-tauri

if [ "${QCM_E2E_SKIP_BUILD:-0}" != 1 ]; then
  free_kb=$(df -k / | awk 'NR==2 {print $4}')
  if [ "$free_kb" -lt 4194304 ]; then
    echo "e2e: under 4 GB free on the boot disk, not building" >&2
    exit 1
  fi
  # The UI in Vite mode `e2e`, and a window WebKit keeps running while it is
  # covered or the screen is locked (by default it suspends a hidden page).
  config=$(cd "$ROOT" && node -e '
    const conf = require("./src-tauri/tauri.conf.json");
    const windows = conf.app.windows.map((w) => ({ ...w, backgroundThrottling: "disabled" }));
    process.stdout.write(JSON.stringify({
      build: { beforeBuildCommand: "pnpm exec vite build --mode e2e" },
      app: { windows },
    }));')
  (cd "$ROOT" && pnpm tauri build --features e2e --no-bundle --config "$config")
fi

# The real data folders, to prove the run left them alone.
real_state() {
  for dir in "$REAL_HOME/Library/Application Support/QuadStickConfigManagerRewrite" \
             "$REAL_HOME/Library/Application Support/QuadStickConfigManager"; do
    [ -d "$dir" ] && find "$dir" -type f -exec stat -f '%m %z %N' {} + 2>/dev/null | sort
  done
  return 0
}
REAL_HOME=$HOME
before=$(real_state)

status=0
run=1
while [ "$run" -le "$RUNS" ]; do
  home=$(mktemp -d "${TMPDIR:-/tmp}/qcm-e2e-home.XXXXXX")
  drive=$home/drive/QUADSTICK
  mkdir -p "$drive"
  cp "$ROOT/tests/QuadStick.Format.Tests/corpus/default.csv" "$drive/default.csv"
  report=$home/report.json

  echo "e2e run $run of $RUNS in $home"
  HOME=$home QCM_E2E_REPORT=$report QCM_E2E_DEVICE_DIR=$drive \
    "$BIN" >"$home/stdout.log" 2>"$home/stderr.log" &
  pid=$!
  waited=0
  while kill -0 "$pid" 2>/dev/null && [ "$waited" -lt 240 ]; do
    sleep 1
    waited=$((waited + 1))
  done
  if kill -0 "$pid" 2>/dev/null; then
    kill "$pid"
    echo "e2e run $run: still running after 240 s, killed" >&2
  fi
  code=0
  wait "$pid" || code=$?

  [ -f "$home/report.log" ] && sed 's/^/  /' "$home/report.log"
  ok=1
  if [ "$code" -eq 0 ]; then echo "  quit: the app closed itself, exit 0"; else echo "  app exited $code" >&2; ok=0; fi
  if [ ! -f "$report" ] || ! grep -q '"passed": true' "$report"; then
    echo "  report missing or not passed: $report" >&2
    ok=0
  fi
  saved="$home/Documents/QuadStick Profiles/untitled.csv"
  if [ ! -f "$saved" ] || ! grep -q square "$saved"; then
    echo "  the saved profile is missing or lacks the edit: $saved" >&2
    ok=0
  fi
  if grep -qi 'panicked' "$home/stderr.log"; then
    echo "  Rust panicked:" >&2
    sed 's/^/    /' "$home/stderr.log" >&2
    ok=0
  fi
  if [ "$ok" -eq 1 ]; then
    echo "e2e run $run: PASS"
    rm -rf "$home"
  else
    echo "e2e run $run: FAIL (kept $home)" >&2
    status=1
  fi
  run=$((run + 1))
done

after=$(real_state)
if [ "$before" != "$after" ]; then
  echo "e2e: the real app data folders changed during the run" >&2
  status=1
else
  echo "e2e: real app data untouched"
fi
exit "$status"
