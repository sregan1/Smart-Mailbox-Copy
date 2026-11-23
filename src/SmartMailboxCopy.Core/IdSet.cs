namespace SmartMailboxCopy;

/// <summary>Thread-safe set of ids, used by the request guards to recognize "our" folders and items.</summary>
internal sealed class IdSet
{
    private readonly HashSet<string> _items = new(StringComparer.Ordinal);   // Graph ids are case-sensitive

    public void Add(string id) { lock (_items) _items.Add(id); }
    public bool Has(string id) { lock (_items) return _items.Contains(id); }
    public bool InUrl(string url) { lock (_items) return _items.Any(id => url.Contains(id)); }
    public int Count { get { lock (_items) return _items.Count; } }
}
