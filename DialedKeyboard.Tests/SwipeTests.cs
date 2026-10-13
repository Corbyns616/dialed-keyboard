using DialedKeyboard.Engine;

namespace DialedKeyboard.Tests;

public class SwipeTests
{
    const double KeyWidth = 58.6;
    const double RowPitch = 73.7;

    /// <summary>Key centres laid out like the ABC page: QWERTY rows, A row in half a key, Z row after a 1.5-wide Shift.</summary>
    static readonly Dictionary<char, SwipePoint> Keys = BuildKeys();

    static Dictionary<char, SwipePoint> BuildKeys()
    {
        var keys = new Dictionary<char, SwipePoint>();
        void Row(string letters, double firstCentre, int row)
        {
            for (int i = 0; i < letters.Length; i++)
                keys[letters[i]] = new SwipePoint((firstCentre + i) * KeyWidth, row * RowPitch);
        }
        Row("qwertyuiop", 0.5, 0);
        Row("asdfghjkl", 1.0, 1);
        Row("zxcvbnm", 2.0, 2);
        return keys;
    }

    static readonly WordPredictor Predictor = WordPredictor.LoadDefault(new UserDictionary());

    /// <summary>A sloppy human swipe: straight strokes between the letters, wobbling and missing centres a little.</summary>
    static List<SwipePoint> SwipeOf(string word, double sloppiness = 0.25)
    {
        var rng = new Random(word.Sum(c => c * 31)); // fixed per word, so runs repeat
        var path = new List<SwipePoint>();
        SwipePoint Off(SwipePoint p) => new(
            p.X + (rng.NextDouble() - 0.5) * 2 * sloppiness * KeyWidth,
            p.Y + (rng.NextDouble() - 0.5) * 2 * sloppiness * KeyWidth);
        var stops = word.Select(c => Off(Keys[c])).ToList();
        path.Add(stops[0]);
        for (int i = 1; i < stops.Count; i++)
            for (int s = 1; s <= 8; s++)
            {
                double t = s / 8.0;
                path.Add(new SwipePoint(
                    stops[i - 1].X + (stops[i].X - stops[i - 1].X) * t + (rng.NextDouble() - 0.5) * 4,
                    stops[i - 1].Y + (stops[i].Y - stops[i - 1].Y) * t + (rng.NextDouble() - 0.5) * 4));
            }
        return path;
    }

    static List<string> Decode(string word, double sloppiness = 0.25) =>
        new SwipeDecoder(Predictor).Decode(SwipeOf(word, sloppiness), Keys, KeyWidth, null, null, 4);

    [Theory]
    [InlineData("hello")]
    [InlineData("the")]
    [InlineData("keyboard")]
    [InlineData("thanks")]
    [InlineData("great")]
    [InlineData("question")]
    [InlineData("tomorrow")]
    [InlineData("people")]
    [InlineData("because")]
    [InlineData("work")]
    public void SwipingAWord_GuessesIt(string word)
    {
        var guesses = Decode(word);
        Assert.Equal(word, guesses.FirstOrDefault());
    }

    [Theory]
    [InlineData("about")]
    [InlineData("really")]
    [InlineData("going")]
    public void EvenASloppySwipe_HasTheWordAmongTheChoices(string word)
    {
        Assert.Contains(word, Decode(word, sloppiness: 0.4));
    }

    [Fact]
    public void ApostrophesComeBack()
    {
        Assert.Contains("don't", Decode("dont"));
    }

    [Fact]
    public void ATap_IsNotASwipe()
    {
        var tap = new List<SwipePoint> { Keys['h'], new(Keys['h'].X + 3, Keys['h'].Y + 2) };
        Assert.Empty(new SwipeDecoder(Predictor).Decode(tap, Keys, KeyWidth, null, null, 4));
    }

    static (TypingController typing, FakeOutput output) CreateTyping()
    {
        var output = new FakeOutput();
        return (new TypingController(output, Predictor, new UserDictionary(), new Expander()), output);
    }

    [Fact]
    public void SwipedWords_AreSpacedAndCapitalisedLikeTypedOnes()
    {
        var (typing, output) = CreateTyping();
        typing.Reset(sentenceStart: true);
        typing.TypeSwipedWord("hello");
        typing.TypeSwipedWord("there");
        typing.TypeChar('.');
        typing.TypeSwipedWord("how");
        Assert.Equal("Hello there. How ", output.Doc.ToString());
    }

    [Fact]
    public void SwipeAfterHalfTypedWord_StartsANewWord()
    {
        var (typing, output) = CreateTyping();
        typing.TypeKey('o', null);
        typing.TypeKey('k', null);
        typing.TypeSwipedWord("thanks");
        Assert.Equal("Ok thanks ", output.Doc.ToString());
    }

    [Fact]
    public void PickingAnotherChoice_ReplacesTheSwipedWord()
    {
        var (typing, output) = CreateTyping();
        typing.Reset(sentenceStart: true);
        string typed = typing.TypeSwipedWord("tie");
        string? replaced = typing.ReplaceSwipedWord(typed, "the");
        Assert.Equal("The", replaced);
        Assert.Equal("The ", output.Doc.ToString());

        typing.TypeKey('x', null);
        Assert.Null(typing.ReplaceSwipedWord("The", "tie")); // moved on: leave the text alone
    }
}
