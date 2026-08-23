using Avalonia.Headless.XUnit;
using OpenBurn.App.ViewModels;
using OpenBurn.Core.Machines;
using OpenBurn.Core.Storage;
using Xunit;

namespace OpenBurn.App.Tests;

/// <summary>
/// Machine profile editing.
///
/// The behaviour worth protecting is that a bundled profile is never overwritten:
/// profiles ship with the application, so saving over one means the next update
/// silently reverts somebody's carefully measured bed size.
/// </summary>
public class MachineEditorTests
{
    private static MachineEditorViewModel Create(out MachineLibrary library)
    {
        AppPaths.OverrideRoot(Path.Combine(Path.GetTempPath(), "openburn-tests", Guid.NewGuid().ToString("N")));
        AppPaths.EnsureCreated();

        library = MachineLibrary.Load();
        var editor = new MachineEditorViewModel(library, library.Profiles.First(), _ => { });
        return editor;
    }

    [AvaloniaFact]
    public void SelectingAProfileFillsTheForm()
    {
        var editor = Create(out var library);
        var blazex = library.Profiles.First(p => p.Id.Contains("blazex", StringComparison.OrdinalIgnoreCase));

        editor.Selected = blazex;

        Assert.Equal(blazex.DisplayName, editor.DisplayName);
        Assert.Equal(blazex.BedWidthMm, editor.BedWidthMm, 6);
        Assert.Equal(blazex.MaxSpindleValue, editor.MaxSpindleValue);
        Assert.Contains(editor.Capabilities, c => c.Flag == MachineCapabilities.Rotary && c.IsSet);
    }

    [AvaloniaFact]
    public void EditingABundledProfileSavesACopyRatherThanOverwritingIt()
    {
        var editor = Create(out var library);
        var original = library.Profiles.First(p => p.Id == "generic-grbl");

        editor.Selected = original;
        Assert.True(editor.IsBundled);

        editor.DisplayName = "My laser";
        editor.BedWidthMm = 123;
        editor.SaveCommand.Execute(null);

        // The bundled one is untouched...
        var bundled = library.Find("generic-grbl");
        Assert.NotNull(bundled);
        Assert.Equal(400, bundled!.BedWidthMm, 3);

        // ...and there is now a separate profile with the change.
        Assert.Contains(library.Profiles, p => Math.Abs(p.BedWidthMm - 123) < 0.001 && p.DisplayName == "My laser");
    }

    [AvaloniaFact]
    public void SavingAUserProfileOverwritesIt()
    {
        var editor = Create(out var library);

        editor.AddNewCommand.Execute(null);
        var id = editor.Selected.Id;
        Assert.False(editor.IsBundled);

        editor.BedWidthMm = 250;
        editor.SaveCommand.Execute(null);

        Assert.Equal(250, library.Find(id)!.BedWidthMm, 3);
        Assert.Equal(id, editor.Selected.Id);
    }

    [AvaloniaFact]
    public void CapabilitiesRoundTripThroughTheForm()
    {
        var editor = Create(out var library);
        editor.AddNewCommand.Execute(null);

        foreach (var capability in editor.Capabilities) capability.IsSet = false;
        editor.Capabilities.First(c => c.Flag == MachineCapabilities.Rotary).IsSet = true;
        editor.Capabilities.First(c => c.Flag == MachineCapabilities.Camera).IsSet = true;

        editor.SaveCommand.Execute(null);

        var saved = library.Find(editor.Selected.Id)!;
        Assert.True(saved.Capabilities.HasFlag(MachineCapabilities.Rotary));
        Assert.True(saved.Capabilities.HasFlag(MachineCapabilities.Camera));
        Assert.False(saved.Capabilities.HasFlag(MachineCapabilities.Homing));
    }

    [AvaloniaFact]
    public void NonsenseValuesAreClampedRatherThanSaved()
    {
        var editor = Create(out _);
        editor.AddNewCommand.Execute(null);

        editor.BedWidthMm = -50;
        editor.LaserWatts = 0;
        editor.MaxSpindleValue = 0;

        var built = editor.Build();

        Assert.True(built.BedWidthMm > 0);
        Assert.True(built.LaserWatts > 0);
        Assert.True(built.MaxSpindleValue > 0);
    }

    [AvaloniaFact]
    public void DuplicatingProducesASeparateProfile()
    {
        var editor = Create(out var library);
        var before = library.Profiles.Count;

        editor.DuplicateCommand.Execute(null);

        Assert.Equal(before + 1, library.Profiles.Count);
        Assert.Contains("copy", editor.Selected.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void BundledProfilesCannotBeDeleted()
    {
        var editor = Create(out var library);
        editor.Selected = library.Profiles.First(p => p.Id == "generic-grbl");

        editor.DeleteCommand.Execute(null);

        Assert.NotNull(library.Find("generic-grbl"));
        Assert.Contains("cannot be deleted", editor.Status, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void UserProfilesCanBeDeleted()
    {
        var editor = Create(out var library);
        editor.AddNewCommand.Execute(null);
        var id = editor.Selected.Id;

        editor.DeleteCommand.Execute(null);

        Assert.Null(library.Find(id));
    }

    [AvaloniaFact]
    public void ASavedProfileSurvivesAReload()
    {
        var editor = Create(out _);
        editor.AddNewCommand.Execute(null);
        editor.DisplayName = "Workshop laser";
        editor.BedWidthMm = 610;
        editor.SaveCommand.Execute(null);

        // A fresh library reads from disk, which is where a profile has to live to
        // be worth anything.
        var reloaded = MachineLibrary.Load();
        Assert.Contains(reloaded.Profiles, p => p.DisplayName == "Workshop laser" && Math.Abs(p.BedWidthMm - 610) < 0.001);
    }
}
