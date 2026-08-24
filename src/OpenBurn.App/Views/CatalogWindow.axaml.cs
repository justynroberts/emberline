using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using OpenBurn.App.ViewModels;

namespace OpenBurn.App.Views;

/// <summary>Search a public icon catalogue and bring artwork onto the bed.</summary>
public partial class CatalogWindow : Window
{
    /// <summary>The XAML previewer needs a parameterless constructor.</summary>
    public CatalogWindow() : this(new CatalogViewModel())
    {
    }

    public CatalogWindow(CatalogViewModel model)
    {
        InitializeComponent();
        DataContext = model;
    }

    /// <summary>The SVG to import, and how, once the operator has chosen.</summary>
    public string? ImportedSvg { get; private set; }

    public string? ImportedName { get; private set; }

    public CatalogImportMode Mode { get; private set; }

    private CatalogViewModel? Model => DataContext as CatalogViewModel;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Model is null) return;
        Model.SearchCommand.Execute(null);
        e.Handled = true;
    }

    private void OnImportEtch(object? sender, RoutedEventArgs e) => Import(CatalogImportMode.Etch);

    private void OnImportCut(object? sender, RoutedEventArgs e) => Import(CatalogImportMode.Cut);

    private async void Import(CatalogImportMode mode)
    {
        if (Model?.Selected is not { } chosen) return;

        try
        {
            ImportedSvg = await Model.FetchChosenAsync();
            ImportedName = chosen.Icon.Name;
            Mode = mode;
        }
        catch (Exception ex)
        {
            Model.Status = $"Could not fetch that artwork: {ex.Message}";
            return;
        }

        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }
}
