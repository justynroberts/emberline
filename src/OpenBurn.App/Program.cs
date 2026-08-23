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
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
