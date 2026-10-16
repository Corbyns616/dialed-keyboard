using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DialedKeyboard.Engine;
using DialedKeyboard.Native;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace DialedKeyboard;

public partial class App : Application
{
    const string ShowSignalName = "DialedKeyboard.ShowSignal";

    Mutex? _singleInstance;
    EventWaitHandle? _showSignal;
    RegisteredWaitHandle? _showSignalWait;
    WinForms.NotifyIcon? _tray;
    DispatcherTimer? _saveTimer;
    SettingsWindow? _settingsWindow;
    MiniButton? _mini;

    public AppSettings Settings { get; private set; } = new();
    public UserDictionary UserDictionary { get; private set; } = new();
    public Expander Expander { get; } = new();
    public TypingController Typing { get; private set; } = null!;
    public WordPredictor Predictor { get; private set; } = null!;
    public KeySender Sender { get; } = new();
    public KeySounds Sounds { get; private set; } = null!;
    MainWindow _main = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        _singleInstance = new Mutex(true, "DialedKeyboard.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            // Already running (maybe minimized): ask that copy to show itself instead.
            if (EventWaitHandle.TryOpenExisting(ShowSignalName, out var signal))
            {
                signal.Set();
                signal.Dispose();
            }
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        _showSignalWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(new Action(RestoreKeyboard)), null, Timeout.Infinite, executeOnlyOnce: false);

        Settings = AppSettings.Load();
        UserDictionary = UserDictionary.Load(AppSettings.UserDictionaryPath);
        Predictor = WordPredictor.LoadDefault(UserDictionary);
        Typing = new TypingController(Sender, Predictor, UserDictionary, Expander);
        Sounds = new KeySounds();
        ApplySettings();

        _main = new MainWindow(this);
        _main.Show();
        _mini = new MiniButton(this);
        _mini.Show();

        CreateTray();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _saveTimer.Tick += (_, _) => SaveUserDictionary();
        _saveTimer.Start();
    }

    /// <summary>Pushes the current settings into the engine, theme and windows.</summary>
    public void ApplySettings()
    {
        Expander.Set(Settings.Expansions.Select(x => (x.Abbreviation, x.Expansion)));
        UserDictionary.Paused = Settings.LearningPaused;
        Typing.Options = new TypingOptions
        {
            AutoCapitalize = Settings.AutoCapitalize,
            DoubleSpacePeriod = Settings.DoubleSpacePeriod,
            SmartPunctuation = Settings.SmartPunctuation,
            AutoExpandOnSpace = Settings.AutoExpandOnSpace,
            ProtectPasswords = Settings.ProtectPasswords,
        };
        Sounds.Enabled = Settings.KeySounds;
        Sounds.Volume = Math.Clamp(Settings.KeySoundVolume, 0, 100) / 100f;
        ApplyTheme(Settings.Theme);
        _main?.ApplySettings();
    }

    void ApplyTheme(string theme)
    {
        bool light = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);
        var colors = light
            ? new Dictionary<string, string>
            {
                ["WindowBg"] = "#F2F2F4", ["KeyBg"] = "#FFFFFF", ["SpecialKeyBg"] = "#DCDCE0",
                ["KeyHover"] = "#E8E8EC", ["KeyPressed"] = "#C8C8CE", ["KeyFg"] = "#1B1B1D",
                ["KeySubFg"] = "#8E8E93", ["AccentFg"] = "#1A73E8", ["AccentBg"] = "#9DB4FF", ["SuggestBg"] = "#E4E4E8",
                ["ExpansionBg"] = "#BFE5CC", ["PhraseBg"] = "#DCCBF5", ["ForgetBg"] = "#F3C2C2",
            }
            : new Dictionary<string, string>
            {
                ["WindowBg"] = "#010101", ["KeyBg"] = "#3A3A3C", ["SpecialKeyBg"] = "#171719",
                ["KeyHover"] = "#48484B", ["KeyPressed"] = "#5E5E62", ["KeyFg"] = "#F5F5F7",
                ["KeySubFg"] = "#9A999E", ["AccentFg"] = "#5E93F5", ["AccentBg"] = "#3B5BDB", ["SuggestBg"] = "#171719",
                ["ExpansionBg"] = "#2F6B46", ["PhraseBg"] = "#5B3F8C", ["ForgetBg"] = "#8E2B2B",
            };
        foreach (var (key, hex) in colors)
            Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    /// <summary>
    /// Settings runs on its own UI thread, like a separate app. Keyboard focus is per thread, so
    /// tapping the on-screen keyboard (this thread) no longer pulls focus out of the box being
    /// typed into in Settings. Anything Settings touches here goes through <see cref="Dispatcher"/>.
    /// </summary>
    public void ShowSettings()
    {
        if (_settingsWindow is { } open)
        {
            open.Dispatcher.BeginInvoke(() => open.Activate());
            return;
        }
        var thread = new Thread(() =>
        {
            var w = new SettingsWindow(this);
            w.Closed += (_, _) =>
            {
                Dispatcher.BeginInvoke(() => _settingsWindow = null);
                w.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            };
            Dispatcher.Invoke(() => _settingsWindow = w);
            w.Show();
            w.Activate();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    /// <summary>Live opacity preview from Settings.</summary>
    public void PreviewOpacity(double opacity) => Dispatcher.BeginInvoke(() => _main.Opacity = opacity);

    public void ToggleKeyboard()
    {
        if (_main.IsVisible) MinimizeKeyboard();
        else RestoreKeyboard();
    }

    /// <summary>Hides the keyboard; the floating ⌨ button stays on screen to bring it back.</summary>
    public void MinimizeKeyboard()
    {
        _main.SaveBounds();
        _main.Hide();
    }

    public void RestoreKeyboard() => _main.Show();

    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            File.AppendAllText(Path.Combine(AppSettings.DataDir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { }
        // Keep typing working: log the error rather than closing the keyboard.
        e.Handled = true;
    }

    public void ExitApp()
    {
        _main.SaveBounds();
        Settings.Save();
        SaveUserDictionary();
        Shutdown();
    }

    void SaveUserDictionary()
    {
        if (!UserDictionary.Dirty) return;
        try { UserDictionary.Save(AppSettings.UserDictionaryPath); }
        catch (IOException) { /* try again on the next tick */ }
    }

    void CreateTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Show / hide keyboard   Ctrl+Alt+K", null, (_, _) => ToggleKeyboard());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _tray = new WinForms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "DialedKeyboard",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) ToggleKeyboard();
        };
    }

    static Drawing.Icon CreateTrayIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var body = new Drawing.SolidBrush(Drawing.Color.FromArgb(79, 124, 255));
            g.FillRectangle(body, 1, 6, 30, 20);
            using var key = new Drawing.SolidBrush(Drawing.Color.White);
            for (int row = 0; row < 2; row++)
                for (int col = 0; col < 5; col++)
                    g.FillRectangle(key, 4 + col * 5, 9 + row * 5, 3, 3);
            g.FillRectangle(key, 8, 20, 16, 3);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        Sounds?.Dispose();
        _showSignalWait?.Unregister(null);
        _showSignal?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
