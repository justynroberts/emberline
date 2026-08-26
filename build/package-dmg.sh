#!/usr/bin/env bash
# Wrap a built .app in a DMG: the thing people expect to download on a Mac.
#
# A zip drops a loose bundle in Downloads and leaves the user to work out where it
# belongs. A DMG opens as a window with the application on the left and a shortcut
# to Applications on the right, and the gesture is obvious without instructions.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RID="${1:-osx-arm64}"
DIST="$HERE/../dist/$RID"
APP="$DIST/Emberline.app"
STAGE="$HERE/../dist/dmg-$RID"
DMG="$HERE/../dist/emberline-$RID.dmg"

[ -d "$APP" ] || { echo "missing $APP — run package-macos.sh first" >&2; exit 1; }

rm -rf "$STAGE" "$DMG"
mkdir -p "$STAGE"

cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"

# A read-only compressed image. UDZO roughly halves it, which matters when the
# .NET runtime is along for the ride.
hdiutil create -volname "Emberline" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null

rm -rf "$STAGE"

# The image is signed as well as the application inside it, so the download itself
# is verifiable rather than only its contents.
IDENTITY="${MACOS_SIGN_IDENTITY:-}"
if [ -z "$IDENTITY" ] && command -v security >/dev/null 2>&1; then
  IDENTITY=$(security find-identity -v -p codesigning 2>/dev/null \
    | grep "Developer ID Application" | head -1 | sed 's/.*"\(.*\)".*/\1/')
fi

if [ -n "$IDENTITY" ] && command -v codesign >/dev/null 2>&1; then
  codesign --force --timestamp --sign "$IDENTITY" "$DMG" && echo "==> Signed the image"

  # The image needs its own notarisation ticket. It does not inherit the one
  # stapled to the application inside it — notarising the app and assuming the
  # download is covered produces a dmg Gatekeeper still refuses.
  PROFILE="${NOTARY_KEYCHAIN_PROFILE:-notarytool}"
  NOTARY_ARGS=""

  if xcrun notarytool history --keychain-profile "$PROFILE" >/dev/null 2>&1; then
    NOTARY_ARGS="--keychain-profile $PROFILE"
  elif [ -n "${NOTARY_APPLE_ID:-}" ] && [ -n "${NOTARY_PASSWORD:-}" ] && [ -n "${NOTARY_TEAM_ID:-}" ]; then
    NOTARY_ARGS="--apple-id $NOTARY_APPLE_ID --password $NOTARY_PASSWORD --team-id $NOTARY_TEAM_ID"
  fi

  if [ -n "$NOTARY_ARGS" ]; then
    echo "==> Notarising the image"
    if xcrun notarytool submit "$DMG" $NOTARY_ARGS --wait; then
      xcrun stapler staple "$DMG" && echo "==> Notarised and stapled the image"
    else
      echo "==> WARNING: image notarisation failed; Gatekeeper will refuse this download" >&2
    fi
  fi
fi

echo "==> Built $DMG"
du -sh "$DMG"
