using Avalonia.Headless.XUnit;
using OpenBurn.App.ViewModels;
using OpenBurn.Core.Documents;
using OpenBurn.Core.Geometry;
using OpenBurn.Core.Storage;
using Xunit;

namespace OpenBurn.App.Tests;

/// <summary>
/// Describing the material on the bed, and lining artwork up against it.
/// </summary>
public class WorkpieceShellTests
{
    private static MainViewModel CreateShell()
    {
        AppPaths.OverrideRoot(Path.Combine(Path.GetTempPath(), "openburn-tests", Guid.NewGuid().ToString("N")));
        AppPaths.EnsureCreated();
        return new MainViewModel(AppSettings.Default);
    }

    private static PathShape Square(double x, double y, double size) => new([new Polyline(
    [
        new Vec2(x, y), new Vec2(x + size, y), new Vec2(x + size, y + size), new Vec2(x, y + size),
    ], closed: true)]);

    [AvaloniaFact]
    public void NothingIsAssumedUntilAWorkpieceIsChosen()
    {
        var shell = CreateShell();
        Assert.False(shell.HasWorkpiece);
        Assert.Equal("No workpiece", shell.WorkpieceSummary);
    }

    [AvaloniaFact]
    public void PickingAPresetPutsItInTheMiddleOfTheBed()
    {
        var shell = CreateShell();

        shell.SelectedWorkpiecePreset = shell.WorkpiecePresets.First(p => p.Name.StartsWith("100 mm square"));

        Assert.True(shell.HasWorkpiece);
        Assert.Equal(100, shell.WorkpieceWidthMm, 3);
        Assert.Equal(100, shell.WorkpieceHeightMm, 3);

        var bed = shell.SelectedMachine;
        Assert.Equal((bed.BedWidthMm - 100) / 2, shell.WorkpieceXMm, 3);
        Assert.Equal((bed.BedHeightMm - 100) / 2, shell.WorkpieceYMm, 3);
    }

    [AvaloniaFact]
    public void ASizeCanBeTypedWithoutPickingAPreset()
    {
        var shell = CreateShell();

        shell.UseCustomWorkpieceCommand.Execute(null);
        shell.WorkpieceWidthMm = 63;
        shell.WorkpieceHeightMm = 41;

        Assert.True(shell.HasWorkpiece);
        Assert.Equal("63 × 41 mm", shell.WorkpieceSummary);
        Assert.Null(shell.SelectedWorkpiecePreset);
    }

    [AvaloniaFact]
    public void ARoundBlankStaysRoundWhenOnlyOneSideIsTyped()
    {
        // A circle described by two different numbers is an ellipse nobody asked for.
        var shell = CreateShell();
        shell.UseCustomWorkpieceCommand.Execute(null);
        shell.WorkpieceIsRound = true;

        shell.WorkpieceWidthMm = 80;

        Assert.Equal(80, shell.WorkpieceHeightMm, 3);
        Assert.Equal("80 mm circle", shell.WorkpieceSummary);
    }

    [AvaloniaFact]
    public void CentringArtworkPutsItInTheMiddleOfTheMaterialNotTheBed()
    {
        var shell = CreateShell();
        shell.SelectedWorkpiecePreset = shell.WorkpiecePresets.First(p => p.Name.StartsWith("100 mm square"));
        shell.WorkpieceXMm = 20;
        shell.WorkpieceYMm = 30;

        var art = Square(200, 200, 10);
        shell.Design.AddShape(art, shell.Design.Layers[0]);

        shell.CentreArtworkOnWorkpieceCommand.Execute(null);

        var centre = art.Bounds.Center;
        Assert.Equal(70, centre.X, 1);    // 20 + 100/2
        Assert.Equal(80, centre.Y, 1);    // 30 + 100/2
    }

    [AvaloniaFact]
    public void CentringUsesTheSelectionWhenThereIsOne()
    {
        var shell = CreateShell();
        shell.SelectedWorkpiecePreset = shell.WorkpiecePresets.First(p => p.Name.StartsWith("100 mm square"));
        shell.WorkpieceXMm = 0;
        shell.WorkpieceYMm = 0;

        var moved = Square(200, 200, 10);
        var untouched = Square(300, 300, 10);
        shell.Design.AddShape(moved, shell.Design.Layers[0]);
        shell.Design.AddShape(untouched, shell.Design.Layers[0]);
        shell.SetSelection([moved], additive: false);

        shell.CentreArtworkOnWorkpieceCommand.Execute(null);

        Assert.Equal(50, moved.Bounds.Center.X, 1);
        Assert.Equal(300, untouched.Bounds.MinX, 1);
    }

    [AvaloniaFact]
    public void ClearingGoesBackToJustTheBed()
    {
        var shell = CreateShell();
        shell.SelectedWorkpiecePreset = shell.WorkpiecePresets.First(p => p.Name.StartsWith("150 mm"));
        Assert.True(shell.HasWorkpiece);

        shell.ClearWorkpieceCommand.Execute(null);

        Assert.False(shell.HasWorkpiece);
        Assert.False(shell.Design.Workpiece.IsSet);
    }

    [AvaloniaFact]
    public void AJobHangingOffTheMaterialIsFlaggedButNotBlocked()
    {
        var shell = CreateShell();
        shell.SelectedWorkpiecePreset = shell.WorkpiecePresets.First(p => p.Name.StartsWith("100 mm square"));
        shell.WorkpieceXMm = 100;
        shell.WorkpieceYMm = 100;

        // Artwork well outside the blank, but comfortably on the bed.
        shell.Design.AddShape(Square(240, 240, 40), shell.Design.Layers[0]);
        shell.RegenerateNow();

        Assert.Contains(shell.Issues, i => i.Title.Contains("workpiece", StringComparison.OrdinalIgnoreCase));

        // Overhanging a blank is sometimes deliberate, so it must not stop the job.
        Assert.DoesNotContain(shell.Issues,
            i => i.Severity == GCode.ValidationSeverity.Error && i.Title.Contains("workpiece", StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public void ArtworkOnTheMaterialRaisesNothing()
    {
        var shell = CreateShell();
        shell.SelectedWorkpiecePreset = shell.WorkpiecePresets.First(p => p.Name.StartsWith("100 mm square"));

        shell.Design.AddShape(Square(shell.WorkpieceXMm + 20, shell.WorkpieceYMm + 20, 40), shell.Design.Layers[0]);
        shell.RegenerateNow();

        Assert.DoesNotContain(shell.Issues, i => i.Title.Contains("workpiece", StringComparison.OrdinalIgnoreCase));
    }
}
