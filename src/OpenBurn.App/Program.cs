using Avalonia;

namespace OpenBurn.App;

internal static class Program
{
    /// <summary>
    /// Entry point. Kept free of anything that touches Avalonia types before
    /// <see cref="BuildAvaloniaApp"/> runs — the visual previewer calls this method
    /// directly and will not have initialised the framework yet.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // A headless end-to-end check of the packaged application. Deliberately
        // before any Avalonia call, so it runs on a machine with no display.
        if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            return SelfTest.RunAsync().GetAwaiter().GetResult();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
