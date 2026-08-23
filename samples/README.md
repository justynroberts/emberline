# Samples

`openburn-badge.svg` deliberately exercises most of the SVG importer in one file:
rounded rectangles, circles, ellipses, polygons, polylines, cubic and smooth-cubic
béziers, an elliptical arc, and nested `translate`/`rotate` groups. If an import
regression appears, this is the file to open first.

Open it from the command line to check a build renders:

```bash
OpenBurn samples/openburn-badge.svg
```
