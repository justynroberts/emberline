using OpenBurn.App.ViewModels;
using OpenBurn.Core.Storage;

namespace OpenBurn.App.Tests;

/// <summary>
/// Base for tests that build a real <see cref="MainViewModel"/>.
///
/// A shell owns dispatcher timers. Left undisposed they keep ticking after the
/// test that made them has finished — and after the headless session has been
/// torn down, which throws from a thread that no longer owns the dispatcher. The
/// failure is then reported as a cleanup error against whichever unrelated test
/// happened to finish last, so it looks like a different flaky test each run.
/// </summary>
public abstract class ShellTest : IDisposable
{
    private readonly List<MainViewModel> _shells = [];

    protected MainViewModel NewShell(AppSettings? settings = null)
    {
        var shell = new MainViewModel(settings ?? AppSettings.Default);
        _shells.Add(shell);
        return shell;
    }

    public void Dispose()
    {
        foreach (var shell in _shells) shell.Dispose();
        _shells.Clear();
        GC.SuppressFinalize(this);
    }
}
