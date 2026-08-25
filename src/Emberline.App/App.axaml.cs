using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Emberline.App.ViewModels;
using Emberline.App.Views;
using Emberline.Core.Storage;

namespace Emberline.App;

public partial class App : Application
{
    public static MainViewModel? Shell { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Before anything reads settings: bring across whatever the application
        // stored when it was called OpenBurn.
        AppPaths.MigrateLegacyData();
        AppPaths.EnsureCreated();
        RegisterBundledFonts();
        var settings = AppSettings.Load();
        ApplyTheme(settings.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The safety notice comes before the workspace. Shown as the main window
            // first, because a modal over a window that does not exist yet has
            // nothing to be modal to, and Avalonia needs a main window to run a
            // message loop at all.
            if (ShouldShowSafetyNotice(settings))
            {
                var splash = new SplashWindow();
                desktop.MainWindow = splash;

                splash.Closed += (_, _) =>
                {
                    if (!splash.Accepted)
                    {
                        desktop.Shutdown();
                        return;
                    }

                    if (splash.DoNotShowAgain)
                    {
                        settings = settings with { SafetyNoticeAcceptedFor = SplashWindow.Version };
                        settings.Save(AppPaths.SettingsFile);
                    }

                    OpenWorkspace(desktop, settings);
                };

                base.OnFrameworkInitializationCompleted();
                return;
            }

            OpenWorkspace(desktop, settings);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static bool ShouldShowSafetyNotice(AppSettings settings) =>
        !string.Equals(settings.SafetyNoticeAcceptedFor, SplashWindow.Version, StringComparison.Ordinal);

    private static void OpenWorkspace(IClassicDesktopStyleApplicationLifetime desktop, AppSettings settings)
    {
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

            desktop.MainWindow.Show();
        }
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
            Emberline.Cam.Text.TextOutliner.RegisterBundledFonts();
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
