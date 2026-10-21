using System.Text;

namespace DialedKeyboard.Engine;

/// <summary>Where typed text goes (the real keyboard injector, or a fake in tests).</summary>
public interface ITextOutput
{
    void Text(string text);
    void Backspace(int count);
    void Enter();
}

/// <param name="IsPhrase">A multi-word phrase recalled from your typing history.</param>
public sealed record Suggestion(string Text, bool IsExpansion, bool IsPhrase = false);

public sealed class TypingOptions
{
    public bool AutoCapitalize { get; set; } = true;
    public bool DoubleSpacePeriod { get; set; } = true;
    /// <summary>Punctuation right after an automatic space pulls the space back: "word ." → "word. ".</summary>
    public bool SmartPunctuation { get; set; } = true;
    public bool AutoExpandOnSpace { get; set; } = true;
    /// <summary>Also offer whole phrases you type often; off means suggestions are one word at a time.</summary>
    public bool SuggestPhrases { get; set; }
    /// <summary>In password boxes: learn nothing, suggest nothing and change nothing that's typed.</summary>
    public bool ProtectPasswords { get; set; } = true;
}

/// <summary>
/// Turns key clicks into text and keeps a mirror of what it has typed, since Windows
/// can't reliably tell us the text around another app's caret. The mirror drives
/// predictions, auto-capitalisation and smart spacing, and is cleared by <see cref="Reset"/>
/// whenever the caret may have moved (click elsewhere, arrow keys, shortcuts).
/// </summary>
public sealed class TypingController
{
    const int MaxBuffer = 400;
    const int TrimTo = 200;

    readonly ITextOutput _out;
    readonly WordPredictor _predictor;
    readonly UserDictionary _user;
    readonly Expander _expander;
    readonly StringBuilder _buf = new();
    SwipeDecoder? _swipe;
    bool _startIsSentence = true;
    bool _softSpace; // the last char is a space we added after a word

    public TypingController(ITextOutput output, WordPredictor predictor, UserDictionary user, Expander expander)
    {
        _out = output;
        _predictor = predictor;
        _user = user;
        _expander = expander;
    }

    public TypingOptions Options { get; set; } = new();
    public bool ShiftOnce { get; set; }
    public bool CapsLock { get; set; }

    /// <summary>Focus is in a password box (as far as Windows can tell).</summary>
    public bool InPasswordField
    {
        get => _inPasswordField;
        set
        {
            if (_inPasswordField == value) return;
            _inPasswordField = value;
            Changed();
        }
    }
    bool _inPasswordField;

    /// <summary>True while typing is treated as a password: nothing learned, suggested or corrected.</summary>
    public bool Private => Options.ProtectPasswords && _inPasswordField;

    /// <summary>Raised after anything that may change suggestions or key labels.</summary>
    public event Action? StateChanged;

    /// <summary>The text this keyboard has typed since the last reset.</summary>
    public string Buffer => _buf.ToString();

    public string CurrentWord
    {
        get
        {
            int j = _buf.Length;
            while (j > 0 && IsWordChar(_buf[j - 1])) j--;
            return _buf.ToString(j, _buf.Length - j);
        }
    }

    /// <summary>True when the next letter would be capitalised automatically.</summary>
    public bool AutoCapNow =>
        Options.AutoCapitalize
        && !Private
        && CurrentWord.Length == 0
        && (_buf.Length == 0 || _buf[^1] is ' ' or '\n')
        && WordBefore(_buf.Length) == WordPredictor.SentenceStart;

    public bool UpperNext => CapsLock || ShiftOnce || AutoCapNow;

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '\'';

    static bool IsAttachingPunct(char c) => c is '.' or ',' or '!' or '?' or ';' or ':';

    /// <summary>A key with a normal and a shifted character was clicked.</summary>
    public void TypeKey(char baseChar, char? shiftChar)
    {
        char c = baseChar;
        if (char.IsLetter(baseChar))
        {
            if (UpperNext) c = char.ToUpperInvariant(baseChar);
        }
        else if (ShiftOnce && shiftChar is char s)
        {
            c = s;
        }
        ShiftOnce = false;
        TypeChar(c);
    }

    public void TypeChar(char c)
    {
        if (IsWordChar(c))
        {
            Emit(c.ToString());
            _softSpace = false;
        }
        else if (IsAttachingPunct(c))
        {
            LearnCurrentWord();
            if (Options.SmartPunctuation && !Private && _softSpace && _buf.Length > 0 && _buf[^1] == ' ')
            {
                Back(1);
                Emit(c + " ");
                _softSpace = true;
            }
            else
            {
                Emit(c.ToString());
                _softSpace = false;
            }
        }
        else
        {
            LearnCurrentWord();
            Emit(c.ToString());
            _softSpace = false;
        }
        Changed();
    }

    public void Space()
    {
        ShiftOnce = false;
        string word = CurrentWord;
        if (word.Length == 0)
        {
            if (Options.DoubleSpacePeriod && !Private && _softSpace && _buf.Length >= 2 && _buf[^1] == ' ' && IsWordChar(_buf[^2]))
            {
                Back(1);
                Emit(". ");
                _softSpace = true;
            }
            else
            {
                Emit(" ");
                _softSpace = false;
            }
            Changed();
            return;
        }

        if (Options.AutoExpandOnSpace && !Private && _expander.TryGet(word, out var expansion))
        {
            Back(word.Length);
            Emit(expansion);
        }
        else
        {
            LearnCurrentWord();
        }
        Emit(" ");
        _softSpace = true;
        Changed();
    }

    public void Backspace()
    {
        ShiftOnce = false;
        Back(1);
        _softSpace = false;
        Changed();
    }

    /// <summary>Deletes the previous word (and the spaces after it). Returns false if nothing is known to delete.</summary>
    public bool DeleteWord()
    {
        if (_buf.Length == 0) return false;
        int i = _buf.Length;
        while (i > 0 && _buf[i - 1] == ' ') i--;
        if (i > 0 && IsWordChar(_buf[i - 1]))
            while (i > 0 && IsWordChar(_buf[i - 1])) i--;
        else
            while (i > 0 && _buf[i - 1] != ' ' && !IsWordChar(_buf[i - 1])) i--;
        Back(_buf.Length - i);
        _softSpace = false;
        Changed();
        return true;
    }

    public void Enter()
    {
        ShiftOnce = false;
        // One word typed on its own and sent with Enter is how a password gets typed, in apps
        // that don't say their box is a password box. Don't keep it.
        if (!(Options.ProtectPasswords && CurrentWord.Length == _buf.Length))
            LearnCurrentWord();
        _out.Enter();
        Reset(sentenceStart: true);
    }

    /// <summary>Inserts a suggestion, replacing the partly typed word, and adds a space.</summary>
    public void Accept(Suggestion s)
    {
        ShiftOnce = false;
        string word = CurrentWord;
        if (s.IsExpansion)
        {
            Back(word.Length);
            Emit(s.Text);
        }
        else if (word.Length > 0)
        {
            var (prev2, prev) = ContextBefore(_buf.Length - word.Length);
            string text = MatchCase(word, s.Text);
            if (text.StartsWith(word, StringComparison.Ordinal))
            {
                Emit(text[word.Length..]); // only type the missing letters
            }
            else
            {
                Back(word.Length);
                Emit(text);
            }
            LearnWords(prev2, prev, text);
        }
        else
        {
            var (prev2, prev) = ContextBefore(_buf.Length);
            string text = AutoCapNow ? Capitalize(s.Text) : s.Text;
            if (_buf.Length > 0 && !char.IsWhiteSpace(_buf[^1]) && "([{\"'".IndexOf(_buf[^1]) < 0)
                Emit(" ");
            Emit(text);
            LearnWords(prev2, prev, text);
        }
        Emit(" ");
        _softSpace = true;
        Changed();
    }

    /// <summary>
    /// Stops offering a suggestion: a word is forgotten and never suggested again (until you type
    /// it yourself), a recalled phrase stops being recalled. Shortcuts can't be forgotten here.
    /// </summary>
    public bool Forget(Suggestion s)
    {
        if (s.IsExpansion) return false;
        if (s.IsPhrase) _user.ForgetPhrase(s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        else _user.Block(s.Text);
        Changed();
        return true;
    }

    /// <summary>Likely words for a swipe across the letter keys, best first, using the words before the caret.</summary>
    public List<string> DecodeSwipe(IReadOnlyList<SwipePoint> path, IReadOnlyDictionary<char, SwipePoint> keys, double keyWidth, int max)
    {
        _swipe ??= new SwipeDecoder(_predictor);
        string word = CurrentWord;
        // A half-typed word gets finished with a space before the swiped one, so it becomes the context.
        var (prev2, prev) = word.Length > 0
            ? (WordBefore(_buf.Length - word.Length), word)
            : ContextBefore(_buf.Length);
        return _swipe.Decode(path, keys, keyWidth, prev2, prev, max);
    }

    /// <summary>
    /// Types a swiped word as a whole word, the way phone keyboards do: a space before it if
    /// needed, capitalised like a typed word, and a space after (punctuation pulls that back).
    /// Returns the text as typed, for <see cref="ReplaceSwipedWord"/>.
    /// </summary>
    public string TypeSwipedWord(string word)
    {
        if (CurrentWord.Length > 0)
            Space();
        if (_buf.Length > 0 && !char.IsWhiteSpace(_buf[^1]) && "([{\"'".IndexOf(_buf[^1]) < 0)
            Emit(" ");
        string text = CapsLock ? word.ToUpperInvariant()
            : ShiftOnce || AutoCapNow ? Capitalize(word)
            : word;
        ShiftOnce = false;
        var (prev2, prev) = ContextBefore(_buf.Length);
        Emit(text);
        LearnWords(prev2, prev, text);
        Emit(" ");
        _softSpace = true;
        Changed();
        return text;
    }

    /// <summary>
    /// Swaps the word just swiped (<paramref name="typed"/>, still followed by its space) for
    /// another candidate. Returns the new text, or null if the text has moved on since.
    /// </summary>
    public string? ReplaceSwipedWord(string typed, string replacement)
    {
        if (!SwipedWordIsLast(typed)) return null;
        Back(typed.Length + 1);
        string text = MatchCase(typed, replacement);
        var (prev2, prev) = ContextBefore(_buf.Length);
        Emit(text);
        LearnWords(prev2, prev, text);
        Emit(" ");
        _softSpace = true;
        Changed();
        return text;
    }

    /// <summary>True while <paramref name="typed"/> plus its space is still the last thing typed.</summary>
    public bool SwipedWordIsLast(string typed)
    {
        int n = typed.Length + 1;
        return _buf.Length >= n && _buf[^1] == ' ' && _buf.ToString(_buf.Length - n, typed.Length) == typed;
    }

    /// <summary>Forget the mirrored text because the caret may have moved.</summary>
    public void Reset(bool sentenceStart = false)
    {
        _buf.Clear();
        _startIsSentence = sentenceStart;
        _softSpace = false;
        Changed();
    }

    public List<Suggestion> GetSuggestions(int max)
    {
        var result = new List<Suggestion>(max);
        if (Private) return result;
        string word = CurrentWord;
        if (word.Length > 0)
        {
            if (_expander.TryGet(word, out var expansion))
                result.Add(new Suggestion(expansion, true));
            var (prev2, prev) = ContextBefore(_buf.Length - word.Length);
            var completions = _predictor.Complete(word, prev2, prev, max);
            if (Options.SuggestPhrases && completions.Count > 0 && _predictor.PredictPhrase(prev2, prev, completions[0]) is { } phrase)
                result.Add(new Suggestion(MatchCase(word, phrase), false, IsPhrase: true));
            foreach (var w in completions)
            {
                if (result.Count >= max) break;
                result.Add(new Suggestion(MatchCase(word, w), false));
            }
        }
        else
        {
            bool cap = AutoCapNow;
            var (prev2, prev) = ContextBefore(_buf.Length);
            if (Options.SuggestPhrases && _predictor.PredictPhrase(prev2, prev, null) is { } phrase)
                result.Add(new Suggestion(cap ? Capitalize(phrase) : phrase, false, IsPhrase: true));
            foreach (var w in _predictor.Next(prev2, prev, max))
            {
                if (result.Count >= max) break;
                result.Add(new Suggestion(cap ? Capitalize(w) : w, false));
            }
        }
        return result;
    }

    /// <summary>The word ending at <paramref name="end"/> (skipping spaces), <see cref="WordPredictor.SentenceStart"/>, or null.</summary>
    string? WordBefore(int end) => WordBefore(end, out _);

    string? WordBefore(int end, out int start)
    {
        int i = end;
        while (i > 0 && _buf[i - 1] == ' ') i--;
        start = i;
        if (i == 0) return _startIsSentence ? WordPredictor.SentenceStart : null;
        char c = _buf[i - 1];
        if (c is '.' or '!' or '?' or '\n') return WordPredictor.SentenceStart;
        if (!IsWordChar(c)) return null;
        int j = i;
        while (j > 0 && IsWordChar(_buf[j - 1])) j--;
        start = j;
        return _buf.ToString(j, i - j);
    }

    /// <summary>The two words before <paramref name="end"/>, nearest last: (word before that, previous word).</summary>
    (string? Prev2, string? Prev) ContextBefore(int end)
    {
        string? prev = WordBefore(end, out int start);
        if (prev == null || prev == WordPredictor.SentenceStart) return (null, prev);
        return (WordBefore(start), prev);
    }

    void LearnCurrentWord()
    {
        string word = CurrentWord;
        if (word.Length == 0 || Private) return;
        var (prev2, prev) = ContextBefore(_buf.Length - word.Length);
        _user.Learn(prev2, prev, word, prev == WordPredictor.SentenceStart);
    }

    /// <summary>Learns one word or every word of an accepted phrase, in order.</summary>
    void LearnWords(string? prev2, string? prev, string text)
    {
        if (Private) return;
        foreach (var w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            _user.Learn(prev2, prev, w, prev == WordPredictor.SentenceStart);
            (prev2, prev) = (prev, w);
        }
    }

    static string MatchCase(string typed, string word)
    {
        if (typed.Length > 1 && typed.Any(char.IsLetter) && !typed.Any(char.IsLower))
            return word.ToUpperInvariant();
        return char.IsUpper(typed[0]) ? Capitalize(word) : word;
    }

    static string Capitalize(string s) =>
        s.Length == 0 || char.IsUpper(s[0]) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    void Emit(string text)
    {
        if (text.Length == 0) return;
        _out.Text(text);
        _buf.Append(text);
        if (_buf.Length > MaxBuffer)
        {
            _buf.Remove(0, _buf.Length - TrimTo);
            _startIsSentence = false;
        }
    }

    void Back(int count)
    {
        if (count <= 0) return;
        _out.Backspace(count);
        _buf.Length = Math.Max(0, _buf.Length - count);
    }

    void Changed() => StateChanged?.Invoke();
}
