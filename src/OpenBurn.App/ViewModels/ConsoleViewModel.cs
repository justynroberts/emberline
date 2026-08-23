using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenBurn.Devices;

namespace OpenBurn.App.ViewModels;

public sealed partial class ConsoleLine : ObservableObject
{
    public required DateTimeOffset Timestamp { get; init; }
    public required ConsoleDirection Direction { get; init; }
    public required string Text { get; init; }

    public string Prefix => Direction switch
    {
        ConsoleDirection.Sent => ">",
        ConsoleDirection.Received => "<",
        ConsoleDirection.Warning => "!",
        ConsoleDirection.Error => "×",
        _ => "·",
    };

    public string Stamp => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");

    /// <summary>Token key the view binds its foreground to, so the console follows the theme.</summary>
    public string ColourKey => Direction switch
    {
        ConsoleDirection.Sent => "Cyan",
        ConsoleDirection.Error => "StateAlarm",
        ConsoleDirection.Warning => "StateHold",
        ConsoleDirection.Info => "InkMuted",
        _ => "Ink",
    };
}

/// <summary>
/// The machine console.
///
/// Capped at a fixed number of lines: a raster job with logging on would otherwise
/// accumulate hundreds of thousands of entries and take the UI down with it. The
/// cap is generous enough to cover any realistic diagnosis.
/// </summary>
public sealed partial class ConsoleViewModel : ObservableObject
{
    private const int MaxLines = 2000;

    [ObservableProperty]
    private string _input = string.Empty;

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private bool _showStatusPolls;

    public ObservableCollection<ConsoleLine> Lines { get; } = [];

    /// <summary>Where a submitted line goes. The shell owns the device, not this.</summary>
    public Func<string, Task>? Submit { get; set; }

    public void Append(ConsoleEntry entry)
    {
        // Marshal to the UI thread: entries arrive on the transport's read loop.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Append(entry));
            return;
        }

        Lines.Add(new ConsoleLine
        {
            Timestamp = entry.Timestamp,
            Direction = entry.Direction,
            Text = entry.Text,
        });

        while (Lines.Count > MaxLines) Lines.RemoveAt(0);
    }

    public void AppendInfo(string text) =>
        Append(new ConsoleEntry(DateTimeOffset.UtcNow, ConsoleDirection.Info, text));

    public void AppendError(string text) =>
        Append(new ConsoleEntry(DateTimeOffset.UtcNow, ConsoleDirection.Error, text));

    [RelayCommand]
    private void Clear() => Lines.Clear();

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = Input.Trim();
        if (text.Length == 0 || Submit is null) return;

        Input = string.Empty;
        _history.Add(text);
        _historyIndex = _history.Count;

        try
        {
            await Submit(text).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppendError(ex.Message);
        }
    }

    private readonly List<string> _history = [];
    private int _historyIndex;

    /// <summary>Up and down arrows walk the command history, as any console should.</summary>
    public void HistoryBack()
    {
        if (_history.Count == 0) return;
        _historyIndex = Math.Max(0, _historyIndex - 1);
        Input = _history[_historyIndex];
    }

    public void HistoryForward()
    {
        if (_history.Count == 0) return;
        _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
        Input = _historyIndex >= _history.Count ? string.Empty : _history[_historyIndex];
    }

    /// <summary>The recent log, for the AI job doctor and for bug reports.</summary>
    public string Tail(int lines = 60)
    {
        var sb = new StringBuilder();
        foreach (var line in Lines.TakeLast(lines))
        {
            sb.Append(line.Stamp).Append(' ').Append(line.Prefix).Append(' ').AppendLine(line.Text);
        }
        return sb.ToString();
    }
}
