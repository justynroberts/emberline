using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace OpenBurn.AI;

public abstract record AssistantEvent
{
    /// <summary>A chunk of reply text as it streams in.</summary>
    public sealed record Text(string Delta) : AssistantEvent;

    /// <summary>The assistant used a tool. Surfaced so its actions are visible, not implicit.</summary>
    public sealed record ToolCall(string Name, string Summary) : AssistantEvent;

    public sealed record Done(string FullText) : AssistantEvent;

    public sealed record Failed(string Message) : AssistantEvent;
}

/// <summary>
/// The in-app assistant.
///
/// A manual streaming tool loop rather than the SDK's tool runner: the runner
/// hides the individual turns, and this application needs to surface every tool
/// call in the transcript so the operator can see exactly what the assistant
/// looked at and what it changed. On a machine that can start a fire, "it did
/// something" is not an acceptable level of visibility.
/// </summary>
public sealed class LaserAssistant
{
    private readonly IAssistantHost _host;
    private readonly AnthropicClient _client;
    private readonly AiOptions _options;
    private readonly List<MessageParam> _history = [];

    /// <summary>How many tool round-trips one question may take before we stop.</summary>
    private const int MaxIterations = 8;

    public LaserAssistant(IAssistantHost host, AiOptions options)
    {
        _host = host;
        _options = options;

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException(
                "No Anthropic API key. Set ANTHROPIC_API_KEY, or put the key in a file called 'anthropic.key' " +
                "in the OpenBurn application data folder.");
        }

        _client = new AnthropicClient { ApiKey = options.ApiKey };
    }

    public IReadOnlyList<MessageParam> History => _history;

    public void Reset() => _history.Clear();

    /// <summary>Ask a question. Events stream out as the reply is produced.</summary>
    public async IAsyncEnumerable<AssistantEvent> AskAsync(
        string question,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _history.Add(new MessageParam { Role = Role.User, Content = question });
        var reply = new StringBuilder();

        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            var turn = new TurnAccumulator();
            string? failure = null;

            await foreach (var streamEvent in StreamTurn(m => failure = m, cancellationToken).ConfigureAwait(false))
            {
                turn.Add(streamEvent);

                if (streamEvent.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                {
                    reply.Append(text.Text);
                    yield return new AssistantEvent.Text(text.Text);
                }
            }

            if (failure is not null)
            {
                yield return new AssistantEvent.Failed(failure);
                yield break;
            }

            var (assistantContent, toolUses) = turn.Build();

            // Nothing to echo means the model produced nothing usable; stop rather
            // than sending an empty assistant turn the API will reject.
            if (assistantContent.Count == 0)
            {
                yield return new AssistantEvent.Done(reply.ToString());
                yield break;
            }

            _history.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });

            if (toolUses.Count == 0)
            {
                yield return new AssistantEvent.Done(reply.ToString());
                yield break;
            }

            var results = new List<ContentBlockParam>();
            foreach (var toolUse in toolUses)
            {
                var (summary, payload) = Execute(toolUse);
                yield return new AssistantEvent.ToolCall(toolUse.Name, summary);
                results.Add(new ToolResultBlockParam { ToolUseID = toolUse.Id, Content = payload });
            }

            _history.Add(new MessageParam { Role = Role.User, Content = results });
        }

        yield return new AssistantEvent.Done(reply.ToString());
    }

    /// <summary>
    /// Stream one turn, converting any transport or API failure into a reported
    /// message rather than an exception escaping an iterator.
    /// </summary>
    private async IAsyncEnumerable<RawMessageStreamEvent> StreamTurn(
        Action<string> onError,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IAsyncEnumerator<RawMessageStreamEvent>? enumerator = null;
        try
        {
            enumerator = _client.Messages.CreateStreaming(BuildParams(), cancellationToken)
                                          .GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception ex)
        {
            onError(Describe(ex));
        }

        if (enumerator is null) yield break;

        try
        {
            while (true)
            {
                RawMessageStreamEvent current;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                    current = enumerator.Current;
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
                catch (Exception ex)
                {
                    onError(Describe(ex));
                    yield break;
                }

                yield return current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One-shot diagnosis of a fault, using the code, the console log and the
    /// controller settings. No tools and no conversation — just an answer.
    /// </summary>
    public async Task<string> DiagnoseAsync(string faultDescription, CancellationToken cancellationToken = default)
    {
        var context = _host.BuildContext();

        var prompt = $"""
            The machine has reported a fault. Explain in plain language what has happened and exactly what to do
            about it, in at most four short paragraphs. Be specific about which setting or which physical part to check.

            Fault: {faultDescription}

            Machine: {context.MachineName}, {context.LaserWatts:0.#} W, bed {context.BedWidthMm:0}×{context.BedHeightMm:0} mm
            State: {context.MachineState}, homed: {context.IsHomed}
            Controller settings: {FormatSettings(context.ControllerSettings)}

            Recent console:
            {context.ConsoleTail ?? "(nothing logged)"}
            """;

        try
        {
            var response = await _client.Messages.Create(new MessageCreateParams
            {
                Model = _options.Model,
                MaxTokens = 2000,
                System = AssistantTools.SystemPrompt,
                Messages = [new MessageParam { Role = Role.User, Content = prompt }],
            }, cancellationToken).ConfigureAwait(false);

            return string.Join("\n", response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        }
        catch (Exception ex)
        {
            return $"Could not reach the assistant: {Describe(ex)}";
        }
    }

    private MessageCreateParams BuildParams() => new()
    {
        Model = _options.Model,
        MaxTokens = _options.MaxTokens,
        // The system prompt and the tool list are byte-stable, so they form a
        // cacheable prefix across every turn of a session.
        System = AssistantTools.SystemPrompt,
        Tools = [.. AssistantTools.All.Select(t => new ToolUnion(t))],
        Messages = [.. _history],
    };

    // ------------------------------------------------------------ execution

    private (string Summary, string Payload) Execute(PendingToolUse toolUse)
    {
        var input = toolUse.Input;

        try
        {
            switch (toolUse.Name)
            {
                case AssistantTools.GetMachineState:
                {
                    var c = _host.BuildContext();
                    return ("read machine state", JsonSerializer.Serialize(new
                    {
                        machine = c.MachineName,
                        laser_watts = c.LaserWatts,
                        bed_mm = new[] { c.BedWidthMm, c.BedHeightMm },
                        connected = c.IsConnected,
                        state = c.MachineState,
                        homed = c.IsHomed,
                        work_position = c.WorkPosition,
                        material = c.SelectedMaterial,
                    }));
                }

                case AssistantTools.GetJobSummary:
                {
                    var c = _host.BuildContext();
                    return ("read job summary", JsonSerializer.Serialize(new { layers = c.Layers, job = c.Job }));
                }

                case AssistantTools.GetControllerSettings:
                {
                    var c = _host.BuildContext();
                    return ("read controller settings",
                        c.ControllerSettings is null || c.ControllerSettings.Count == 0
                            ? "No settings have been read from the controller yet. The machine may not be connected."
                            : JsonSerializer.Serialize(c.ControllerSettings.ToDictionary(kv => "$" + kv.Key, kv => kv.Value)));
                }

                case AssistantTools.GetConsoleLog:
                {
                    var c = _host.BuildContext();
                    return ("read console log", c.ConsoleTail ?? "(nothing logged)");
                }

                case AssistantTools.SetLayerSettings:
                {
                    var changes = ParseLayerChanges(input);
                    if (changes.Count == 0) return ("no layer changes", "No valid changes were supplied.");
                    return ($"changed {changes.Count} layer setting(s)", _host.ApplyLayerChanges(changes));
                }

                case AssistantTools.PrepareTestGrid:
                {
                    var powers = Numbers(input, "powers");
                    var speeds = Numbers(input, "speeds");
                    var cell = Number(input, "cell_size_mm") ?? 8;

                    if (powers.Length == 0 || speeds.Length == 0)
                    {
                        return ("test grid rejected", "A test grid needs at least one power and one speed.");
                    }

                    return ($"prepared a {powers.Length}×{speeds.Length} test grid",
                            _host.PrepareTestGrid(powers, speeds, cell));
                }

                case AssistantTools.ProposeMachineAction:
                {
                    var kind = Text(input, "kind") ?? "unknown";
                    var description = Text(input, "description") ?? kind;
                    _host.ProposeAction(new ProposedAction(kind, description, StringMap(input, "parameters")));

                    return ($"proposed: {description}",
                        "A confirmation card has been shown to the operator. Nothing has moved. " +
                        "Do not claim the action has happened — the operator has to press the button.");
                }

                default:
                    return ("unknown tool", $"There is no tool called '{toolUse.Name}'.");
            }
        }
        catch (Exception ex)
        {
            return ($"{toolUse.Name} failed", $"The tool failed: {ex.Message}");
        }
    }

    // -------------------------------------------------------------- parsing

    private static List<LayerChange> ParseLayerChanges(IReadOnlyDictionary<string, JsonElement> input)
    {
        var result = new List<LayerChange>();
        if (!input.TryGetValue("changes", out var changes) || changes.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in changes.EnumerateArray())
        {
            var name = item.TryGetProperty("layer_name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;

            result.Add(new LayerChange(
                name,
                PropertyNumber(item, "speed_mm_min"),
                PropertyNumber(item, "power_percent"),
                PropertyInteger(item, "passes"),
                PropertyNumber(item, "line_interval_mm"),
                PropertyBoolean(item, "air_assist")));
        }
        return result;
    }

    private static double? PropertyNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static int? PropertyInteger(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static bool? PropertyBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    private static double? Number(IReadOnlyDictionary<string, JsonElement> input, string name) =>
        input.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string? Text(IReadOnlyDictionary<string, JsonElement> input, string name) =>
        input.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double[] Numbers(IReadOnlyDictionary<string, JsonElement> input, string name)
    {
        if (!input.TryGetValue(name, out var array) || array.ValueKind != JsonValueKind.Array) return [];
        return [.. array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetDouble())];
    }

    private static Dictionary<string, string> StringMap(IReadOnlyDictionary<string, JsonElement> input, string name)
    {
        var result = new Dictionary<string, string>();
        if (!input.TryGetValue(name, out var obj) || obj.ValueKind != JsonValueKind.Object) return result;

        foreach (var property in obj.EnumerateObject()) result[property.Name] = property.Value.ToString();
        return result;
    }

    private static string FormatSettings(IReadOnlyDictionary<int, double>? settings) =>
        settings is null || settings.Count == 0
            ? "(not read)"
            : string.Join(", ", settings.OrderBy(kv => kv.Key).Select(kv => $"${kv.Key}={kv.Value:0.###}"));

    private static string Describe(Exception ex) => ex switch
    {
        Anthropic.Exceptions.AnthropicApiException api => $"The Claude API returned an error: {api.Message}",
        HttpRequestException => "Could not reach api.anthropic.com. Check the network connection.",
        TaskCanceledException => "The request timed out.",
        _ => ex.Message,
    };
}

internal sealed record PendingToolUse(string Id, string Name, IReadOnlyDictionary<string, JsonElement> Input);

/// <summary>
/// Reassembles one streamed turn.
///
/// The tool loop needs two things the stream only provides in pieces: the
/// assistant content to echo back, and the tool_use blocks to execute. Tool
/// inputs in particular arrive as partial JSON fragments that only parse once
/// concatenated, which is the part that quietly breaks if you assume otherwise.
/// </summary>
internal sealed class TurnAccumulator
{
    private sealed class Block
    {
        public required string Kind { get; init; }
        public StringBuilder Buffer { get; } = new();
        public string? Id { get; init; }
        public string? Name { get; init; }
        public string? Signature { get; set; }
        public string? Data { get; init; }
    }

    private readonly Dictionary<int, Block> _blocks = [];

    public void Add(RawMessageStreamEvent streamEvent)
    {
        if (streamEvent.TryPickContentBlockStart(out var start))
        {
            var index = (int)start.Index;
            _blocks[index] = start.ContentBlock.Value switch
            {
                ToolUseBlock tool => new Block { Kind = "tool_use", Id = tool.ID, Name = tool.Name },
                TextBlock text => Seed(new Block { Kind = "text" }, text.Text),
                ThinkingBlock thinking => Seed(new Block { Kind = "thinking" }, thinking.Thinking),
                RedactedThinkingBlock redacted => new Block { Kind = "redacted", Data = redacted.Data },
                _ => new Block { Kind = "other" },
            };
            return;
        }

        if (streamEvent.TryPickContentBlockDelta(out var delta))
        {
            var index = (int)delta.Index;
            if (!_blocks.TryGetValue(index, out var block)) return;

            if (delta.Delta.TryPickText(out var text)) block.Buffer.Append(text.Text);
            else if (delta.Delta.TryPickInputJson(out var json)) block.Buffer.Append(json.PartialJson);
            else if (delta.Delta.TryPickThinking(out var thinking)) block.Buffer.Append(thinking.Thinking);
            else if (delta.Delta.TryPickSignature(out var signature)) block.Signature = signature.Signature;
        }
    }

    private static Block Seed(Block block, string? initial)
    {
        if (!string.IsNullOrEmpty(initial)) block.Buffer.Append(initial);
        return block;
    }

    public (List<ContentBlockParam> Content, List<PendingToolUse> ToolUses) Build()
    {
        var content = new List<ContentBlockParam>();
        var toolUses = new List<PendingToolUse>();

        foreach (var (_, block) in _blocks.OrderBy(kv => kv.Key))
        {
            switch (block.Kind)
            {
                case "text":
                {
                    var text = block.Buffer.ToString();
                    if (text.Length > 0) content.Add(new TextBlockParam { Text = text });
                    break;
                }

                case "thinking":
                {
                    // The signature must survive untouched — the API rejects tampering.
                    if (block.Signature is { Length: > 0 } signature)
                    {
                        content.Add(new ThinkingBlockParam { Thinking = block.Buffer.ToString(), Signature = signature });
                    }
                    break;
                }

                case "redacted":
                {
                    if (block.Data is { Length: > 0 } data) content.Add(new RedactedThinkingBlockParam { Data = data });
                    break;
                }

                case "tool_use":
                {
                    var input = ParseInput(block.Buffer.ToString());
                    var id = block.Id ?? Guid.NewGuid().ToString("N");
                    var name = block.Name ?? "unknown";

                    content.Add(new ToolUseBlockParam { ID = id, Name = name, Input = input });
                    toolUses.Add(new PendingToolUse(id, name, input));
                    break;
                }
            }
        }

        return (content, toolUses);
    }

    private static Dictionary<string, JsonElement> ParseInput(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];

            var result = new Dictionary<string, JsonElement>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.Clone();
            }
            return result;
        }
        catch (JsonException)
        {
            // A truncated stream can leave the fragment unparseable. An empty input
            // is recoverable; a thrown exception mid-iterator is not.
            return [];
        }
    }
}
