#!/usr/bin/env bash
# Wrap a macOS self-contained publish folder into a double-clickable .app bundle.
# One source of truth for the bundle's Info.plist, shared by the Makefile and
# the release workflow so a local `make package` and CI build byte-for-byte the
# same thing.
#
#   make-macos-app.sh <publish-dir> <version> <output.app>
set -euo pipefail

PUBLISH="$1"; VERSION="$2"; APP="$3"
EXE="QuadStickConfigManager"      # AssemblyName == the apphost == CFBundleExecutable
SHORT="${VERSION%%-*}"            # CFBundleVersion must be dotted integers only

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

# The whole self-contained publish (apphost + managed dlls + native dylibs)
# sits beside the executable: the standard Avalonia macOS layout. No single-file
# temp-extraction, no shipped debug symbols.
cp -R "$PUBLISH"/* "$APP/Contents/MacOS/"
rm -f "$APP/Contents/MacOS/"*.pdb
chmod +x "$APP/Contents/MacOS/$EXE"

# Dock / Finder icon. Same AppIcon the running window uses, in macOS .icns form.
cp "$(dirname "$0")/../src/QuadStick.App/Assets/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Quadstick: Config Manager</string>
  <key>CFBundleDisplayName</key><string>Quadstick: Config Manager</string>
  <key>CFBundleIdentifier</key><string>com.bbrizly.quadstickconfigmanager</string>
  <key>CFBundleExecutable</key><string>$EXE</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleShortVersionString</key><string>$SHORT</string>
  <key>CFBundleVersion</key><string>$SHORT</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
</dict>
</plist>
PLIST

echo "Built $APP (v$VERSION)"
