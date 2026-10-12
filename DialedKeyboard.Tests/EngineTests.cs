using System.Text;
using DialedKeyboard.Engine;

namespace DialedKeyboard.Tests;

/// <summary>Stands in for a text box: applies what the keyboard sends.</summary>
sealed class FakeOutput : ITextOutput
{
    public readonly StringBuilder Doc = new();
    public readonly List<string> Log = new();
    public void Text(string text) { Doc.Append(text); Log.Add("T:" + text); }
    public void Backspace(int count) { Doc.Length = Math.Max(0, Doc.Length - count); Log.Add("B:" + count); }
    public void Enter() { Doc.Append('\n'); Log.Add("Enter"); }
}

public class EngineTests
{
    static readonly (string, long)[] Words =
    {
        ("hello", 1000), ("help", 900), ("helmet", 50), ("the", 5000), ("you", 4000),
        ("tomato", 100), ("tomorrow", 60), ("see", 800), ("I", 3000), ("don't", 2000), ("done", 500),
    };

    static (TypingController typing, FakeOutput output, UserDictionary user, WordPredictor predictor) Create()
    {
        var user = new UserDictionary();
        var predictor = new WordPredictor(Words, user);
        var expander = new Expander();
        expander.Set(new[] { ("brb", "be right back") });
        var output = new FakeOutput();
        return (new TypingController(output, predictor, user, expander), output, user, predictor);
    }

    static void Type(TypingController t, string s)
    {
        foreach (char c in s)
        {
            if (c == ' ') t.Space();
            else t.TypeKey(char.ToLowerInvariant(c), null);
        }
    }

    [Fact]
    public void AcceptingCompletion_TypesOnlyMissingLetters()
    {
        var (t, o, _, _) = Create();
        t.Reset(sentenceStart: false);
        Type(t, "hel");
        var s = t.GetSuggestions(6);
        Assert.Equal("hello", s[0].Text);
        o.Log.Clear();
        t.Accept(s[0]);
        Assert.Equal("hello ", o.Doc.ToString());
        Assert.Equal(new[] { "T:lo", "T: " }, o.Log);
    }

    [Fact]
    public void FirstLetterOfSentence_IsCapitalized()
    {
        var (t, o, _, _) = Create();
        Type(t, "hi. ok");
        Assert.Equal("Hi. Ok", o.Doc.ToString());
    }

    [Fact]
    public void PunctuationAfterAutoSpace_PullsSpaceBack()
    {
        var (t, o, _, _) = Create();
        t.Reset();
        Type(t, "hel");
        t.Accept(t.GetSuggestions(6)[0]);
        t.TypeKey(',', '<');
        Assert.Equal("hello, ", o.Doc.ToString());
    }

    [Fact]
    public void DoubleSpace_TypesPeriod()
    {
        var (t, o, _, _) = Create();
        t.Reset();
        Type(t, "ok  so");
        Assert.Equal("ok. So", o.Doc.ToString());
    }

    [Fact]
    public void Shortcut_ExpandsOnSpace_AndIsOfferedFirst()
    {
        var (t, o, _, _) = Create();
        t.Reset();
        Type(t, "brb");
        var first = t.GetSuggestions(6)[0];
        Assert.True(first.IsExpansion);
        Assert.Equal("be right back", first.Text);
        t.Space();
        Assert.Equal("be right back ", o.Doc.ToString());
    }

    [Fact]
    public void ApostropheFreeTyping_FindsContraction()
    {
        var (t, o, _, _) = Create();
        t.Reset();
        Type(t, "dont");
        var s = t.GetSuggestions(6);
        Assert.Equal("don't", s[0].Text);
        t.Accept(s[0]);
        Assert.Equal("don't ", o.Doc.ToString());
    }

    [Fact]
    public void LearnedWords_RankHigher()
    {
        var (_, _, user, predictor) = Create();
        Assert.Equal("tomato", predictor.Complete("tom", null, 5)[0]);
        user.Learn(null, "tomorrow", false);
        user.Learn(null, "tomorrow", false);
        Assert.Equal("tomorrow", predictor.Complete("tom", null, 5)[0]);
    }

    [Fact]
    public void NextWord_UsesLearnedPairs()
    {
        var (t, _, _, _) = Create();
        t.Reset();
        Type(t, "see you see ");
        Assert.Equal("you", t.GetSuggestions(6)[0].Text);
    }

    [Fact]
    public void NewWords_SuggestedAfterTwoUses()
    {
        var (_, _, user, predictor) = Create();
        user.Learn(null, "Corvane", false);
        Assert.DoesNotContain("Corvane", predictor.Complete("cor", null, 5));
        user.Learn(null, "Corvane", false);
        Assert.Contains("Corvane", predictor.Complete("cor", null, 5));
    }

    [Fact]
    public void DeleteWord_RemovesWordAndTrailingSpace()
    {
        var (t, o, _, _) = Create();
        t.Reset();
        Type(t, "see you ");
        Assert.True(t.DeleteWord());
        Assert.Equal("see ", o.Doc.ToString());
        Assert.Equal("see ", t.Buffer);
    }

    [Fact]
    public void BackspaceIntoPreviousWord_ResumesCompletion()
    {
        var (t, _, _, _) = Create();
        t.Reset();
        Type(t, "hel ");
        t.Backspace();
        Assert.Equal("hel", t.CurrentWord);
        Assert.Equal("hello", t.GetSuggestions(6)[0].Text);
    }

    [Fact]
    public void ParseFrequencyList_DropsFragmentsAndAddsContractions()
    {
        var input = new StringReader("you 100\ni 90\ndon 80\nt 70\nhello 60\nab1 5\n");
        var words = WordPredictor.ParseFrequencyList(input).Select(w => w.Word).ToList();
        Assert.Contains("you", words);
        Assert.Contains("I", words);
        Assert.Contains("hello", words);
        Assert.Contains("don't", words);
        Assert.DoesNotContain("don", words);
        Assert.DoesNotContain("t", words);
        Assert.DoesNotContain("ab1", words);
    }

    [Fact]
    public void RepeatedPhrase_IsOfferedWhole()
    {
        var (t, o, _, _) = Create();
        for (int i = 0; i < 3; i++)
        {
            t.Reset(sentenceStart: true);
            Type(t, "see you tomorrow ");
        }
        t.Reset(sentenceStart: true);
        o.Doc.Clear();
        Type(t, "see ");
        var phrase = t.GetSuggestions(6).First(s => s.IsPhrase);
        Assert.Equal("you tomorrow", phrase.Text);
        t.Accept(phrase);
        Assert.Equal("See you tomorrow ", o.Doc.ToString());
    }

    [Fact]
    public void PhraseFromPartialWord_CompletesAndContinues()
    {
        var (t, o, _, _) = Create();
        for (int i = 0; i < 3; i++)
        {
            t.Reset();
            Type(t, "see you tomorrow ");
        }
        t.Reset();
        o.Doc.Clear();
        Type(t, "see yo");
        var phrase = t.GetSuggestions(6).First(s => s.IsPhrase);
        Assert.Equal("you tomorrow", phrase.Text);
    }

    [Fact]
    public void NoPhrase_WithoutAHabit()
    {
        var (t, _, _, _) = Create();
        t.Reset();
        Type(t, "see you tomorrow ");
        t.Reset();
        Type(t, "see ");
        Assert.DoesNotContain(t.GetSuggestions(6), s => s.IsPhrase);
    }

    [Fact]
    public void TwoWordContext_BeatsOneWord()
    {
        var (_, _, user, predictor) = Create();
        // After "see", "you" is usual; but after "I see", it's always "the".
        for (int i = 0; i < 5; i++) user.Learn("x", "see", "you", false);
        for (int i = 0; i < 3; i++) user.Learn("I", "see", "the", false);
        Assert.Equal("you", predictor.Next(null, "see", 3)[0]);
        Assert.Equal("the", predictor.Next("I", "see", 3)[0]);
    }

    [Fact]
    public void PasswordsAndCodes_AreNeverLearned()
    {
        var (t, _, user, _) = Create();
        t.Reset();
        Type(t, "hunter2 abc123 ");
        Assert.Empty(user.Words);
    }

    [Fact]
    public void Paused_LearnsNothing()
    {
        var (t, _, user, _) = Create();
        user.Paused = true;
        t.Reset();
        Type(t, "see you ");
        Assert.Empty(user.Words);
    }

    [Fact]
    public void Forget_RemovesWordAndItsPairs()
    {
        var (_, _, user, _) = Create();
        user.Learn("I", "see", "you", false);
        user.Forget("see");
        Assert.Equal(0, user.Count("see"));
        Assert.Equal(0, user.Bigram("I", "see"));
        Assert.Null(user.BigramNexts("see"));
        Assert.Null(user.TrigramNexts("I", "see"));
        Assert.Equal(1, user.Count("you"));
    }

    [Fact]
    public void UserDictionary_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dk-test-{Guid.NewGuid():N}.json");
        try
        {
            var user = new UserDictionary();
            user.Learn("I", "see", "you", false);
            user.Save(path);
            var loaded = UserDictionary.Load(path);
            Assert.Equal(1, loaded.Count("you"));
            Assert.Equal(1, loaded.Bigram("see", "you"));
            Assert.Equal(1, loaded.Trigram("I", "see", "you"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class ClipboardHistoryTests
{
    [Fact]
    public void NewestFirst_DuplicatesMoveToTop_CappedAtMax()
    {
        var h = new ClipboardHistory();
        h.Add("one"); h.Add("two"); h.Add("one");
        Assert.Equal(new[] { "one", "two" }, h.Items);

        for (int i = 0; i < ClipboardHistory.MaxItems + 5; i++) h.Add("item " + i);
        Assert.Equal(ClipboardHistory.MaxItems, h.Items.Count);
        Assert.Equal("item " + (ClipboardHistory.MaxItems + 4), h.Items[0]);
    }

    [Fact]
    public void SkipsBlankAndHugeText()
    {
        var h = new ClipboardHistory();
        h.Add("  \n "); h.Add(null); h.Add(new string('x', ClipboardHistory.MaxLength + 1));
        Assert.Empty(h.Items);
    }

    [Fact]
    public void PreviewFlattensLines()
    {
        Assert.Equal("first ↵ second", ClipboardHistory.Preview("  first\r\n\r\n  second  "));
        Assert.EndsWith("…", ClipboardHistory.Preview(new string('a', 300), 50));
    }
}
