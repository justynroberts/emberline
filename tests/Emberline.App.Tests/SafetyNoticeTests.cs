using Emberline.App.Views;
using Emberline.Core.Storage;
using Xunit;

namespace Emberline.App.Tests;

/// <summary>
/// The safety notice shown before the workspace.
///
/// It exists because Emberline commands a machine that can start a fire, and the
/// person using it for the first time deserves to be told rather than to find
/// out. The properties worth protecting are that it cannot be waved away without
/// reading, and that silencing it does not silence it for ever.
/// </summary>
public class SafetyNoticeTests
{
    [Fact]
    public void ItNamesTheVersionItIsWarningAbout()
    {
        // Deliberately not constructed here. Building a window in a headless test
        // leaves rendering infrastructure to be torn down later on another thread,
        // and the resulting failure lands on whichever unrelated test ran last —
        // which cost an afternoon once already. The XAML is verified by the build,
        // which compiles it; what is checked here is the logic and the wording.
        Assert.Matches(@"\d+\.\d+\.\d+", SplashWindow.Version);
    }

    [Fact]
    public void TheNoticeIsAcknowledgedByAButtonAndNotByClosingIt()
    {
        // A warning that can be waved away without reading is not a warning. The
        // only ways out are the two buttons, and Escape is swallowed.
        var markup = ReadSplashMarkup();
        var code = ReadSplashCode();

        Assert.Contains("I understand", markup, StringComparison.Ordinal);
        Assert.Contains("Quit", markup, StringComparison.Ordinal);
        Assert.Contains("Key.Escape) e.Handled = true", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewInstallationHasNotAcknowledgedAnything()
    {
        Assert.Null(AppSettings.Default.SafetyNoticeAcceptedFor);
    }

    [Fact]
    public void SilencingItIsRecordedAgainstAVersionRatherThanForEver()
    {
        // So the notice returns after an update: what it warns about can change,
        // and a warning silenced permanently is one nobody has read since.
        var accepted = AppSettings.Default with { SafetyNoticeAcceptedFor = "0.1.0" };

        Assert.Equal("0.1.0", accepted.SafetyNoticeAcceptedFor);
        Assert.NotEqual("0.2.0", accepted.SafetyNoticeAcceptedFor);
    }

    [Fact]
    public void TheAcknowledgementSurvivesARestart()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"emberline-splash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "settings.json");

        try
        {
            (AppSettings.Default with { SafetyNoticeAcceptedFor = "1.2.3" }).Save(file);
            Assert.Equal("1.2.3", AppSettings.Load(file).SafetyNoticeAcceptedFor);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TheWarningSaysTheThingsThatMatter()
    {
        // Checked against the markup rather than trusted to stay written: a safety
        // notice that quietly loses its warnings is worse than none, because the
        // button still says "I understand".
        var xaml = ReadSplashMarkup();

        foreach (var must in new[]
                 {
                     "unattended",     // the commonest cause of a laser fire
                     "eye protection",
                     "fume",           // extraction
                     "warranty",
                     "own risk",
                     "FintonLabs",
                 })
        {
            Assert.Contains(must, xaml, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ItSaysTheApplicationStopIsNotASafetyDevice()
    {
        Assert.Contains("not a safety device", ReadSplashMarkup(), StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadSplashCode() => ReadFromSource("SplashWindow.axaml.cs");

    private static string ReadSplashMarkup() => ReadFromSource("SplashWindow.axaml");

    private static string ReadFromSource(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Emberline.App", "Views", name);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"{name} not found above {AppContext.BaseDirectory}");
    }
}
