using System.Net;
using System.Text;
using OpenBurn.Catalog;
using Xunit;

namespace OpenBurn.Catalog.Tests;

/// <summary>
/// The icon catalogue client.
///
/// Every test answers its own requests. Nothing here touches the network: a test
/// suite that depends on somebody else's server is a test suite that fails on a
/// train, and it would be testing Iconify rather than OpenBurn.
/// </summary>
public class IconCatalogTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string, (HttpStatusCode Status, string Body)> _reply;
        public List<string> Requested { get; } = [];

        public StubHandler(Func<string, (HttpStatusCode, string)> reply) => _reply = reply;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            var (status, body) = _reply(url);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, url.EndsWith(".svg") ? "image/svg+xml" : "application/json"),
            });
        }
    }

    private const string SearchJson = """
        {
          "icons": ["mdi:fox", "ph:tree-bold", "mystery:thing"],
          "collections": {
            "mdi": {
              "name": "Material Design Icons",
              "license": { "title": "Apache 2.0", "spdx": "Apache-2.0", "url": "https://example.invalid/apache" },
              "author": { "name": "Pictogrammers" }
            },
            "ph": {
              "name": "Phosphor",
              "license": { "title": "MIT", "spdx": "MIT" }
            }
          }
        }
        """;

    private static IconCatalog Catalog(out StubHandler handler, string body = SearchJson)
    {
        handler = new StubHandler(_ => (HttpStatusCode.OK, body));
        return new IconCatalog(new HttpClient(handler), "https://catalogue.invalid");
    }

    [Fact]
    public async Task SearchReturnsIconsWithTheirSetAndLicence()
    {
        var catalogue = Catalog(out _);

        var results = await catalogue.SearchAsync("fox");

        Assert.Equal(3, results.Count);

        var fox = results[0];
        Assert.Equal("mdi:fox", fox.Id);
        Assert.Equal("fox", fox.Name);
        Assert.Equal("Material Design Icons", fox.SetName);
        Assert.Equal("Apache 2.0", fox.LicenceTitle);
        Assert.Equal("Pictogrammers", fox.Author);
    }

    [Fact]
    public async Task AnIconFromAnUndeclaredSetSaysSoRatherThanClaimingALicence()
    {
        // The dangerous failure is quietly showing "MIT" for something unknown.
        var catalogue = Catalog(out _);

        var results = await catalogue.SearchAsync("fox");
        var mystery = results.First(i => i.Prefix == "mystery");

        Assert.Equal("Licence not stated", mystery.LicenceTitle);
        Assert.Null(mystery.LicenceSpdx);
        Assert.False(mystery.NoAttributionRequired);
    }

    [Fact]
    public async Task AttributionIsFlaggedOnlyWhenTheLicenceAsksForIt()
    {
        var body = SearchJson.Replace("\"spdx\": \"MIT\"", "\"spdx\": \"CC-BY-4.0\"");
        var catalogue = Catalog(out _, body);

        var results = await catalogue.SearchAsync("tree");

        Assert.True(results.First(i => i.Prefix == "ph").RequiresAttribution);
        Assert.False(results.First(i => i.Prefix == "mdi").RequiresAttribution);
    }

    [Fact]
    public async Task AnEmptyQueryDoesNotCallOutAtAll()
    {
        var catalogue = Catalog(out var handler);

        Assert.Empty(await catalogue.SearchAsync("   "));
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task TheQueryIsEscapedRatherThanPastedIntoTheUrl()
    {
        var catalogue = Catalog(out var handler);

        await catalogue.SearchAsync("tree & leaf");

        // An unescaped ampersand would end the query parameter and silently
        // search for "tree" instead.
        Assert.DoesNotContain("query=tree & leaf", handler.Requested[0], StringComparison.Ordinal);
        Assert.Contains("%26", handler.Requested[0], StringComparison.Ordinal);
    }

    [Fact]
    public void FetchedArtworkIsRestatedInMillimetres()
    {
        // Icons arrive sized in abstract units, which the importer would otherwise
        // read as a shape a couple of millimetres across.
        const string svg = """<svg xmlns="http://www.w3.org/2000/svg" width="1em" height="1em" viewBox="0 0 24 24"><path d="M2 2h20v20H2z"/></svg>""";

        var resized = IconCatalog.Resize(svg, 60);

        Assert.Contains("width=\"60mm\"", resized, StringComparison.Ordinal);
        Assert.Contains("height=\"60mm\"", resized, StringComparison.Ordinal);
        Assert.DoesNotContain("1em", resized, StringComparison.Ordinal);

        // The viewBox carries the geometry and must survive untouched.
        Assert.Contains("viewBox=\"0 0 24 24\"", resized, StringComparison.Ordinal);
        Assert.Contains("<path d=\"M2 2h20v20H2z\"", resized, StringComparison.Ordinal);
    }

    [Fact]
    public void ResizingSomethingThatIsNotSvgLeavesItAlone()
    {
        Assert.Equal("not svg", IconCatalog.Resize("not svg", 50));
        Assert.Equal("", IconCatalog.Resize("", 50));
    }

    [Fact]
    public async Task FetchingAskesForTheIconBySetAndName()
    {
        var handler = new StubHandler(url => (HttpStatusCode.OK,
            url.EndsWith(".svg", StringComparison.Ordinal)
                ? """<svg xmlns="http://www.w3.org/2000/svg" width="1em" height="1em" viewBox="0 0 24 24"><path d="M0 0h24v24H0z"/></svg>"""
                : SearchJson));

        var catalogue = new IconCatalog(new HttpClient(handler), "https://catalogue.invalid");
        var icon = (await catalogue.SearchAsync("fox"))[0];

        var svg = await catalogue.FetchSvgAsync(icon, 40);

        Assert.Contains("/mdi/fox.svg", handler.Requested[^1], StringComparison.Ordinal);
        Assert.Contains("width=\"40mm\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHostIsVisibleSoTheInterfaceCanSayWhereSearchesGo()
    {
        Assert.Equal("https://api.iconify.design", new IconCatalog().Host);
        Assert.Equal("https://catalogue.invalid", new IconCatalog(new HttpClient(), "https://catalogue.invalid/").Host);
    }
}
