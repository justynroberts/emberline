# Releasing Emberline

## Cutting a release

```bash
scripts/release.sh 0.2.0 --dry-run    # build and verify, publish nothing
scripts/release.sh 0.2.0
```

That script is the only thing here that ships anything. It runs the tests,
builds all four platforms, signs, notarises and staples the macOS images, checks
that Gatekeeper actually accepts them, and only then tags and publishes.

It refuses rather than guesses: a dirty tree, an existing version, a missing
certificate, missing notarytool credentials, a failing test, or a disk image
Gatekeeper rejects all stop it before anything becomes visible. The credential
checks run first, so a missing profile costs a second rather than a ten-minute
build.

macOS ships as a notarised `.dmg` per architecture; Windows and Linux ship as
zips, cross-published from the same machine.

## Releases are built locally, not in CI

This looks backwards and is deliberate. Signing needs the Developer ID
certificate and notarising needs the notarytool keychain profile, and both live
on the release machine. A GitHub runner has neither, so anything it builds is
ad-hoc signed — which tells whoever downloads it that the application is
damaged. A tag-triggered release workflow would also race the local one and
overwrite good images with broken ones, so there isn't one.

CI still builds and tests every platform on push, which is what catches a
cross-platform break. It just does not publish.

## macOS signing

Signing is what stops Gatekeeper telling somebody their download is damaged.
Locally it happens automatically when a Developer ID certificate is in the
keychain — `build/package-macos.sh` finds it, and falls back to ad-hoc signing so
a machine without one still produces a runnable local build.

`codesign --deep` is used deliberately even though Apple deprecates it. The
documented replacement — signing each file individually — does not work on a
.NET bundle: the whole payload lives in `Contents/MacOS/`, codesign requires
everything there to be code, and the payload includes `.deps.json`, device
profiles and sample artwork. Moving that into `Contents/Resources/` is the real
fix and means teaching the runtime to look somewhere other than beside its own
executable.

## Notarisation

**Signing alone is not enough to distribute.** A signed but un-notarised build
reports `source=Unnotarized Developer ID` and is refused on any Mac other than
the one that built it.

Credentials are already stored in a keychain profile named `notarytool`, so the
scripts notarise automatically with no configuration and no password anywhere in
the environment. Check it rather than assuming it is missing:

```bash
xcrun notarytool history --keychain-profile notarytool
```

If that ever needs recreating, the app-specific password comes from
appleid.apple.com → Sign-In and Security → App-Specific Passwords:

```bash
xcrun notarytool store-credentials "notarytool" --apple-id <id> --team-id 2H574B6N62
```

The application and the disk image are notarised **separately** — the image does
not inherit the app's ticket. Both are stapled, so the check works offline. The
order is sign → notarise → staple, and it matters.

Verify before publishing. This is the only check that reflects what a user sees:

```bash
spctl -a -vvv -t install dist/emberline-osx-arm64.dmg
# accepted
# source=Notarized Developer ID
```

Anything other than `Notarized Developer ID` means the download is broken for
everyone but you.

