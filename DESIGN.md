# OpenBurn — design record

Written before the CSS, per the house-style rules. A later session should read
this before changing the look, and pick differently again for the *next* project.

## Archetype: **Soft product**

Recent siblings in `~/work/` used Editorial (`dontforget`, `newsfin`), Blueprint
(`finvector`), Console/telemetry (`portfolio`, `tessera`), Kiosk (`3d-servicemap`,
`24keypad`) and Brutalist (`finscreen`).

Every G-code sender in existence — LaserGRBL, LightBurn, Candle, UGS — is
console/telemetry: grey panels, hairlines, dense rows, the look of a 2009 machine
utility. Choosing Soft product is both the archetype not used recently here *and*
the thing that stops OpenBurn looking like everything it is replacing. The user is
making something, not administering a server, and the interface should agree.

## Axis picks

| Axis | Pick |
|---|---|
| **Layout** | Full-bleed canvas with **floating** chrome — a 60 px icon rail pinned left, a 328 px inspector floating right, a status bar floating top, drawers that rise from the bottom. Nothing is docked; every panel hovers over the work |
| **Type scale** | Moderate — 1.25 ratio on a 13 px base. Pro-tool density, with one genuinely large size reserved for the coordinate readout, which is the number people actually look at |
| **Surface** | **Translucent panels** with a soft ambient shadow, over the canvas. The workspace is visible through the chrome, which is the whole point of a workspace application |
| **Radius** | Generous, mixed by role — 16 px panels, 10 px controls, 8 px inputs, pill for chips |
| **Accent** | **Duotone with semantic assignment, never decoration.** Ember `#D9531E`/`#FF7A3D` means laser, power, cut, danger. Cyan `#0E8FA3`/`#3FD0E3` means motion, travel, connection, safe. If something is ember it is about the beam. The neutral ground is warm-shifted graphite so ember reads as heat rather than as an error |
| **Motion** | **Focus-in** — panels arrive from `scale(.97) opacity(0)` to sharp over 260 ms on `CubicEaseOut`, staggered 45 ms. It reads as a lens pulling focus, which suits an optical instrument. Reused for panel mount, drawers, dialogs and cards |
| **Ground** | Plain sunken graphite behind a functional bed grid — 10 mm minor, 50 mm major, with the minor grid dropping out below 1.4 px/mm so it never becomes noise |

## Semantic colour

Machine state is the only other place colour is allowed, and it is always a token:

| State | Token |
|---|---|
| Idle | `StateIdle` — slate |
| Run | `StateRun` — cyan |
| Hold / Door | `StateHold` — amber |
| Alarm | `StateAlarm` — red |
| Home / Jog | `StateJog` — violet |
| Disconnected | `StateOff` — muted grey |

The toolpath preview uses a duotone ramp from cyan at zero power to ember at full,
in eight buckets. Power distribution therefore reads at a glance with no legend,
which is worth more than any numeric display.

## Type

```
DisplayFont: Bricolage Grotesque (bundled variable TTF)
MonoFont:    Spline Sans Mono (bundled variable TTF)
```

Both are embedded as `AvaloniaResource`, not fetched, so the application renders
correctly on a workshop machine with no network — which is where laser cutters
tend to live.

Mono carries the console, the G-code view, coordinate readouts and every numeric
input, so figures stay column-aligned.

## Theming

Three states, as required: explicit light, explicit dark, and following the
operating system. `RequestedThemeVariant` on the application, with a complete
palette defined in **both** `ThemeDictionaries` — a colour that exists in only one
variant is a bug waiting for the first person who toggles.

Cycled from the ◐ button in the tool rail: system → light → dark → system.

## Motion discipline

Transitional, never ambient. Entry animations on panels and cards, 120 ms
hover and press feedback on every interactive element, and nothing that loops on a
control the operator uses. A forever-pulsing button on a machine that can start a
fire reads as a rendering fault, not as information — state is carried by colour,
icon and text.

## The FintonLabs info button

Bottom of the tool rail: a 32 px circular ghost button labelled `i`, with
`AutomationProperties.Name="About this app"`, opening a dialog with the version,
the machine in use, the licence, a safety note, and **Made by FintonLabs** linking
to fintonlabs.com. Closes on Escape and on its close button.

## Checked before calling it done

- [x] Light, dark and system all read correctly, with no flash on load
- [x] Bricolage renders — verified in a screenshot of the running application, not just the resource reference
- [x] Motion on entry, hover and press; nothing loops on a persistent control
- [x] Info button present, keyboard reachable, opens and closes, credit link works
- [x] Verified by running the built application and capturing both themes
