using Avalonia;

namespace Emberline.App;

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

        // Anything that gets this far has already escaped every handler in the
        // application. Write it down before the process goes: a laser controller
        // that vanishes leaving nothing behind is not diagnosable, and "it crashed"
        // is not a bug report anybody can act on.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"),
                           "AppDomain.UnhandledException");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
