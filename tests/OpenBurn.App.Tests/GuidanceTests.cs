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
public class GuidanceTests : ShellTest
{
    private MainViewModel CreateShell()
    {
        return NewShell();
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

/// <summary>
/// The crosshair on the canvas has to agree with the coordinate readout above it.
/// They are fed from different properties, so they can silently drift apart.
/// </summary>
public class HeadPositionTests : ShellTest
{
    private MainViewModel CreateShell()
    {
        return NewShell();
    }

    [AvaloniaFact]
    public async Task MovingTheHeadTellsTheCanvasAndNotJustTheReadout()
    {
        var shell = CreateShell();
        await shell.ConnectAsync(Core.Machines.ConnectionKind.Virtual);

        var raised = new List<string>();
        shell.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        await shell.MoveHeadToAsync(60, 45);

        // Wait for the machine to report where it ended up.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !raised.Contains(nameof(shell.WorkPositionText)))
        {
            await Task.Delay(50);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        Assert.Contains(nameof(shell.WorkPositionText), raised);

        // The canvas binds DisplayHeadPosition. If only the readout property is
        // raised, the numbers move and the crosshair does not.
        Assert.Contains(nameof(shell.DisplayHeadPosition), raised);
    }

    [AvaloniaFact]
    public async Task TheCrosshairShowsWhereTheMachineActuallyIs()
    {
        var shell = CreateShell();
        await shell.ConnectAsync(Core.Machines.ConnectionKind.Virtual);

        await shell.MoveHeadToAsync(70, 30);

        // Wait for the move to finish, not merely to start.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && shell.DisplayHeadPosition is null or { X: < 69 })
        {
            await Task.Delay(50);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        Assert.NotNull(shell.DisplayHeadPosition);
        Assert.Equal(shell.HeadPosition, shell.DisplayHeadPosition);
        Assert.InRange(shell.DisplayHeadPosition!.Value.X, 60, 80);
    }

    [AvaloniaFact]
    public void WithNothingConnectedThereIsNoCrosshairToDraw()
    {
        var shell = CreateShell();
        Assert.Null(shell.DisplayHeadPosition);
    }
}
