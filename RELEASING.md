# Releasing Emberline

## Cutting a release

Tag and push. The workflow in `.github/workflows/release.yml` builds all four
platforms, signs the macOS builds if the certificate secrets are present, and
attaches the results to a GitHub release.

```bash
git tag v0.2.0 && git push origin v0.2.0
```

macOS ships as a signed `.dmg`; Windows and Linux ship as zips.

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

## CI

GitHub runners have no keychain, so the workflow falls back to explicit secrets:
`MACOS_CERTIFICATE` (the Developer ID cert exported as `.p12`, base64-encoded),
`MACOS_CERTIFICATE_PASSWORD`, `NOTARY_APPLE_ID`, `NOTARY_PASSWORD`,
`NOTARY_TEAM_ID`. Export the certificate from Keychain Access → right-click the
*Developer ID Application* entry → Export, then `base64 -i cert.p12 | pbcopy`.

Until those are set, tagged builds still succeed but produce ad-hoc signed
images. Releasing from a local machine gives a properly notarised one.
