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
the one that built it. Opening it needs right-click → Open, which is not
something to ask a user to do.

Notarisation needs three values, as environment variables locally and as
repository secrets in CI:

| Secret | What it is |
|---|---|
| `NOTARY_APPLE_ID` | The Apple ID email on the developer account |
| `NOTARY_PASSWORD` | An **app-specific password** from appleid.apple.com — not the account password |
| `NOTARY_TEAM_ID` | The ten-character team identifier |
| `MACOS_CERTIFICATE` | The Developer ID cert exported as `.p12`, base64-encoded |
| `MACOS_CERTIFICATE_PASSWORD` | The password set when exporting that `.p12` |

Generate the app-specific password at appleid.apple.com → Sign-In and Security →
App-Specific Passwords. Export the certificate from Keychain Access → right-click
the *Developer ID Application* entry → Export, then
`base64 -i cert.p12 | pbcopy`.

The application and the disk image are notarised separately — the image does not
inherit the app's ticket. Both are stapled, so the check works offline.

Without these set, the build still succeeds and still signs; it just prints that
it did not notarise rather than pretending it did.
