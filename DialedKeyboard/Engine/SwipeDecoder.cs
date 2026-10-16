namespace DialedKeyboard.Engine;

public readonly record struct SwipePoint(double X, double Y)
{
    public static double Distance(SwipePoint a, SwipePoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

/// <summary>
/// Turns a finger/mouse path drawn across the letter keys into likely words (swipe typing).
/// Each dictionary word has an ideal path through its letters' key centres; the drawn path is
/// compared with it point by point, and the shape match is combined with how likely the word is.
/// </summary>
public sealed class SwipeDecoder
{
    /// <summary>Points both paths are resampled to before comparing.</summary>
    const int Samples = 48;
    /// <summary>A key counts as a possible first/last letter within this many key widths of the path's ends.</summary>
    const double EndRadius = 1.05;
    /// <summary>Every letter of a word must have the path pass within this many key widths of it.</summary>
    const double LetterRadius = 1.15;
    const double ShapeWeight = 10;
    const double LengthWeight = 2;
    const double LanguageWeight = 0.5;

    sealed record Word(string Letters, string Lower, string Display);

    readonly WordPredictor _predictor;
    Dictionary<(char First, char Last), List<Word>>? _byEnds;

    public SwipeDecoder(WordPredictor predictor) => _predictor = predictor;

    /// <param name="path">The drawn path, in the same units as <paramref name="keys"/>.</param>
    /// <param name="keys">Centre of each letter key (a–z).</param>
    /// <param name="keyWidth">Distance between neighbouring key centres.</param>
    /// <returns>Best words first.</returns>
    public List<string> Decode(IReadOnlyList<SwipePoint> path, IReadOnlyDictionary<char, SwipePoint> keys,
        double keyWidth, string? prev2, string? prev, int max)
    {
        var result = new List<string>();
        if (path.Count < 2 || keyWidth <= 0) return result;
        double pathLength = Length(path);
        if (pathLength < keyWidth * 0.5) return result;

        var drawn = Resample(path, Samples);
        var dense = Resample(path, Samples * 3);
        var starts = NearKeys(path[0], keys, keyWidth);
        var ends = NearKeys(path[^1], keys, keyWidth);

        var scored = new List<(double Score, string Display)>();
        foreach (var word in Candidates(starts, ends))
        {
            if (_predictor.IsBlocked(word.Lower)) continue;
            var template = Template(word.Letters, keys);
            if (template == null || template.Count < 2) continue;
            if (!PassesNearEveryLetter(template, dense, keyWidth * LetterRadius)) continue;

            double templateLength = Length(template);
            double shape = MeanDistance(drawn, Resample(template, Samples)) / keyWidth;
            double lengthMismatch = Math.Abs(Math.Log(pathLength / Math.Max(templateLength, keyWidth * 0.5)));
            double score = -ShapeWeight * shape * shape
                           - LengthWeight * lengthMismatch
                           + LanguageWeight * _predictor.Likelihood(word.Lower, prev2, prev);
            scored.Add((score, word.Display));
        }

        foreach (var (_, display) in scored.OrderByDescending(s => s.Score))
        {
            if (!result.Contains(display, StringComparer.OrdinalIgnoreCase))
                result.Add(display);
            if (result.Count >= max) break;
        }
        return result;
    }

    IEnumerable<Word> Candidates(List<char> starts, List<char> ends)
    {
        _byEnds ??= Index(_predictor.SwipeVocabulary(userOnly: false));
        foreach (char s in starts)
            foreach (char e in ends)
                if (_byEnds.TryGetValue((s, e), out var list))
                    foreach (var w in list)
                        yield return w;

        // Names and slang you've taught it, which aren't in the bundled list.
        foreach (var w in Index(_predictor.SwipeVocabulary(userOnly: true)).Values.SelectMany(l => l))
            if (starts.Contains(w.Letters[0]) && ends.Contains(w.Letters[^1]))
                yield return w;
    }

    static Dictionary<(char, char), List<Word>> Index(IEnumerable<(string Lower, string Display)> vocabulary)
    {
        var index = new Dictionary<(char, char), List<Word>>();
        foreach (var (lower, display) in vocabulary)
        {
            string letters = WordPredictor.Strip(lower);
            if (letters.Length < 2 || !letters.All(c => c is >= 'a' and <= 'z')) continue;
            var key = (letters[0], letters[^1]);
            if (!index.TryGetValue(key, out var list))
                index[key] = list = new List<Word>();
            list.Add(new Word(letters, lower, display));
        }
        return index;
    }

    /// <summary>The nearest keys to a point: always the closest one, plus any others within reach.</summary>
    static List<char> NearKeys(SwipePoint p, IReadOnlyDictionary<char, SwipePoint> keys, double keyWidth)
    {
        var byDistance = keys.OrderBy(k => SwipePoint.Distance(p, k.Value)).Take(3).ToList();
        var near = new List<char>();
        foreach (var (letter, centre) in byDistance)
            if (near.Count == 0 || SwipePoint.Distance(p, centre) <= keyWidth * EndRadius)
                near.Add(letter);
        return near;
    }

    /// <summary>Key centres for a word's letters, with repeated letters ("ll" in hello) visited once.</summary>
    static List<SwipePoint>? Template(string letters, IReadOnlyDictionary<char, SwipePoint> keys)
    {
        var points = new List<SwipePoint>(letters.Length);
        char last = '\0';
        foreach (char c in letters)
        {
            if (c == last) continue;
            if (!keys.TryGetValue(c, out var p)) return null;
            points.Add(p);
            last = c;
        }
        return points;
    }

    static bool PassesNearEveryLetter(List<SwipePoint> template, List<SwipePoint> dense, double radius)
    {
        foreach (var letter in template)
        {
            bool near = false;
            foreach (var p in dense)
            {
                if (SwipePoint.Distance(letter, p) <= radius)
                {
                    near = true;
                    break;
                }
            }
            if (!near) return false;
        }
        return true;
    }

    static double Length(IReadOnlyList<SwipePoint> points)
    {
        double length = 0;
        for (int i = 1; i < points.Count; i++)
            length += SwipePoint.Distance(points[i - 1], points[i]);
        return length;
    }

    static double MeanDistance(List<SwipePoint> a, List<SwipePoint> b)
    {
        double sum = 0;
        for (int i = 0; i < a.Count; i++)
            sum += SwipePoint.Distance(a[i], b[i]);
        return sum / a.Count;
    }

    /// <summary>Spaces <paramref name="count"/> points evenly along the path.</summary>
    public static List<SwipePoint> Resample(IReadOnlyList<SwipePoint> points, int count)
    {
        var result = new List<SwipePoint>(count);
        double total = Length(points);
        if (total == 0)
        {
            for (int i = 0; i < count; i++) result.Add(points[0]);
            return result;
        }
        double step = total / (count - 1);
        result.Add(points[0]);
        double carried = 0;
        for (int i = 1; i < points.Count && result.Count < count; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            double segment = SwipePoint.Distance(a, b);
            while (segment > 0 && carried + segment >= step && result.Count < count)
            {
                double t = (step - carried) / segment;
                a = new SwipePoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
                result.Add(a);
                segment = SwipePoint.Distance(a, b);
                carried = 0;
            }
            carried += segment;
        }
        while (result.Count < count) result.Add(points[^1]);
        return result;
    }
}
