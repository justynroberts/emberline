using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBurn.Catalog;

/// <summary>One icon, with the licence of the set it came from.</summary>
public sealed record CatalogIcon(
    string Id,
    string Prefix,
    string Name,
    string SetName,
    string LicenceTitle,
    string? LicenceSpdx,
    string? LicenceUrl,
    string? Author)
{
    /// <summary>Display name: "fox" from "Material Design Icons".</summary>
    public string Title => $"{Name.Replace('-', ' ')} · {SetName}";

    /// <summary>
    /// Whether the licence lets the artwork be used without crediting anybody.
    /// CC0 and public-domain sets ask nothing; MIT and Apache technically require
    /// their notice to travel with the software, which for a burned coaster is
    /// not a meaningful obligation but is worth being honest about.
    /// </summary>
    public bool NoAttributionRequired =>
        LicenceSpdx is not null &&
        (LicenceSpdx.Contains("CC0", StringComparison.OrdinalIgnoreCase) ||
         LicenceSpdx.Contains("Unlicense", StringComparison.OrdinalIgnoreCase) ||
         LicenceSpdx.Contains("PDDL", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the licence asks for a credit wherever the work appears.</summary>
    public bool RequiresAttribution =>
        LicenceSpdx is not null && LicenceSpdx.Contains("CC-BY", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Search a public icon catalogue and fetch the artwork as SVG.
///
/// Iconify aggregates a hundred and fifty open icon sets behind one API and — the
/// reason it is used here rather than a larger clipart site — reports the licence
/// of every set in the same response as the search results. That means the licence
/// can be shown next to the picture, before the import, rather than being
/// something the user is expected to go and look up afterwards.
///
/// This is the only part of OpenBurn besides the assistant that talks to the
/// internet, and it does so only when somebody types a search.
/// </summary>
public sealed class IconCatalog
{
    public const string DefaultHost = "https://api.iconify.design";

    private readonly HttpClient _http;
    private readonly string _host;

    public IconCatalog(HttpClient? http = null, string host = DefaultHost)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _host = host.TrimEnd('/');
    }

    /// <summary>The host contacted, so the interface can say where the search goes.</summary>
    public string Host => _host;

    public async Task<IReadOnlyList<CatalogIcon>> SearchAsync(
        string query, int limit = 48, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        limit = Math.Clamp(limit, 1, 200);
        var url = $"{_host}/search?query={Uri.EscapeDataString(query.Trim())}&limit={limit}";

        var response = await _http.GetFromJsonAsync<SearchResponse>(url, cancellationToken).ConfigureAwait(false);
        if (response?.Icons is null) return [];

        var results = new List<CatalogIcon>(response.Icons.Count);

        foreach (var id in response.Icons)
        {
            var split = id.IndexOf(':');
            if (split <= 0) continue;

            var prefix = id[..split];
            var name = id[(split + 1)..];

            IconSet? set = null;
            response.Collections?.TryGetValue(prefix, out set);
            var licence = set?.Licence;

            results.Add(new CatalogIcon(
                Id: id,
                Prefix: prefix,
                Name: name,
                SetName: set?.Name ?? prefix,
                LicenceTitle: licence?.Title ?? "Licence not stated",
                LicenceSpdx: licence?.Spdx,
                LicenceUrl: licence?.Url,
                Author: set?.Author?.Name));
        }

        return results;
    }

    /// <summary>
    /// Fetch one icon as SVG at a real-world size.
    ///
    /// Asked for in millimetres, because that is what everything downstream works
    /// in, and the importer reads the width and height attributes to place it.
    /// </summary>
    public async Task<string> FetchSvgAsync(
        CatalogIcon icon, double sizeMm = 50, CancellationToken cancellationToken = default)
    {
        var url = $"{_host}/{Uri.EscapeDataString(icon.Prefix)}/{Uri.EscapeDataString(icon.Name)}.svg";
        var svg = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);

        return Resize(svg, sizeMm);
    }

    /// <summary>
    /// Restate the size in millimetres, keeping the viewBox.
    ///
    /// Icons come back sized in abstract units — "1em" or a pixel count — which
    /// the importer would read as a shape a few millimetres across. The viewBox is
    /// what carries the geometry, so only the outer dimensions need replacing.
    /// </summary>
    public static string Resize(string svg, double sizeMm)
    {
        if (string.IsNullOrWhiteSpace(svg)) return svg;

        var mm = Math.Clamp(sizeMm, 1, 2000).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var open = svg.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        if (open < 0) return svg;

        var close = svg.IndexOf('>', open);
        if (close < 0) return svg;

        var tag = svg[open..(close + 1)];
        var rebuilt = System.Text.RegularExpressions.Regex.Replace(
            tag,
            "\\s(width|height)\\s*=\\s*\"[^\"]*\"",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        rebuilt = rebuilt.Insert(4, $" width=\"{mm}mm\" height=\"{mm}mm\"");
        return svg[..open] + rebuilt + svg[(close + 1)..];
    }

    // ------------------------------------------------------------ the wire

    private sealed class SearchResponse
    {
        [JsonPropertyName("icons")] public List<string>? Icons { get; set; }
        [JsonPropertyName("collections")] public Dictionary<string, IconSet>? Collections { get; set; }
    }

    private sealed class IconSet
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("license")] public LicenceInfo? Licence { get; set; }
        [JsonPropertyName("author")] public AuthorInfo? Author { get; set; }
    }

    private sealed class LicenceInfo
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("spdx")] public string? Spdx { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }

    private sealed class AuthorInfo
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }
}
