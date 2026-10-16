using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using DialedKeyboard.Native;

namespace DialedKeyboard;

/// <summary>
/// The floating ⌨ button, always on screen so the keyboard is never lost. It glows and
/// sends out a pulse ring. Click it to show or hide the keyboard; drag it to move it.
/// </summary>
public sealed class MiniButton : Window
{
    const double ButtonSize = 56;
    /// <summary>Room around the button for the glow and pulse ring.</summary>
    const double Halo = 22;
    const double Size = ButtonSize + Halo * 2;
    const double DragThreshold = 4;

    static readonly Color Glow = Color.FromRgb(79, 124, 255);
    /// <summary>
    /// The pulse never stops, and redrawing a see-through window is costly, so it runs at a
    /// gentle frame rate rather than 60 a second: much less CPU, and it looks the same.
    /// </summary>
    const int PulseFrameRate = 24;

    readonly App _app;
    Point _pressPoint;
    bool _pressed, _moved;

    public MiniButton(App app)
    {
        _app = app;
        Width = Height = Size;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ToolTip = "Show / hide keyboard (Ctrl+Alt+K) — drag to move";

        // Pulse ring: grows out from the button and fades, over and over.
        var ringScale = new ScaleTransform(1, 1);
        var ring = new Ellipse
        {
            Width = ButtonSize,
            Height = ButtonSize,
            Stroke = new SolidColorBrush(Glow),
            StrokeThickness = 3,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = ringScale,
            IsHitTestVisible = false,
        };

        var label = new TextBlock
        {
            Text = "⌨",
            FontSize = 28,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var glow = new DropShadowEffect { Color = Glow, ShadowDepth = 0, BlurRadius = 14, Opacity = 0.9 };
        var buttonScale = new ScaleTransform(1, 1);
        var button = new Border
        {
            Width = ButtonSize,
            Height = ButtonSize,
            CornerRadius = new CornerRadius(ButtonSize / 2),
            Background = new LinearGradientBrush(Color.FromRgb(98, 143, 255), Color.FromRgb(59, 91, 219), 90),
            BorderBrush = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
            BorderThickness = new Thickness(1.5),
            Child = label,
            Effect = glow,
            Cursor = Cursors.Hand,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = buttonScale,
        };

        Content = new Grid { Children = { ring, button } };

        var beat = new Duration(TimeSpan.FromSeconds(1.6));
        var ringGrow = Paced(new DoubleAnimation(1, 1.7, beat) { RepeatBehavior = RepeatBehavior.Forever });
        ringScale.BeginAnimation(ScaleTransform.ScaleXProperty, ringGrow);
        ringScale.BeginAnimation(ScaleTransform.ScaleYProperty, ringGrow);
        ring.BeginAnimation(OpacityProperty, Paced(new DoubleAnimation(0.8, 0, beat) { RepeatBehavior = RepeatBehavior.Forever }));

        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var halfBeat = new Duration(TimeSpan.FromSeconds(0.8));
        glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty,
            Paced(new DoubleAnimation(10, 30, halfBeat) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease }));
        var breathe = Paced(new DoubleAnimation(1, 1.06, halfBeat) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        buttonScale.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
        buttonScale.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);

        var work = SystemParameters.WorkArea;
        var s = app.Settings;
        Left = s.MiniLeft is double l && l >= work.Left - Halo && l <= work.Right - Size + Halo ? l : work.Right - Size - 8;
        Top = s.MiniTop is double t && t >= work.Top - Halo && t <= work.Bottom - Size + Halo ? t : work.Bottom - Size - 8;
    }

    static DoubleAnimation Paced(DoubleAnimation animation)
    {
        Timeline.SetDesiredFrameRate(animation, PulseFrameRate);
        return animation;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NoActivate.Apply(new WindowInteropHelper(this).Handle);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _pressed = true;
        _moved = false;
        _pressPoint = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_pressed) return;
        var p = e.GetPosition(this);
        double dx = p.X - _pressPoint.X, dy = p.Y - _pressPoint.Y;
        if (!_moved && Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold) return;
        _moved = true;
        Left += dx;
        Top += dy;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        ReleaseMouseCapture();
        if (_moved)
        {
            _app.Settings.MiniLeft = Left;
            _app.Settings.MiniTop = Top;
        }
        else
        {
            _app.ToggleKeyboard();
        }
    }
}
