using System.Reflection;
using System.Text.RegularExpressions;

namespace DialedKeyboard.Engine;

/// <summary>
/// Suggests word completions and next words by combining a base frequency list
/// with what you have typed before (<see cref="UserDictionary"/>).
/// Matching ignores apostrophes, so "dont" finds "don't" and "im" finds "I'm".
/// </summary>
public sealed class WordPredictor
{
    public const string SentenceStart = "<s>";

    const double UserWeight = 3.0;
    const double BigramWeight = 4.0;
    const double TrigramWeight = 5.0;
    const int MaxPhraseWords = 6;

    readonly Entry[] _entries;           // sorted by Search
    readonly string[] _searchKeys;       // parallel to _entries, for binary search
    readonly Dictionary<string, Entry> _byLower = new(StringComparer.Ordinal);
    readonly List<Entry> _top;           // most common words, fallback for next-word
    readonly UserDictionary _user;

    sealed record Entry(string Search, string Lower, string Display, double BaseScore);

    // The frequency list splits contractions ("don" + "t"), so add the common ones directly.
    static readonly (string Word, long Count)[] Contractions =
    {
        ("don't", 4_000_000), ("I'm", 3_500_000), ("it's", 3_000_000), ("you're", 1_200_000),
        ("that's", 1_500_000), ("can't", 1_200_000), ("I'll", 1_000_000), ("I've", 700_000),
        ("I'd", 500_000), ("won't", 450_000), ("didn't", 1_000_000), ("doesn't", 500_000),
        ("isn't", 350_000), ("aren't", 200_000), ("wasn't", 350_000), ("weren't", 100_000),
        ("he's", 800_000), ("she's", 500_000), ("we're", 700_000), ("they're", 500_000),
        ("let's", 600_000), ("what's", 700_000), ("there's", 500_000), ("couldn't", 250_000),
        ("wouldn't", 250_000), ("shouldn't", 120_000), ("haven't", 350_000), ("hasn't", 100_000),
        ("you'll", 300_000), ("we'll", 300_000), ("they'll", 100_000), ("you've", 300_000),
        ("we've", 250_000), ("they've", 100_000), ("he'll", 80_000), ("she'll", 50_000),
        ("who's", 150_000), ("where's", 120_000), ("how's", 60_000), ("here's", 120_000),
        ("you'd", 150_000), ("he'd", 80_000), ("she'd", 50_000), ("we'd", 60_000), ("they'd", 50_000),
        ("ain't", 100_000), ("y'all", 30_000),
    };

    // Fragments left over from split contractions.
    static readonly HashSet<string> Junk = new(StringComparer.Ordinal)
    {
        "don", "ll", "ve", "re", "didn", "doesn", "isn", "wasn", "couldn", "wouldn", "shouldn",
        "haven", "hasn", "aren", "weren", "ain", "mustn", "needn",
    };

    static readonly Regex WordPattern = new("^[a-z]+$", RegexOptions.Compiled);

    public WordPredictor(IEnumerable<(string Word, long Count)> words, UserDictionary user)
    {
        _user = user;
        foreach (var (word, count) in words)
        {
            string lower = word.ToLowerInvariant();
            var entry = new Entry(Strip(lower), lower, word, Math.Log(count + 1));
            if (!_byLower.TryGetValue(lower, out var existing) || existing.BaseScore < entry.BaseScore)
                _byLower[lower] = entry;
        }
        _entries = _byLower.Values.OrderBy(e => e.Search, StringComparer.Ordinal).ToArray();
        _searchKeys = _entries.Select(e => e.Search).ToArray();
        _top = _entries.OrderByDescending(e => e.BaseScore).Take(40).ToList();
    }

    /// <summary>Loads the bundled English frequency list.</summary>
    public static WordPredictor LoadDefault(UserDictionary user)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DialedKeyboard.words_en.txt")
            ?? throw new InvalidOperationException("Word list resource missing.");
        using var reader = new StreamReader(stream);
        return new WordPredictor(ParseFrequencyList(reader).ToList(), user);
    }

    /// <summary>Parses "word count" lines, dropping junk and adding contractions.</summary>
    public static IEnumerable<(string Word, long Count)> ParseFrequencyList(TextReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            int space = line.IndexOf(' ');
            if (space <= 0 || !long.TryParse(line.AsSpan(space + 1), out long count)) continue;
            string word = line[..space];
            if (!WordPattern.IsMatch(word) || Junk.Contains(word)) continue;
            if (word.Length == 1 && word != "a" && word != "i") continue;
            yield return (word == "i" ? "I" : word, count);
        }
        foreach (var c in Contractions)
            yield return c;
    }

    public static string Strip(string s) => s.Replace("'", "");

    /// <summary>Best completions of <paramref name="prefix"/>, most likely first.</summary>
    public List<string> Complete(string prefix, string? prev, int max) => Complete(prefix, null, prev, max);

    public List<string> Complete(string prefix, string? prev2, string? prev, int max)
    {
        string typedLower = prefix.ToLowerInvariant();
        string p = Strip(typedLower);
        if (p.Length == 0) return new List<string>();

        var candidates = new List<(double Score, string Text)>();
        int i = Array.BinarySearch(_searchKeys, p, StringComparer.Ordinal);
        if (i < 0) i = ~i;
        for (; i < _entries.Length && _searchKeys[i].StartsWith(p, StringComparison.Ordinal); i++)
        {
            var e = _entries[i];
            if (e.Lower == typedLower) continue; // already typed in full
            candidates.Add((Score(e.Lower, e.BaseScore, prev2, prev), _user.Display(e.Lower) ?? e.Display));
        }

        // Your own words that aren't in the dictionary (names, slang) once you've used them twice.
        foreach (var (key, entry) in _user.Words)
        {
            if (entry.Count < 2 || key == typedLower || _byLower.ContainsKey(key)) continue;
            if (!Strip(key).StartsWith(p, StringComparison.Ordinal)) continue;
            candidates.Add((Score(key, 0, prev2, prev), entry.Display ?? key));
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .Select(c => c.Text)
            .Distinct(StringComparer.Ordinal)
            .Take(max)
            .ToList();
    }

    /// <summary>Likely next words after <paramref name="prev"/> (a word or <see cref="SentenceStart"/>).</summary>
    public List<string> Next(string? prev, int max) => Next(null, prev, max);

    public List<string> Next(string? prev2, string? prev, int max)
    {
        var result = new List<string>(max);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        keys.UnionWith(_user.TrigramNexts(prev2, prev)?.Keys ?? Enumerable.Empty<string>());
        keys.UnionWith(_user.BigramNexts(prev)?.Keys ?? Enumerable.Empty<string>());
        foreach (var key in keys
            .OrderByDescending(k => TrigramWeight * Math.Log(1 + _user.Trigram(prev2, prev, k))
                                    + BigramWeight * Math.Log(1 + _user.Bigram(prev, k))
                                    + BaseScore(k) * 0.2)
            .Take(max))
        {
            result.Add(DisplayOf(key));
        }
        foreach (var e in _top)
        {
            if (result.Count >= max) break;
            string text = _user.Display(e.Lower) ?? e.Display;
            if (!result.Contains(text, StringComparer.OrdinalIgnoreCase))
                result.Add(text);
        }
        return result;
    }

    /// <summary>
    /// Recalls a phrase you type often: starting from <paramref name="firstWord"/> (or from
    /// the context when null), keeps adding the word you almost always type next.
    /// Returns null unless at least two words can be offered.
    /// </summary>
    public string? PredictPhrase(string? prev2, string? prev, string? firstWord)
    {
        var words = new List<string>();
        string? a = prev2, b = prev;
        if (firstWord != null)
        {
            words.Add(firstWord);
            (a, b) = (b, firstWord);
        }
        while (words.Count < MaxPhraseWords && StrongNext(a, b) is { } next)
        {
            words.Add(DisplayOf(next));
            (a, b) = (b, next);
        }
        int chained = words.Count - (firstWord != null ? 1 : 0);
        return words.Count >= 2 && chained >= 1 ? string.Join(' ', words) : null;
    }

    /// <summary>The next word, only if you've clearly made a habit of it after this context.</summary>
    string? StrongNext(string? prev2, string? prev)
    {
        if (Dominant(_user.TrigramNexts(prev2, prev), minCount: 2) is { } fromTriple)
            return fromTriple;
        return Dominant(_user.BigramNexts(prev), minCount: 3);
    }

    static string? Dominant(Dictionary<string, int>? nexts, int minCount)
    {
        if (nexts == null || nexts.Count == 0) return null;
        var best = nexts.MaxBy(kv => kv.Value);
        int total = nexts.Values.Sum();
        return best.Value >= minCount && best.Value * 2 > total ? best.Key : null;
    }

    /// <summary>
    /// Words swipe typing can produce: the bundled list, or (<paramref name="userOnly"/>) your
    /// own words that aren't in it, once you've used them twice.
    /// </summary>
    public IEnumerable<(string Lower, string Display)> SwipeVocabulary(bool userOnly)
    {
        if (!userOnly)
        {
            foreach (var e in _entries)
                yield return (e.Lower, _user.Display(e.Lower) ?? e.Display);
            yield break;
        }
        foreach (var (key, entry) in _user.Words)
            if (entry.Count >= 2 && !_byLower.ContainsKey(key))
                yield return (key, entry.Display ?? key);
    }

    /// <summary>How likely <paramref name="lower"/> is here, from its frequency, your history and the words before it.</summary>
    public double Likelihood(string lower, string? prev2, string? prev) => Score(lower, BaseScore(lower), prev2, prev);

    double Score(string lower, double baseScore, string? prev2, string? prev) =>
        baseScore
        + UserWeight * Math.Log(1 + _user.Count(lower))
        + BigramWeight * Math.Log(1 + _user.Bigram(prev, lower))
        + TrigramWeight * Math.Log(1 + _user.Trigram(prev2, prev, lower));

    double BaseScore(string lower) => _byLower.TryGetValue(lower, out var e) ? e.BaseScore : 0;

    string DisplayOf(string lower) =>
        _user.Display(lower) ?? (_byLower.TryGetValue(lower, out var e) ? e.Display : lower);
}
