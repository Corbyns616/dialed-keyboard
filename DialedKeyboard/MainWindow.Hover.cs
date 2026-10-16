using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DialedKeyboard;

/// <summary>
/// Hover to press (dwell clicking): rest the pointer on a key and it presses by itself, for
/// anyone who finds clicking hard. A fill rises over the key while you wait. To press the same
/// key again, move off it and back.
/// </summary>
public partial class MainWindow
{
    /// <summary>How far the pointer must move after a hover press before another one can start.</summary>
    const double HoverRearmDistance = 6;

    readonly DispatcherTimer _hoverTimer = new();
    readonly Dictionary<ButtonBase, string> _keySounds = new();
    Canvas _hoverLayer = null!;
    Border _hoverFill = null!;
    ButtonBase? _hoverTarget;
    /// <summary>The key just pressed by hovering; it can't press again until the pointer leaves it.</summary>
    ButtonBase? _hoverPressed;
    Point _hoverPressedAt;

    void SetUpHover()
    {
        _hoverFill = new Border
        {
            CornerRadius = new CornerRadius(8),
            Opacity = 0.55,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            RenderTransformOrigin = new Point(0.5, 1),
            RenderTransform = new ScaleTransform(1, 0),
        };
        _hoverFill.SetResourceReference(Border.BackgroundProperty, "AccentBg");
        _hoverLayer = new Canvas { IsHitTestVisible = false, Children = { _hoverFill } };
        Grid.SetRowSpan(_hoverLayer, 2);
        Panel.SetZIndex(_hoverLayer, 100);
        KeyArea.Children.Add(_hoverLayer);

        _hoverTimer.Tick += (_, _) => HoverPress();
        KeyArea.PreviewMouseMove += Hover_Move;
        KeyArea.MouseLeave += (_, _) =>
        {
            CancelHover();
            _hoverPressed = null;
        };
    }

    void Hover_Move(object sender, MouseEventArgs e)
    {
        if (!_app.Settings.HoverToPress || _toggleSwiping) return; // a no-hold word watches the pointer itself
        // A held button means clicking or swiping, which works as usual.
        if (e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed)
        {
            CancelHover();
            return;
        }
        var btn = KeyUnder(e.OriginalSource as DependencyObject);
        if (_hoverPressed != null)
        {
            // Wait for a real move: when a press swaps the page, a new key appears under a still pointer.
            if ((e.GetPosition(this) - _hoverPressedAt).Length < HoverRearmDistance || btn == _hoverPressed) return;
            _hoverPressed = null;
        }
        if (btn == null || !btn.IsEnabled)
        {
            CancelHover();
            return;
        }
        if (btn != _hoverTarget) StartHover(btn);
    }

    /// <summary>The key (or panel button) under the pointer, if any.</summary>
    ButtonBase? KeyUnder(DependencyObject? element)
    {
        while (element != null && element != KeyArea)
        {
            if (element is ButtonBase button) return button;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    void StartHover(ButtonBase btn)
    {
        _hoverTarget = btn;
        var time = TimeSpan.FromSeconds(Math.Clamp(_app.Settings.HoverSeconds, 0.3, 5));
        _hoverTimer.Interval = time;
        _hoverTimer.Stop();
        _hoverTimer.Start();
        ShowHoverFill(btn, time);
    }

    /// <summary>The fill that rises over <paramref name="btn"/> while the pointer rests on it.</summary>
    void ShowHoverFill(ButtonBase btn, TimeSpan time)
    {
        var topLeft = btn.TranslatePoint(new Point(0, 0), _hoverLayer);
        Canvas.SetLeft(_hoverFill, topLeft.X);
        Canvas.SetTop(_hoverFill, topLeft.Y);
        _hoverFill.Width = btn.ActualWidth;
        _hoverFill.Height = btn.ActualHeight;
        _hoverFill.Visibility = Visibility.Visible;
        _hoverFill.BeginAnimation(OpacityProperty, null);
        _hoverFill.Opacity = 0.55;
        ((ScaleTransform)_hoverFill.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0, 1, time));
    }

    void CancelHover()
    {
        _hoverTimer.Stop();
        _hoverTarget = null;
        ((ScaleTransform)_hoverFill.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _hoverFill.Visibility = Visibility.Collapsed;
    }

    void HoverPress()
    {
        _hoverTimer.Stop();
        var btn = _hoverTarget;
        _hoverTarget = null;
        if (btn == null || !btn.IsEnabled || !btn.IsVisible) return;
        _hoverPressed = btn;
        _hoverPressedAt = Mouse.GetPosition(this);

        // Flash the fill away so it's clear the key went in.
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => { if (_hoverTarget == null) _hoverFill.Visibility = Visibility.Collapsed; };
        _hoverFill.BeginAnimation(OpacityProperty, fade);

        // With the Swipe key on, resting on a letter starts a word rather than typing the letter.
        if (_swipeToggleOn && NoHoldSwipe && !(_ctrl || _alt || _win) && _lettersPage.IsVisible
            && _charKeys.FirstOrDefault(k => k.Key == btn && k.Def.Base is >= 'a' and <= 'z') is { } letter)
        {
            var centre = btn.TranslatePoint(new Point(btn.ActualWidth / 2, btn.ActualHeight / 2), _lettersPage);
            StartToggleSwipe(centre, letter.Def);
            return;
        }

        // Keys play their sound on mouse-down, which hovering skips, so play it here. Suggestions play their own.
        if (_keySounds.TryGetValue(btn, out var sound)) _app.Sounds.Play(sound);
        else if (Array.IndexOf(_suggestButtons, btn) < 0) _app.Sounds.Play("space");
        btn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, btn));
    }
}
