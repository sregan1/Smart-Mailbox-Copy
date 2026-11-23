using System.Security.Cryptography;
using System.Text;

namespace SmartMailboxCopy;

/// <summary>
/// Remembers which source items were already imported so an interrupted copy can resume without duplicating mail.
/// One line per imported source item id, appended right after each successful import.
/// Known limit: if the process is killed between an import and its journal line, that one item is imported again on resume.
/// </summary>
internal sealed class Journal
{
    private readonly string _path;
    private readonly HashSet<string> _done = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public Journal(string sourceUpn, string sourceFolder, string targetUpn, string targetFolder, bool fresh)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartMailboxCopy", "journals");
        Directory.CreateDirectory(dir);
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{sourceUpn}|{sourceFolder}|{targetUpn}|{targetFolder}".ToLowerInvariant())));
        _path = Path.Combine(dir, key + ".jsonl");
        if (fresh && File.Exists(_path)) File.Delete(_path);
        if (File.Exists(_path))
            foreach (var line in File.ReadAllLines(_path))
                if (line.Length > 0) _done.Add(line);
    }

    public int Count { get { lock (_lock) return _done.Count; } }
    public bool IsDone(string sourceItemId) { lock (_lock) return _done.Contains(sourceItemId); }

    public void MarkDone(string sourceItemId)
    {
        lock (_lock)
        {
            if (_done.Add(sourceItemId)) File.AppendAllText(_path, sourceItemId + Environment.NewLine);
        }
    }
}
