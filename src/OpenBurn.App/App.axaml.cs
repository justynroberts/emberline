using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using OpenBurn.App.ViewModels;
using OpenBurn.App.Views;
using OpenBurn.Core.Storage;

namespace OpenBurn.App;

public partial class App : Application
{
    public static MainViewModel? Shell { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        AppPaths.EnsureCreated();
        RegisterBundledFonts();
        var settings = AppSettings.Load();
        ApplyTheme(settings.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = new MainViewModel(settings);
            Shell = shell;

            desktop.MainWindow = new MainWindow { DataContext = shell };
            desktop.ShutdownRequested += (_, _) => shell.PersistOnExit();

            // Files named on the command line open at startup, which is what a
            // desktop application is expected to do when you drop a file on its icon.
            foreach (var arg in desktop.Args ?? [])
            {
                if (arg.StartsWith('-') || !File.Exists(arg)) continue;
                shell.ImportFile(Path.GetFullPath(arg));
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Hand the bundled typefaces to the text engine.
    ///
    /// They are embedded as application resources so the interface renders
    /// correctly on a workshop machine with no network, but that also means the
    /// system font manager cannot see them — without this, asking the text tool
    /// for Bricolage Grotesque silently engraves Helvetica instead.
    /// </summary>
    private static void RegisterBundledFonts()
    {
        try
        {
            OpenBurn.Cam.Text.TextOutliner.RegisterBundledFonts();
        }
        catch (Exception)
        {
            // A missing bundled font is a cosmetic loss, not a reason to refuse to
            // start; the text tool falls back to the system fonts.
        }
    }

    /// <summary>
    /// Three theme states, as the house style requires: explicit light, explicit
    /// dark, and following the operating system.
    /// </summary>
    public static void ApplyTheme(ThemeMode mode)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
