using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Emberline.Core.Jobs;
using Emberline.Core.Storage;

namespace Emberline.App.Views;

/// <summary>
/// What was burned, on what, with which settings, and whether it worked.
///
/// The point of keeping this is the last column of that sentence: a record of the
/// settings that actually succeeded on this machine beats any built-in table,
/// because it was measured on this lens, this material and this focal height.
/// </summary>
public partial class JobLibraryWindow : Window
{
    private readonly JobLibrary? _library;

    public JobLibraryWindow() : this(null)
    {
    }

    public JobLibraryWindow(JobLibrary? library)
    {
        _library = library;
        InitializeComponent();

        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
        this.FindControl<Button>("RefreshButton")!.Click += (_, _) => Reload();

        var search = this.FindControl<TextBox>("SearchBox")!;
        search.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) Reload();
        };

        Reload();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Reload()
    {
        var list = this.FindControl<ItemsControl>("JobList")!;
        var summary = this.FindControl<TextBlock>("SummaryText")!;

        if (_library is null)
        {
            list.ItemsSource = Array.Empty<JobRecord>();
            summary.Text = "No job history is available.";
            return;
        }

        try
        {
            var term = this.FindControl<TextBox>("SearchBox")?.Text?.Trim();
            var records = string.IsNullOrEmpty(term) ? _library.Recent(100) : _library.Search(term, 100);

            list.ItemsSource = records;

            var total = _library.Count();
            summary.Text = records.Count == total
                ? $"{total} job{(total == 1 ? "" : "s")} recorded."
                : $"{records.Count} of {total} jobs shown.";
        }
        catch (Exception ex)
        {
            list.ItemsSource = Array.Empty<JobRecord>();
            summary.Text = $"Could not read the job library: {ex.Message}";
        }
    }

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape) Close();
        base.OnKeyDown(e);
    }
}
