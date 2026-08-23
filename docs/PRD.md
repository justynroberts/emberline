# OpenBurn — Product Requirements Document

**Product** OpenBurn · **Tagline** Open-source laser cutting and engraving
**Version** 0.1 · **Platforms** Windows, macOS, Linux
**Licence** GPLv3 while incorporating LaserGRBL-derived code

> This is the specification as supplied. Implementation notes recording what is
> built, what is deferred and why are collected at the end.

---

## 1. Product vision

OpenBurn is a modern, open-source desktop application for designing, positioning,
engraving and cutting using GRBL-compatible laser machines.

The objective is an accessible alternative to proprietary laser software, improving
the experience around Wi-Fi connected lasers, camera-assisted positioning, material
presets, device discovery, modern design workflows, extensible device support and
cross-platform operation.

The first reference machine is the **BlazeX M5 Pro 10 W**, supporting USB initially
and native Wi-Fi once its network protocol has been documented. OpenBurn should
ultimately support a broad ecosystem of GRBL-compatible lasers.

## 2. Principles

- **Open** — the complete application is open source; device protocols documented
  wherever legally and technically possible.
- **Cross-platform** — Windows and macOS are first-class; Linux supported.
- **Hardware agnostic** — no assumption of a specific manufacturer or machine.
- **Local first** — no cloud account required; designs, material libraries and
  machine profiles work entirely offline.
- **Safe** — camera monitoring and software controls are never substitutes for
  physical laser safety systems.
- **Extensible** — new machines, cameras, transports and import formats are
  addable without modifying the core.

## 3. Target users

**Hobbyist** — owns a diode laser, wants to engrave SVGs, photographs and text.
**Maker** — several materials, wants repeatable material profiles.
**Small business** — batches of coasters, signs, keyrings, labels, gifts, jewellery.
**Advanced** — G-code visibility, machine console, device configuration, custom
commands, multiple machines, camera calibration, network connectivity.

## 4. Technology

C# 14 · .NET 10 · Avalonia 12 · Skia-backed canvas · computer vision · SQLite ·
JSON configuration.

## 5. Why C#

OpenBurn should not transcode LaserGRBL wholesale. LaserGRBL already contains
mature implementations of GRBL communication, G-code handling, raster engraving,
dithering, vectorisation, machine state, jogging and job execution. The strategy is
to identify the reusable algorithms, port them to modern C#, and refactor them
behind interfaces into `OpenBurn.Core`. The old LaserGRBL UI should not become
OpenBurn's UI.

## 6–7. Architecture and repository structure

```
OpenBurn.UI      Canvas · Layers · Camera · Jobs · Devices · Materials · Console · Settings
OpenBurn.Core    Document model · Geometry · CAM · G-code · Job engine · Material profiles
Transport        Serial · TCP · WebSocket            Camera   USB · RTSP · MJPEG · IP
                                    │
                                  GRBL → Laser machine
```

```
src/OpenBurn.{App,Core,Cam,GCode,Devices,Transport,Camera,Vision,Materials}
tests/OpenBurn.{Core,Cam,GCode,Transport}.Tests
devices/  materials/  docs/
```

## 8–9. Workspace and canvas

A visual representation of the laser bed. Pan, zoom, select, move, resize, rotate,
duplicate, group, ungroup, align, distribute, mirror, lock, hide. Millimetres and
inches, millimetres default.

## 10. Import

MVP: SVG, PNG, JPG, JPEG, BMP, G-code. Later: DXF, PDF, AI, EPS. Imported artwork
retains its real-world dimensions wherever the source format contains them.

## 11. Layers

Artwork is assigned to operation layers carrying operation (Engrave, Cut, Fill,
Score), speed, power, passes, line interval, air assist, enabled and output order.
Users can reorder operations.

## 12. Material library

A hierarchy of category and material — slate, wood, metal, acrylic — where a
profile holds material, thickness, laser power, operation, speed, power, passes,
air assist and notes. Profiles can be built in, user-created, imported or exported.

## 13. Machine profiles

Manufacturer, model, laser power, bed size, origin, firmware, connection types,
maximum speed, maximum power, capabilities.

## 14. Device abstraction

`ILaserDevice` — Connect, Disconnect, Home, Frame, StartJob, PauseJob, ResumeJob,
StopJob, Jog, GetStatus. `ITransport` — Serial, Tcp, WebSocket, Http. This allows
the same machine over USB or Wi-Fi without changing the job engine.

## 15. Device discovery

USB port enumeration with GRBL probing. Network discovery by mDNS, UDP, known
manufacturer mechanisms, and IP scanning **only when explicitly requested**.

## 16. BlazeX support

Phase 1: USB → serial → GRBL. Phase 2: Wi-Fi → BlazeX network protocol → job
transport. The network protocol must be isolated in a BlazeX device module rather
than leaking manufacturer-specific behaviour into Core.

## 17–24. Camera system

A first-class subsystem. Sources: USB/UVC, IP (RTSP, MJPEG, HTTP snapshot), and
manufacturer cameras via plugins.

Calibration must compensate for lens distortion, camera angle, perspective, camera
position and bed dimensions, and is stored per machine-and-camera pair.

The bed overlay workflow is capture → lens correction → perspective correction →
crop to bed → scale to machine coordinates → canvas background, producing a
near top-down view even from an angled camera.

Camera-assisted placement: place material, capture bed, material appears on canvas,
import artwork, position visually, frame, start.

Fiducial alignment detects markers and derives translation, rotation, scale and
perspective. Object detection identifies individual workpieces so a design can be
applied to each. Job monitoring keeps the camera live during a run.

Any computer-vision safety feature is an additional monitoring aid and must never
replace operator supervision, physical interlocks, emergency stop or fire safety
equipment.

## 25. Framing

Trace the bounding box of the intended operation at safe or non-burning power.
Modes: rectangle, hull, exact outline.

## 26. Job engine

States: Queued, Preparing, Running, Paused, Completed, Cancelled, Failed. Supports
progress, estimated time, pause, resume, emergency cancellation, device disconnect
detection and error recovery.

## 27. Job library

Completed jobs stored locally with file, machine, material, settings and duration,
so a previous job can be reopened and reproduced.

## 28. Machine console

Direct GRBL access for advanced users. Commands logged. Dangerous configuration
commands require confirmation.

## 29–30. Rotary and multiple machines

Machine profiles may declare rotary capability with roller or chuck type, diameter
and steps per rotation. Users can configure multiple lasers; switching updates bed
dimensions, material profiles, camera calibration, connection and capabilities.

## 31. Plugin architecture

Long-term extensibility through Device, Transport, Camera, Importer, Exporter and
Material plugins.

## 32–33. G-code pipeline and simulation

Artwork → geometry → operations → CAM → toolpaths → G-code → validation → device.
The generated G-code is always inspectable before execution. Simulation previews
the toolpath and identifies jobs outside machine bounds, invalid coordinates,
unsupported commands, excessive speeds and missing homing state.

## 34. Application data

`openburn/` containing machines, materials, cameras, jobs, plugins, settings.json
and openburn.db. No cloud dependency.

## 35. MVP staging

**0.1** — Windows and macOS, SVG and image import, basic canvas, move/scale/rotate,
layers, engrave, cut, raster engraving, G-code generation and preview, USB GRBL
connection, BlazeX profile, jog, home, frame, start, pause, stop, material presets,
basic job history.

**0.2** — Camera: USB webcam, live preview, calibration, lens and perspective
correction, capture bed, canvas overlay, camera-assisted placement.

**0.3** — BlazeX Wi-Fi: discovery, connection, status, job transfer, framing and
execution over Wi-Fi.

**0.4** — Production: multiple machines and cameras, rotary, batch jobs, object
duplication, fiducial registration.

## 36–37. Development and test strategy

Do not begin by porting LaserGRBL wholesale. Extract and understand the GRBL
protocol, G-code parser, job streaming, raster conversion and vectorisation; build
clean modern APIs around them; add compatibility tests against known LaserGRBL
inputs and outputs; and only then take responsibility for real hardware.

Hardware software needs considerably more testing than a normal desktop
application, so OpenBurn includes a **virtual laser** simulating GRBL 1.1 — Idle,
Run, Hold, Alarm, position, `ok`, `error` — so CI can test complete jobs with no
laser attached.

## 38. Success criteria

A new user can install OpenBurn, connect a GRBL laser, open an SVG or photograph,
select a material, position it visually, preview the toolpath, frame the job, start
engraving, monitor progress and reproduce the job later. With a camera installed
they can additionally place a physical object anywhere in the working area and
position artwork over it visually.

## 39. Longer-term vision

An open laser platform rather than another G-code sender: design and CAM above,
cameras, vision and materials beside it, a device layer below spanning USB, Wi-Fi
and network, driving multiple lasers.

**North star** — install OpenBurn, turn on the laser, OpenBurn finds it. Place an
object on the bed. The camera shows it. Drop artwork onto the object. Choose the
material. Press Frame → Start. Everything underneath disappears for the normal user.

---

## Implementation notes

Recorded against the specification above.

### Delivered

MVP 0.1 in full, plus most of 0.2 and the generic half of 0.3.

- Transports: serial, TCP, WebSocket, HTTP command, and an in-process simulator.
- Discovery: USB enumeration; opt-in subnet probe that confirms GRBL before
  reporting a device.
- Import: SVG with nested transforms and real-world sizing, five raster formats,
  G-code with a preview that never rewrites the original.
- CAM: ten dithering kernels, run-merged raster with overscan and white-skipping,
  vector cut/score/engrave, hatch/cross-hatch/offset fills, marching-squares tracing.
- Job engine: character-counting streaming, all seven states, resume-from-line
  after a dropped link, acceleration-aware estimates, pre-flight validation.
- Camera: MJPEG, snapshot, file and synthetic sources; four-corner calibration with
  a reported residual; lens correction; bed overlay; workpiece detection.
- Editing: drag, scale and rotate on the canvas with pixel-space handles, marquee
  selection with the enclosed-versus-touched convention, snapping, align,
  distribute, group, array, and undo across all of it.
- Text to cuttable outlines, with counters preserved and bundled fonts registered
  so the engraved letterforms match the ones on screen.
- Toolpath simulation sharing the estimator's segment timing, and a G-code view.
- Materials, machine profiles as JSON, job library in SQLite, machine console.
- Controller settings editor with confirmation on the dangerous values, job
  library window, rotary support for rollers and chucks.
- Fiducial re-registration: mark where a workpiece was, take it off, put it back,
  and the artwork follows it.
- Job monitoring: the camera stays live beside the progress bar while a job runs.
- Bitmap tracing with a live preview: outlines or centrelines, Otsu-seeded
  threshold, simplify and smooth, all judged against the image they came from.
- 382 tests, none requiring hardware, including headless UI tests that drive the
  real canvas with synthetic pointer input.

### Deferred, with reasons

**USB/UVC capture.** Every managed cross-platform option is a wrapper over a native
library whose Apple Silicon support is unreliable. `ICameraSource` is in place and
IP cameras work everywhere; UVC needs a per-platform implementation behind that
interface, which is a self-contained piece of work rather than an architectural one.

**BlazeX-specific Wi-Fi protocol.** Not documented, and guessing at an undocumented
protocol that commands a ten-watt laser is not a defensible thing to ship. What is
shipped instead is the generic network path — TCP 23 and WebSocket 81, which is
what the ESP32-class controller in these machines almost certainly exposes — plus
`ProtocolProbe`, which records exactly what the machine answers to a set of
read-only queries and writes a Markdown transcript. The driver should be written
from that transcript against real hardware.

**DXF import** is now implemented — lines, arcs, lightweight and old-style
polylines including bulge arcs, circles, ellipses, splines from fit points, and
block references with scale and rotation. **PDF, AI and EPS** remain post-MVP per
§10; all three are PostScript-family formats needing an interpreter rather than a
parser.

**Rotary** is now implemented for both roller and chuck attachments. The axis is
rescaled from the controller's own steps-per-millimetre rather than a guess, and
the panel says where that number came from — a rotary job at the wrong scale looks
correct on screen and is only discovered on the workpiece. **Batch jobs** are
covered by the array tool and by camera-driven placement onto detected workpieces.
**Plugin loading** is now implemented — device drivers, transports, cameras,
importers and materials, each in its own assembly load context so two plugins can
depend on different versions of the same library. It is off by default and says
plainly that there is no sandbox.

**Flame and smoke detection.** §24 requires it be described as an additional aid;
shipping an unvalidated fire detector invites exactly the misplaced trust that
section warns against. The blob-detection groundwork it would build on is present
and tested.

### Added beyond the specification

An optional Claude-powered assistant, at the standing request that accompanied this
work. It can read machine state, controller settings, the job and the console log,
change layer settings and prepare a test grid. It cannot move the machine: there is
no code path from the assistant to the gantry, and a proposed action becomes a card
the operator must click. Everything works with it switched off and offline.
