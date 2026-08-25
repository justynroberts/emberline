# CLAUDE.md

Guidance for Claude Code when working in the Emberline repository.

## What this is

A cross-platform desktop application for driving GRBL-compatible laser cutters and
engravers. C# 14 on .NET 10, Avalonia 12 for the UI, GPLv3. The reference machine
is the BlazeX M5 Pro 10 W. `docs/PRD.md` is the specification; `DESIGN.md` records
the visual decisions and why.

## Commands

```bash
dotnet build Emberline.slnx          # whole solution
dotnet test                         # 352 tests, no hardware needed
dotnet test tests/Emberline.Cam.Tests           # one project
dotnet test --filter "FullyQualifiedName~Raster"   # one area

dotnet run --project src/Emberline.App
dotnet run --project src/Emberline.App -- samples/emberline-badge.svg

./build/package-macos.sh arm64
./build/package-windows.sh win-x64
```

The SDK lives in `~/.dotnet` on this machine, so `export PATH="$HOME/.dotnet:$PATH"`
before any `dotnet` command in a fresh shell.

## Layout

Dependencies point inward. `Emberline.Core` references nothing but the framework.

```
Core        document model, geometry, machine profiles, job contracts, storage
GCode       GRBL protocol, interpreter, streamer, estimator, validator   ← Core
Cam         raster, vector, importers, tracing                           ← Core, GCode
Transport   serial, tcp, websocket, http, virtual                        ← Core, VirtualLaser
Devices     ILaserDevice, GRBL driver, discovery, probe                  ← Core, GCode, Transport
Camera      frame sources                                                ← Core
Vision      lens, homography, rectification, detection                   ← Core, Camera
Materials   material library                                             ← Core
Plugins     plugin contracts, registry, load host                       ← Core, Cam, Devices, Transport, Camera, Materials
AI          the optional assistant                                       ← Core, GCode, Cam, Materials, Devices
App         Avalonia UI                                                  ← everything
```

`Camera` must **not** reference `Vision` — that was a cycle once. The synthetic
camera lives in `Vision` for exactly this reason.

## Things that will bite

**Character-counting streaming.** `GcodeStreamer` is the highest-risk code in the
repository: if it is wrong, every job on every machine is wrong. Two subtleties are
load-bearing and both are commented in place — the re-entrancy guard on `Pump`, and
recording the send *before* handing bytes to the transport. Do not "simplify"
either without running `tests/Emberline.Transport.Tests`.

**The simulator is the test rig.** `Emberline.VirtualLaser` implements GRBL 1.1
including planner back-pressure, and its acknowledgement timing is what makes the
streaming tests meaningful. If you change when it emits `ok`, you are changing what
the tests prove.

**The assistant cannot move the machine.** `IAssistantHost` has no method that
starts, jogs or homes anything; `ProposeAction` displays a card. This is
architectural, not a prompt rule. Do not add a convenience method that bypasses it.

**Plugins cannot displace built-ins.** `PluginRegistry` refuses a registration
that clashes with a built-in importer or driver and reports it. Do not "helpfully"
relax that — silently shadowing the tested G-code path with third-party code is
not a trade anybody agreed to.

**Discovery probes must never send 0x18.** It is GRBL's soft reset, and it is the
most reliable way to make a quiet controller announce itself — which is exactly
why it was there. A scan sweeps the whole subnet before it knows what anything
is, so that byte reaches a laser part-way through a job and ends the job. `$I`
only, plus a listen for controllers that greet on connect. `DiscoveryTests` pins
this; do not "improve" detection rates by putting the reset back.

**Counting build errors with `grep ': error'` misses XAML entirely.** Avalonia
writes `Avalonia error AVLN2100`, with no colon before "error", so that idiom
reports a clean build while the application is in fact broken — it shipped a
package that could not start. Check `Error(s)` from the build summary, or grep for
`AVLN` as well as `error CS`.

**`async void` handlers are a loaded gun.** An exception in one is raised on the
dispatcher after the handler has returned, finds no catch, and aborts the process
— silently, mid-job. Every one in `MainWindow` goes through `GuardAsync`, which
catches, writes a `CrashLog` entry and reports in the console. Add a new event
handler that awaits, and it needs the same treatment.

**Plugin assemblies must not be loaded by path.** `LoadFromAssemblyPath` holds the
file open for the process lifetime, which on Windows locks the DLL so a plugin
cannot be updated or removed while Emberline runs. Read the bytes and use
`LoadFromStream` — `PluginLoadContext.LoadFromFileWithoutLocking` does this for the
plugin and its dependencies. Only Windows can fail this, so the test for it earns
its keep on a Mac by never failing.

**Skia needs help on Linux.** `SkiaSharp.NativeAssets.Linux` ships the native
library, and fontconfig has to be installed on the machine. Without either, every
path touching text or image decoding throws `DllNotFoundException` — and macOS and
Windows carry their natives in the main package, so this only ever shows up on one
of the three platforms.

**Tests must never touch the real application data folder.** `TestEnvironment`
redirects `AppPaths` into a temporary directory from a module initialiser. That is
one line, it fails silently if removed — nothing throws, the writes just go
somewhere else — and it once got deleted by a bulk edit and left hundreds of
duplicate machine profiles in the user's own folder before anybody noticed. Two
tests assert the redirect is in place; do not delete them.

**`TraceOptions` collides with `System.Diagnostics.TraceOptions`.** Any file that
wants both needs a using alias — `SelfTest.cs` has one.

**The tracer's edge bookkeeping is deliberately not a hash set.** Skeleton walking
addresses each pixel-to-pixel edge by index, four slots per pixel. Hashing the pair
of endpoints looks tidier and is a trap: adjacent pixel indices differ by one or by
a row, so the default `long` hash folds every key into a few thousand codes and the
walk goes quadratic — ninety seconds on a three-megapixel image, against under two.

**Millimetres everywhere, Y up.** Inches exist only in the view layer. The SVG
importer performs the single Y flip at the boundary; nothing downstream flips again.

**Avalonia 12, not 11.** `DataFormats` is obsolete (`DataFormat`), drag events
carry `DataTransfer` not `Data`, `Window.SystemDecorations` is `WindowDecorations`,
`TextBox.Watermark` is `PlaceholderText`, and `TextOptions.TextRenderingMode` does
not exist. Filter build output for `error AVLN` as well as `error CS` — XAML errors
do not match a plain `: error` grep.

## Adding things

A machine is a JSON file in `devices/`. A transport implements `ITransport` and
registers in `DeviceFactory`. A camera implements `ICameraSource`. A dithering
kernel is an entry in `Dither.Kernels` plus one in the catalogue. None of these
requires touching Core.

For a machine with an undocumented network protocol, use `ProtocolProbe` — it
records what the controller actually answers to read-only queries. Write the driver
from that transcript, not from guesswork.

## Style

Comments explain why, not what. The codebase assumes the reader can read C# and
cannot read the author's mind: the comments that earn their place are the ones
about GRBL's buffer, why a fill is inset, why the assistant is fenced off, and why
box-averaging is used when downsampling. Delete anything that restates the code.
