# Third-party notices

Emberline is original work. Nothing here is derived from LaserGRBL, LightBurn or
any other sender — the GRBL protocol it speaks is a published specification, not
somebody's source. What follows is what Emberline *bundles* or depends on, and the
terms that come with each.

## Bundled fonts

Both faces ship inside the application, so their licences ship with them. Both are
under the **SIL Open Font License 1.1**, which permits bundling and redistribution
provided the licence travels with the font and the font is not sold on its own.
The full text is in `fonts/OFL.txt`.

| Font | Copyright |
|---|---|
| Bricolage Grotesque | Copyright The Bricolage Project Authors |
| Spline Sans Mono | Copyright The Spline Sans Mono Project Authors |

## Bundled interface icons

The toolbar and panel icons are **Material Design Icons** by Pictogrammers, under
the **Apache License 2.0**. Their path data is embedded in
`src/Emberline.App/Styles/Icons.axaml` — each geometry is commented with the icon it
came from — so the interface draws identically offline and does not depend on any
service staying up.

<https://github.com/Templarian/MaterialDesign> ·
<https://github.com/Templarian/MaterialDesign/blob/master/LICENSE>

## Artwork imported from catalogues

Emberline can search public icon catalogues through the Iconify API. Nothing is
bundled: artwork is fetched only when you search and import, and each icon carries
the licence of the set it came from — shown in the search window before import.
Those licences are the sets' own, not Emberline's, and honouring them is the
responsibility of whoever burns the result. Sets that declare no licence are
labelled as such rather than assumed permissive.

## Packages

All permissively licensed and restored from NuGet rather than vendored, so their
own notices come with the packages.

| Package | Licence |
|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter | MIT |
| CommunityToolkit.Mvvm | MIT |
| SkiaSharp | MIT |
| Microsoft.Data.Sqlite | MIT |
| System.IO.Ports | MIT |
| Anthropic | MIT |
| xUnit, Avalonia.Headless.XUnit (tests only) | Apache-2.0 / MIT |

## Emberline itself

Copyright Justyn Roberts. Released under the GNU General Public License v3, in
`LICENSE`. GPLv3 is a choice, not an inheritance: no dependency requires it, so it
can be changed by the copyright holder.
