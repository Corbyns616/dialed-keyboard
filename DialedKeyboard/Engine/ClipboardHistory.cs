namespace DialedKeyboard.Engine;

/// <summary>
/// The last few things copied, newest first. Kept in memory only (never written to disk),
/// since a clipboard can hold anything.
/// </summary>
public sealed class ClipboardHistory
{
    public const int MaxItems = 30;
    /// <summary>Longer copies are skipped; pasting them back is what Ctrl+V is for.</summary>
    public const int MaxLength = 20_000;

    readonly List<string> _items = new();

    public IReadOnlyList<string> Items => _items;

    public event Action? Changed;

    /// <summary>Adds text to the top; copying something already in the list moves it back to the top.</summary>
    public void Add(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength) return;
        if (_items.Count > 0 && _items[0] == text) return;
        _items.Remove(text);
        _items.Insert(0, text);
        if (_items.Count > MaxItems) _items.RemoveRange(MaxItems, _items.Count - MaxItems);
        Changed?.Invoke();
    }

    public void Remove(string text)
    {
        if (_items.Remove(text)) Changed?.Invoke();
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Changed?.Invoke();
    }

    /// <summary>A one-line-ish preview for a button: trimmed, line breaks shown as ↵, capped in length.</summary>
    public static string Preview(string text, int max = 160)
    {
        string flat = string.Join(" ↵ ", text.Trim().Split('\n').Select(l => l.Trim('\r', ' ', '\t')).Where(l => l.Length > 0));
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }
}
