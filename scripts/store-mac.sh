#!/usr/bin/env bash
# Ship a Mac App Store update from here, the twin of store-windows.sh.
# package-appstore.sh builds and signs the .pkg and fastlane deliver uploads it.
# --submit is the second run: it waits for Apple to finish processing that
# upload, attaches it to the version and sends it to App Review. It never
# uploads again, because Apple takes one upload per version number.
#
# Prereqs (one time, see scripts/appstore/APPSTORE.md):
#   App Store Connect API key saved as ~/.appstoreconnect/private_keys/AuthKey_<id>.p8
#   export ASC_KEY_ID=<id> ASC_ISSUER_ID=<issuer uuid>   # in ~/.zshrc
#
# Usage:
#   scripts/store-mac.sh 1.8.4 "What's new"            # build and upload
#   scripts/store-mac.sh 1.8.4 "What's new" --submit   # attach it and send for review
set -euo pipefail

BUNDLE_ID=com.bbrizly.quadstickconfigmanager

VERSION="${1:?version, e.g. 1.8.4}"
NOTES="${2:?what is new in this version, a sentence or two}"
case "${3-}" in
  "")       SUBMIT=false ;;
  --submit) SUBMIT=true ;;
  *) echo "Unknown option: $3 (the only one is --submit)"; exit 1 ;;
esac

: "${ASC_KEY_ID:?export ASC_KEY_ID, the App Store Connect API key id}"
: "${ASC_ISSUER_ID:?export ASC_ISSUER_ID, the issuer id above the keys list}"
KEY="$HOME/.appstoreconnect/private_keys/AuthKey_$ASC_KEY_ID.p8"
[ -f "$KEY" ] || { echo "Missing $KEY"; exit 1; }
command -v fastlane >/dev/null || { echo "fastlane is not installed: brew install fastlane"; exit 1; }

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PKG="$ROOT/dist/appstore/QuadStickConfigManager.pkg"

if [ "$SUBMIT" = false ]; then
  APP_SIGN="$(security find-identity -v -p codesigning | sed -n 's/.*"\(Apple Distribution: .*\)"/\1/p' | head -1)"
  PKG_SIGN="$(security find-identity -v | sed -n 's/.*"\(3rd Party Mac Developer Installer: .*\)"/\1/p' | head -1)"
  [ -n "$APP_SIGN" ] && [ -n "$PKG_SIGN" ] || { echo "Apple Distribution or Mac Installer certificate is not in the keychain."; exit 1; }
  # Always a fresh build: a stale pkg in dist/appstore has been uploaded before.
  "$ROOT/scripts/appstore/package-appstore.sh" "$VERSION" "$APP_SIGN" "$PKG_SIGN"
  BINARY=(--pkg "$PKG")
else
  BINARY=(--skip_binary_upload true)
fi

# Empty folders so deliver sends only the release notes, never an old listing.
EMPTY="$(mktemp -d)"
API="$(mktemp)"
trap 'rm -rf "$EMPTY" "$API"' EXIT
# fastlane wants the key itself in this file, not its path. mktemp makes it 0600.
python3 -c 'import json,sys; print(json.dumps({"key_id": sys.argv[1], "issuer_id": sys.argv[2], "key": open(sys.argv[3]).read()}))' \
  "$ASC_KEY_ID" "$ASC_ISSUER_ID" "$KEY" > "$API"

# Release notes go to en-US only. If the listing gains another language, it
# needs its own note here or the submission is refused.
NOTES_JSON="$(python3 -c 'import json,sys; print(json.dumps({"en-US": sys.argv[1]}))' "$NOTES")"

fastlane deliver \
  --api_key_path "$API" \
  --app_identifier "$BUNDLE_ID" \
  --platform osx \
  "${BINARY[@]}" \
  --app_version "$VERSION" \
  --release_notes "$NOTES_JSON" \
  --metadata_path "$EMPTY" \
  --screenshots_path "$EMPTY" \
  --skip_screenshots true \
  --run_precheck_before_submit false \
  --automatic_release true \
  --submit_for_review "$SUBMIT" \
  --submission_information '{"export_compliance_uses_encryption": false}' \
  --force

if [ "$SUBMIT" = true ]; then
  echo "Sent to App Review. It goes live on its own once approved."
else
  echo "Uploaded $VERSION. Nothing is submitted yet."
  echo "Send it with: scripts/store-mac.sh $VERSION \"$NOTES\" --submit"
fi
