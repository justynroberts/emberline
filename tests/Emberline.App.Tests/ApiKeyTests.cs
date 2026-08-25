using Avalonia.Headless.XUnit;
using Emberline.AI;
using Emberline.Core.Storage;
using Xunit;

namespace Emberline.App.Tests;

/// <summary>
/// Setting the assistant's API key from the interface.
///
/// The key is the one piece of genuinely sensitive data Emberline holds, so what
/// matters is not only that it saves but where it lands and what happens to it
/// afterwards.
/// </summary>
public class ApiKeyTests : ShellTest
{
    private const string Key = "sk-ant-api03-not-a-real-key-0123456789";

    private static void ClearKeyFile()
    {
        if (File.Exists(AiOptions.KeyFilePath)) File.Delete(AiOptions.KeyFilePath);
    }

    [AvaloniaFact]
    public void SavingAKeyWritesItToItsOwnFileAndNotIntoSettings()
    {
        ClearKeyFile();
        var shell = NewShell();

        shell.Assistant.ApiKeyInput = Key;
        shell.Assistant.SaveKeyCommand.Execute(null);

        Assert.True(File.Exists(AiOptions.KeyFilePath));
        Assert.Equal(Key, File.ReadAllText(AiOptions.KeyFilePath).Trim());

        // settings.json is what people paste into forum posts. The key must not be in it.
        var settings = Path.Combine(AppPaths.Root, "settings.json");
        if (File.Exists(settings)) Assert.DoesNotContain("sk-ant", File.ReadAllText(settings), StringComparison.Ordinal);

        ClearKeyFile();
    }

    [AvaloniaFact]
    public void TheKeyIsNotLeftSittingInTheBoundProperty()
    {
        ClearKeyFile();
        var shell = NewShell();

        shell.Assistant.ApiKeyInput = Key;
        shell.Assistant.SaveKeyCommand.Execute(null);

        Assert.Equal(string.Empty, shell.Assistant.ApiKeyInput);
        ClearKeyFile();
    }

    [AvaloniaFact]
    public void TheKeyFileIsReadableOnlyByItsOwner()
    {
        if (OperatingSystem.IsWindows()) return;

        ClearKeyFile();
        AiOptions.SaveApiKey(Key);

        var mode = File.GetUnixFileMode(AiOptions.KeyFilePath);

        Assert.True(mode.HasFlag(UnixFileMode.UserRead));
        Assert.False(mode.HasFlag(UnixFileMode.GroupRead));
        Assert.False(mode.HasFlag(UnixFileMode.OtherRead));

        ClearKeyFile();
    }

    [AvaloniaFact]
    public void SomethingTooShortToBeAKeyIsRefused()
    {
        ClearKeyFile();
        var shell = NewShell();

        shell.Assistant.ApiKeyInput = "nope";

        Assert.False(shell.Assistant.CanSaveKey);
        shell.Assistant.SaveKeyCommand.Execute(null);

        Assert.False(File.Exists(AiOptions.KeyFilePath));
        Assert.NotNull(shell.Assistant.StatusMessage);
    }

    [AvaloniaFact]
    public void ForgettingTheKeyDeletesTheFileRatherThanBlankingIt()
    {
        ClearKeyFile();
        AiOptions.SaveApiKey(Key);
        var shell = NewShell();

        shell.Assistant.ForgetKeyCommand.Execute(null);

        Assert.False(File.Exists(AiOptions.KeyFilePath));
    }

    [AvaloniaFact]
    public void TheEntryBoxAppearsWhenThereIsNoKeyAndHidesOnceThereIs()
    {
        if (AiOptions.KeyComesFromEnvironment) return;   // the environment wins; nothing to show

        ClearKeyFile();
        var shell = NewShell();
        Assert.True(shell.Assistant.ShowKeyEntry);

        shell.Assistant.ApiKeyInput = Key;
        shell.Assistant.SaveKeyCommand.Execute(null);

        Assert.False(shell.Assistant.ShowKeyEntry);
        Assert.True(shell.Assistant.HasStoredKey);

        // And it comes back when asked to change it.
        shell.Assistant.ChangeKeyCommand.Execute(null);
        Assert.True(shell.Assistant.ShowKeyEntry);

        ClearKeyFile();
    }

    [AvaloniaFact]
    public void AStoredKeyIsShownMaskedRatherThanInFull()
    {
        ClearKeyFile();
        AiOptions.SaveApiKey(Key);
        var shell = NewShell();

        Assert.DoesNotContain(Key, shell.Assistant.KeyStatus, StringComparison.Ordinal);
        Assert.Contains("…", shell.Assistant.KeyStatus, StringComparison.Ordinal);

        ClearKeyFile();
    }

    [Fact]
    public void MaskingKeepsEnoughToRecogniseAKeyAndNotEnoughToUseIt()
    {
        var masked = AiOptions.Mask(Key);

        Assert.StartsWith("sk-ant-", masked, StringComparison.Ordinal);
        Assert.EndsWith(Key[^4..], masked, StringComparison.Ordinal);
        Assert.True(masked.Length < 20);
        Assert.Equal("", AiOptions.Mask(null));
    }
}
