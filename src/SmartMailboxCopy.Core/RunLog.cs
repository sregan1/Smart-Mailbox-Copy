using System.Text;
using System.Text.RegularExpressions;

namespace SmartMailboxCopy;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Logging for a run, to the screen and to files, all written as it happens (so a crash or kill loses nothing):
///   logs/run-&lt;time&gt;.log         everything, including a Debug trace of every request (method, path, status, time)
///   logs/run-&lt;time&gt;.errors.log  only warnings and errors, with full exception details
///   reports/copy-&lt;time&gt;.md      the readable summary (Info and above), saved at the end
/// The screen shows Info and above (Warn yellow, Error red); pass verbose to show the Debug trace on screen too.
/// Anything registered with <see cref="Hide"/> (addresses, client secrets) is replaced before it reaches any output.
/// Mail content (subjects, addresses, attachment names) and folder names are never logged.
/// </summary>
public sealed class RunLog
{
    private static readonly Regex FilterValue = new(@"eq '[^']*'", RegexOptions.Compiled);

    private readonly StringBuilder _report = new();
    private readonly List<string> _hidden = new();
    private readonly string _root;
    private readonly object _lock = new();
    private readonly string _stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    private readonly DateTime _started = DateTime.Now;
    private readonly bool _verbose;
    private int _warnings, _errors;

    public RunLog(bool verbose = false)
    {
        _verbose = verbose;
        _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartMailboxCopy");
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        Directory.CreateDirectory(Path.Combine(_root, "reports"));
        FullLogPath = Path.Combine(_root, "logs", $"run-{_stamp}.log");
        ErrorLogPath = Path.Combine(_root, "logs", $"run-{_stamp}.errors.log");
        ItemsPath = Path.Combine(_root, "logs", $"items-{_stamp}.csv");
    }

    /// <summary>Raised for every line (after redaction), for a UI to display. May be raised from any thread.</summary>
    public event Action<LogLevel, string>? Entry;

    public string FullLogPath { get; }
    /// <summary>One row per item the copy handled (copied, skipped or failed): folder position, opaque item id, result. No subjects, addresses or names.</summary>
    public string ItemsPath { get; }
    /// <summary>Which mailbox of a batch the following item rows belong to ("3"); empty for a single copy.</summary>
    public string Context { get; set; } = "";
    private StreamWriter? _items;
    public string ErrorLogPath { get; }
    public int Warnings => _warnings;
    public int Errors => _errors;

    public void Hide(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v) && v.Length > 3) lock (_lock) _hidden.Add(v);
    }

    public string Clean(string text)
    {
        lock (_lock) foreach (var h in _hidden) text = text.Replace(h, "<hidden>", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    /// <summary>Removes folder names from request URLs (they appear in $filter values).</summary>
    public static string RedactUrl(string url) => FilterValue.Replace(url, "eq '<name>'");

    public void Debug(string message) => Write(LogLevel.Debug, message, null);
    public void Info(string message) => Write(LogLevel.Info, message, null);
    public void Warn(string message) => Write(LogLevel.Warn, message, null);
    public void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);

    private void Write(LogLevel level, string message, Exception? ex)
    {
        var text = Clean(message);
        var detail = ex == null ? "" : Clean(ex.ToString());
        var now = DateTime.Now;
        var tag = level switch { LogLevel.Debug => "DEBUG", LogLevel.Info => "INFO ", LogLevel.Warn => "WARN ", _ => "ERROR" };
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} +{(now - _started).TotalSeconds,8:0.0}s {tag} [t{Environment.CurrentManagedThreadId}] {text}";

        lock (_lock)
        {
            if (level == LogLevel.Warn) _warnings++;
            if (level == LogLevel.Error) _errors++;
            if (level >= LogLevel.Info) _report.AppendLine(level == LogLevel.Info ? text : $"{tag.Trim()}: {text}");

            if (level >= LogLevel.Info || _verbose)
            {
                var old = Console.ForegroundColor;
                if (level == LogLevel.Warn) Console.ForegroundColor = ConsoleColor.Yellow;
                else if (level == LogLevel.Error) Console.ForegroundColor = ConsoleColor.Red;
                else if (level == LogLevel.Debug) Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(level >= LogLevel.Warn ? $"{tag.Trim()}: {text}" : text);
                if (detail != "") Console.WriteLine(detail);
                Console.ForegroundColor = old;
            }
            try
            {
                File.AppendAllText(FullLogPath, line + Environment.NewLine + (detail == "" ? "" : detail + Environment.NewLine));
                if (level >= LogLevel.Warn) File.AppendAllText(ErrorLogPath, line + Environment.NewLine + (detail == "" ? "" : detail + Environment.NewLine));
            }
            catch { /* logging must never stop a copy */ }
        }
        try { Entry?.Invoke(level, detail == "" ? text : text + Environment.NewLine + detail); } catch { /* a UI problem must never stop a copy */ }
    }

    /// <summary>Appends one row to the item list. Cheap (one small write), and flushed at once so a crash loses nothing.</summary>
    public void Item(string folder, string itemId, string result, string detail = "")
    {
        lock (_lock)
        {
            try
            {
                if (_items == null)
                {
                    var isNew = !File.Exists(ItemsPath) || new FileInfo(ItemsPath).Length == 0;   // decide before the writer exists: switching AutoFlush on writes the encoding marker at once
                    _items = new StreamWriter(new FileStream(ItemsPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(true)) { AutoFlush = true };
                    if (isNew) _items.WriteLine("time,mailbox,folder,item id,result,detail");
                }
                string Q(string v) => "\"" + Clean(v).Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
                _items.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{Context},{folder},{itemId},{result},{Q(detail.Length > 300 ? detail[..300] : detail)}");
            }
            catch { /* the item list must never stop a copy */ }
        }
    }

    public string SaveReport()
    {
        lock (_lock) { _items?.Dispose(); _items = null; }   // release the item list so it can be opened freely
        var path = Path.Combine(_root, "reports", $"copy-{_stamp}.md");
        lock (_lock) File.WriteAllText(path, _report.ToString());
        return path;
    }
}
