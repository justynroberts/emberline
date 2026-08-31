# Emberline website — design record

The application's own design record is `../DESIGN.md`. This file covers the
marketing site only, which is a different surface with a different job: the app
has to be operable for hours, the site has to be understood in ninety seconds.

## Archetype: **Gallery**

Recent siblings in `~/work/` used Kiosk (`3d-servicemap`, `24keypad`),
Console/telemetry (`aidetect`, `ableton-ai`, `portfolio`, `tessera`), Poster
(`chordic`), Editorial (`dontforget`, `newsfin`), Blueprint (`finvector`) and
Brutalist (`finscreen`). The Emberline application itself is Soft product.

Gallery is unused across that set, and it is also the honest choice here. The
argument for this application is that it does not look like a 2009 machine
utility — and that argument cannot be made in prose. The screenshots *are* the
pitch, so they get the page and the writing gets out of the way.

## Axis picks

| Axis | Pick | Why this and not the sibling default |
|---|---|---|
| **Layout** | **Full-bleed alternating media bands** with the caption hung in an offset column beside each shot, never above it | Every sibling docks something — a rail (`dontforget`, `aidetect`), a split (`ableton-ai`), a card grid. Here the image sets the rhythm and the text is marginalia to it. |
| **Type scale** | **Restrained, 1.2 ratio** on a 17px base | Gallery restraint. `chordic` went dramatic (Poster), the console projects went dense; a middle setting is unused, and the screenshots supply the drama. |
| **Surface** | **No cards anywhere.** Screenshots sit directly on the ground with a 1px hairline and one soft ambient shadow; text blocks are separated by whitespace alone | Distinct from `3d-servicemap`'s glass, `finscreen`'s offset shadows, `aidetect`'s tinted header strips. The only elevated things on the page are the product shots. |
| **Radius** | **Media-only: 12px on screenshots, 0px on everything else** including buttons | A radius language keyed to content type rather than to control role. No sibling does this; `chordic` and `ableton-ai` keyed radius to musical geometry, which is the same idea applied elsewhere. |
| **Accent** | **Duotone, ember `#D9531E`/`#FF7A3D` and cyan `#0E8FA3`/`#3FD0E3`** — carried over from the application unchanged | Brand fidelity outranks novelty on a product site: a page painted in colours the software does not use would be lying about it. Ember means the beam, cyan means motion — the same semantics as in the app, so the screenshots and the page agree. |
| **Motion signature** | **Raster-pass reveal** — sections uncover top-to-bottom behind a `clip-path` inset with a thin ember scan edge riding the boundary, 620ms, staggered 70ms | The machine engraves a bitmap by sweeping the head line by line down the work. The page arrives the same way it would if it were burned onto the screen. Distinct from `aidetect`'s horizontal sweep (vertical, and carries a lit edge) and from the app's own focus-in. |
| **Ground** | **Warm graphite / warm off-white**, with the application's own bed grid — 10mm minor, 50mm major — drawn faintly behind the hero only | Matches the warm-shifted neutral the app uses so ember reads as heat. `24keypad` used a dot grid and `chordic` a mesh wash; a *measured* engineering grid at true proportions is neither, and it is the one texture that means something here. |

## Screenshots

Real, and regenerated rather than curated: `tests/Emberline.App.Tests/Screenshots.cs`
drives the actual `MainWindow` on Avalonia's headless Skia backend and captures
rendered frames, so what is on this page is what the application draws. Run it
with `EMBERLINE_SCREENSHOTS=site/img dotnet test tests/Emberline.App.Tests
--filter Screenshots`.

Both themes are captured, and the page swaps the whole set when the site theme is
toggled — the light site shows the light application. It costs one attribute
swap and it demonstrates the app's dual theming better than a sentence claiming it.

## Theming

Three states as required — explicit light, explicit dark, and following the
system — with `data-theme` on `<html>`, the stored choice applied by a blocking
script in `<head>` so there is no flash, and every token defined on bare `:root`.

## Type

Bricolage Grotesque for display and body, per the house rules, using the variable
`wght` and `wdth` axes for hierarchy rather than a second family. **Spline Sans
Mono** in the mono slot — the same face the application bundles, so the G-code
and coordinate samples on the page are set in the type they appear in on screen.
