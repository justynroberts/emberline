# Device profiles

One JSON file per machine. Dropping a file in this folder is all it takes to add a
machine — no code, no rebuild. That is the "hardware agnostic" principle from the
PRD made concrete.

Emberline loads these at startup and merges them with any profiles the user has
created in `~/.emberline/machines/`.

## Fields

| Field | Meaning |
|---|---|
| `id` | Stable identifier. Changing it orphans saved camera calibrations and job history. |
| `laserWatts` | Optical output. Drives the material-library lookup, so be honest — many vendors quote input power. |
| `bedWidthMm`, `bedHeightMm` | Usable travel, not the frame size. |
| `origin` | `FrontLeft`, `FrontRight`, `RearLeft`, `RearRight` or `Center`. |
| `maxSpindleValue` | The S value meaning 100 % power. **Must match the controller's `$30`** — if it does not, every burn is at the wrong power. |
| `accelerationX/Y` | mm/sec². Should match `$120`/`$121`. Only affects time estimates. |
| `capabilities` | Comma-separated: `Homing`, `SoftLimits`, `AirAssist`, `Rotary`, `ZAxis`, `Camera`, `Framing`, `LaserMode`. |
| `driverId` | `grbl` for anything standard; a vendor key when the machine needs special handling. |

## Adding your machine

Copy `generic-grbl.json`, change `id`, `displayName`, the bed size and the
wattage, and restart. If it needs behaviour the generic GRBL driver does not have,
run **Machine → Protocol probe** first: it records exactly what the controller
answers to a set of read-only queries and writes a Markdown transcript. That
transcript is what a device driver should be written from — not guesswork.
