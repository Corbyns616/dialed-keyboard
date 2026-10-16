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

    sealed record CharKey(KeyDef Def, TextBlock Main, ButtonBase Key);

    /// <summary>How far (in key widths) a press has to travel before it counts as a swipe rather than a tap.</summary>
    const double SwipeStartDistance = 0.45;

    readonly App _app;
    /// <summary>Where the keys type: the app (<see cref="App.Typing"/>), or the sticker search box.</summary>
    TypingController _typing;
    readonly KeySender _sender;
    readonly List<CharKey> _charKeys = new();
    readonly Button[] _suggestButtons = new Button[SuggestionCount];
    UniformGrid _suggestBar = null!;
    readonly Suggestion?[] _suggestions = new Suggestion?[SuggestionCount];
    readonly Dictionary<KeyKind, ButtonBase> _modifierKeys = new();
    readonly List<Path> _shiftIcons = new();
    readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(380) };
    // Hold a suggestion to forget it: the hold turns the slot into "Forget …?", and a tap confirms.
    readonly DispatcherTimer _forgetHoldTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    readonly DispatcherTimer _forgetConfirmTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    int _forgetHeldIndex = -1, _forgetIndex = -1;
    Suggestion? _forgetSuggestion;
    bool _forgetHoldFired;
    KeyDef? _held;
    bool _holdTyped;
    bool _rightCtrlDown;
    readonly DispatcherTimer _backspaceRepeat = new();
    bool _backspaceHeld;
    Grid _lettersPage = null!, _symbolsPage = null!, _clipPage = null!;
    /// <summary>The letters or symbols page to go back to when the clipboard panel closes.</summary>
    Grid _keyPage = null!;
    readonly ClipboardHistory _clips = new();
    UniformGrid _clipList = null!;
    ScrollViewer _clipScroll = null!;
    TextBlock _clipEmpty = null!;
    ButtonBase? _clipKey;
    bool _ctrl, _alt, _win;
    // Swipe typing on the ABC page.
    readonly List<Point> _swipePath = new();
    readonly Polyline _swipeTrail = new();
    bool _swipeArmed, _swiping, _swipeSuppressClick;
    /// <summary>The last swiped word as typed, and the other guesses offered in its place.</summary>
    string? _swipedWord;
    List<string> _swipeChoices = new();
    bool _dragging;
    Point _dragStart;
    InputWatcher? _watcher;
    PasswordFieldWatcher? _passwordWatcher;

    public MainWindow(App app)
    {
        _app = app;
        _typing = app.Typing;
        _sender = app.Sender;
        InitializeComponent();
        _holdTimer.Tick += HoldTimer_Tick;
        _backspaceRepeat.Tick += BackspaceRepeat_Tick;
        _forgetHoldTimer.Tick += ForgetHold_Tick;
        _forgetConfirmTimer.Tick += (_, _) => CancelForget();
        _swipeRestTimer.Tick += SwipeRest_Tick;
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
        _watcher.ExternalInput += () => Dispatcher.BeginInvoke(() =>
        {
            CancelToggleSwipe();
            // Clicking into an app means typing goes there again, not into the sticker search.
            if (_searchMode)
            {
                EndSearchTyping();
                RefreshKeys();
            }
            _app.Typing.Reset();
        });
        _passwordWatcher = new PasswordFieldWatcher();
        _passwordWatcher.Changed += inPassword => Dispatcher.BeginInvoke(() => _app.Typing.InPasswordField = inPassword);

        // Ctrl+Alt+K shows/hides the keyboard from anywhere.
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        NativeMethods.RegisterHotKey(hwnd, ToggleHotkeyId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, 'K');

        // Remember what gets copied, in any app, for the clipboard panel.
        NativeMethods.AddClipboardFormatListener(hwnd);
        CaptureClipboard();
    }

    const int ToggleHotkeyId = 1;

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == ToggleHotkeyId)
        {
            _app.ToggleKeyboard();
            handled = true;
        }
        else if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            Dispatcher.BeginInvoke(CaptureClipboard);
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
        NativeMethods.RemoveClipboardFormatListener(new WindowInteropHelper(this).Handle);
        _watcher?.Dispose();
        _passwordWatcher?.Dispose();
        base.OnClosed(e);
    }

    public void ApplySettings()
    {
        Opacity = Math.Clamp(_app.Settings.Opacity, 0.3, 1.0);
        bool stickers = _app.Settings.StickersEnabled;
        if (_stickerColumn != null) _stickerColumn.Width = new GridLength(stickers ? 1 : 0, GridUnitType.Star);
        if (_stickerKey != null) _stickerKey.Visibility = stickers ? Visibility.Visible : Visibility.Collapsed;
        // The Swipe key only exists for no-hold swiping.
        if (_swipeColumn != null) _swipeColumn.Width = new GridLength(NoHoldSwipe ? 1 : 0, GridUnitType.Star);
        if (_swipeKey != null) _swipeKey.Visibility = NoHoldSwipe ? Visibility.Visible : Visibility.Collapsed;
        if (!NoHoldSwipe)
        {
            CancelToggleSwipe();
            _swipeToggleOn = false;
        }
        // Suggestions go in on release when holding one can forget it, so a hold doesn't type it first.
        foreach (var btn in _suggestButtons)
            btn.ClickMode = _app.Settings.HoldToForget ? ClickMode.Release : ClickMode.Press;
        if (!_app.Settings.HoldToForget) CancelForget();
        if (!_app.Settings.HoverToPress) CancelHover();
        if (!stickers && (_stickerPage.Visibility == Visibility.Visible || _searchMode))
        {
            EndSearchTyping();
            ShowPage(_keyPage);
        }
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
            var column = new ColumnDefinition { Width = new GridLength(def.Width, GridUnitType.Star) };
            if (def.Kind == KeyKind.Stickers) _stickerColumn = column;
            if (def.Kind == KeyKind.Swipe) _swipeColumn = column;
            strip.ColumnDefinitions.Add(column);
            var key = CreateKey(def);
            Grid.SetColumn(key, strip.Children.Count);
            strip.Children.Add(key);
        }
        strip.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(10 - KeyLayout.StripKeys.Sum(d => d.Width), GridUnitType.Star),
        });
        var bar = _suggestBar = new UniformGrid { Rows = 1, Columns = SuggestionCount };
        Grid.SetColumn(bar, KeyLayout.StripKeys.Length);
        strip.Children.Add(bar);
        for (int i = 0; i < SuggestionCount; i++)
        {
            int index = i;
            var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
            var btn = new Button { Content = text, ClickMode = ClickMode.Press };
            btn.Style = (Style)FindResource("KeyButton");
            btn.SetResourceReference(BackgroundProperty, "SuggestBg");
            btn.PreviewMouseLeftButtonDown += (_, _) => StartForgetHold(index);
            btn.PreviewMouseLeftButtonUp += (_, _) => _forgetHoldTimer.Stop();
            btn.LostMouseCapture += (_, _) => _forgetHoldTimer.Stop();
            btn.Click += (_, _) =>
            {
                if (_forgetHoldFired)
                {
                    _forgetHoldFired = false; // this release ends the hold that asked to forget
                    return;
                }
                if (_forgetIndex == index)
                {
                    ConfirmForget();
                    return;
                }
                _forgetIndex = -1; // a different suggestion was picked, so keep the one that was offered
                _forgetSuggestion = null;
                if (_suggestions[index] != null) _app.Sounds.Play("space");
                AcceptSuggestion(index);
            };
            _suggestButtons[i] = btn;
            bar.Children.Add(btn);
        }
        KeyArea.Children.Add(strip);

        _lettersPage = BuildPage(KeyLayout.Letters);
        SetUpSwipe();
        _symbolsPage = BuildPage(KeyLayout.Symbols);
        _symbolsPage.Visibility = Visibility.Collapsed;
        _keyPage = _lettersPage;
        _clipPage = BuildClipboardPage();
        _clips.Changed += RefreshClipboardList;
        RefreshClipboardList();
        _stickerPage = BuildStickerPage();
        SetUpHover();
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
        _keySounds[btn] = SoundFor(def);

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
            _charKeys.Add(new CharKey(def, main, btn));
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
            else if (def.Kind == KeyKind.Clipboard)
            {
                btn.Content = KeyIcon("M8.5,5 L6,5 L6,21 L18,21 L18,5 L15.5,5 M9,3 L15,3 L15,7 L9,7 Z M9,11.5 L15,11.5 M9,15.5 L13.5,15.5", out _);
                btn.ToolTip = "Clipboard: copy, paste and recent copies";
                _clipKey = btn;
            }
            else if (def.Kind == KeyKind.Stickers)
            {
                // A sticker with a peeled corner and a smile.
                btn.Content = KeyIcon("M14.5,3.5 L5.5,3.5 Q3.5,3.5 3.5,5.5 L3.5,18.5 Q3.5,20.5 5.5,20.5 L18.5,20.5 Q20.5,20.5 20.5,18.5 L20.5,9.5 Z M14.5,3.5 L14.5,9.5 L20.5,9.5 M8,14 Q11,17.5 15,14 M8.5,10 L8.5,10.2 M12,10 L12,10.2", out _);
                btn.ToolTip = "Stickers: find a picture and send it as a sticker";
                _stickerKey = btn;
            }
            else if (def.Kind == KeyKind.Swipe)
            {
                btn.Content = KeyIcon("M3.5,16.5 C6,7.5 9.5,7.5 11.5,12.5 S16.5,18 20,7.5 M16.8,8.2 L20,7.5 L20.8,10.6", out _);
                btn.ToolTip = "Swipe: when lit, click the first letter, move over the others, then click the last letter";
                _swipeKey = btn;
            }
            if (def.Kind is KeyKind.Ctrl or KeyKind.Alt or KeyKind.Win)
                _modifierKeys[def.Kind] = btn;
        }

        btn.Click += (_, _) =>
        {
            if (_swipeSuppressClick)
                return; // a swipe ended on this key; it already typed its word
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
        if (_searchMode)
        {
            _backspaceRepeat.Interval = TimeSpan.FromMilliseconds(70);
            _typing.Backspace();
            return;
        }
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

    // ---------- Swipe typing ----------

    /// <summary>
    /// Press on a letter and slide across the others to type a whole word. The page watches
    /// the pointer on its way to the keys (preview events), so the pressed key keeps working
    /// as a normal tap or long-press until the pointer has clearly moved.
    /// </summary>
    void SetUpSwipe()
    {
        _swipeTrail.IsHitTestVisible = false;
        _swipeTrail.StrokeLineJoin = PenLineJoin.Round;
        _swipeTrail.StrokeStartLineCap = PenLineCap.Round;
        _swipeTrail.StrokeEndLineCap = PenLineCap.Round;
        _swipeTrail.SetResourceReference(Shape.StrokeProperty, "AccentFg");
        var overlay = new Canvas { IsHitTestVisible = false, Children = { _swipeTrail } };
        Grid.SetRowSpan(overlay, KeyLayout.Letters.Length);
        _lettersPage.Children.Add(overlay);

        _lettersPage.PreviewMouseLeftButtonDown += Swipe_Down;
        _lettersPage.PreviewMouseMove += Swipe_Move;
        _lettersPage.PreviewMouseLeftButtonUp += Swipe_Up;
        _lettersPage.LostMouseCapture += (_, _) => { if (_swiping) CancelSwipe(); };
    }

    double KeyPitch => _lettersPage.ActualWidth / 10;

    void Swipe_Down(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (_toggleSwiping)
        {
            // The click that ends a no-hold word; the key under it isn't pressed.
            e.Handled = true;
            FinishToggleSwipe(IsLetterKey(source) ? e.GetPosition(_lettersPage) : null);
            return;
        }
        _swiping = false;
        _swipePath.Clear();
        // With Ctrl/Alt/⊞ on, letters are shortcuts, so no swiping.
        bool letter = !(_ctrl || _alt || _win) && IsLetterKey(source);
        if (letter && NoHoldSwipe && _swipeToggleOn)
        {
            e.Handled = true; // the letter starts a word instead of typing itself
            StartToggleSwipe(e.GetPosition(_lettersPage), LetterKeyOf(source)?.Def);
            return;
        }
        _swipeArmed = letter && HoldSwipe;
        if (_swipeArmed) _swipePath.Add(e.GetPosition(_lettersPage));
    }

    void Swipe_Move(object sender, MouseEventArgs e)
    {
        if (_toggleSwiping)
        {
            ToggleSwipe_Move(e.GetPosition(_lettersPage));
            return;
        }
        if (!_swipeArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_lettersPage);
        _swipePath.Add(p);
        if (_swiping)
        {
            _swipeTrail.Points.Add(p);
            return;
        }
        if ((p - _swipePath[0]).Length < KeyPitch * SwipeStartDistance) return;

        // It's a swipe: stop the pressed key's long-press and start drawing the trail.
        _swiping = true;
        _held = null;
        _holdTimer.Stop();
        _swipeTrail.BeginAnimation(OpacityProperty, null);
        _swipeTrail.Opacity = 0.75;
        _swipeTrail.StrokeThickness = Math.Max(4, KeyPitch * 0.14);
        _swipeTrail.Points = new PointCollection(_swipePath);
    }

    void Swipe_Up(object sender, MouseButtonEventArgs e)
    {
        _swipeArmed = false;
        if (!_swiping) return;
        _swiping = false;

        // The key under the pointer is about to see this release as a click; swallow it.
        _swipeSuppressClick = true;
        Dispatcher.BeginInvoke(() => _swipeSuppressClick = false, DispatcherPriority.Input);
        FadeTrail();
        TypeSwipe();
    }

    void CancelSwipe()
    {
        _swiping = _swipeArmed = false;
        FadeTrail();
    }

    void FadeTrail()
    {
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => { if (!_swiping && !_toggleSwiping) _swipeTrail.Points.Clear(); };
        _swipeTrail.BeginAnimation(OpacityProperty, fade);
    }

    bool IsLetterKey(DependencyObject? element)
    {
        while (element != null && element != _lettersPage)
        {
            if (element is ButtonBase button)
                return _charKeys.Any(k => k.Key == button && k.Def.Base is >= 'a' and <= 'z');
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    /// <summary>Where each letter key's centre is on the ABC page right now (it changes as the window resizes).</summary>
    Dictionary<char, SwipePoint> LetterCentres()
    {
        var centres = new Dictionary<char, SwipePoint>();
        foreach (var k in _charKeys)
        {
            if (k.Def.Base is not (>= 'a' and <= 'z') || !k.Key.IsVisible) continue;
            var c = k.Key.TranslatePoint(new Point(k.Key.ActualWidth / 2, k.Key.ActualHeight / 2), _lettersPage);
            centres[k.Def.Base] = new SwipePoint(c.X, c.Y);
        }
        return centres;
    }

    // ---------- Clipboard panel ----------

    const ushort VK_A = 0x41, VK_C = 0x43, VK_V = 0x56, VK_X = 0x58;

    /// <summary>
    /// Big Copy / Cut / Paste / Select all buttons on top, recent copies below (newest first).
    /// Tap a copy to paste it; right-click one to remove it from the list.
    /// </summary>
    Grid BuildClipboardPage()
    {
        var page = new Grid { Visibility = Visibility.Collapsed };
        Grid.SetRow(page, 1);
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(KeyLayout.RowHeights[0], GridUnitType.Star) });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(KeyLayout.RowHeights.Sum() - KeyLayout.RowHeights[0], GridUnitType.Star) });

        var actions = new Grid();
        void Action(string label, double width, Action run, string tip)
        {
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
            var text = new TextBlock { Text = label };
            text.SetResourceReference(TextBlock.FontSizeProperty, "KeyLabelFontSize");
            var btn = new Button { Content = text, ClickMode = ClickMode.Press, ToolTip = tip };
            btn.Style = (Style)FindResource("KeyButton");
            btn.SetResourceReference(BackgroundProperty, "SpecialKeyBg");
            btn.PreviewMouseLeftButtonDown += (_, _) => _app.Sounds.Play("");
            btn.Click += (_, _) => run();
            Grid.SetColumn(btn, actions.Children.Count);
            actions.Children.Add(btn);
        }
        Action("Copy", 1.25, () => CtrlCombo(VK_C), "Ctrl+C");
        Action("Cut", 1, () => CtrlCombo(VK_X), "Ctrl+X");
        Action("Paste", 1.25, () => CtrlCombo(VK_V), "Ctrl+V");
        Action("Select all", 1.75, () => CtrlCombo(VK_A), "Ctrl+A");
        Action("▲", 1, () => ScrollClips(-1), "Scroll up");
        Action("▼", 1, () => ScrollClips(1), "Scroll down");
        Action("Clear", 1.25, () => _clips.Clear(), "Forget the recent copies (your current clipboard stays)");
        Action("ABC", 1.5, () => { ShowPage(_keyPage); RefreshKeys(); }, "Back to the keys");
        page.Children.Add(actions);

        _clipList = new UniformGrid { Columns = 2, VerticalAlignment = VerticalAlignment.Top };
        _clipScroll = new ScrollViewer
        {
            Content = _clipList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
        };
        Grid.SetRow(_clipScroll, 1);
        page.Children.Add(_clipScroll);

        _clipEmpty = new TextBlock
        {
            Text = "Nothing copied yet. Copy something in any app and it shows up here.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20),
        };
        _clipEmpty.SetResourceReference(TextBlock.FontSizeProperty, "KeyLabelFontSize");
        _clipEmpty.SetResourceReference(TextBlock.ForegroundProperty, "KeySubFg");
        Grid.SetRow(_clipEmpty, 1);
        page.Children.Add(_clipEmpty);

        KeyArea.Children.Add(page);
        return page;
    }

    void RefreshClipboardList()
    {
        _clipList.Children.Clear();
        foreach (string item in _clips.Items)
        {
            var text = new TextBlock
            {
                Text = ClipboardHistory.Preview(item),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            text.SetResourceReference(TextBlock.FontSizeProperty, "KeySmallFontSize");
            var btn = new Button
            {
                Content = text,
                ClickMode = ClickMode.Press,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 2, 10, 2),
                ToolTip = item.Length > 600 ? item[..600] + "…" : item,
            };
            btn.Style = (Style)FindResource("KeyButton");
            btn.SetResourceReference(HeightProperty, "ClipItemHeight");
            btn.PreviewMouseLeftButtonDown += (_, _) => _app.Sounds.Play("space");
            btn.Click += (_, _) => PasteClip(item);
            btn.PreviewMouseRightButtonUp += (_, e) =>
            {
                _clips.Remove(item);
                e.Handled = true;
            };
            _clipList.Children.Add(btn);
        }
        _clipEmpty.Visibility = _clips.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Ctrl plus a letter (copy, paste…); the caret or text may change, so stop trusting our copy of it.</summary>
    void CtrlCombo(ushort vk)
    {
        _sender.Press(vk, new[] { KeySender.VK_CONTROL });
        _typing.Reset();
    }

    void ScrollClips(int direction)
    {
        double step = Math.Max(40, _clipScroll.ViewportHeight * 0.8);
        _clipScroll.ScrollToVerticalOffset(_clipScroll.VerticalOffset + direction * step);
    }

    /// <summary>Puts a recent copy back on the clipboard and pastes it, then returns to the keys.</summary>
    void PasteClip(string text)
    {
        if (TrySetClipboard(text))
        {
            CtrlCombo(VK_V);
        }
        else
        {
            // Clipboard busy: type it out instead.
            _sender.Text(text);
            _typing.Reset();
        }
        ShowPage(_keyPage);
        RefreshKeys();
    }

    static bool TrySetClipboard(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(25);
            }
        }
        return false;
    }

    /// <summary>
    /// Adds the clipboard's text to the recent copies. Skips anything an app marks as private
    /// (password managers set these flags so clipboard history tools leave passwords alone).
    /// </summary>
    void CaptureClipboard()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data == null) return;
                if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")) return;
                if (data.GetDataPresent("CanIncludeInClipboardHistory") &&
                    data.GetData("CanIncludeInClipboardHistory") is MemoryStream flag &&
                    flag.Length >= 4 && BitConverter.ToInt32(flag.ToArray(), 0) == 0)
                    return;
                if (data.GetDataPresent(DataFormats.UnicodeText))
                    _clips.Add(data.GetData(DataFormats.UnicodeText) as string);
                return;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(25); // another app has the clipboard open; try again shortly
            }
        }
    }

    void ShowPage(Grid page)
    {
        if (page != _lettersPage) CancelToggleSwipe();
        if (page != _clipPage && page != _stickerPage) _keyPage = page;
        foreach (var p in new[] { _lettersPage, _symbolsPage, _clipPage, _stickerPage })
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        if (page == _clipPage) _clipScroll.ScrollToTop();
    }

    // ---------- Key handling ----------

    void OnKey(KeyDef def)
    {
        _swipedWord = null; // any other key moves on from the last swipe's choices
        _forgetIndex = -1; // and from a suggestion waiting to be forgotten
        _forgetSuggestion = null;
        CancelToggleSwipe(); // a key outside the letters (Esc, Ctrl, Swipe…) drops a half-drawn word
        if (_searchMode && !SearchKey(def))
        {
            RefreshKeys();
            return;
        }
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
                ShowPage(_symbolsPage.Visibility == Visibility.Visible ? _lettersPage : _symbolsPage);
                break;
            case KeyKind.Clipboard:
                ShowPage(_clipPage.Visibility == Visibility.Visible ? _keyPage : _clipPage);
                break;
            case KeyKind.Stickers:
                if (_stickerPage.Visibility == Visibility.Visible) ShowPage(_keyPage);
                else ShowStickerPage();
                break;
            case KeyKind.Swipe:
                ToggleSwipeKey();
                if (_swipeToggleOn && _lettersPage.Visibility != Visibility.Visible) ShowPage(_lettersPage);
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
        CancelToggleSwipe();
        if (_searchMode && index == 0)
        {
            SubmitSearch(); // the first slot shows the search; tapping it searches
            RefreshKeys();
            return;
        }
        if (_suggestions[index] is not { } s) return;
        if (ShowingSwipeChoices && _typing.ReplaceSwipedWord(_swipedWord!, s.Text) is { } typed)
        {
            // Keep the word it replaced on offer, in case that was right after all.
            _swipeChoices.Remove(s.Text);
            _swipeChoices.Insert(0, _swipedWord!);
            _swipedWord = typed;
            RefreshKeys();
            return;
        }
        _typing.Accept(s);
    }

    void StartForgetHold(int index)
    {
        _forgetHoldFired = false;
        _forgetHoldTimer.Stop();
        if (!_app.Settings.HoldToForget || _searchMode || _suggestions[index] is not { IsExpansion: false }) return;
        _forgetHeldIndex = index;
        _forgetHoldTimer.Start();
    }

    void ForgetHold_Tick(object? sender, EventArgs e)
    {
        _forgetHoldTimer.Stop();
        if (_suggestions[_forgetHeldIndex] is not { } s) return;
        _forgetHoldFired = true;
        _forgetIndex = _forgetHeldIndex;
        _forgetSuggestion = s;
        _forgetConfirmTimer.Stop();
        _forgetConfirmTimer.Start();
        _app.Sounds.Play("shift");
        RefreshKeys();
    }

    void ConfirmForget()
    {
        var s = _forgetSuggestion;
        _forgetConfirmTimer.Stop();
        _forgetIndex = -1;
        _forgetSuggestion = null;
        if (s != null && _typing.Forget(s))
        {
            _app.Sounds.Play("backspace");
            _swipeChoices.Remove(s.Text);
        }
        RefreshKeys();
    }

    void CancelForget()
    {
        _forgetConfirmTimer.Stop();
        if (_forgetIndex < 0) return;
        _forgetIndex = -1;
        _forgetSuggestion = null;
        RefreshKeys();
    }

    /// <summary>Right after a swipe, the suggestion bar offers the other guesses for that word.</summary>
    bool ShowingSwipeChoices =>
        _swipedWord != null && _swipeChoices.Count > 0 && _typing.SwipedWordIsLast(_swipedWord);

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
        if (_clipKey != null) _clipKey.Tag = _clipPage.Visibility == Visibility.Visible ? "on" : null;
        if (_stickerKey != null) _stickerKey.Tag = _stickerPage.Visibility == Visibility.Visible || _searchMode ? "on" : null;
        if (_swipeKey != null) _swipeKey.Tag = _swipeToggleOn ? "on" : null;

        var list = _typing.Private ? new List<Suggestion>()
            : ShowingSwipeChoices
            ? _swipeChoices.Take(SuggestionCount).Select(w => new Suggestion(w, false)).ToList()
            : _typing.GetSuggestions(SuggestionCount);
        if (_searchMode)
            list = new List<Suggestion> { new("", false) };
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
        if (_forgetIndex >= 0 && _forgetSuggestion != null)
        {
            var slot = _suggestButtons[_forgetIndex];
            ((TextBlock)slot.Content).Text = $"Tap to forget “{_forgetSuggestion.Text}”";
            slot.Tag = "forget";
            slot.ToolTip = "Tap to forget it. Tap anything else to keep it.";
            slot.IsEnabled = true;
        }
        // Say why the suggestions are gone, so it doesn't look broken.
        if (_typing.Private)
            ((TextBlock)_suggestButtons[SuggestionCount / 2].Content).Text = "Password box: not learning";
        if (_searchMode)
        {
            var search = _suggestButtons[0];
            ((TextBlock)search.Content).Text = "🔍 " + _query.Value + "▏";
            search.Tag = "on";
            search.ToolTip = "Searching for stickers. Press Enter (or tap here) to search.";
            search.IsEnabled = true;
        }
        // The search text, or the offer to forget a word, gets the whole bar so it can be read.
        int solo = _searchMode ? 0 : _forgetIndex;
        _suggestBar.Columns = solo >= 0 ? 1 : SuggestionCount;
        for (int i = 0; i < SuggestionCount; i++)
            _suggestButtons[i].Visibility = solo < 0 || i == solo ? Visibility.Visible : Visibility.Collapsed;
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
        Resources["ClipItemHeight"] = Math.Max(20, rowHeight - 2 * gapY);
        Resources["StickerItemHeight"] = Math.Max(40, 1.7 * rowHeight - 2 * gapY);
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
