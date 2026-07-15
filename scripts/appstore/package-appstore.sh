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

# 1. Publish single-file and wrap. Single-file embeds the managed dlls and the
#    deps/runtimeconfig json into the apphost, so Contents/MacOS holds only
#    Mach-O binaries (apphost + native dylibs). Loose json/dll files there are
#    neither signable code nor sealed resources, and MAS deep-verify rejects
#    them. Native libraries stay loose (not self-extracted), so no sandbox
#    temp-extraction crash at launch.
dotnet publish "$ROOT/src/QuadStick.App/QuadStick.App.csproj" -c Release -r "$RID" \
  --self-contained true -p:PublishSingleFile=true -o "$OUT/pub" --nologo
rm -f "$OUT/pub/"*.pdb
"$ROOT/scripts/make-macos-app.sh" "$OUT/pub" "$VERSION" "$APP"

# 1b. MAS rejects an arm64-only build unless it declares macOS 12.0+ (otherwise
#     it demands a universal Intel binary). Bump the floor here only; the
#     direct-download builds keep their wider macOS 11 support. Must run before
#     signing, since editing Info.plist after codesign breaks the signature.
/usr/libexec/PlistBuddy -c "Set :LSMinimumSystemVersion 12.0" "$APP/Contents/Info.plist"

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
