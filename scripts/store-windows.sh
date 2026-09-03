#!/usr/bin/env bash
# Ship a Windows release to the Microsoft Store from here, without opening
# Partner Center. The .msix still has to be packed on Windows (makeappx is a
# Windows SDK tool), so a cloud runner packs it, this pulls it back, and the
# upload and the submission happen locally through the Store CLI.
#
# Prereqs (one time, see packaging/windows/STORE.md):
#   brew install gh microsoft/msstore-cli/msstore-cli
#   msstore reconfigure     # asks for the Partner Center ids and secret
#
# Usage:
#   scripts/store-windows.sh 1.8.0            # upload, leave it in draft
#   scripts/store-windows.sh 1.8.0 --submit   # upload and send for certification
set -euo pipefail

# The Store product id, the same one in the apps.microsoft.com link in README.
APP_ID=9PPQZQNL4WKP

VERSION="${1:?version, e.g. 1.8.0}"
case "${2-}" in
  "")       SUBMIT=no ;;
  --submit) SUBMIT=yes ;;
  *) echo "Unknown option: $2 (the only one is --submit)"; exit 1 ;;
esac

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
MSIX="dist/QuadstickConfigManager-$VERSION.msix"

command -v gh      >/dev/null || { echo "gh is not installed: brew install gh"; exit 1; }
command -v msstore >/dev/null || { echo "msstore is not installed: brew install microsoft/msstore-cli/msstore-cli"; exit 1; }

# Reuse a package already sitting in dist/, so a failed submission can be
# retried without waiting out another ten minute build.
if [ -f "$MSIX" ]; then
  echo "Using $MSIX"
else
  echo "Packing $VERSION on a Windows runner..."
  gh workflow run store-windows.yml -f version="$VERSION"
  # `gh workflow run` hands back no run id, so wait for one that is still going.
  RUN=""
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    sleep 3
    RUN="$(gh run list --workflow=store-windows.yml --limit 1 \
      --json databaseId,status -q '.[] | select(.status != "completed") | .databaseId')"
    [ -n "$RUN" ] && break
  done
  [ -n "$RUN" ] || { echo "The build never started. Look at the Actions tab."; exit 1; }
  gh run watch "$RUN" --exit-status
  mkdir -p dist
  gh run download "$RUN" -n msix -D dist
  [ -f "$MSIX" ] || { echo "The build finished but $MSIX is not there."; exit 1; }
fi

if [ "$SUBMIT" = yes ]; then
  msstore publish "$MSIX" --appId "$APP_ID"
  echo "Sent for certification. Watch it with: msstore submission status $APP_ID"
else
  msstore publish "$MSIX" --appId "$APP_ID" --noCommit
  echo "Uploaded as a draft. Nothing is submitted yet."
  echo "Send it with: msstore submission publish $APP_ID"
fi
