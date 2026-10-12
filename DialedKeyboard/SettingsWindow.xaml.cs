using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DialedKeyboard.Engine;

namespace DialedKeyboard;

public partial class SettingsWindow : Window
{
    readonly App _app;
    readonly double _originalOpacity;
    readonly ObservableCollection<ExpansionItem> _expansions;
    bool _saved;

    public SettingsWindow(App app)
    {
        _app = app;
        InitializeComponent();

        var s = app.Settings;
        _originalOpacity = s.Opacity;
        OpacitySlider.Value = s.Opacity;
        ThemeBox.SelectedIndex = string.Equals(s.Theme, "Light", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        SoundsBox.IsChecked = s.KeySounds;
        SoundVolumeSlider.Value = s.KeySoundVolume;
        AutoCapBox.IsChecked = s.AutoCapitalize;
        DoubleSpaceBox.IsChecked = s.DoubleSpacePeriod;
        SmartPunctBox.IsChecked = s.SmartPunctuation;
        AutoExpandBox.IsChecked = s.AutoExpandOnSpace;

        _expansions = new ObservableCollection<ExpansionItem>(
            s.Expansions.Select(x => new ExpansionItem { Abbreviation = x.Abbreviation, Expansion = x.Expansion }));
        ExpansionGrid.ItemsSource = _expansions;

        PauseLearningBox.IsChecked = s.LearningPaused;
        RefreshLearned();
    }

    sealed record LearnedRow(string Word, int Count, string Next);

    void RefreshLearned()
    {
        // The dictionary belongs to the keyboard thread (it learns as you type), so read it there.
        string filter = LearnedSearch.Text.Trim().ToLowerInvariant();
        var (info, rows) = _app.Dispatcher.Invoke(() => ReadLearned(_app.UserDictionary, filter));
        LearnedInfo.Text = info;
        LearnedList.ItemsSource = rows;
    }

    static (string Info, List<LearnedRow> Rows) ReadLearned(UserDictionary user, string filter)
    {
        string info =
            $"Learned from your typing: {user.Words.Count:N0} words, " +
            $"{user.Bigrams.Sum(b => b.Value.Count):N0} word pairs and " +
            $"{user.Trigrams.Sum(t => t.Value.Count):N0} three-word sequences. " +
            "Words you use often rank higher, and phrases you repeat show up as a purple suggestion. " +
            "Words with digits or symbols are never saved. Changes here apply right away.";

        var rows = user.Words
            .Where(kv => filter.Length == 0 || kv.Key.Contains(filter, StringComparison.Ordinal))
            .OrderByDescending(kv => kv.Value.Count)
            .Take(500)
            .Select(kv => new LearnedRow(
                kv.Value.Display ?? kv.Key,
                kv.Value.Count,
                string.Join(", ", (user.BigramNexts(kv.Key) ?? new Dictionary<string, int>())
                    .OrderByDescending(n => n.Value).Take(3).Select(n => n.Key))))
            .ToList();
        return (info, rows);
    }

    /// <summary>Adds an empty row at the bottom and puts the cursor in its Shortcut cell, ready to type.</summary>
    void AddShortcut_Click(object sender, RoutedEventArgs e)
    {
        ExpansionGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var item = new ExpansionItem();
        _expansions.Add(item);
        ExpansionGrid.SelectedItem = item;
        ExpansionGrid.ScrollIntoView(item);
        ExpansionGrid.UpdateLayout();
        ExpansionGrid.CurrentCell = new DataGridCellInfo(item, ExpansionGrid.Columns[0]);
        ExpansionGrid.Focus();
        ExpansionGrid.BeginEdit();
    }

    void RemoveShortcut_Click(object sender, RoutedEventArgs e)
    {
        ExpansionGrid.CancelEdit(DataGridEditingUnit.Row);
        foreach (var item in ExpansionGrid.SelectedItems.OfType<ExpansionItem>().ToList())
            _expansions.Remove(item);
    }

    void LearnedSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshLearned();

    void ForgetSelected_Click(object sender, RoutedEventArgs e)
    {
        var words = LearnedList.SelectedItems.Cast<LearnedRow>().Select(r => r.Word).ToList();
        _app.Dispatcher.Invoke(() => words.ForEach(_app.UserDictionary.Forget));
        RefreshLearned();
    }

    void ForgetAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Forget every word and phrase the keyboard has learned? This can't be undone.",
                "DialedKeyboard", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _app.Dispatcher.Invoke(() => _app.UserDictionary.Clear());
        RefreshLearned();
    }

    void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        // Live preview; reverted on cancel.
        _app.PreviewOpacity(e.NewValue);
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        ExpansionGrid.CommitEdit(DataGridEditingUnit.Row, true);

        double opacity = OpacitySlider.Value;
        string theme = ThemeBox.SelectedIndex == 1 ? "Light" : "Dark";
        bool sounds = SoundsBox.IsChecked == true, autoCap = AutoCapBox.IsChecked == true,
            doubleSpace = DoubleSpaceBox.IsChecked == true, smartPunct = SmartPunctBox.IsChecked == true,
            autoExpand = AutoExpandBox.IsChecked == true, paused = PauseLearningBox.IsChecked == true;
        int volume = (int)SoundVolumeSlider.Value;
        var expansions = _expansions
            .Where(x => !string.IsNullOrWhiteSpace(x.Abbreviation) && !string.IsNullOrWhiteSpace(x.Expansion))
            .Select(x => new ExpansionItem { Abbreviation = x.Abbreviation.Trim(), Expansion = x.Expansion })
            .ToList();

        // Settings live on the keyboard thread; apply and save them there.
        string? error = _app.Dispatcher.Invoke(() =>
        {
            var s = _app.Settings;
            s.Opacity = opacity;
            s.Theme = theme;
            s.KeySounds = sounds;
            s.KeySoundVolume = volume;
            s.AutoCapitalize = autoCap;
            s.DoubleSpacePeriod = doubleSpace;
            s.SmartPunctuation = smartPunct;
            s.AutoExpandOnSpace = autoExpand;
            s.LearningPaused = paused;
            s.Expansions = expansions;
            _app.ApplySettings();
            try
            {
                s.Save();
                return null;
            }
            catch (IOException ex)
            {
                return ex.Message;
            }
        });
        if (error != null)
        {
            MessageBox.Show(this, "Couldn't save settings: " + error, "DialedKeyboard");
            return;
        }
        _saved = true;
        Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        if (!_saved)
        {
            _app.PreviewOpacity(_originalOpacity);
        }
        base.OnClosed(e);
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.DataDir);
        Process.Start(new ProcessStartInfo("explorer.exe", AppSettings.DataDir) { UseShellExecute = true });
    }
}
