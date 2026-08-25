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
| **Guided setup** | A wizard that walks through machine, material, artwork, settings and a final check — without ever starting the job for you |
| **Workpiece** | Describe the blank on the bed, see its outline under the artwork, centre onto it, and get warned when a job runs off it |
| **Photo engraving** | Invert, brightness, contrast, gamma, sharpen, tone clipping and twelve dithering kernels, with a live preview of what will actually burn |
| **CAM** | Raster engraving, vector cut/score/engrave, hatch and offset fills, text to outlines |
| **Artwork search** | Search 150+ open icon sets, see each licence before importing, and bring a shape in to etch or to cut |
| **Documents** | Designs save and reopen as a self-contained `.openburn` file — layers, artwork, workpiece and image adjustments, with photographs embedded |
| **Overlaps** | Shapes that overlap are cut as their combined outline rather than each separately, with holes preserved |
| **Tracing** | Bitmap to paths with a live preview — outlines or centrelines, threshold seeded from the image itself, simplify and smooth |
| **Job engine** | Character-counting streaming, pause/resume/stop, resume-from-line after a dropped link, live progress and acceleration-aware time estimates |
| **Editing** | Drag, scale and rotate on the canvas, marquee selection, snapping, align, distribute, group, array, and undo across all of it |
| **Preview** | Toolpath simulation with scrubbing, and a G-code view of the exact lines that will be sent |
| **Safety** | Pre-flight validation, framing at pointer power, emergency stop on Escape, an assistant that cannot move the machine |
| **Camera** | MJPEG, HTTP snapshot and synthetic sources; lens and perspective correction; bed overlay; workpiece detection; fiducial re-registration; live view while a job runs |
| **Materials** | A built-in library keyed by laser wattage, with rescaling and hazard warnings |
| **Configuration** | A `$` settings editor with every value named, explained and unit-labelled, and confirmation on the ones that can drive a machine into itself |
| **Rotary** | Roller and chuck attachments, with the axis rescaled from the real steps-per-millimetre rather than a guess |
| **Assistant** | Optional Claude-powered help with settings, faults and job setup — and it can draw, adding SVG artwork straight to the canvas |
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

## If you have never used a laser

Press **✦** in the tool rail. It walks through the five things a job needs —
which machine, what is on the bed, what you are burning onto it, how hard and how
fast, and a check of the result — setting up the same objects the panels do, so
nothing it does is trapped inside the wizard.

It stops short of starting the job. The last step hands back to the workspace with
the machine ready and the artwork placed, because pressing Start is a decision to
take while looking at the bed, not at a dialog.

Beyond that: hover over anything. Every button, dropdown, slider and tick box says
what it does in plain words — including the ones that are greyed out, which is
exactly when you need to know why. The Start button explains what is stopping it:
not connected, nothing drawn yet, or a specific problem with the job.

Hover over anything. Every button, dropdown, slider and tick box says what it
does in plain words — including the ones that are greyed out, which is exactly
when you need to know why. The Start button explains what is stopping it: not
connected, nothing drawn yet, or a specific problem with the job.

Press **Virtual** in the machine panel to run the whole thing against a built-in
simulator. Jobs stream, progress moves, the preview plays, and nothing burns. It
is the same code path the real machine uses, so anything you learn there is true.

---

## Getting settings right

The built-in material library is a starting point, not an answer. Every machine,
lens, focal height and batch of plywood is different, and settings measured on a
different wattage are a guess with arithmetic applied.

Press **Test grid**. It burns a power-versus-speed matrix on an offcut in a few
minutes and gives you numbers that are true for *your* machine. Everything else in
the material system exists to stop you starting from nothing.

---

## Engraving a photograph

Select an imported bitmap and the **Object** tab shows the picture as it will be
engraved, with the controls that decide it.

**Invert** is the one to know about. Engraving burns the dark parts, so a picture
meant to come out light-on-dark — slate, anodised aluminium, a painted tile —
must be inverted or it engraves as a negative.

The rest are tone controls: brightness and contrast, gamma for the mid-tones,
sharpen, and two clips — *skip lighter than*, which leaves pale areas untouched
and saves time, and *full power darker than*, which deepens the blacks.

Dithering decides how greys become laser pulses. At engraving speed the beam is
close to on-or-off, so this changes the result more than any other single setting,
and which kernel looks best genuinely depends on the material. Each one says what
it suits.

The original picture is never altered. Every adjustment is applied when the job is
generated, so you can push the sliders around all afternoon and still get back to
where you started.

---

## Finding artwork

Press **✧** in the Draw group. It searches open icon catalogues — 150-odd sets,
around 200,000 shapes — and shows the licence against every result, because that
is what decides whether a piece of artwork can go on something you sell. Sets that
declare no licence say so rather than being left blank, which would read as
permission. Tick **No credit required** to hide anything under a CC-BY licence.

Two ways in, because an icon is a filled shape and a laser follows lines:

**Import to etch** puts it on a fill layer, so the whole shape is darkened and it
looks like the icon does on screen.

**Import to cut** puts it on a cut layer, so only the outline is followed and the
shape drops out.

The geometry is identical either way — the difference is the layer, so you can
change your mind afterwards by moving the shape rather than importing it again.

The previews are rendered by OpenBurn's own importer, not by a browser engine, so
what you see is what will land on the bed. This and the assistant are the only two
things in OpenBurn that talk to the internet, and searching sends only the word
you typed.

---

## Tracing an image

Press **◈** in the tool rail. With a bitmap selected it traces that; with nothing
selected it asks for a file, because wanting to trace a photograph is not the same
as wanting it on the bed as a raster first.

Everything re-traces live, because the only way to know whether a threshold is
right is to look at what it produces. The one choice that matters:

**Outlines** follow the edge of every dark region and come back closed. This is
what you want for silhouettes, stencils and cut lines.

**Centrelines** run a single line down the middle of each stroke. This is what you
want for line art, signatures and hand lettering — trace those as outlines and you
get both sides of every pen stroke, so the laser burns each line twice and the
result comes out looking hollow.

The threshold starts from the image's own histogram rather than a fixed 128, which
matters for anything scanned or photographed: warm lighting can leave "black" ink
sitting at 150, where a fixed threshold finds precisely nothing.

Tracing never touches the image. It adds a separate shape, so you can trace the
same source again with different settings and keep whichever you prefer.

---

## The assistant

Optional, and off until you give it a key. Open the assistant drawer and paste an
Anthropic key from `console.anthropic.com` — it takes effect immediately, no
restart. `ANTHROPIC_API_KEY` in your environment still works and takes precedence.

It can also draw. Ask it to design something and it adds SVG artwork to the
canvas as editable paths — centred on the workpiece if you have set one. That is
a document change, not a machine action, so it happens without a confirmation
prompt: nothing burns, and Ctrl+Z removes it. The confirmation gate exists for the
laser, and widening it to cover harmless edits would only train you to dismiss
confirmations without reading them.

The key is written to `anthropic.key` in the application data folder, with
owner-only permissions, and never into `settings.json` — that is the file people
paste into forum posts when asking for help. It is shown back masked, and can be
removed again from the same panel.

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

Or use **Machines → ⋯** in the app: add, duplicate and edit profiles there, with
every field explained. Bundled profiles are never overwritten — editing one saves
your own copy, so an update cannot revert a bed size you measured.

If your machine needs behaviour the generic GRBL driver does not have, run
**Machine → Protocol probe** first. It sends a set of read-only queries and writes
a Markdown transcript of exactly what the controller answered. That transcript is
what a device driver should be written from — not guesswork, which is not a
reasonable way to build software that commands a ten-watt laser.

---

## Plugins

New machines, transports, cameras, file formats and materials can be added without
touching OpenBurn. See [`src/OpenBurn.Plugins/README.md`](src/OpenBurn.Plugins/README.md).

Loading is off by default and has to be turned on deliberately. A plugin is
ordinary code running in the OpenBurn process with your permissions; there is no
sandbox. What OpenBurn does guarantee is that a plugin cannot displace a built-in —
registering `.svg` or the `grbl` driver is refused and reported — and that a plugin
which throws while registering is skipped rather than taking the application down.

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
OpenBurn.Plugins      Plugin contracts, registry and load host
OpenBurn.AI           The optional assistant
```

Core depends on nothing. The job engine has no idea whether it is talking to a USB
cable, a socket or a simulator. See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

---

## Testing

```bash
dotnet test                     # 547 tests, no hardware required
OpenBurn --selftest             # headless end-to-end check of a built application
```

`--selftest` is worth knowing about: it loads the device profiles from beside the
executable, imports the sample SVG, generates a job, streams it to the virtual
controller and checks every line was acknowledged, then exits with a status code.
It catches the class of problem the unit tests cannot — a build assembled wrongly
rather than code written wrongly.

547 tests, no hardware required. The ones that matter most run complete jobs
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
