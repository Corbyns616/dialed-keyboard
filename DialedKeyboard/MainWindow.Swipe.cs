using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DialedKeyboard.Engine;

namespace DialedKeyboard;

/// <summary>
/// No-hold swiping, for anyone who can't keep a button held while dragging. With the Swipe key
/// on, click the first letter, move over the others (no button held) and click again to type
/// the word. With hover to press on, resting on a letter starts the word and resting again ends it.
/// </summary>
public partial class MainWindow
{
    ButtonBase? _swipeKey;
    ColumnDefinition? _swipeColumn;
    /// <summary>The Swipe key is lit: clicking a letter starts a word instead of typing the letter.</summary>
    bool _swipeToggleOn;
    /// <summary>A no-hold word is being drawn.</summary>
    bool _toggleSwiping;
    KeyDef? _toggleStartKey;
    readonly DispatcherTimer _swipeRestTimer = new();
    Point _swipeRestAt;

    bool HoldSwipe => _app.Settings.SwipeMode == SwipeModes.Hold;
    bool NoHoldSwipe => _app.Settings.SwipeMode == SwipeModes.NoHold;

    void ToggleSwipeKey()
    {
        CancelToggleSwipe();
        _swipeToggleOn = !_swipeToggleOn;
    }

    void StartToggleSwipe(Point p, KeyDef? key)
    {
        _toggleSwiping = true;
        _toggleStartKey = key;
        _swipePath.Clear();
        _swipePath.Add(p);
        _swipeRestAt = p;
        _held = null;
        _holdTimer.Stop();
        _swipeTrail.BeginAnimation(OpacityProperty, null);
        _swipeTrail.Opacity = 0.75;
        _swipeTrail.StrokeThickness = Math.Max(4, KeyPitch * 0.14);
        _swipeTrail.Points = new PointCollection(_swipePath);
        if (key != null) _app.Sounds.Play(key.Base.ToString());
    }

    void ToggleSwipe_Move(Point p)
    {
        _swipePath.Add(p);
        _swipeTrail.Points.Add(p);
        if (!_app.Settings.HoverToPress || (p - _swipeRestAt).Length < HoverRearmDistance) return;
        // Hover users end the word by resting on its last letter.
        _swipeRestAt = p;
        var time = TimeSpan.FromSeconds(Math.Clamp(_app.Settings.HoverSeconds, 0.3, 5));
        _swipeRestTimer.Interval = time;
        _swipeRestTimer.Stop();
        _swipeRestTimer.Start();
        if (KeyUnder(_lettersPage.InputHitTest(p) as DependencyObject) is { } key) ShowHoverFill(key, time);
        else CancelHover();
    }

    void SwipeRest_Tick(object? sender, EventArgs e)
    {
        _swipeRestTimer.Stop();
        if (!_toggleSwiping) return;
        // Like a hover press: the key under the pointer mustn't press again until the pointer moves.
        _hoverPressed = KeyUnder(_lettersPage.InputHitTest(_swipeRestAt) as DependencyObject);
        _hoverPressedAt = Mouse.GetPosition(this);
        FinishToggleSwipe(null);
    }

    /// <summary>Ends a no-hold word and types it. A click with no movement types the first letter instead.</summary>
    void FinishToggleSwipe(Point? last)
    {
        _swipeRestTimer.Stop();
        CancelHover();
        if (last is Point p) _swipePath.Add(p);
        _toggleSwiping = false;
        FadeTrail();

        double length = 0;
        for (int i = 1; i < _swipePath.Count; i++) length += (_swipePath[i] - _swipePath[i - 1]).Length;
        if (length < KeyPitch * 0.5)
        {
            if (_toggleStartKey is { } key) OnKey(key);
            return;
        }
        TypeSwipe();
    }

    void CancelToggleSwipe()
    {
        if (!_toggleSwiping) return;
        _toggleSwiping = false;
        _swipeRestTimer.Stop();
        CancelHover();
        FadeTrail();
    }

    /// <summary>Decodes the drawn path and types the best word, offering the others in the suggestion bar.</summary>
    void TypeSwipe()
    {
        _forgetIndex = -1;
        _forgetSuggestion = null;
        var path = _swipePath.Select(p => new SwipePoint(p.X, p.Y)).ToList();
        var guesses = _typing.DecodeSwipe(path, LetterCentres(), KeyPitch, SuggestionCount + 1);
        if (guesses.Count == 0) return;
        _app.Sounds.Play("space");
        _swipedWord = _typing.TypeSwipedWord(guesses[0]);
        _swipeChoices = guesses.Skip(1).ToList();
        RefreshKeys();
    }

    /// <summary>The letter key (on the ABC page) that <paramref name="element"/> belongs to.</summary>
    CharKey? LetterKeyOf(DependencyObject? element)
    {
        var button = KeyUnder(element);
        return button == null ? null : _charKeys.FirstOrDefault(k => k.Key == button && k.Def.Base is >= 'a' and <= 'z');
    }
}

/// <summary>Values for <see cref="AppSettings.SwipeMode"/>.</summary>
public static class SwipeModes
{
    public const string Off = "Off";
    /// <summary>Hold the button down and drag across the letters.</summary>
    public const string Hold = "Hold";
    /// <summary>Tap the Swipe key, click the first letter, move, click the last letter.</summary>
    public const string NoHold = "NoHold";
}
