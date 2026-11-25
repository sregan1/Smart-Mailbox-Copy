using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using SmartMailboxCopy.App.Localization;

namespace SmartMailboxCopy.App;

public abstract class Notifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>One step in the step bar at the top. The bubble shows its number, or a check once the step is behind you.</summary>
public sealed class NavItem : Notifier
{
    private bool _current, _done;
    public required string Label { get; init; }
    public required int Number { get; init; }
    public bool IsFirst => Number == 1;
    public bool IsLast { get; init; }
    public bool Current { get => _current; set { _current = value; Changed(nameof(Mark)); Changed(); } }
    public bool Done { get => _done; set { _done = value; Changed(nameof(Mark)); Changed(); } }
    public string Mark => Done && !Current ? "✓" : Number.ToString();
}

public sealed class MailboxItem : Notifier
{
    private bool _selected;
    public required string Name { get; init; }
    public required string Upn { get; init; }
    public required string Mail { get; init; }
    public required MailboxKind Kind { get; init; }
    public required bool Enabled { get; init; }
    public string KindText => Kind switch { MailboxKind.User => Loc.T("Boxes_KindUser"), MailboxKind.UnlicensedResource => Loc.T("Boxes_KindResource"), _ => Loc.T("Boxes_KindGuest") };
    public string EnabledText => Enabled ? Loc.T("Boxes_Enabled") : Loc.T("Boxes_Disabled");
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
    public bool Ready { get => _ready; set { _ready = value; Changed(); Changed(nameof(StatusLevel)); } }
    public string StatusLevel => Ready ? "Ready" : "Look";
}

/// <summary>One mailbox while the copy runs.</summary>
public sealed class RunRow : Notifier
{
    private MailboxState _state = MailboxState.Waiting; private string _message = "";
    public required int Index { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public MailboxState State { get => _state; set { _state = value; Changed(); Changed(nameof(StateText)); Changed(nameof(StatusLevel)); Changed(nameof(HasProblem)); } }
    public string StateText => StateName(State, running: true);
    public string StatusLevel => State switch { MailboxState.Done => "Ready", MailboxState.Problems => "Look", MailboxState.Failed => "Error", MailboxState.Running => "Info", _ => "" };
    public string Message { get => _message; set { _message = value; Changed(); } }
    public bool HasProblem => State is MailboxState.Problems or MailboxState.Failed;

    /// <summary>The state's name. While a mailbox runs it reads "Copying…"; in the results table a finished one reads "Copied".</summary>
    public static string StateName(MailboxState s, bool running) => s switch
    {
        MailboxState.Waiting => Loc.T("State_Waiting"), MailboxState.Running => Loc.T("State_Copying"),
        MailboxState.Done => Loc.T(running ? "State_Done" : "State_Copied"), MailboxState.Problems => Loc.T("State_Problems"),
        MailboxState.Failed => Loc.T("State_Failed"), _ => Loc.T("State_NotRun")
    };
}

public sealed class FolderRow : Notifier
{
    public string Path { get; init; } = "";
    public string Display { get; init; } = "";
    /// <summary>The engine's name for the folder: a well-known name such as "inbox", or empty for a folder people created.</summary>
    public string WellKnown { get; init; } = "";
    public int Items { get; init; }
    private int _imported, _already, _failed; private string _status = "Waiting";
    public string Kind => WellKnown switch
    {
        "" => Loc.T("Kind_UserFolder"), "inbox" => Loc.T("Folder_Inbox"), "sentitems" => Loc.T("Folder_Sent"), "drafts" => Loc.T("Folder_Drafts"),
        "deleteditems" => Loc.T("Folder_Deleted"), "junkemail" => Loc.T("Folder_Junk"), "archive" => Loc.T("Folder_Archive"), _ => WellKnown
    };
    public int Imported { get => _imported; set { _imported = value; Changed(); } }
    public int AlreadyDone { get => _already; set { _already = value; Changed(); } }
    public int Failed { get => _failed; set { _failed = value; Changed(); } }

    /// <summary>The engine's status: Waiting, Copying, Done, "N failed" or Mismatch.</summary>
    public string Status { get => _status; set { _status = value; Changed(); Changed(nameof(StatusText)); Changed(nameof(StatusLevel)); } }
    public string StatusText => Status switch
    {
        "Waiting" => Loc.T("State_Waiting"), "Copying" => Loc.T("FStat_Copying"), "Done" => Loc.T("State_Done"), "Mismatch" => Loc.T("FStat_Mismatch"),
        var s when s.EndsWith(" failed", StringComparison.Ordinal) => Loc.T("FStat_Failed", s[..^" failed".Length]), var s => s
    };
    public string StatusLevel => Status switch { "Done" => "Ready", "Copying" => "Info", "Mismatch" => "Look", "Waiting" => "", _ => "Look" };
}

public sealed class LogLine
{
    public LogLevel Level { get; init; }
    public string Text { get; init; } = "";
    public Brush Brush => ThemeManager.Brush(Level switch { LogLevel.Error => "DangerBrush", LogLevel.Warn => "WarningTextBrush", LogLevel.Debug => "TextTertiaryBrush", _ => "TextPrimaryBrush" });
}

public sealed record StatItem(string Label, string Value);

public sealed class ResultRow
{
    public string Source { get; init; } = "";
    public string Target { get; init; } = "";
    public string Result { get; init; } = "";
    public string StatusLevel { get; init; } = "";
    public string Copied { get; init; } = "";
    public string AlreadyThere { get; init; } = "";
    public string Failed { get; init; } = "";
    public string Verified { get; init; } = "";
    public string Notes { get; init; } = "";
    public bool HasProblem { get; init; }
}
