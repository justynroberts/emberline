using System.Net.Http;
using System.Text;

namespace OpenBurn.Transport;

/// <summary>
/// Command-over-HTTP, as used by ESP3D and several manufacturer web interfaces
/// (<c>GET /command?commandText=…</c>).
///
/// It is a poor fit for streaming a job — one request per line would be absurd —
/// so this exists for discovery probing, configuration reads and one-shot
/// commands. The job engine will refuse to stream over it and say why.
/// </summary>
public sealed class HttpTransport : TransportBase
{
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly string _commandPath;
    private readonly StringBuilder _lineBuffer = new();
    private bool _connected;

    public HttpTransport(string host, int port = 80, string commandPath = "/command?commandText=")
    {
        _baseUri = new Uri($"http://{host}:{port}");
        _commandPath = commandPath;
        _http = new HttpClient { BaseAddress = _baseUri, Timeout = TimeSpan.FromSeconds(6) };
    }

    public override TransportKind Kind => TransportKind.Http;
    public override string Description => _baseUri.ToString();
    public override bool IsConnected => _connected;

    /// <summary>Streaming a job one HTTP request per line is not viable; the job engine checks this.</summary>
    public static bool SupportsStreaming => false;

    public override async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        // A build-info query is the cheapest way to confirm something GRBL-shaped
        // is listening.
        var response = await SendCommandAsync("$I", cancellationToken).ConfigureAwait(false);
        _connected = response is not null;
        if (!_connected) throw new IOException($"No GRBL-compatible HTTP endpoint at {_baseUri}.");
        Deliver(response!);
    }

    public override Task DisconnectAsync()
    {
        _connected = false;
        return Task.CompletedTask;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        foreach (var ch in Encoding.ASCII.GetString(data.Span))
        {
            if (ch is '\n' or '\r')
            {
                var line = _lineBuffer.ToString();
                _lineBuffer.Clear();
                if (line.Length == 0) continue;
                var response = await SendCommandAsync(line, cancellationToken).ConfigureAwait(false);
                Deliver(response ?? "error:3");
            }
            else
            {
                _lineBuffer.Append(ch);
            }
        }
    }

    public override async ValueTask WriteRealtimeAsync(byte value, CancellationToken cancellationToken = default)
    {
        // ESP3D exposes the real-time bytes as escaped commands.
        var command = value switch
        {
            (byte)'?' => "?",
            (byte)'~' => "~",
            (byte)'!' => "!",
            0x18 => "%18",
            _ => $"%{value:X2}",
        };
        var response = await SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
        if (response is not null) Deliver(response);
    }

    private async Task<string?> SendCommandAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            var url = _commandPath + Uri.EscapeDataString(command);
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private void Deliver(string text)
    {
        var normalised = text.EndsWith('\n') ? text : text + "\n";
        RaiseData(Encoding.ASCII.GetBytes(normalised));
    }
}
