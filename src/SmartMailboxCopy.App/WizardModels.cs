using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace SmartMailboxCopy.App;

public abstract class Notifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>One line of the step list on the left.</summary>
public sealed class NavItem : Notifier
{
    private bool _current, _done;
    public required string Title { get; init; }
    public required int Number { get; init; }
    public bool Current { get => _current; set { _current = value; Changed(nameof(Mark)); Changed(nameof(Weight)); Changed(nameof(TitleBrush)); Changed(nameof(MarkBrush)); } }
    public bool Done { get => _done; set { _done = value; Changed(nameof(Mark)); Changed(nameof(MarkBrush)); } }
    public string Mark => Done && !Current ? "✓" : Number.ToString();
    public FontWeight Weight => Current ? FontWeights.SemiBold : FontWeights.Normal;
    public Brush TitleBrush => Current ? Brushes.Black : Brushes.DimGray;
    public Brush MarkBrush => Current ? Brushes.RoyalBlue : Done ? Brushes.DarkGreen : Brushes.Gray;
}

public sealed class MailboxItem : Notifier
{
    private bool _selected;
    public required string Name { get; init; }
    public required string Upn { get; init; }
    public required string Mail { get; init; }
    public required MailboxKind Kind { get; init; }
    public required bool Enabled { get; init; }
    public string KindText => Kind switch { MailboxKind.User => "User", MailboxKind.UnlicensedResource => "Shared, room or equipment (no license)", _ => "Guest" };
    public string EnabledText => Enabled ? "Enabled" : "Disabled";
    public string Domain => Upn.Contains('@') ? Upn[(Upn.IndexOf('@') + 1)..] : "";
    public bool Selected { get => _selected; set { if (_selected == value) return; _selected = value; Changed(); } }
}

/// <summary>One row of the source-to-target map. Only a Ready row is copied.</summary>
public sealed class MapItem : Notifier
{
    private string _target = "", _status = ""; private bool _ready;
    public required string Source { get; init; }
    public string Target { get => _target; set { if (_target == value) return; _target = value; Changed(); } }
    public string Status { get => _status; set { _status = value; Changed(); } }
    public bool Ready { get => _ready; set { _ready = value; Changed(); } }
}

/// <summary>One mailbox while the copy runs.</summary>
public sealed class RunRow : Notifier
{
    private MailboxState _state = MailboxState.Waiting; private string _message = "";
    public required int Index { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public MailboxState State { get => _state; set { _state = value; Changed(); Changed(nameof(StateText)); Changed(nameof(HasProblem)); } }
    public string StateText => State switch
    {
        MailboxState.Waiting => "Waiting", MailboxState.Running => "Copying…", MailboxState.Done => "Done", MailboxState.Problems => "Needs a look",
        MailboxState.Failed => "Failed", _ => "Not run"
    };
    public string Message { get => _message; set { _message = value; Changed(); } }
    public bool HasProblem => State is MailboxState.Problems or MailboxState.Failed;
}

public sealed class FolderRow : Notifier
{
    public string Path { get; init; } = "";
    public string Display { get; init; } = "";
    public string Kind { get; init; } = "";
    public int Items { get; init; }
    private int _imported, _already, _failed; private string _status = "Waiting";
    public int Imported { get => _imported; set { _imported = value; Changed(); } }
    public int AlreadyDone { get => _already; set { _already = value; Changed(); } }
    public int Failed { get => _failed; set { _failed = value; Changed(); } }
    public string Status { get => _status; set { _status = value; Changed(); } }
}

public sealed class LogLine
{
    public LogLevel Level { get; init; }
    public string Text { get; init; } = "";
    public Brush Brush => Level switch { LogLevel.Error => Brushes.Firebrick, LogLevel.Warn => Brushes.DarkGoldenrod, LogLevel.Debug => Brushes.Gray, _ => SystemColors.WindowTextBrush };
}

public sealed record StatItem(string Label, string Value);

public sealed class ResultRow
{
    public string Source { get; init; } = "";
    public string Target { get; init; } = "";
    public string Result { get; init; } = "";
    public string Copied { get; init; } = "";
    public string AlreadyThere { get; init; } = "";
    public string Failed { get; init; } = "";
    public string Verified { get; init; } = "";
    public string Notes { get; init; } = "";
    public bool HasProblem { get; init; }
}
