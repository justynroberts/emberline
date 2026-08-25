using Avalonia.Headless.XUnit;
using Emberline.App.ViewModels;
using Emberline.Core.Documents;
using Emberline.Core.Geometry;
using Emberline.Core.Storage;
using Emberline.Core.Machines;
using Xunit;

namespace Emberline.App.Tests;

/// <summary>
/// The right panel follows the work rather than stacking every section in one
/// column. What matters is that it moves when the context genuinely changes, and
/// stays put otherwise — a panel that rearranges itself while you are reaching for
/// a control is worse than one that scrolls.
/// </summary>
public class InspectorTests : ShellTest
{
    private MainViewModel CreateShell()
    {
        return NewShell();
    }

    private static PathShape Square(double x = 0) => new([new Polyline(
    [
        new Vec2(x, 0), new Vec2(x + 10, 0), new Vec2(x + 10, 10), new Vec2(x, 10),
    ], closed: true)]);

    [AvaloniaFact]
    public void ExactlyOneTabIsShowingAtATime()
    {
        var shell = CreateShell();

        foreach (var tab in Enum.GetValues<InspectorTab>())
        {
            shell.InspectorTab = tab;
            var showing = new[] { shell.IsMachineTab, shell.IsDesignTab, shell.IsObjectTab, shell.IsJobTab };
            Assert.Single(showing, on => on);
        }
    }

    [AvaloniaFact]
    public void ItOpensOnDesignRatherThanConnection()
    {
        // Most sessions start with artwork, not with plugging something in.
        Assert.Equal(InspectorTab.Design, CreateShell().InspectorTab);
    }

    [AvaloniaFact]
    public void SelectingSomethingTurnsToItsSettings()
    {
        var shell = CreateShell();
        var square = Square();
        shell.Design.AddShape(square, shell.Design.Layers[0]);

        shell.SetSelection([square], additive: false);

        Assert.Equal(InspectorTab.Object, shell.InspectorTab);
    }

    [AvaloniaFact]
    public void DeselectingGoesBackToTheDesign()
    {
        var shell = CreateShell();
        var square = Square();
        shell.Design.AddShape(square, shell.Design.Layers[0]);

        shell.SetSelection([square], additive: false);
        shell.SetSelection([], additive: false);

        Assert.Equal(InspectorTab.Design, shell.InspectorTab);
    }

    [AvaloniaFact]
    public void ChangingWhichShapeIsSelectedDoesNotMoveThePanel()
    {
        var shell = CreateShell();
        var a = Square();
        var b = Square(40);
        shell.Design.AddShape(a, shell.Design.Layers[0]);
        shell.Design.AddShape(b, shell.Design.Layers[0]);

        shell.SetSelection([a], additive: false);
        shell.InspectorTab = InspectorTab.Machine;   // the operator went to jog

        shell.SetSelection([b], additive: false);

        // Still a selection, so nothing about the context actually changed.
        Assert.Equal(InspectorTab.Machine, shell.InspectorTab);
    }

    [AvaloniaFact]
    public void DeselectingOnlyReturnsToDesignFromTheObjectTab()
    {
        var shell = CreateShell();
        var square = Square();
        shell.Design.AddShape(square, shell.Design.Layers[0]);

        shell.SetSelection([square], additive: false);
        shell.InspectorTab = InspectorTab.Machine;
        shell.SetSelection([], additive: false);

        Assert.Equal(InspectorTab.Machine, shell.InspectorTab);
    }

    [AvaloniaFact]
    public void AJobStartingBringsUpTheJobTab()
    {
        var shell = CreateShell();

        shell.FollowJobContext(running: true);

        Assert.Equal(InspectorTab.Job, shell.InspectorTab);
    }

    [AvaloniaFact]
    public void AJobStartingLeavesTheMachineTabAlone()
    {
        // Watching the machine panel during a job is a deliberate choice — usually
        // to reach the jog controls or the camera. Do not pull it away.
        var shell = CreateShell();
        shell.InspectorTab = InspectorTab.Machine;

        shell.FollowJobContext(running: true);

        Assert.Equal(InspectorTab.Machine, shell.InspectorTab);
    }

    [AvaloniaFact]
    public void TheTabCommandTakesTheNameFromTheButton()
    {
        var shell = CreateShell();

        shell.SelectInspectorTabCommand.Execute("Job");
        Assert.Equal(InspectorTab.Job, shell.InspectorTab);

        shell.SelectInspectorTabCommand.Execute("machine");
        Assert.Equal(InspectorTab.Machine, shell.InspectorTab);

        shell.SelectInspectorTabCommand.Execute("nonsense");
        Assert.Equal(InspectorTab.Machine, shell.InspectorTab);
    }
}

/// <summary>
/// What the machine panel says it will connect to must be what it connects to.
/// </summary>
public class ConnectionAddressTests : ShellTest
{
    private MainViewModel Shell(AppSettings settings)
    {
        return NewShell(settings);
    }

    [AvaloniaFact]
    public void TheRememberedAddressIsShownRatherThanKeptOutOfSight()
    {
        var shell = Shell(AppSettings.Default with { LastNetworkAddress = "192.168.3.52:8080" });

        Assert.Equal("192.168.3.52:8080", shell.NetworkAddress);
    }

    [AvaloniaFact]
    public void WithNothingRememberedTheBoxStartsEmpty()
    {
        Assert.Equal(string.Empty, Shell(AppSettings.Default).NetworkAddress);
    }

    [AvaloniaFact]
    public async Task ConnectingWithAnEmptyBoxRefusesInsteadOfGuessing()
    {
        // Previously this fell back to the remembered address, so a press
        // connected to a machine whose address was nowhere on screen.
        var shell = Shell(AppSettings.Default with { LastNetworkAddress = "192.168.3.52:8080" });
        shell.NetworkAddress = "";

        await shell.ConnectAsync(ConnectionKind.Tcp);

        Assert.False(shell.IsConnected);
        Assert.Contains(shell.Console.Lines, l => l.Text.Contains("address", StringComparison.OrdinalIgnoreCase));
    }
}
