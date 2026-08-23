using Avalonia;
using Avalonia.Headless;
using OpenBurn.App;

[assembly: AvaloniaTestApplication(typeof(OpenBurn.App.Tests.TestAppBuilder))]

// Headless Avalonia tests all share one dispatcher and one UI thread, so running
// classes in parallel lets an await in one test be starved by another's work. It
// surfaces as an occasional failure in whichever test happened to be waiting on a
// connection — the worst kind, since it looks real and passes when run alone.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace OpenBurn.App.Tests;

/// <summary>
/// Boots the real application into Avalonia's headless platform.
///
/// The whole point is that these tests drive the *actual* control with synthetic
/// pointer input rather than calling its methods directly — so a regression in
/// hit testing, capture or the drag state machine is caught, and not just a
/// regression in the maths those things call.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            // No text rendering needed; skipping it keeps the tests fast and
            // removes any dependency on which fonts a CI runner happens to have.
            UseHeadlessDrawing = true,
        });
}
