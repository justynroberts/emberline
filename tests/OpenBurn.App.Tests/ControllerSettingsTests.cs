using Avalonia.Headless.XUnit;
using OpenBurn.App.ViewModels;
using OpenBurn.Core.Machines;
using OpenBurn.Devices;
using OpenBurn.GCode.Grbl;
using OpenBurn.Transport;
using OpenBurn.VirtualLaser;
using Xunit;

namespace OpenBurn.App.Tests;

/// <summary>
/// The controller settings editor, against a real device.
///
/// Two behaviours matter more than the rest: nothing is written until Apply, and
/// the settings that can drive a machine into itself go through a confirmation
/// first. Both are tested against the virtual controller rather than a mock, so
/// the write actually has to land.
/// </summary>
public class ControllerSettingsTests
{
    private static async Task<(GrblDevice Device, ControllerSettingsViewModel Model)> ConnectAsync()
    {
        var device = new GrblDevice(MachineProfile.Virtual()) { StatusPollHz = 20 };
        await device.ConnectAsync(new VirtualTransport(realTimeScale: 200));

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (device.Settings.Count < 20 && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);

        return (device, new ControllerSettingsViewModel(device, _ => { }));
    }

    [AvaloniaFact]
    public async Task SettingsAreGroupedAndDescribed()
    {
        var (device, model) = await ConnectAsync();
        await using var _ = device;

        Assert.NotEmpty(model.Groups);
        Assert.Contains(model.Groups, g => g.Name == GrblSettings.GroupSpindle);
        Assert.Contains(model.Groups, g => g.Name.StartsWith("Axis:", StringComparison.Ordinal));

        var maxSpindle = model.Groups.SelectMany(g => g.Settings).Single(s => s.Key == 30);
        Assert.Equal("Maximum spindle speed", maxSpindle.Name);
        Assert.Contains("scales", maxSpindle.Description, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task NothingIsWrittenUntilApply()
    {
        // A table that writes as you type will send "$100=8" on the way to "$100=80".
        var (device, model) = await ConnectAsync();
        await using var _ = device;

        var setting = model.Groups.SelectMany(g => g.Settings).Single(s => s.Key == 110);
        var original = device.Settings[110];

        setting.EditedText = "9000";

        Assert.True(setting.IsDirty);
        Assert.True(model.HasChanges);
        Assert.Equal(original, device.Settings[110]);
    }

    [AvaloniaFact]
    public async Task ApplyWritesTheChangedValues()
    {
        var (device, model) = await ConnectAsync();
        await using var _ = device;

        var setting = model.Groups.SelectMany(g => g.Settings).Single(s => s.Key == 110);
        setting.EditedText = "9000";

        await model.ApplyAllCommand.ExecuteAsync(null);

        Assert.Equal(9000, device.Settings[110], 3);
        Assert.False(model.HasChanges);

        var reread = await device.ReadSettingsAsync();
        Assert.Equal(9000, reread[110], 3);
    }

    [AvaloniaFact]
    public async Task DangerousSettingsNeedConfirmingAndCanBeRefused()
    {
        var (device, model) = await ConnectAsync();
        await using var _ = device;

        var homingDirection = model.Groups.SelectMany(g => g.Settings).Single(s => s.Key == 23);
        Assert.True(homingDirection.IsDangerous);
        Assert.NotNull(homingDirection.DangerNote);

        var original = device.Settings[23];
        var asked = 0;

        model.ConfirmDangerous = _ => { asked++; return Task.FromResult(false); };
        homingDirection.EditedText = "0";

        await model.ApplyAllCommand.ExecuteAsync(null);

        Assert.Equal(1, asked);
        Assert.Equal(original, device.Settings[23], 3);
        Assert.True(model.HasChanges);
    }

    [AvaloniaFact]
    public async Task ConfirmingADangerousChangeWritesIt()
    {
        var (device, model) = await ConnectAsync();
        await using var _ = device;

        model.ConfirmDangerous = _ => Task.FromResult(true);

        var homingDirection = model.Groups.SelectMany(g => g.Settings).Single(s => s.Key == 23);
        homingDirection.EditedText = "0";

        await model.ApplyAllCommand.ExecuteAsync(null);

        Assert.Equal(0, device.Settings[23], 3);
    }

    [AvaloniaFact]
    public async Task HarmlessSettingsDoNotAskForConfirmation()
    {
        var (device, model) = await ConnectAsync();
        await using var _ = device;

        var asked = 0;
        model.ConfirmDangerous = _ => { asked++; return Task.FromResult(true); };

        // Arc tolerance cannot hurt anybody.
        var arcTolerance = model.Groups.SelectMany(g => g.Settings).Single(s => s.Key == 12);
        Assert.False(arcTolerance.IsDangerous);
        arcTolerance.EditedText = "0.005";

        await model.ApplyAllCommand.ExecuteAsync(null);

        Assert.Equal(0, asked);
        Assert.Equal(0.005, device.Settings[12], 5);
    }

    [AvaloniaFact]
    public async Task NonNumericInputIsRejectedWithoutWriting()
    {
        var (device, model) = await ConnectAsync();
        await using var _ = device;

        var setting = model.Groups.SelectMany(g => g.Settings).Single(s => s.Key == 110);
        var original = device.Settings[110];
        setting.EditedText = "fast";

        Assert.False(setting.IsValid);
        await model.ApplyAllCommand.ExecuteAsync(null);

        Assert.Equal(original, device.Settings[110], 3);
        Assert.Contains("not numbers", model.Status, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task RevertRestoresWhatTheMachineHas()
    {
        var (device, model) = await ConnectAsync();
        await using var _ = device;

        var setting = model.Groups.SelectMany(g => g.Settings).Single(s => s.Key == 110);
        setting.EditedText = "1234";
        Assert.True(model.HasChanges);

        model.RevertAllCommand.Execute(null);

        Assert.False(setting.IsDirty);
        Assert.Equal(setting.MachineText, setting.EditedText);
    }

    [AvaloniaFact]
    public async Task FilteringNarrowsTheList()
    {
        var (device, model) = await ConnectAsync();
        await using var _ = device;

        var before = model.Groups.Sum(g => g.Settings.Count);
        model.Filter = "homing";
        var after = model.Groups.Sum(g => g.Settings.Count);

        Assert.True(after < before);
        Assert.True(after > 0);
        Assert.All(model.Groups.SelectMany(g => g.Settings), s =>
            Assert.True(
                s.Name.Contains("homing", StringComparison.OrdinalIgnoreCase) ||
                s.Description.Contains("homing", StringComparison.OrdinalIgnoreCase),
                $"{s.Label} {s.Name} does not match the filter"));
    }

    [AvaloniaFact]
    public async Task AuditWarningsSurfaceInTheEditor()
    {
        var (device, model) = await ConnectAsync();
        await using var _ = device;

        // The simulator reports $10=3 and laser mode on, so a clean machine has few
        // warnings. Turn laser mode off and the audit should notice.
        model.ConfirmDangerous = _ => Task.FromResult(true);

        var laserMode = model.Groups.SelectMany(g => g.Settings).Single(s => s.Key == 32);
        laserMode.EditedText = "0";
        await model.ApplyAllCommand.ExecuteAsync(null);

        Assert.Contains(model.Warnings, w => w.Text.Contains("Laser mode", StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public void WithNoMachineTheEditorExplainsItselfRatherThanShowingNothing()
    {
        var model = new ControllerSettingsViewModel(null, _ => { });

        Assert.Empty(model.Groups);
        Assert.Contains("Not connected", model.Status, StringComparison.OrdinalIgnoreCase);
    }
}
