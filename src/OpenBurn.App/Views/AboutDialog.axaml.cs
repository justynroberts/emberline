using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using OpenBurn.Core.Machines;

namespace OpenBurn.App.Views;

/// <summary>
/// The info panel every FintonLabs project carries. Reachable from the tool rail
/// on every screen, closes on Escape, on the backdrop, and on its close button.
/// </summary>
public partial class AboutDialog : Window
{
    public AboutDialog() : this(null, null)
    {
    }

    public AboutDialog(MachineProfile? machine, IReadOnlyList<OpenBurn.Plugins.LoadedPlugin>? plugins = null)
    {
        InitializeComponent();

        var section = this.FindControl<StackPanel>("PluginSection")!;
        if (plugins is { Count: > 0 })
        {
            this.FindControl<TextBlock>("PluginText")!.Text =
                string.Join("\n", plugins.Select(p => $"{p.Name} {p.Version} — {p.Description}"));
        }
        else
        {
            section.IsVisible = false;
        }

        var version = typeof(AboutDialog).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        this.FindControl<TextBlock>("VersionText")!.Text = $"version {version}  ·  .NET {Environment.Version}";

        this.FindControl<TextBlock>("MachineText")!.Text = machine is null
            ? "No machine selected"
            : machine.Description;

        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape) Close();
        base.OnKeyDown(e);
    }
}
