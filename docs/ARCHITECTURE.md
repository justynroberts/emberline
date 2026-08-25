# Architecture

## The shape of it

```
                              Emberline.App
                       Avalonia UI — canvas, panels
                                   │
        ┌──────────────────────────┼──────────────────────────┐
        │                          │                          │
  Emberline.Cam              Emberline.Devices           Emberline.Vision
  raster · vector           ILaserDevice · GRBL        lens · homography
  importers · trace         discovery · probe          detection
        │                          │                          │
  Emberline.GCode            Emberline.Transport         Emberline.Camera
  protocol · interpreter    serial · tcp · ws          frame sources
  streamer · estimator      http · virtual
        │                          │
        └──────────┬───────────────┘
                   │
             Emberline.Core
     document · geometry · machines
        jobs · units · storage
```

`Emberline.Core` references nothing but the framework. Every arrow points inward.

## Three decisions worth knowing about

### 1. Character-counting streaming

GRBL has a 128-byte serial receive buffer. The obvious approach — send a line,
wait for `ok` — leaves that buffer empty most of the time, the motion planner
starves, and a raster engrave both bands visibly and takes roughly twice as long.

`GcodeStreamer` tracks the byte length of every unacknowledged line and keeps
pushing while the total stays under the buffer size. It has no I/O, no timers and
no threads: it emits the lines that should be written and consumes the
acknowledgements that come back, which is what makes the protocol testable against
the in-process simulator rather than against a machine.

Two failure modes it handles that are easy to miss:

- **Re-entrancy.** A synchronous transport can deliver the `ok` for line N from
  inside the write of line N. Without a pump guard that recurses one stack frame
  per line and overflows on any real job.
- **Ordering.** The send is recorded *before* the bytes go to the transport, so a
  synchronous acknowledgement sees a consistent cursor. Getting this backwards
  produces a job that streams perfectly and never registers as finished.

### 2. Nothing above the transport knows what it is talking to

`ITransport` is a byte pipe. `ILaserDevice` is a machine. The job engine drives a
USB cable, a WebSocket and a simulator through the same code path, which is why
the simulator is a genuine test of the real thing rather than a mock that agrees
with whatever the code does.

### 3. The assistant has no path to the machine

`IAssistantHost` exposes reads, two reversible writes, and `ProposeAction`.
`ProposeAction` puts a card on screen. The lambda that would perform the action
only runs from a button press.

This is deliberately architectural rather than a rule in the system prompt. A
prompt can be argued with; an absent code path cannot.

## Testing without hardware

`Emberline.VirtualLaser` implements GRBL 1.1 in process: the receive buffer, the
planner block queue, acknowledgement timing gated on planner capacity, real-time
bytes, status reports, the settings table, homing, alarms and soft limits. Time is
driven by `Tick()` rather than a wall clock, so a two-hour job runs in a
millisecond and does so identically every time.

`SyntheticCameraSource` does the same job for the camera path: it renders a bed
seen through a wide-angle lens at an angle, with known-correct ground truth, so
calibration and detection are testable in CI.

The suite is 196 tests and needs no laser, no camera and no network.

## Adding things

| To add | Do this |
|---|---|
| A machine | Drop a JSON file in `devices/` |
| A connection type | Implement `ITransport`, register it in `DeviceFactory` |
| Vendor-specific behaviour | Implement `ILaserDevice`, usually wrapping `GrblDevice`; register a `driverId` |
| A camera | Implement `ICameraSource` |
| An import format | Add an importer in `Emberline.Cam/Import` |
| A dithering kernel | Add its taps to `Dither.Kernels` and an entry to the catalogue |

Nothing on that list requires touching `Emberline.Core`.
