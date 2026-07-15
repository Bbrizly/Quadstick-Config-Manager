#!/usr/bin/env bash
# Build, sign, and package the app for Mac App Store upload.
# Produces dist/appstore/QuadStickConfigManager.pkg for Transporter.
#
# Prereqs (one time, see APPSTORE.md):
#   - "Apple Distribution" + "Mac Installer Distribution" certs in Keychain
#   - Mac App Store provisioning profile saved as scripts/appstore/embedded.provisionprofile
#
# Usage:
#   scripts/appstore/package-appstore.sh 1.0.0 "Apple Distribution: YOUR NAME (TEAMID)" "3rd Party Mac Developer Installer: YOUR NAME (TEAMID)"
set -euo pipefail

VERSION="${1:?version, e.g. 1.0.0}"
APP_SIGN="${2:?Apple Distribution signing identity}"
PKG_SIGN="${3:?Mac Installer Distribution signing identity}"

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HERE="$ROOT/scripts/appstore"
OUT="$ROOT/dist/appstore"
APP="$OUT/Quadstick Config Manager.app"
RID="osx-$(uname -m | sed 's/x86_64/x64/')"

[ -f "$HERE/embedded.provisionprofile" ] || { echo "Missing $HERE/embedded.provisionprofile (Mac App Store profile from developer.apple.com)"; exit 1; }

rm -rf "$OUT"; mkdir -p "$OUT"

# 1. Publish and wrap, same bundle the normal release uses.
dotnet publish "$ROOT/src/QuadStick.App/QuadStick.App.csproj" -c Release -r "$RID" \
  --self-contained true -o "$OUT/pub" --nologo
rm -f "$OUT/pub/"*.pdb
"$ROOT/scripts/make-macos-app.sh" "$OUT/pub" "$VERSION" "$APP"

# 2. Embed the provisioning profile (required for MAS).
cp "$HERE/embedded.provisionprofile" "$APP/Contents/embedded.provisionprofile"

# 3. Sign inside-out: native dylibs and managed dlls, then nested
#    executables, then the app. Managed .NET assemblies are code too, and
#    MAS deep-verify rejects any unsigned code object in the bundle.
find "$APP/Contents/MacOS" \( -name '*.dylib' -o -name '*.dll' \) -print0 | while IFS= read -r -d '' lib; do
  codesign --force --timestamp --sign "$APP_SIGN" "$lib"
done
find "$APP/Contents/MacOS" -type f -perm +111 ! -name '*.dylib' ! -name 'QuadStickConfigManager' -print0 | while IFS= read -r -d '' bin; do
  codesign --force --timestamp --options runtime \
    --entitlements "$HERE/entitlements-inherit.plist" --sign "$APP_SIGN" "$bin"
done
codesign --force --timestamp --options runtime \
  --entitlements "$HERE/entitlements-app.plist" --sign "$APP_SIGN" "$APP"

# 4. Verify before packaging.
codesign --verify --deep --strict --verbose=2 "$APP"

# 5. Installer package for App Store Connect.
productbuild --component "$APP" /Applications \
  --sign "$PKG_SIGN" "$OUT/QuadStickConfigManager.pkg"

echo "Done: $OUT/QuadStickConfigManager.pkg"
echo "Upload with the Transporter app (Mac App Store, free) or:"
echo "  xcrun altool --upload-app -f '$OUT/QuadStickConfigManager.pkg' -t macos --apiKey ... --apiIssuer ..."
