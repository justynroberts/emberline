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

# Signing and notarisation.
#
# A Developer ID certificate is what lets somebody else open this without being
# told the application is damaged. Ad-hoc signing keeps a local build runnable and
# is not distributable.
#
# --deep is used deliberately, and Apple has deprecated it. The alternative is to
# sign every file individually, which fails here: .NET puts its whole payload in
# Contents/MacOS, codesign requires everything in that directory to be code, and
# the payload includes device profiles, sample artwork and .deps.json. Moving that
# data to Contents/Resources would be the textbook fix and would mean teaching the
# runtime to look somewhere other than beside its own executable. --deep signs the
# bundle correctly today; if it stops working, that is the fix.
IDENTITY="${MACOS_SIGN_IDENTITY:-}"

if [ -z "$IDENTITY" ] && command -v security >/dev/null 2>&1; then
  IDENTITY=$(security find-identity -v -p codesigning 2>/dev/null \
    | grep "Developer ID Application" | head -1 | sed 's/.*"\(.*\)".*/\1/')
fi

if [ -n "$IDENTITY" ] && command -v codesign >/dev/null 2>&1; then
  echo "==> Signing as: $IDENTITY"
  find "$APP" -name "_CodeSignature" -type d -exec rm -rf {} + 2>/dev/null || true

  codesign --force --deep --timestamp --options runtime \
    --entitlements "$HERE/entitlements.plist" --sign "$IDENTITY" "$APP" >/dev/null 2>&1

  if codesign --verify --strict "$APP" 2>/dev/null; then
    echo "==> Signature valid"
  else
    echo "==> WARNING: signature did not verify" >&2
  fi

  # Notarisation, when credentials are supplied. Without it Gatekeeper still
  # refuses the bundle on another machine — signing alone is not enough, and
  # saying otherwise would set somebody up to be told their download is damaged.
  if [ -n "${NOTARY_APPLE_ID:-}" ] && [ -n "${NOTARY_PASSWORD:-}" ] && [ -n "${NOTARY_TEAM_ID:-}" ]; then
    echo "==> Notarising (this takes a few minutes)"
    ZIP="$OUT/notarise.zip"
    ditto -c -k --keepParent "$APP" "$ZIP"

    if xcrun notarytool submit "$ZIP" \
        --apple-id "$NOTARY_APPLE_ID" --password "$NOTARY_PASSWORD" --team-id "$NOTARY_TEAM_ID" \
        --wait; then
      xcrun stapler staple "$APP" && echo "==> Notarised and stapled"
    else
      echo "==> WARNING: notarisation failed; the bundle is signed but Gatekeeper will refuse it" >&2
    fi
    rm -f "$ZIP"
  else
    echo "==> Not notarised. Set NOTARY_APPLE_ID, NOTARY_PASSWORD and NOTARY_TEAM_ID to notarise."
    echo "    Without it, opening this on another Mac needs right-click then Open."
  fi
elif command -v codesign >/dev/null 2>&1; then
  echo "==> No Developer ID certificate; ad-hoc signing only (not distributable)"
  codesign --force --deep --sign - "$APP" 2>/dev/null
fi

echo "==> Built $APP"
du -sh "$APP"
