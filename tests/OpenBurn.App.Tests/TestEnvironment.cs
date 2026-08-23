using System.Runtime.CompilerServices;
using OpenBurn.Core.Storage;

namespace OpenBurn.App.Tests;

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
        Path.GetTempPath(), "openburn-tests", Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    public static void Initialise()
    {
    }
}
