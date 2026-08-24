using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenBurn.Catalog;
using OpenBurn.Cam.Import;
using SkiaSharp;

namespace OpenBurn.App.ViewModels;

/// <summary>One search result, with its picture and its licence.</summary>
public sealed partial class CatalogResult : ObservableObject
{
    public required CatalogIcon Icon { get; init; }

    [ObservableProperty]
    private Bitmap? _preview;

    public string Title => Icon.Title;

    public string Licence => Icon.Author is { Length: > 0 } author
        ? $"{Icon.LicenceTitle} · {author}"
        : Icon.LicenceTitle;

    public bool NeedsCredit => Icon.RequiresAttribution;
}

/// <summary>
/// Search a public icon catalogue.
///
/// The licence is shown against every result rather than buried in a menu,
/// because it is the thing that decides whether a piece of artwork can be put on
/// something you sell. Sets that do not declare one say so plainly instead of
/// being left blank, which would read as permission.
/// </summary>
public sealed partial class CatalogViewModel : ObservableObject
{
    private readonly IconCatalog _catalogue;
    private CancellationTokenSource? _inFlight;

    public CatalogViewModel(IconCatalog? catalogue = null) => _catalogue = catalogue ?? new IconCatalog();

    public ObservableCollection<CatalogResult> Results { get; } = [];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _freeToUseOnly;
    [ObservableProperty] private double _sizeMm = 50;
    [ObservableProperty] private CatalogResult? _selected;

    /// <summary>Said plainly in the window, because this is the one search that leaves the machine.</summary>
    public string PrivacyNote =>
        $"Searching sends only what you type to {_catalogue.Host}. Nothing about your design or machine leaves this computer.";

    public bool HasResults => Results.Count > 0;

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(Query)) return;

        _inFlight?.Cancel();
        _inFlight = new CancellationTokenSource();
        var token = _inFlight.Token;

        IsSearching = true;
        Status = "Searching…";
        Results.Clear();
        OnPropertyChanged(nameof(HasResults));

        try
        {
            var found = await _catalogue.SearchAsync(Query, 60, token).ConfigureAwait(true);
            if (token.IsCancellationRequested) return;

            var shown = FreeToUseOnly
                ? found.Where(i => !i.RequiresAttribution).ToList()
                : [.. found];

            foreach (var icon in shown) Results.Add(new CatalogResult { Icon = icon });

            Status = shown.Count switch
            {
                0 when found.Count > 0 => $"{found.Count} found, all needing a credit. Untick the filter to see them.",
                0 => "Nothing found. Try a simpler word — catalogues index single nouns better than phrases.",
                _ => $"{shown.Count} result(s).",
            };

            OnPropertyChanged(nameof(HasResults));
            await LoadPreviewsAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // A newer search replaced this one.
        }
        catch (HttpRequestException ex)
        {
            Status = $"Could not reach the catalogue: {ex.Message}. Everything else in OpenBurn works offline.";
        }
        catch (Exception ex)
        {
            Status = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>
    /// Fetch the pictures after the list is on screen, one at a time.
    ///
    /// Sixty requests fired at once would be rude to a free service and would make
    /// the window sit blank until the slowest finished; this way results fill in.
    /// </summary>
    private async Task LoadPreviewsAsync(CancellationToken token)
    {
        foreach (var result in Results.ToList())
        {
            if (token.IsCancellationRequested) return;

            try
            {
                var svg = await _catalogue.FetchSvgAsync(result.Icon, 64, token).ConfigureAwait(true);
                result.Preview = Render(svg);
            }
            catch (Exception)
            {
                // A picture that will not load is not worth interrupting a search for.
            }
        }
    }

    /// <summary>
    /// Draw the preview with OpenBurn's own importer.
    ///
    /// Deliberately not a general SVG renderer. What matters is not how the icon
    /// looks in a browser but what OpenBurn will actually put on the bed, and the
    /// only way to show that honestly is to run it through the same importer. An
    /// icon that comes back empty here would have come in empty too, and it is far
    /// better to see that before importing than after.
    /// </summary>
    private static Bitmap? Render(string svg)
    {
        try
        {
            var imported = SvgImporter.Import(svg);
            if (imported.Paths.Count == 0) return null;

            var bounds = Core.Geometry.Rect2.Empty;
            foreach (var path in imported.Paths) bounds = bounds.Union(path.Bounds);
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return null;

            const int size = 96;
            const float pad = 6;
            var scale = (float)Math.Min((size - pad * 2) / bounds.Width, (size - pad * 2) / bounds.Height);

            using var surface = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(surface);
            canvas.Clear(SKColors.Transparent);

            var offsetX = pad + (float)((size - pad * 2 - bounds.Width * scale) / 2);
            var offsetY = pad + (float)((size - pad * 2 - bounds.Height * scale) / 2);

            using var stroke = new SKPaint
            {
                Color = new SKColor(0xFF, 0x7A, 0x3D),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.3f,
                StrokeJoin = SKStrokeJoin.Round,
            };

            var builder = new SKPathBuilder();
            foreach (var path in imported.Paths)
            {
                if (path.Count < 2) continue;

                // Y flips: the importer works in machine coordinates, the canvas
                // in screen ones.
                float X(double x) => offsetX + (float)((x - bounds.MinX) * scale);
                float Y(double y) => size - offsetY - (float)((y - bounds.MinY) * scale);

                builder.MoveTo(X(path[0].X), Y(path[0].Y));
                for (var i = 1; i < path.Count; i++) builder.LineTo(X(path[i].X), Y(path[i].Y));
                if (path.IsClosed) builder.Close();
            }

            using var drawn = builder.Detach();
            canvas.DrawPath(drawn, stroke);
            canvas.Flush();

            using var image = SKImage.FromBitmap(surface);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            using var stream = new MemoryStream(data.ToArray());
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Fetch the chosen icon at the requested size, ready to import.</summary>
    public Task<string> FetchChosenAsync() =>
        Selected is null
            ? Task.FromResult(string.Empty)
            : _catalogue.FetchSvgAsync(Selected.Icon, SizeMm);
}
