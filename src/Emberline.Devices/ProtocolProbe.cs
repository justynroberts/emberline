using System.Text;
using Emberline.Transport;

namespace Emberline.Devices;

public sealed record ProbeExchange(string Sent, IReadOnlyList<string> Received, TimeSpan Elapsed);

public sealed record ProbeReport
{
    public required string Endpoint { get; init; }
    public required DateTimeOffset RunAt { get; init; }
    public required IReadOnlyList<ProbeExchange> Exchanges { get; init; }

    public bool LooksLikeGrbl => Exchanges.Any(e =>
        e.Received.Any(r =>
            r.Contains("Grbl", StringComparison.OrdinalIgnoreCase) ||
            r.StartsWith("ok", StringComparison.Ordinal) ||
            r.StartsWith("$", StringComparison.Ordinal) ||
            r.StartsWith("<", StringComparison.Ordinal)));

    /// <summary>A Markdown report suitable for pasting into a protocol issue.</summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Protocol probe — {Endpoint}");
        sb.AppendLine();
        sb.AppendLine($"Run at {RunAt:u}. GRBL-compatible: **{(LooksLikeGrbl ? "yes" : "not detected")}**.");
        sb.AppendLine();

        foreach (var exchange in Exchanges)
        {
            sb.AppendLine($"## `{Escape(exchange.Sent)}`  _({exchange.Elapsed.TotalMilliseconds:0} ms)_");
            sb.AppendLine();
            if (exchange.Received.Count == 0)
            {
                sb.AppendLine("_no response_");
            }
            else
            {
                sb.AppendLine("```");
                foreach (var line in exchange.Received) sb.AppendLine(line);
                sb.AppendLine("```");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("\x18", "<0x18 soft reset>", StringComparison.Ordinal).Trim();
}

/// <summary>
/// Records exactly what a controller says in response to a known set of probes.
///
/// This exists so that adding support for a machine whose network protocol is not
/// published is an evidence-gathering exercise rather than guesswork. The user
/// runs the probe, Emberline writes a Markdown transcript, and that transcript is
/// what a device driver gets written from. Nothing here sends motion commands —
/// every probe is a query.
/// </summary>
public static class ProtocolProbe
{
    /// <summary>Read-only queries. Deliberately no motion, no homing, no settings writes.</summary>
    private static readonly string[] Probes =
    [
        "\x18",   // soft reset — makes GRBL re-announce
        "?",      // status report
        "$I",     // build info
        "$$",     // settings
        "$G",     // parser state
        "$#",     // work offsets
        "$",      // help
    ];

    public static async Task<ProbeReport> RunAsync(ITransport transport, CancellationToken cancellationToken = default)
    {
        var exchanges = new List<ProbeExchange>();
        var received = new List<string>();
        var assembler = new GCode.Grbl.LineAssembler();

        void OnData(ReadOnlyMemory<byte> data)
        {
            lock (received) received.AddRange(assembler.Push(Encoding.ASCII.GetString(data.Span)));
        }

        transport.DataReceived += OnData;
        try
        {
            if (!transport.IsConnected) await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);

            foreach (var probe in Probes)
            {
                lock (received) received.Clear();
                var started = DateTimeOffset.UtcNow;

                await transport.WriteAsync(Encoding.ASCII.GetBytes(probe.Length == 1 && probe[0] < 32 ? probe : probe + "\n"),
                    cancellationToken).ConfigureAwait(false);

                // Long enough for a $$ dump over a slow wireless link.
                await Task.Delay(900, cancellationToken).ConfigureAwait(false);

                List<string> snapshot;
                lock (received) snapshot = [.. received];
                exchanges.Add(new ProbeExchange(probe, snapshot, DateTimeOffset.UtcNow - started));
            }
        }
        finally
        {
            transport.DataReceived -= OnData;
        }

        return new ProbeReport
        {
            Endpoint = transport.Description,
            RunAt = DateTimeOffset.UtcNow,
            Exchanges = exchanges,
        };
    }
}
