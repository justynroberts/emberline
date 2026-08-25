#!/usr/bin/env bash
# Build Emberline.app for macOS.
#
# Self-contained: the .NET runtime is bundled, so a user does not have to install
# anything. That matters for a hobbyist tool — "install the .NET 10 runtime first"
# loses most of the audience.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
ARCH="${1:-$(uname -m)}"
case "$ARCH" in
  arm64|aarch64) RID=osx-arm64 ;;
  x86_64|x64)    RID=osx-x64 ;;
  *) echo "unknown architecture: $ARCH" >&2; exit 1 ;;
esac

VERSION="$(grep -o '<Version>[^<]*' "$ROOT/Directory.Build.props" | head -1 | cut -d'>' -f2)"
OUT="$ROOT/dist/$RID"
APP="$OUT/Emberline.app"

echo "==> Publishing $RID (version $VERSION)"
rm -rf "$OUT"
dotnet publish "$ROOT/src/Emberline.App/Emberline.App.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=false \
  -p:DebugType=none \
  -o "$OUT/publish"

echo "==> Assembling the bundle"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$OUT/publish/." "$APP/Contents/MacOS/"

# Device profiles and samples ship inside the bundle so a fresh install has
# machines to choose from.
mkdir -p "$APP/Contents/MacOS/devices" "$APP/Contents/MacOS/samples"
cp "$ROOT/devices/"*.json "$APP/Contents/MacOS/devices/"
cp "$ROOT/samples/"*.svg "$APP/Contents/MacOS/samples/" 2>/dev/null || true

if [ -f "$ROOT/src/Emberline.App/Assets/emberline.icns" ]; then
  cp "$ROOT/src/Emberline.App/Assets/emberline.icns" "$APP/Contents/Resources/emberline.icns"
elif command -v iconutil >/dev/null 2>&1; then
  "$HERE/make-icons.sh" && cp "$ROOT/src/Emberline.App/Assets/emberline.icns" "$APP/Contents/Resources/emberline.icns"
fi

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Emberline</string>
    <key>CFBundleDisplayName</key><string>Emberline</string>
    <key>CFBundleIdentifier</key><string>com.fintonlabs.emberline</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleExecutable</key><string>Emberline</string>
    <key>CFBundleIconFile</key><string>emberline</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSCameraUsageDescription</key>
    <string>Emberline uses a camera to show the laser bed so you can position artwork over your material.</string>
    <key>CFBundleDocumentTypes</key>
    <array>
        <dict>
            <key>CFBundleTypeName</key><string>Artwork</string>
            <key>CFBundleTypeRole</key><string>Editor</string>
            <key>LSItemContentTypes</key>
            <array>
                <string>public.svg-image</string>
                <string>public.png</string>
                <string>public.jpeg</string>
            </array>
        </dict>
    </array>
</dict>
</plist>
PLIST

chmod +x "$APP/Contents/MacOS/Emberline"

# Ad-hoc signing. Without it, Gatekeeper refuses to run an unsigned bundle at all
# on Apple Silicon; with it the user still has to right-click-Open the first time,
# which the README explains.
if command -v codesign >/dev/null 2>&1; then
  codesign --force --deep --sign - "$APP" 2>/dev/null && echo "==> Ad-hoc signed"
fi

echo "==> Built $APP"
du -sh "$APP"
