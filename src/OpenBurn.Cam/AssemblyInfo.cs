using System.Runtime.CompilerServices;

// The scan-line run merger is the highest-risk function in the raster path and is
// worth testing directly, but it is not something callers should be reaching for.
[assembly: InternalsVisibleTo("OpenBurn.Cam.Tests")]
