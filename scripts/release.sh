#!/usr/bin/env bash
# The only thing in this repository that ships anything.
#
# Releases are built here rather than in CI, because signing and notarising need
# the Developer ID certificate and the notarytool keychain profile, and those
# live on this machine. A GitHub runner has neither, so a release built there is
# ad-hoc signed — which tells whoever downloads it that the application is
# damaged.
#
#   scripts/release.sh 0.2.0
#   scripts/release.sh 0.2.0 --dry-run     # everything except tag and publish
#
# It refuses rather than guesses: a dirty tree, a version that already exists, a
# failing test, or an unnotarised disk image all stop it before anything becomes
# visible.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_NOLOGO=1

VERSION="" ; DRY_RUN=0
for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=1 ;;
    *) [[ "$arg" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] && VERSION="$arg" ;;
  esac
done

red()   { printf '\033[31m%s\033[0m' "$1"; }
green() { printf '\033[32m%s\033[0m' "$1"; }
dim()   { printf '\033[2m%s\033[0m'  "$1"; }
step()  { printf '\n%s %s\n' "$(green ▸)" "$1"; }
die()   { printf '\n%s %s\n' "$(red ✗)" "$1" >&2; [ $# -gt 1 ] && printf '%s\n' "$(dim "  $2")" >&2; exit 1; }

[ -n "$VERSION" ] || die "no version given" "scripts/release.sh 0.2.0 [--dry-run]"

# ---- refuse before building anything ---------------------------------------

step "Checking the tree is clean"
[ -z "$(git status --porcelain)" ] || \
  die "the working tree has uncommitted changes" \
      "a release must correspond to a commit, or nobody can tell what shipped"

BRANCH=$(git rev-parse --abbrev-ref HEAD)
[ "$BRANCH" = "main" ] || dim "  on $BRANCH, not main — continuing, but this is unusual"$'\n'

step "Checking the version is new"
git tag --list | grep -qx "v$VERSION" && die "v$VERSION already exists as a tag" "pick a later version"

step "Checking the signing identity is present"
security find-identity -v -p codesigning 2>/dev/null | grep -q "Developer ID Application" || \
  die "no Developer ID certificate in the keychain" \
      "without it the build is ad-hoc signed and cannot be distributed"

# Checked now rather than after a ten-minute build, because the failure is the
# same either way and finding out early costs nothing.
step "Checking notarytool credentials"
xcrun notarytool history --keychain-profile "${NOTARY_KEYCHAIN_PROFILE:-notarytool}" >/dev/null 2>&1 || \
  die "the notarytool keychain profile is missing" \
      "xcrun notarytool store-credentials \"notarytool\" --apple-id <id> --team-id <team>"

# ---- prove it works before making it visible -------------------------------

step "Running the tests"
dotnet test --nologo -v q || die "tests failed" "a release with failing tests is not a release"

step "Setting the version to $VERSION"
# A dry run has to leave the tree exactly as it found it. Without this the bump
# survives, the next dry run sees a dirty tree, and refuses — so the rehearsal
# can only be performed once.
[ "$DRY_RUN" = "1" ] && trap 'git checkout -- Directory.Build.props 2>/dev/null || true' EXIT
sed -i '' "s|<Version>[^<]*</Version>|<Version>$VERSION</Version>|" Directory.Build.props

step "Building, signing, notarising and stapling"
bash build/package-macos.sh arm64
bash build/package-dmg.sh osx-arm64
bash build/package-macos.sh x86_64
bash build/package-dmg.sh osx-x64

# Windows and Linux cross-publish from here. They are unsigned either way, so
# there is nothing a runner would add.
bash build/package-windows.sh win-x64
bash build/package-linux.sh linux-x64

# ---- verify the artifacts, not the intention -------------------------------

step "Verifying the artifacts"
ARM="dist/emberline-osx-arm64.dmg"
X64="dist/emberline-osx-x64.dmg"

for f in "$ARM" "$X64"; do
  [ -f "$f" ] || die "$f was not produced"

  xcrun stapler validate "$f" >/dev/null 2>&1 || \
    die "$f has no notarisation ticket stapled" \
        "the image needs its own ticket; it does not inherit the app's"

  # The only check that reflects what somebody downloading this actually sees.
  ASSESSMENT=$(spctl -a -vvv -t install "$f" 2>&1 || true)
  case "$ASSESSMENT" in
    *"Notarized Developer ID"*) echo "  $(basename "$f") — Gatekeeper accepts it" ;;
    *) die "Gatekeeper does not accept $(basename "$f")" "$ASSESSMENT" ;;
  esac
done

cd dist
zip -qry "emberline-win-x64.zip" "win-x64"
zip -qry "emberline-linux-x64.zip" "linux-x64"
cd ..

if [ "$DRY_RUN" = "1" ]; then
  printf '\n%s %s is built and verified. Nothing was tagged or published.\n  %s\n\n' \
    "$(green ✓)" "$VERSION" "$(dim 'artifacts in dist/, tree left untouched')"
  exit 0
fi

# ---- only now does any of it become visible --------------------------------

step "Committing, tagging and publishing"
git add -A
git commit -m "Release $VERSION"
git push origin "$BRANCH"
git tag -a "v$VERSION" -m "Emberline $VERSION"
git push origin "v$VERSION"

NOTES_ARG="--generate-notes"
[ -f "dist/NOTES.md" ] && NOTES_ARG="--notes-file dist/NOTES.md"

gh release create "v$VERSION" \
  "$ARM" "$X64" dist/emberline-win-x64.zip dist/emberline-linux-x64.zip \
  --title "Emberline $VERSION" $NOTES_ARG

printf '\n%s Emberline %s published.\n\n' "$(green ✓)" "$VERSION"
