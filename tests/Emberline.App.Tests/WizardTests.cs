using Avalonia.Headless.XUnit;
using Emberline.App.ViewModels;
using Emberline.App.Views;
using Emberline.Core.Machines;
using Emberline.Core.Storage;
using Xunit;

namespace Emberline.App.Tests;

/// <summary>
/// The guided run through a first job.
///
/// Everything it does goes through the same view model the panels use, so the
/// checks that matter are that it really sets those things up, that it says what
/// is missing rather than silently refusing to advance, and that it stops short of
/// starting the job.
/// </summary>
public class WizardTests : ShellTest
{
    private (MainViewModel Shell, WizardViewModel Wizard) Create()
    {
        var shell = NewShell();
        return (shell, new WizardViewModel(shell));
    }

    [AvaloniaFact]
    public void ItStartsAtTheMachineStep()
    {
        var (_, wizard) = Create();

        Assert.Equal(0, wizard.StepIndex);
        Assert.True(wizard.IsMachineStep);
        Assert.False(wizard.CanGoBack);
        Assert.Equal("Step 1 of 5", wizard.StepNumber);
    }

    [AvaloniaFact]
    public void ExactlyOneStepIsShowingAtATime()
    {
        var (_, wizard) = Create();

        for (var i = 0; i < wizard.Steps.Count; i++)
        {
            wizard.StepIndex = i;
            var showing = new[]
            {
                wizard.IsMachineStep, wizard.IsMaterialStep, wizard.IsArtworkStep,
                wizard.IsSettingsStep, wizard.IsCheckStep,
            };
            Assert.Single(showing, on => on);
        }
    }

    [AvaloniaFact]
    public void ItSaysWhatIsMissingRatherThanRefusingToMove()
    {
        // A wizard that greys out its own forward button without saying why is
        // the thing everybody hates about wizards.
        var (_, wizard) = Create();

        Assert.True(wizard.HasBlocker);
        Assert.Contains("Not connected", wizard.StepBlocker);

        wizard.NextCommand.Execute(null);
        Assert.Equal(1, wizard.StepIndex);
    }

    [AvaloniaFact]
    public async Task ConnectingClearsTheMachineStepsComplaint()
    {
        var (_, wizard) = Create();

        await wizard.ConnectVirtualCommand.ExecuteAsync(null);

        Assert.True(wizard.IsConnected);
        Assert.False(wizard.HasBlocker);
        Assert.Contains("connected", wizard.ConnectionSummary, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void ChoosingAMachineChangesItInTheDocumentToo()
    {
        // There is no separate wizard state to get out of step with the document.
        var (shell, wizard) = Create();
        var other = wizard.Machines.First(m => m.Id != shell.SelectedMachine.Id);

        wizard.SelectedMachine = other;

        Assert.Same(other, shell.SelectedMachine);
        Assert.Contains(other.DisplayName, wizard.MachineSummary);
    }

    [AvaloniaFact]
    public void TheMaterialStepSetsTheWorkpiece()
    {
        var (shell, wizard) = Create();
        wizard.StepIndex = 1;

        wizard.SelectedWorkpiecePreset = wizard.WorkpiecePresets.First(p => p.Name.StartsWith("100 mm square"));

        Assert.True(wizard.HasWorkpiece);
        Assert.True(shell.Design.Workpiece.IsSet);
        Assert.Equal("100 mm square", wizard.WorkpieceSummary);
    }

    [AvaloniaFact]
    public void TheArtworkStepAddsTextToTheRealDesign()
    {
        var (shell, wizard) = Create();
        wizard.StepIndex = 2;

        Assert.True(wizard.HasBlocker);
        Assert.Contains("Nothing to burn", wizard.StepBlocker);

        wizard.Text = "Hello";
        wizard.AddTextCommand.Execute(null);

        Assert.Single(shell.Design.Shapes);
        Assert.False(wizard.HasBlocker);
        Assert.Equal("One shape on the bed.", wizard.ArtworkSummary);
        Assert.Equal("", wizard.Text);
    }

    [AvaloniaFact]
    public void TheSettingsStepAppliesMaterialSettingsToTheLayers()
    {
        var (shell, wizard) = Create();
        wizard.StepIndex = 3;

        // Put the layers somewhere the profile certainly is not, so applying it
        // has to move them.
        foreach (var layer in shell.Design.Layers)
        {
            layer.SpeedMmMin = 12;
            layer.PowerPercent = 3;
        }

        wizard.SelectedMaterial = wizard.Materials.First();
        wizard.ApplyMaterialCommand.Execute(null);

        Assert.NotNull(wizard.SelectedMaterial);
        Assert.Contains(shell.Design.Layers, l => Math.Abs(l.SpeedMmMin - 12) > 0.001 || Math.Abs(l.PowerPercent - 3) > 0.001);
    }

    [AvaloniaFact]
    public async Task TheCheckStepReportsOnTheRealJob()
    {
        var (shell, wizard) = Create();
        await wizard.ConnectVirtualCommand.ExecuteAsync(null);

        wizard.Text = "Ready";
        wizard.AddTextCommand.Execute(null);

        wizard.StepIndex = 4;
        wizard.Refresh();

        Assert.True(wizard.IsCheckStep);
        Assert.True(wizard.IsLastStep);
        Assert.NotEqual("—", wizard.EstimateText);
        Assert.True(wizard.CanStart, wizard.ReadyText);
        Assert.Contains("Frame", wizard.ReadyText);
    }

    [AvaloniaFact]
    public void TheCheckStepExplainsItselfWhenTheJobCannotRun()
    {
        var (_, wizard) = Create();
        wizard.StepIndex = 4;
        wizard.Refresh();

        Assert.False(wizard.CanStart);
        Assert.NotEmpty(wizard.ReadyText);
    }

    [AvaloniaFact]
    public void TheWizardHasNoWayToStartTheJob()
    {
        // Pressing Start is a decision to take while looking at the bed.
        var commands = typeof(WizardViewModel).GetProperties()
            .Select(p => p.Name)
            .Where(n => n.Contains("Start", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("Run", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("Frame", StringComparison.OrdinalIgnoreCase))
            .Where(n => n.EndsWith("Command", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(commands);
    }

    [AvaloniaFact]
    public void TheWindowOpensOnTheRealViewModel()
    {
        var (_, wizard) = Create();

        // Constructing the window is the point: it parses the XAML, so a broken
        // binding or a mistyped resource fails here rather than at runtime. It
        // must be closed again — a headless window left open tears down its
        // compositor later, on another thread, and the failure is then reported
        // against whichever unrelated test happened to run last.
        var window = new WizardWindow(wizard);
        try
        {
            Assert.Same(wizard, window.DataContext);
        }
        finally
        {
            window.Close();
        }
    }
}
