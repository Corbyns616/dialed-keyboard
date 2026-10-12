using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;
using DialedKeyboard.Engine;
using DialedKeyboard.Native;

namespace DialedKeyboard;

public partial class MainWindow : Window
{
    const int SuggestionCount = 3;
    /// <summary>Height of the suggestion strip, relative to a key row.</summary>
    const double SuggestionRowHeight = 0.8;

    sealed record CharKey(KeyDef Def, TextBlock Main);

    readonly App _app;
    readonly TypingController _typing;
    readonly KeySender _sender;
    readonly List<CharKey> _charKeys = new();
    readonly Button[] _suggestButtons = new Button[SuggestionCount];
    readonly Suggestion?[] _suggestions = new Suggestion?[SuggestionCount];
    readonly Dictionary<KeyKind, ButtonBase> _modifierKeys = new();
    readonly List<Path> _shiftIcons = new();
    readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(380) };
    KeyDef? _held;
    bool _holdTyped;
    bool _rightCtrlDown;
    readonly DispatcherTimer _backspaceRepeat = new();
    bool _backspaceHeld;
    Grid _lettersPage = null!, _symbolsPage = null!;
    bool _ctrl, _alt, _win;
    bool _dragging;
    Point _dragStart;
    InputWatcher? _watcher;

    public MainWindow(App app)
    {
        _app = app;
        _typing = app.Typing;
        _sender = app.Sender;
        InitializeComponent();
        _holdTimer.Tick += HoldTimer_Tick;
        _backspaceRepeat.Tick += BackspaceRepeat_Tick;
        BuildKeyboard();
        ApplySettings();
        PlaceWindow();
        _typing.StateChanged += RefreshKeys;
        RefreshKeys();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Clicking the keyboard must never take focus away from the app being typed into.
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        NoActivate.Apply(hwnd);

        _watcher = new InputWatcher(hwnd);
        _watcher.ExternalInput += () => Dispatcher.BeginInvoke(() => _typing.Reset());

        // Ctrl+Alt+K shows/hides the keyboard from anywhere.
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        NativeMethods.RegisterHotKey(hwnd, ToggleHotkeyId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, 'K');
    }

    const int ToggleHotkeyId = 1;

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == ToggleHotkeyId)
        {
            _app.ToggleKeyboard();
            handled = true;
        }
        return IntPtr.Zero;
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // A tool window has no taskbar button, so a real minimize would strand it.
        // Swap to the ⌨ button instead (e.g. after Win+D or Win+Down).
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
            _app.MinimizeKeyboard();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        HoldRightCtrl(false);
        ReleaseBackspace();
        NativeMethods.UnregisterHotKey(new WindowInteropHelper(this).Handle, ToggleHotkeyId);
        _watcher?.Dispose();
        base.OnClosed(e);
    }

    public void ApplySettings()
    {
        Opacity = Math.Clamp(_app.Settings.Opacity, 0.3, 1.0);
        RefreshKeys();
    }

    void PlaceWindow()
    {
        var s = _app.Settings;
        var work = SystemParameters.WorkArea;
        if ((s.Width, s.Height) is (1000, 380) or (820, 300))
        {
            // Untouched size from an older version's layout; use the phone-style default.
            s.Width = 600;
            s.Height = 456;
            s.Left = s.Top = null;
        }
        Width = Math.Clamp(s.Width, MinWidth, work.Width);
        Height = Math.Clamp(s.Height, MinHeight, work.Height);
        Left = s.Left is double l && l >= work.Left - Width + 50 && l <= work.Right - 50
            ? l : work.Left + (work.Width - Width) / 2;
        Top = s.Top is double t && t >= work.Top && t <= work.Bottom - 50
            ? t : work.Bottom - Height - 10;
    }

    public void SaveBounds()
    {
        var s = _app.Settings;
        s.Left = Left;
        s.Top = Top;
        s.Width = Width;
        s.Height = Height;
    }

    // ---------- Building the UI ----------

    void BuildKeyboard()
    {
        KeyArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(SuggestionRowHeight, GridUnitType.Star) });
        KeyArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(KeyLayout.RowHeights.Sum(), GridUnitType.Star) });

        // Esc / Ctrl / Alt line up with the key columns; the suggestions share the rest.
        var strip = new Grid();
        foreach (var def in KeyLayout.StripKeys)
        {
            strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(def.Width, GridUnitType.Star) });
            var key = CreateKey(def);
            Grid.SetColumn(key, strip.Children.Count);
            strip.Children.Add(key);
        }
        strip.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(10 - KeyLayout.StripKeys.Sum(d => d.Width), GridUnitType.Star),
        });
        var bar = new UniformGrid { Rows = 1, Columns = SuggestionCount };
        Grid.SetColumn(bar, KeyLayout.StripKeys.Length);
        strip.Children.Add(bar);
        for (int i = 0; i < SuggestionCount; i++)
        {
            int index = i;
            var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
            var btn = new Button { Content = text, ClickMode = ClickMode.Press };
            btn.Style = (Style)FindResource("KeyButton");
            btn.SetResourceReference(BackgroundProperty, "SuggestBg");
            btn.Click += (_, _) =>
            {
                if (_suggestions[index] != null) _app.Sounds.Play("space");
                AcceptSuggestion(index);
            };
            _suggestButtons[i] = btn;
            bar.Children.Add(btn);
        }
        KeyArea.Children.Add(strip);

        _lettersPage = BuildPage(KeyLayout.Letters);
        _symbolsPage = BuildPage(KeyLayout.Symbols);
        _symbolsPage.Visibility = Visibility.Collapsed;
    }

    Grid BuildPage(KeyDef[][] rows)
    {
        var page = new Grid();
        Grid.SetRow(page, 1);
        for (int r = 0; r < rows.Length; r++)
        {
            page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(KeyLayout.RowHeights[r], GridUnitType.Star) });
            var rowGrid = new Grid();
            Grid.SetRow(rowGrid, r);
            var row = rows[r];
            for (int c = 0; c < row.Length; c++)
            {
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(row[c].Width, GridUnitType.Star) });
                if (row[c].Kind == KeyKind.Spacer) continue;
                var key = CreateKey(row[c]);
                Grid.SetColumn(key, c);
                rowGrid.Children.Add(key);
            }
            page.Children.Add(rowGrid);
        }
        KeyArea.Children.Add(page);
        return page;
    }

    ButtonBase CreateKey(KeyDef def)
    {
        ButtonBase btn = new Button { ClickMode = ClickMode.Press };
        btn.Style = (Style)FindResource("KeyButton");
        // The click sounds the moment the key goes down, like a real keyboard.
        btn.PreviewMouseLeftButtonDown += (_, _) => _app.Sounds.Play(SoundFor(def));

        if (def.Kind == KeyKind.Backspace)
        {
            // The press deletes one character; keep holding and it auto-repeats until released.
            btn.PreviewMouseLeftButtonDown += (_, _) => StartBackspaceHold();
            btn.PreviewMouseLeftButtonUp += (_, _) => ReleaseBackspace();
            btn.LostMouseCapture += (_, _) => ReleaseBackspace();
        }

        if (def.Kind == KeyKind.Char)
        {
            var main = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
            };
            var content = new Grid();
            content.Children.Add(main);
            if (def.Hint is char hint)
            {
                var sub = new TextBlock
                {
                    Text = hint.ToString(),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 1, -1, 0),
                };
                sub.SetResourceReference(TextBlock.FontSizeProperty, "KeySmallFontSize");
                sub.SetResourceReference(TextBlock.ForegroundProperty, "KeySubFg");
                content.Children.Add(sub);

                // Like a phone: a long press types the corner symbol, so the tap is decided on release.
                btn.ClickMode = ClickMode.Release;
                btn.PreviewMouseLeftButtonDown += (_, _) => StartHold(def);
                btn.PreviewMouseLeftButtonUp += (_, _) => _holdTimer.Stop();
                btn.PreviewMouseRightButtonUp += (_, e) =>
                {
                    _app.Sounds.Play(SoundFor(def));
                    TypeHint(def);
                    e.Handled = true;
                };
            }
            btn.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            btn.VerticalContentAlignment = VerticalAlignment.Stretch;
            btn.Content = content;
            _charKeys.Add(new CharKey(def, main));
        }
        else
        {
            if (def.Kind != KeyKind.Space)
                btn.SetResourceReference(BackgroundProperty, "SpecialKeyBg");
            var label = new TextBlock { Text = def.Label };
            if (def.Kind == KeyKind.Space)
            {
                label.SetResourceReference(TextBlock.FontSizeProperty, "KeySmallFontSize");
                label.FontWeight = FontWeights.SemiBold;
            }
            else if (def.Label.Length > 1)
            {
                label.SetResourceReference(TextBlock.FontSizeProperty, "KeyLabelFontSize");
            }
            btn.Content = label;
            // Font arrows come out thin and small, so draw these two like a phone does.
            if (def.Kind == KeyKind.Shift)
            {
                btn.Content = KeyIcon("M12,3 L21,12.5 L16,12.5 L16,20.5 L8,20.5 L8,12.5 L3,12.5 Z", out var shiftIcon);
                _shiftIcons.Add(shiftIcon);
            }
            else if (def.Kind == KeyKind.Enter)
            {
                btn.Content = KeyIcon("M19.5,6 L19.5,14 L5,14 M9.5,9.5 L5,14 L9.5,18.5", out _);
            }
            if (def.Kind is KeyKind.Ctrl or KeyKind.Alt or KeyKind.Win)
                _modifierKeys[def.Kind] = btn;
        }

        btn.Click += (_, _) =>
        {
            if (_holdTyped && def.Hint != null)
            {
                _holdTyped = false; // the long press already typed the corner symbol
                return;
            }
            OnKey(def);
        };
        return btn;
    }

    /// <summary>Which recording a key plays; letters have their own, anything unnamed gets a random letter's.</summary>
    static string SoundFor(KeyDef def) => def.Kind switch
    {
        KeyKind.Char => def.Base.ToString(),
        KeyKind.Space => "space",
        KeyKind.Enter => "enter",
        KeyKind.Backspace or KeyKind.DeleteWord => "backspace",
        KeyKind.Shift or KeyKind.Ctrl or KeyKind.Alt or KeyKind.Win => "shift",
        KeyKind.Page => "caps lock",
        KeyKind.Nav when def.Vk == KeySender.VK_TAB => "tab",
        _ => "",
    };

    /// <summary>A key icon drawn on a 24×24 grid, scaled with the key text.</summary>
    static Viewbox KeyIcon(string data, out Path path)
    {
        path = new Path
        {
            Data = Geometry.Parse(data),
            StrokeThickness = 1.8,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        path.SetResourceReference(Shape.StrokeProperty, "KeyFg");
        var box = new Viewbox { Child = new Grid { Width = 24, Height = 24, Children = { path } } };
        box.SetResourceReference(HeightProperty, "KeyFontSize");
        return box;
    }

    void StartBackspaceHold()
    {
        _backspaceRepeat.Interval = TimeSpan.FromMilliseconds(400);
        _backspaceRepeat.Start();
    }

    /// <summary>
    /// Holds Backspace down like a physical key: repeated key-downs (as keyboard auto-repeat
    /// sends them) and a single key-up on release.
    /// </summary>
    void BackspaceRepeat_Tick(object? sender, EventArgs e)
    {
        if (!_backspaceHeld)
        {
            _backspaceHeld = true;
            _backspaceRepeat.Interval = TimeSpan.FromMilliseconds(45);
            // We can't follow how much text the held key removes, so stop trusting our copy.
            _typing.Reset();
        }
        _sender.Hold(KeySender.VK_BACK, true);
    }

    void ReleaseBackspace()
    {
        _backspaceRepeat.Stop();
        if (!_backspaceHeld) return;
        _backspaceHeld = false;
        _sender.Hold(KeySender.VK_BACK, false);
        RefreshKeys();
    }

    void HoldRightCtrl(bool down)
    {
        if (down == _rightCtrlDown) return;
        _rightCtrlDown = down;
        _sender.Hold(KeySender.VK_RCONTROL, down);
    }

    void StartHold(KeyDef def)
    {
        _held = def;
        _holdTyped = false;
        _holdTimer.Stop();
        _holdTimer.Start();
    }

    void HoldTimer_Tick(object? sender, EventArgs e)
    {
        _holdTimer.Stop();
        if (_held == null) return;
        _holdTyped = true;
        TypeHint(_held);
    }

    void TypeHint(KeyDef def)
    {
        if (def.Hint is char hint)
            OnKey(def with { Base = hint, Hint = null });
    }

    // ---------- Key handling ----------

    void OnKey(KeyDef def)
    {
        bool mods = _ctrl || _alt || _win;
        switch (def.Kind)
        {
            case KeyKind.Char:
                if (mods)
                {
                    ushort vk = KeySender.VkForChar(def.Base);
                    if (vk != 0) SendCombo(vk);
                }
                else
                {
                    _typing.TypeKey(def.Base, null);
                }
                break;
            case KeyKind.Space:
                if (mods) SendCombo(KeySender.VK_SPACE); else _typing.Space();
                break;
            case KeyKind.Backspace:
                if (mods || _typing.ShiftOnce) SendCombo(KeySender.VK_BACK); else _typing.Backspace();
                break;
            case KeyKind.Enter:
                // Shift+Enter is a line break in most chat apps, so pass it through as a combo.
                if (mods || _typing.ShiftOnce) SendCombo(KeySender.VK_RETURN); else _typing.Enter();
                break;
            case KeyKind.DeleteWord:
                if (!_typing.DeleteWord())
                    _sender.Press(KeySender.VK_BACK, new[] { KeySender.VK_CONTROL });
                break;
            case KeyKind.Nav:
                SendCombo(def.Vk);
                break;
            case KeyKind.Shift:
                // Phone-style: tap for one capital, tap again for caps lock, tap again for off.
                if (_typing.CapsLock)
                {
                    _typing.CapsLock = false;
                }
                else if (_typing.ShiftOnce)
                {
                    _typing.ShiftOnce = false;
                    _typing.CapsLock = true;
                }
                else
                {
                    _typing.ShiftOnce = true;
                }
                break;
            case KeyKind.Page:
                bool symbols = _symbolsPage.Visibility != Visibility.Visible;
                _symbolsPage.Visibility = symbols ? Visibility.Visible : Visibility.Collapsed;
                _lettersPage.Visibility = symbols ? Visibility.Collapsed : Visibility.Visible;
                break;
            case KeyKind.Ctrl:
                // A sticky Right Ctrl: the first tap holds it down, the next tap lets it go.
                // Apps that act on Right Ctrl alone (OpenWhispr hold-to-talk) see a real hold.
                _ctrl = !_ctrl;
                HoldRightCtrl(_ctrl);
                break;
            case KeyKind.Alt:
                _alt = !_alt;
                break;
            case KeyKind.Win:
                // Clicking ⊞ twice taps the Windows key on its own (opens Start).
                if (_win) _sender.Press(KeySender.VK_LWIN, Array.Empty<ushort>());
                _win = !_win;
                break;
        }
        RefreshKeys();
    }

    /// <summary>Sends a key with the active modifiers, then clears them.</summary>
    void SendCombo(ushort vk)
    {
        var mods = new List<ushort>();
        // Ctrl is already physically held down (see KeyKind.Ctrl) and stays down until tapped again.
        if (_alt) mods.Add(KeySender.VK_MENU);
        if (_win) mods.Add(KeySender.VK_LWIN);
        if (_typing.ShiftOnce) mods.Add(KeySender.VK_SHIFT);
        _sender.Press(vk, mods);
        _alt = _win = false;
        _typing.ShiftOnce = false;
        // A shortcut or caret move means our idea of the surrounding text is stale.
        _typing.Reset();
    }

    void AcceptSuggestion(int index)
    {
        if (_suggestions[index] is { } s)
            _typing.Accept(s);
    }

    void RefreshKeys()
    {
        bool upper = _typing.UpperNext;
        foreach (var k in _charKeys)
            k.Main.Text = (upper ? char.ToUpperInvariant(k.Def.Base) : k.Def.Base).ToString();

        // The shift arrow turns blue whenever the next letter will be a capital, as on a phone.
        // Caps lock fills it in.
        foreach (var icon in _shiftIcons)
        {
            icon.SetResourceReference(Shape.StrokeProperty, upper ? "AccentFg" : "KeyFg");
            if (_typing.CapsLock) icon.SetResourceReference(Shape.FillProperty, "AccentFg");
            else icon.Fill = null;
        }

        SetOn(KeyKind.Ctrl, _ctrl);
        SetOn(KeyKind.Alt, _alt);
        SetOn(KeyKind.Win, _win);

        var list = _typing.GetSuggestions(SuggestionCount);
        for (int i = 0; i < SuggestionCount; i++)
        {
            var btn = _suggestButtons[i];
            _suggestions[i] = i < list.Count ? list[i] : null;
            ((TextBlock)btn.Content).Text = _suggestions[i]?.Text ?? "";
            btn.Tag = _suggestions[i] switch
            {
                { IsExpansion: true } => "exp",
                { IsPhrase: true } => "phrase",
                _ => null,
            };
            btn.ToolTip = _suggestions[i]?.IsPhrase == true ? _suggestions[i]!.Text : null;
            btn.IsEnabled = _suggestions[i] != null;
        }
    }

    void SetOn(KeyKind kind, bool on)
    {
        if (_modifierKeys.TryGetValue(kind, out var btn))
            btn.Tag = on ? "on" : null;
    }

    // ---------- Window chrome ----------

    void KeyArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // One full key row, and one key column (rows are 10 units wide).
        double rowHeight = KeyArea.ActualHeight / (SuggestionRowHeight + KeyLayout.RowHeights.Sum());
        double pitch = KeyArea.ActualWidth / 10;
        // Phone keys are a little taller than wide; size text by whichever runs out first.
        double size = Math.Min(rowHeight, pitch * 1.25);
        Resources["KeyFontSize"] = Math.Clamp(size * 0.36, 10, 40);
        Resources["KeyLabelFontSize"] = Math.Clamp(size * 0.27, 9, 30);
        Resources["KeySmallFontSize"] = Math.Clamp(size * 0.18, 7, 20);
        double gapX = pitch * 0.073, gapY = rowHeight * 0.11;
        Resources["KeyMargin"] = new Thickness(gapX, gapY, gapX, gapY);
    }

    void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStart = e.GetPosition(this);
        TitleBar.CaptureMouse();
    }

    void TitleBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        Left += p.X - _dragStart.X;
        Top += p.Y - _dragStart.Y;
    }

    void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        TitleBar.ReleaseMouseCapture();
        SaveBounds();
    }

    void Edge_DragDelta(object sender, DragDeltaEventArgs e)
    {
        string edge = (string)((Thumb)sender).Tag;
        if (edge.Contains('R'))
            Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        if (edge.Contains('B'))
            Height = Math.Max(MinHeight, Height + e.VerticalChange);
        if (edge.Contains('L'))
        {
            double w = Math.Max(MinWidth, Width - e.HorizontalChange);
            Left += Width - w;
            Width = w;
        }
        if (edge.Contains('T'))
        {
            double h = Math.Max(MinHeight, Height - e.VerticalChange);
            Top += Height - h;
            Height = h;
        }
    }

    void Edge_DragCompleted(object sender, DragCompletedEventArgs e) => SaveBounds();

    void Smaller_Click(object sender, RoutedEventArgs e) => Scale(1 / 1.12);

    void Bigger_Click(object sender, RoutedEventArgs e) => Scale(1.12);

    /// <summary>Grows or shrinks the keyboard, keeping its bottom edge and centre in place.</summary>
    void Scale(double factor)
    {
        var work = SystemParameters.WorkArea;
        double w = Math.Clamp(Width * factor, MinWidth, work.Width);
        double h = Math.Clamp(Height * factor, MinHeight, work.Height);
        Left = Math.Clamp(Left + (Width - w) / 2, work.Left, work.Right - w);
        Top = Math.Clamp(Top + (Height - h), work.Top, work.Bottom - h);
        Width = w;
        Height = h;
        SaveBounds();
    }

    void Settings_Click(object sender, RoutedEventArgs e) => _app.ShowSettings();

    void Minimize_Click(object sender, RoutedEventArgs e) => _app.MinimizeKeyboard();

    void Exit_Click(object sender, RoutedEventArgs e) => _app.ExitApp();
}
