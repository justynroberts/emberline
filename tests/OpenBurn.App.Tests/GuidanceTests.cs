using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using OpenBurn.App.ViewModels;
using OpenBurn.Core.Documents;
using OpenBurn.Core.Geometry;
using OpenBurn.Core.Storage;
using Xunit;

namespace OpenBurn.App.Tests;

/// <summary>
/// What the interface tells somebody who has not used a laser before.
///
/// A disabled button with no explanation is the worst thing in a machine
/// controller for a beginner: nothing on screen says whether the problem is the
/// machine, the artwork or the settings. These check that it says.
/// </summary>
public class GuidanceTests
{
    private static MainViewModel CreateShell()
    {
        AppPaths.OverrideRoot(Path.Combine(Path.GetTempPath(), "openburn-tests", Guid.NewGuid().ToString("N")));
        AppPaths.EnsureCreated();
        return new MainViewModel(AppSettings.Default);
    }

    [AvaloniaFact]
    public void StartSaysToConnectSomethingWhenNothingIsConnected()
    {
        var shell = CreateShell();

        Assert.False(shell.CanStartJob);
        Assert.Contains("Not connected", shell.StartHint);
        // And points at the way to try it with no hardware at all.
        Assert.Contains("Virtual", shell.StartHint);
    }

    [AvaloniaFact]
    public async Task StartSaysThereIsNothingToBurnWhenTheDesignIsEmpty()
    {
        var shell = CreateShell();
        await shell.ConnectAsync(Core.Machines.ConnectionKind.Virtual);

        Assert.True(shell.IsConnected);
        Assert.Empty(shell.Design.Shapes);
        Assert.Contains("nothing to burn", shell.StartHint, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task StartExplainsItselfOnceThereIsAJobToRun()
    {
        var shell = CreateShell();
        await shell.ConnectAsync(Core.Machines.ConnectionKind.Virtual);

        shell.Design.AddShape(new PathShape([new Polyline(
            [new Vec2(50, 50), new Vec2(120, 50), new Vec2(120, 120), new Vec2(50, 120)], closed: true)]),
            shell.Design.Layers[0]);
        shell.RegenerateNow();

        Assert.True(shell.CanStartJob, shell.StartHint);
        Assert.Contains("Frame", shell.StartHint);
    }

    [AvaloniaFact]
    public void TheHintIsAlwaysASentenceSomebodyCanAct1On()
    {
        // Never blank, never a bare state name.
        var shell = CreateShell();
        Assert.True(shell.StartHint.Length > 30);
        Assert.EndsWith(".", shell.StartHint.Trim());
    }

    [AvaloniaFact]
    public void EveryButtonInTheMainWindowExplainsItself()
    {
        // The rule is the point: a control that does something to a machine or a
        // design must say what, on hover. Regressions here are silent otherwise.
        var xaml = ReadView("MainWindow.axaml");

        var missing = ControlsWithoutTooltips(xaml);
        Assert.True(missing.Count == 0, "No tooltip on: " + string.Join(", ", missing));
    }

    [AvaloniaFact]
    public void SoDoesEveryOtherWindow()
    {
        foreach (var view in new[]
                 {
                     "TraceWindow.axaml", "SettingsWindow.axaml", "MachineWindow.axaml",
                     "JobLibraryWindow.axaml", "CalibrationWindow.axaml", "ConfirmDialog.axaml",
                     "AboutDialog.axaml",
                 })
        {
            var missing = ControlsWithoutTooltips(ReadView(view));
            Assert.True(missing.Count == 0, $"{view} — no tooltip on: {string.Join(", ", missing)}");
        }
    }

    private static List<string> ControlsWithoutTooltips(string xaml)
    {
        var missing = new List<string>();
        var pattern = new Regex(@"<(Button|ComboBox|CheckBox|ToggleButton|Slider)(\s|/|>)((?:[^<>]|""[^""]*"")*?)>",
            RegexOptions.Singleline);

        foreach (Match m in pattern.Matches(xaml))
        {
            var separator = m.Groups[2].Value;
            var attributes = m.Groups[3].Value;

            // "<ComboBox.ItemTemplate>" and friends are property elements, not controls.
            if (attributes.StartsWith('.') || separator.Length == 0) continue;
            if (attributes.Contains("ToolTip.Tip")) continue;

            var content = Regex.Match(attributes, @"Content=""([^""]*)""");
            missing.Add($"<{m.Groups[1].Value} {(content.Success ? content.Groups[1].Value : "?")}>");
        }
        return missing;
    }

    /// <summary>Find the view source next to the repository, walking up from the test binary.</summary>
    private static string ReadView(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "OpenBurn.App", "Views", name);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"Could not find {name} above {AppContext.BaseDirectory}");
    }
}
