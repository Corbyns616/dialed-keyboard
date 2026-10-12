namespace DialedKeyboard.Engine;

/// <summary>Abbreviation → phrase shortcuts, e.g. "brb" → "be right back".</summary>
public sealed class Expander
{
    Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public void Set(IEnumerable<(string Abbreviation, string Expansion)> items)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (abbr, exp) in items)
        {
            if (!string.IsNullOrWhiteSpace(abbr) && !string.IsNullOrEmpty(exp))
                map[abbr.Trim()] = exp;
        }
        _map = map;
    }

    public bool TryGet(string abbreviation, out string expansion) =>
        _map.TryGetValue(abbreviation, out expansion!);
}
