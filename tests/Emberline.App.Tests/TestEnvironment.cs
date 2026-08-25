using System.Runtime.CompilerServices;
using Emberline.Core.Storage;
using Xunit;

namespace Emberline.App.Tests;

/// <summary>
/// One scratch application-data root for the whole test assembly.
///
/// Tests used to call <c>AppPaths.OverrideRoot</c> each with a fresh directory,
/// which reads as careful isolation and is the opposite. The root is a static,
/// and a view model kicks off loads that read it *later* — MaterialLibrary.LoadAsync
/// is deliberately not awaited — so one test would reassign the root while a
/// previous test's load was still using it. The result was an occasional failure
/// in whichever unrelated test happened to be running, which passed when run
/// alone: the most expensive kind of test to trust.
///
/// Setting it once, before any test runs, removes the race. Nothing here depends
/// on starting from an empty directory; the things that do build their own.
/// </summary>
public static class TestEnvironment
{
    public static string Root { get; } = Path.Combine(
        Path.GetTempPath(), "emberline-tests", Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    public static void Initialise()
    {
        AppPaths.OverrideRoot(Root);
        AppPaths.EnsureCreated();
    }
}

/// <summary>
/// The tests must never write to the real application data folder.
///
/// They did, for most of this project's life: the machine editor tests duplicate
/// profiles, and with no isolation every run left another copy in the user's
/// Emberline folder. Hundreds accumulated before anyone looked at the machine
/// dropdown and asked why it was full of copies of the same laser.
///
/// The isolation is one line in a module initialiser, which is exactly the kind
/// of thing that can be deleted by accident and never noticed — it did not throw,
/// it just quietly wrote somewhere else. So it is asserted rather than assumed.
/// </summary>
public class TestIsolationTests
{
    [Fact]
    public void ApplicationDataIsRedirectedIntoATemporaryFolder()
    {
        Assert.StartsWith(Path.GetTempPath(), AppPaths.Root, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingWritesToTheRealApplicationDataFolder()
    {
        var real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Emberline");

        Assert.NotEqual(real, AppPaths.Root);
        Assert.DoesNotContain("Application Support/Emberline", AppPaths.Root, StringComparison.Ordinal);
    }
}
