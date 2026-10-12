using System.Text.Json;

namespace DialedKeyboard.Engine;

/// <summary>
/// The keyboard's memory: words you type, which word follows which (pairs), and which
/// word follows a two-word context (triples). Triples are what let it recall whole phrases.
/// </summary>
public sealed class UserDictionary
{
    public sealed class WordEntry
    {
        public int Count { get; set; }
        /// <summary>Preferred casing (e.g. a name), or null to use the default.</summary>
        public string? Display { get; set; }
    }

    public Dictionary<string, WordEntry> Words { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, int>> Bigrams { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Keyed by "word1 word2" → next word → count.</summary>
    public Dictionary<string, Dictionary<string, int>> Trigrams { get; set; } = new(StringComparer.Ordinal);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Dirty { get; private set; }

    /// <summary>When true, nothing new is remembered.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Paused { get; set; }

    public int Count(string key) => Words.TryGetValue(key, out var e) ? e.Count : 0;

    public string? Display(string key) => Words.TryGetValue(key, out var e) ? e.Display : null;

    public int Bigram(string? prev, string key) =>
        prev != null && Bigrams.TryGetValue(prev.ToLowerInvariant(), out var next) && next.TryGetValue(key, out var n) ? n : 0;

    public int Trigram(string? prev2, string? prev, string key) =>
        TrigramNexts(prev2, prev) is { } next && next.TryGetValue(key, out var n) ? n : 0;

    public Dictionary<string, int>? BigramNexts(string? prev) =>
        prev != null && Bigrams.TryGetValue(prev.ToLowerInvariant(), out var next) ? next : null;

    public Dictionary<string, int>? TrigramNexts(string? prev2, string? prev) =>
        prev2 != null && prev != null && Trigrams.TryGetValue(TrigramKey(prev2, prev), out var next) ? next : null;

    static string TrigramKey(string prev2, string prev) => prev2.ToLowerInvariant() + " " + prev.ToLowerInvariant();

    /// <summary>
    /// Only plain words are remembered. Anything with digits or symbols could be a
    /// password or code and must not end up in a file on disk.
    /// </summary>
    public static bool IsLearnable(string word) =>
        word.Length is > 0 and <= 25 && word.Any(char.IsLetter) && word.All(c => char.IsLetter(c) || c == '\'');

    public void Learn(string? prev, string word, bool sentenceStart) => Learn(null, prev, word, sentenceStart);

    public void Learn(string? prev2, string? prev, string word, bool sentenceStart)
    {
        if (Paused) return;
        word = word.Trim('\'');
        if (!IsLearnable(word)) return;

        string key = word.ToLowerInvariant();
        if (!Words.TryGetValue(key, out var entry))
            Words[key] = entry = new WordEntry();
        entry.Count++;
        // Casing typed mid-sentence reflects how the word is really written ("Corvane", "iPhone").
        if (!sentenceStart)
            entry.Display = word == key ? null : word;

        if (prev != null)
        {
            Increment(Bigrams, prev.ToLowerInvariant(), key);
            if (prev2 != null)
                Increment(Trigrams, TrigramKey(prev2, prev), key);
        }
        Dirty = true;
    }

    static void Increment(Dictionary<string, Dictionary<string, int>> table, string context, string key)
    {
        if (!table.TryGetValue(context, out var next))
            table[context] = next = new Dictionary<string, int>(StringComparer.Ordinal);
        next[key] = next.GetValueOrDefault(key) + 1;
    }

    /// <summary>Removes a word and every pair/phrase it appears in.</summary>
    public void Forget(string word)
    {
        string key = word.ToLowerInvariant();
        Words.Remove(key);
        Bigrams.Remove(key);
        foreach (var next in Bigrams.Values) next.Remove(key);
        foreach (var ctx in Trigrams.Keys.Where(k => k.Split(' ').Contains(key)).ToList())
            Trigrams.Remove(ctx);
        foreach (var next in Trigrams.Values) next.Remove(key);
        RemoveEmpty(Bigrams);
        RemoveEmpty(Trigrams);
        Dirty = true;
    }

    public void Clear()
    {
        Words.Clear();
        Bigrams.Clear();
        Trigrams.Clear();
        Dirty = true;
    }

    static void RemoveEmpty(Dictionary<string, Dictionary<string, int>> table)
    {
        foreach (var k in table.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList())
            table.Remove(k);
    }

    public static UserDictionary Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<UserDictionary>(File.ReadAllText(path)) ?? new UserDictionary();
        }
        catch (Exception)
        {
            // A corrupt file shouldn't stop the keyboard from starting; keep a copy and start fresh.
            try { File.Copy(path, path + ".bad", overwrite: true); } catch (IOException) { }
        }
        return new UserDictionary();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this));
        File.Move(tmp, path, overwrite: true);
        Dirty = false;
    }
}
