# OpenBurn

**Open-source laser cutting and engraving.** Windows, macOS and Linux.

OpenBurn is a modern desktop application for designing, positioning, engraving and
cutting with GRBL-compatible laser machines. It exists because the good open-source
option — LaserGRBL — is Windows-only and speaks nothing but USB serial, and the
good cross-platform option is proprietary.

The first reference machine is the **BlazeX M5 Pro 10 W**, over USB now and over
Wi-Fi as the network protocol is confirmed. Nothing in the core assumes it.

---

## What works today

| | |
|---|---|
| **Connection** | USB serial, TCP (telnet), WebSocket, HTTP command, and a built-in simulator |
| **Discovery** | USB port enumeration, and an opt-in local network scan that probes for GRBL |
| **Import** | SVG (paths, primitives, nested transforms, real-world sizing), DXF (lines, arcs, polylines with bulges, ellipses, splines, blocks), PNG/JPEG/BMP/GIF/WebP, G-code |
| **CAM** | Raster engraving with ten dithering kernels, vector cut/score/engrave, hatch and offset fills, bitmap tracing, text to outlines |
| **Job engine** | Character-counting streaming, pause/resume/stop, resume-from-line after a dropped link, live progress and acceleration-aware time estimates |
| **Editing** | Drag, scale and rotate on the canvas, marquee selection, snapping, align, distribute, group, array, and undo across all of it |
| **Preview** | Toolpath simulation with scrubbing, and a G-code view of the exact lines that will be sent |
| **Safety** | Pre-flight validation, framing at pointer power, emergency stop on Escape, an assistant that cannot move the machine |
| **Camera** | MJPEG, HTTP snapshot and synthetic sources; lens and perspective correction; bed overlay; workpiece detection |
| **Materials** | A built-in library keyed by laser wattage, with rescaling and hazard warnings |
| **Configuration** | A `$` settings editor with every value named, explained and unit-labelled, and confirmation on the ones that can drive a machine into itself |
| **Rotary** | Roller and chuck attachments, with the axis rescaled from the real steps-per-millimetre rather than a guess |
| **Assistant** | Optional Claude-powered help with settings, faults and job setup |
| **Job library** | Every job recorded locally with its settings, so "what worked last time" is answerable |

---

## Running it

### From a build

```bash
# macOS
open dist/osx-arm64/OpenBurn.app

# Windows
dist\win-x64\OpenBurn.exe

# Linux
./dist/linux-x64/OpenBurn
```

On macOS the bundle is ad-hoc signed, so the first launch needs
**right-click → Open** to get past Gatekeeper. After that it opens normally.

### From source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/justynroberts/openburn
cd openburn
dotnet run --project src/OpenBurn.App

# open a file at startup
dotnet run --project src/OpenBurn.App -- samples/openburn-badge.svg
```

### Building distributables

```bash
./build/package-macos.sh arm64     # or x86_64
./build/package-windows.sh win-x64 # runs on any platform
./build/package-linux.sh linux-x64
```

---

## Try it without a laser

Press **Virtual** in the machine panel. That connects to a full in-process GRBL
1.1 controller — receive buffer, planner queue, status reports, alarms, soft
limits and all — so you can load artwork, frame it, run a job and watch the head
move without owning a machine or risking one.

The same simulator is what the test suite runs complete jobs against, which is why
the streaming protocol is trustworthy without a laser plugged into CI.

For the camera workflow, pick **Synthetic bed camera** and press Capture:
it renders a bed seen through a wide-angle lens at an angle, so calibration,
perspective correction and object detection can all be practised on a laptop.

---

## First run with a real machine

1. **Pick your machine** from the dropdown, or copy `devices/generic-grbl.json`
   and edit the bed size and wattage.
2. **Connect** over USB, or type the address and press Wi-Fi.
3. **Read `$$`** from the console drawer. OpenBurn checks the settings that matter
   and tells you if `$30` disagrees with the profile or `$32` laser mode is off.
4. **Home** the machine, then jog to your material and press **Zero XY**.
5. **Import** artwork, choose a material, press **Apply**.
6. **Frame** — the head traces the outline at pointer power. This is the last
   chance to notice the artwork is twenty millimetres off.
7. **Start.**

Escape is the emergency stop from anywhere in the window.

---

## Getting settings right

The built-in material library is a starting point, not an answer. Every machine,
lens, focal height and batch of plywood is different, and settings measured on a
different wattage are a guess with arithmetic applied.

Press **Test grid**. It burns a power-versus-speed matrix on an offcut in a few
minutes and gives you numbers that are true for *your* machine. Everything else in
the material system exists to stop you starting from nothing.

---

## The assistant

Optional and off until you give it a key. Set `ANTHROPIC_API_KEY` in your
environment, or save the key in `anthropic.key` inside the application data
folder, then open the assistant panel (✦ in the tool rail).

It can read the machine state, the controller settings, the job and the console
log; it can change layer settings and prepare a test grid; and it can ask you to
press a button.

**It cannot move the machine.** There is no code path from the assistant to the
gantry. When it wants something to happen it produces a confirmation card and
waits — that is enforced by the architecture, not by an instruction in a prompt.

Everything else in OpenBurn works with the assistant switched off, offline, and
with no account of any kind.

---

## Adding a machine

Drop a JSON file in `devices/`. No code, no rebuild. See
[`devices/README.md`](devices/README.md) for the fields.

If your machine needs behaviour the generic GRBL driver does not have, run
**Machine → Protocol probe** first. It sends a set of read-only queries and writes
a Markdown transcript of exactly what the controller answered. That transcript is
what a device driver should be written from — not guesswork, which is not a
reasonable way to build software that commands a ten-watt laser.

---

## Architecture

```
OpenBurn.App          Avalonia UI — canvas, panels, dialogs
OpenBurn.Core         Document model, geometry, machine profiles, job contracts, storage
OpenBurn.GCode        GRBL protocol, G-code interpreter, streamer, estimator, validator
OpenBurn.Cam          Raster and vector CAM, importers, bitmap tracing
OpenBurn.Devices      ILaserDevice, the GRBL driver, discovery, protocol probe
OpenBurn.Transport    Serial, TCP, WebSocket, HTTP, virtual
OpenBurn.Camera       Frame sources — MJPEG, snapshot, file
OpenBurn.Vision       Lens correction, homography, bed rectification, detection
OpenBurn.Materials    Material library
OpenBurn.VirtualLaser An in-process GRBL 1.1 controller
OpenBurn.AI           The optional assistant
```

Core depends on nothing. The job engine has no idea whether it is talking to a USB
cable, a socket or a simulator. See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

---

## Testing

```bash
dotnet test                     # 317 tests, no hardware required
OpenBurn --selftest             # headless end-to-end check of a built application
```

`--selftest` is worth knowing about: it loads the device profiles from beside the
executable, imports the sample SVG, generates a job, streams it to the virtual
controller and checks every line was acknowledged, then exits with a status code.
It catches the class of problem the unit tests cannot — a build assembled wrongly
rather than code written wrongly.

317 tests, no hardware required. The ones that matter most run complete jobs
through the real streamer against the virtual controller and assert the receive
buffer is never overrun, because if character-counting streaming is wrong then
every job on every machine is wrong.

The UI tests are not screenshot comparisons: they drive the real canvas control
with synthetic pointer events through Avalonia's headless platform, so hit
testing, pointer capture and the drag state machine are covered rather than only
the maths underneath them.

---

## Safety

Software is not a safety system.

- Never leave a running laser unattended.
- Wear eye protection rated for your laser's wavelength.
- Have a fire extinguisher within reach, and extraction or ventilation running.
- Camera monitoring is an aid, not a substitute for watching the machine.
- Never cut PVC, vinyl, chrome-tanned leather or polycarbonate. They release
  chlorine or cyanide compounds that will damage your lungs and your machine.
  OpenBurn will warn you; it cannot stop you.

---

## Licence

GPLv3. OpenBurn incorporates algorithms derived from LaserGRBL, which is
GPL-licensed.

Made by [FintonLabs](https://fintonlabs.com).
