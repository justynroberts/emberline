using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace OpenBurn.App.Views;

/// <summary>
/// The safety notice, shown before the workspace.
///
/// It exists because OpenBurn commands a machine that can start a fire, and
/// somebody using it for the first time deserves to be told that plainly rather
/// than discovering it. It is acknowledged with a button rather than dismissed
/// with a close box, and the only other way out is Quit — a warning that can be
/// waved away without reading is not a warning.
///
/// It can be silenced for a version, and comes back after an update, because what
/// it is warning about can change between them.
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    /// <summary>True when the operator acknowledged rather than quitting.</summary>
    public bool Accepted { get; private set; }

    public bool DoNotShowAgain { get; set; }

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.1.0";

    public string VersionText => $"version {Version}  ·  never yet proven on hardware";

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnAccept(object? sender, RoutedEventArgs e)
    {
        Accepted = true;
        Close();
    }

    private void OnQuit(object? sender, RoutedEventArgs e)
    {
        Accepted = false;
        Close();
    }

    /// <summary>
    /// Closing the window with the title bar is the same as quitting, deliberately.
    /// Reaching the workspace requires pressing the button that says understood.
    /// </summary>
    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        // Escape does not dismiss it either.
        if (e.Key == Avalonia.Input.Key.Escape) e.Handled = true;
        base.OnKeyDown(e);
    }
}
