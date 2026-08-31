using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Emberline.App.ViewModels;
using Emberline.App.Views;
using Emberline.Core.Machines;
using Emberline.Core.Storage;
using Xunit;

namespace Emberline.App.Tests;

/// <summary>
/// Renders the screenshots used by the website.
///
/// They come from the headless Skia backend driving the real MainWindow, so they
/// are the actual application rather than a mockup, and they can be regenerated
/// when the UI changes instead of going stale in a folder. Set
/// EMBERLINE_SCREENSHOTS to an output directory to run it; the suite skips it
/// otherwise, because writing files is not what the other tests are for.
/// </summary>
public class Screenshots
{
    private static string? OutputDirectory =>
        Environment.GetEnvironmentVariable("EMBERLINE_SCREENSHOTS") is { Length: > 0 } dir ? dir : null;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Emberline.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root");
    }

    private static void Settle(int frames = 12)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Shoot(Window window, string dir, string name)
    {
        Settle();
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException($"no frame for {name}");
        frame.Save(Path.Combine(dir, name + ".png"));
    }

    [AvaloniaFact]
    public async Task Capture()
    {
        var dir = OutputDirectory;
        if (dir is null) return;
        Directory.CreateDirectory(dir);

        var badge = Path.Combine(RepoRoot(), "samples", "emberline-badge.svg");

        foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            var suffix = theme == ThemeMode.Light ? "light" : "dark";
            App.ApplyTheme(theme);

            using var shell = new MainViewModel(AppSettings.Default with { Theme = theme });
            var window = new MainWindow { DataContext = shell, Width = 1480, Height = 940 };
            window.Show();
            Settle();

            shell.ImportFile(badge);
            await shell.ConnectAsync(ConnectionKind.Virtual);
            Settle(30);

            shell.InspectorTab = InspectorTab.Design;
            Shoot(window, dir, $"workspace-{suffix}");

            shell.InspectorTab = InspectorTab.Machine;
            Shoot(window, dir, $"machine-{suffix}");

            shell.InspectorTab = InspectorTab.Job;
            Shoot(window, dir, $"job-{suffix}");

            shell.ShowGcode = true;
            shell.InspectorTab = InspectorTab.Design;
            Shoot(window, dir, $"gcode-{suffix}");
            shell.ShowGcode = false;

            var wizard = new WizardWindow(new WizardViewModel(shell)) { Width = 900, Height = 500 };
            wizard.Show();
            Shoot(wizard, dir, $"wizard-{suffix}");
            wizard.Close();

            window.Close();
            Settle();
        }
    }
}
