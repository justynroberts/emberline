using OpenBurn.App;
using OpenBurn.Core.Storage;
using Xunit;

namespace OpenBurn.App.Tests;

/// <summary>
/// What happens when something goes wrong badly enough to matter.
///
/// The application used to have no crash logging. An unhandled exception aborted
/// the process and left a system report full of unsymbolicated frames — enough to
/// know it died, not enough to know why. "It crashed" is not a bug report anybody
/// can act on.
/// </summary>
public class CrashLogTests
{
    private static void Clear()
    {
        if (Directory.Exists(CrashLog.Folder))
        {
            foreach (var f in Directory.GetFiles(CrashLog.Folder, "crash-*.log")) File.Delete(f);
        }
    }

    [Fact]
    public void AReportNamesTheExceptionAndWhereItCameFrom()
    {
        Clear();

        var path = CrashLog.Write(new InvalidOperationException("the thing went wrong"), "Opening the widget");

        Assert.NotNull(path);
        var text = File.ReadAllText(path!);

        Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
        Assert.Contains("the thing went wrong", text, StringComparison.Ordinal);
        Assert.Contains("Opening the widget", text, StringComparison.Ordinal);
    }

    [Fact]
    public void InnerExceptionsAreFollowedAllTheWayDown()
    {
        // The useful message is almost always the innermost one.
        Clear();

        var buried = new FileNotFoundException("device profile is missing");
        var wrapped = new InvalidOperationException("could not start", new AggregateException(buried));

        var text = File.ReadAllText(CrashLog.Write(wrapped, "Starting")!);

        Assert.Contains("could not start", text, StringComparison.Ordinal);
        Assert.Contains("device profile is missing", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WritingIsIntoTheApplicationFolderAndNowhereElse()
    {
        Clear();
        var path = CrashLog.Write(new Exception("x"), "somewhere");

        Assert.StartsWith(AppPaths.Root, path!, StringComparison.Ordinal);
    }

    [Fact]
    public void ItNeverThrowsEvenOnRubbish()
    {
        // This runs while the process is already failing. An exception here would
        // replace a diagnosable crash with a mysterious one.
        Clear();

        var exception = Record.Exception(() => CrashLog.Write(new Exception(new string('x', 200_000)), ""));
        Assert.Null(exception);
    }

    [Fact]
    public void TheFolderDoesNotGrowForEver()
    {
        Clear();

        for (var i = 0; i < 26; i++) CrashLog.Write(new Exception($"crash {i}"), "loop");

        var kept = Directory.GetFiles(CrashLog.Folder, "crash-*.log").Length;
        Assert.InRange(kept, 1, 21);

        Clear();
    }
}
