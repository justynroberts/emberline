using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Emberline.App.Views;

/// <summary>
/// A confirmation with the consequences spelled out.
///
/// The detail panel is not decoration: this dialog exists for actions where the
/// operator needs to read what will happen, and a dialog that only says "are you
/// sure" trains people to click through it.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog() : this("Confirm", string.Empty, string.Empty)
    {
    }

    public ConfirmDialog(string title, string message, string detail, string confirmText = "Confirm")
    {
        InitializeComponent();

        Title = title;
        this.FindControl<TextBlock>("TitleText")!.Text = title;
        this.FindControl<TextBlock>("MessageText")!.Text = message;
        this.FindControl<TextBlock>("DetailText")!.Text = detail;

        var confirm = this.FindControl<Button>("ConfirmButton")!;
        confirm.Content = confirmText;
        confirm.Click += (_, _) => { Confirmed = true; Close(); };

        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close();
    }

    public bool Confirmed { get; private set; }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape) Close();
        base.OnKeyDown(e);
    }
}
