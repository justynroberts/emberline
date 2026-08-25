using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emberline.AI;

namespace Emberline.App.ViewModels;

public sealed partial class AssistantMessage : ObservableObject
{
    public required bool FromUser { get; init; }

    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>Tool calls are shown as their own entries so the assistant's actions are visible.</summary>
    public bool IsToolNote { get; init; }

    public string RoleLabel => IsToolNote ? "tool" : FromUser ? "you" : "assistant";

    public string ColourKey => IsToolNote ? "InkFaint" : FromUser ? "Cyan" : "Ink";
}

/// <summary>
/// A machine action the assistant has proposed.
///
/// This is the safety gate made visible: the assistant can put one of these on
/// screen, and nothing else. The operator presses the button or it never happens.
/// </summary>
public sealed partial class PendingActionViewModel : ObservableObject
{
    public required string Kind { get; init; }
    public required string Description { get; init; }
    public required Func<Task> Confirm { get; init; }
    public required Action Dismiss { get; init; }
}

/// <summary>The assistant panel.</summary>
public sealed partial class AssistantViewModel : ObservableObject
{
    private readonly IAssistantHost _host;
    private LaserAssistant? _assistant;
    private CancellationTokenSource? _inFlight;

    public AssistantViewModel(IAssistantHost host)
    {
        _host = host;
        Options = AiOptions.Load();
    }

    public ObservableCollection<AssistantMessage> Messages { get; } = [];
    public ObservableCollection<PendingActionViewModel> PendingActions { get; } = [];

    public AiOptions Options { get; private set; }

    [ObservableProperty]
    private string _input = string.Empty;

    [ObservableProperty]
    private bool _isThinking;

    [ObservableProperty]
    private string? _statusMessage;

    public bool IsConfigured => Options.IsUsable;

    public string SetupHint =>
        "The assistant is optional and everything else works without it. Paste an Anthropic API key below to " +
        "switch it on. Nothing is sent anywhere until you type a question, and the only host it ever talks to " +
        "is api.anthropic.com.";

    // ------------------------------------------------------------- the key

    /// <summary>What is typed into the key box. Never persisted from here directly.</summary>
    [ObservableProperty]
    private string _apiKeyInput = string.Empty;

    [ObservableProperty]
    private bool _isEditingKey;

    public bool HasStoredKey => !string.IsNullOrWhiteSpace(Options.ApiKey);

    /// <summary>An environment key wins, and the application must not overwrite it.</summary>
    public bool KeyIsFromEnvironment => AiOptions.KeyComesFromEnvironment;

    public string KeyStatus
    {
        get
        {
            if (KeyIsFromEnvironment) return $"Using ANTHROPIC_API_KEY from your environment ({AiOptions.Mask(Options.ApiKey)}).";
            if (HasStoredKey) return $"Key saved ({AiOptions.Mask(Options.ApiKey)}). It is stored in {AiOptions.KeyFilePath}, readable only by you.";
            return "No key yet.";
        }
    }

    /// <summary>Show the entry box: when there is no key, or when asked to change one.</summary>
    public bool ShowKeyEntry => IsEditingKey || (!HasStoredKey && !KeyIsFromEnvironment);

    public bool CanSaveKey => ApiKeyInput.Trim().Length > 10;

    partial void OnApiKeyInputChanged(string value) => OnPropertyChanged(nameof(CanSaveKey));

    partial void OnIsEditingKeyChanged(bool value) => OnPropertyChanged(nameof(ShowKeyEntry));

    [RelayCommand]
    private void SaveKey()
    {
        var key = ApiKeyInput.Trim();
        if (key.Length <= 10)
        {
            StatusMessage = "That does not look like a key.";
            return;
        }

        try
        {
            AiOptions.SaveApiKey(key);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save the key: {ex.Message}";
            return;
        }

        // Never leave it sitting in a bound property.
        ApiKeyInput = string.Empty;
        IsEditingKey = false;
        Reload();

        StatusMessage = Options.IsUsable
            ? "Key saved. The assistant is ready."
            : "Key saved, but the assistant still could not start.";
    }

    [RelayCommand]
    private void ChangeKey()
    {
        ApiKeyInput = string.Empty;
        IsEditingKey = true;
        StatusMessage = null;
    }

    [RelayCommand]
    private void ForgetKey()
    {
        try
        {
            AiOptions.SaveApiKey(null);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not remove the key: {ex.Message}";
            return;
        }

        ApiKeyInput = string.Empty;
        IsEditingKey = false;
        Options = AiOptions.Load();
        _assistant = null;
        RaiseKeyState();

        StatusMessage = KeyIsFromEnvironment
            ? "Removed the saved key. ANTHROPIC_API_KEY is still set in your environment, so that one is being used."
            : "Key removed.";
    }

    private void RaiseKeyState()
    {
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(HasStoredKey));
        OnPropertyChanged(nameof(KeyIsFromEnvironment));
        OnPropertyChanged(nameof(KeyStatus));
        OnPropertyChanged(nameof(ShowKeyEntry));
        OnPropertyChanged(nameof(CanSaveKey));
    }

    /// <summary>Re-read the key and enable the assistant, so no restart is needed.</summary>
    [RelayCommand]
    public void Reload()
    {
        Options = AiOptions.Load() with { Enabled = true };
        _assistant = null;
        RaiseKeyState();

        StatusMessage = Options.IsUsable
            ? null
            : "Still no API key. Paste one below, or set ANTHROPIC_API_KEY in your environment.";

        if (Options.IsUsable) Options.Save();
    }

    private LaserAssistant? Ensure()
    {
        if (_assistant is not null) return _assistant;
        if (!Options.IsUsable)
        {
            Reload();
            if (!Options.IsUsable) return null;
        }

        try
        {
            _assistant = new LaserAssistant(_host, Options);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            return null;
        }
        return _assistant;
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var question = Input.Trim();
        if (question.Length == 0 || IsThinking) return;

        var assistant = Ensure();
        if (assistant is null)
        {
            StatusMessage = SetupHint;
            return;
        }

        Input = string.Empty;
        StatusMessage = null;
        Messages.Add(new AssistantMessage { FromUser = true, Text = question });

        var reply = new AssistantMessage { FromUser = false, Text = string.Empty };
        Messages.Add(reply);

        IsThinking = true;
        _inFlight = new CancellationTokenSource();

        try
        {
            await foreach (var evt in assistant.AskAsync(question, _inFlight.Token).ConfigureAwait(true))
            {
                switch (evt)
                {
                    case AssistantEvent.Text text:
                        reply.Text += text.Delta;
                        break;

                    case AssistantEvent.ToolCall tool:
                        Messages.Insert(Messages.IndexOf(reply), new AssistantMessage
                        {
                            FromUser = false,
                            IsToolNote = true,
                            Text = $"{tool.Name}: {tool.Summary}",
                        });
                        break;

                    case AssistantEvent.Failed failed:
                        StatusMessage = failed.Message;
                        if (reply.Text.Length == 0) Messages.Remove(reply);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            reply.Text += "\n(cancelled)";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsThinking = false;
            _inFlight?.Dispose();
            _inFlight = null;
            if (reply.Text.Length == 0 && Messages.Contains(reply)) Messages.Remove(reply);
        }
    }

    [RelayCommand]
    private void Cancel() => _inFlight?.Cancel();

    [RelayCommand]
    private void Clear()
    {
        Messages.Clear();
        PendingActions.Clear();
        _assistant?.Reset();
        StatusMessage = null;
    }

    /// <summary>Ask the assistant to diagnose a fault, with the console log attached.</summary>
    public async Task DiagnoseAsync(string faultDescription)
    {
        var assistant = Ensure();
        if (assistant is null)
        {
            StatusMessage = SetupHint;
            return;
        }

        Messages.Add(new AssistantMessage { FromUser = true, Text = $"Diagnose: {faultDescription}" });
        var reply = new AssistantMessage { FromUser = false, Text = "…" };
        Messages.Add(reply);

        IsThinking = true;
        try
        {
            reply.Text = await assistant.DiagnoseAsync(faultDescription).ConfigureAwait(true);
        }
        finally
        {
            IsThinking = false;
        }
    }

    /// <summary>Called by the host when the assistant proposes something that would move the machine.</summary>
    public void AddPendingAction(string kind, string description, Func<Task> confirm)
    {
        Dispatcher.UIThread.Post(() =>
        {
            PendingActionViewModel? entry = null;
            entry = new PendingActionViewModel
            {
                Kind = kind,
                Description = description,
                Confirm = async () =>
                {
                    if (entry is not null) PendingActions.Remove(entry);
                    await confirm().ConfigureAwait(true);
                },
                Dismiss = () => { if (entry is not null) PendingActions.Remove(entry); },
            };
            PendingActions.Add(entry);
        });
    }

    [RelayCommand]
    private async Task ConfirmActionAsync(PendingActionViewModel? action)
    {
        if (action is null) return;
        await action.Confirm().ConfigureAwait(true);
    }

    [RelayCommand]
    private void DismissAction(PendingActionViewModel? action) => action?.Dismiss();

    /// <summary>Canned prompts, so the panel is useful before anyone knows what to type at it.</summary>
    public IReadOnlyList<string> Suggestions { get; } =
    [
        "What settings should I use for 3 mm plywood?",
        "Why is my engrave darker at the edges?",
        "Check my controller settings for anything wrong",
        "How long will this job take, and can I speed it up?",
    ];

    [RelayCommand]
    private async Task UseSuggestionAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Input = text;
        await SendAsync().ConfigureAwait(true);
    }
}
