# CLAUDE.md

Guidance for Claude Code when working in the OpenBurn repository.

## What this is

A cross-platform desktop application for driving GRBL-compatible laser cutters and
engravers. C# 14 on .NET 10, Avalonia 12 for the UI, GPLv3. The reference machine
is the BlazeX M5 Pro 10 W. `docs/PRD.md` is the specification; `DESIGN.md` records
the visual decisions and why.

## Commands

```bash
dotnet build OpenBurn.slnx          # whole solution
dotnet test                         # 352 tests, no hardware needed
dotnet test tests/OpenBurn.Cam.Tests           # one project
dotnet test --filter "FullyQualifiedName~Raster"   # one area

dotnet run --project src/OpenBurn.App
dotnet run --project src/OpenBurn.App -- samples/openburn-badge.svg

./build/package-macos.sh arm64
./build/package-windows.sh win-x64
```

The SDK lives in `~/.dotnet` on this machine, so `export PATH="$HOME/.dotnet:$PATH"`
before any `dotnet` command in a fresh shell.

## Layout

Dependencies point inward. `OpenBurn.Core` references nothing but the framework.

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
either without running `tests/OpenBurn.Transport.Tests`.

**The simulator is the test rig.** `OpenBurn.VirtualLaser` implements GRBL 1.1
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
