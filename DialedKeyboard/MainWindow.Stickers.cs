using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DialedKeyboard.Engine;
using DialedKeyboard.Native;
using DialedKeyboard.Stickers;

namespace DialedKeyboard;

/// <summary>
/// The sticker panel: search the web for a picture, tap one and its background is removed to
/// make a sticker (saved under "Mine"), then tap a sticker to paste it into the app you're in.
/// </summary>
public partial class MainWindow
{
    /// <summary>What's typed into the sticker search, while the keys are typing there.</summary>
    sealed class QueryBox : ITextOutput
    {
        readonly StringBuilder _text = new();
        public string Value => _text.ToString();
        public void Text(string text) => _text.Append(text);
        public void Backspace(int count) => _text.Length = Math.Max(0, _text.Length - count);
        public void Enter() { } // MainWindow runs the search
        public void Clear() => _text.Clear();
    }

    /// <summary>Pictures are worked on at this size; stickers come out a little smaller once cropped.</summary>
    const int StickerWorkSize = 640;

    static readonly Lazy<BackgroundRemover> Remover = new(() => new BackgroundRemover(BackgroundRemover.DefaultModelPath));

    Grid _stickerPage = null!;
    ButtonBase? _stickerKey;
    ColumnDefinition? _stickerColumn;
    TextBlock _stickerSearchText = null!;
    UniformGrid _stickerList = null!;
    ScrollViewer _stickerScroll = null!;
    TextBlock _stickerStatus = null!;
    Grid _stickerPreview = null!;
    Image _stickerPreviewImage = null!;
    string? _previewPath;
    /// <summary>Cancels the search or sticker being made when something newer starts.</summary>
    CancellationTokenSource? _stickerWork;
    // The search being shown, and how many of its pages are loaded.
    string _searchQuery = "";
    int _searchPage, _searchPages;
    bool _stickerPageFilled;

    // While searching, the keys type into the search box through their own TypingController
    // (same word suggestions and swipe typing, but nothing is learned and nothing reaches the app).
    readonly QueryBox _query = new();
    TypingController _searchTyping = null!;
    bool _searchMode;

    Grid BuildStickerPage()
    {
        _searchTyping = new TypingController(_query, _app.Predictor, new UserDictionary { Paused = true }, new Engine.Expander())
        {
            Options = new TypingOptions
            {
                AutoCapitalize = false, DoubleSpacePeriod = false, SmartPunctuation = false, AutoExpandOnSpace = false,
            },
        };
        _searchTyping.StateChanged += RefreshKeys;

        var page = new Grid { Visibility = Visibility.Collapsed };
        Grid.SetRow(page, 1);
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(KeyLayout.RowHeights[0], GridUnitType.Star) });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(KeyLayout.RowHeights.Sum() - KeyLayout.RowHeights[0], GridUnitType.Star) });

        var actions = new Grid();
        ButtonBase Action(object content, double width, Action run, string tip)
        {
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
            var btn = new Button { Content = content, ClickMode = ClickMode.Press, ToolTip = tip };
            btn.Style = (Style)FindResource("KeyButton");
            btn.SetResourceReference(BackgroundProperty, "SpecialKeyBg");
            btn.PreviewMouseLeftButtonDown += (_, _) => _app.Sounds.Play("");
            btn.Click += (_, _) => run();
            Grid.SetColumn(btn, actions.Children.Count);
            actions.Children.Add(btn);
            return btn;
        }
        TextBlock Label(string text)
        {
            var t = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis };
            t.SetResourceReference(TextBlock.FontSizeProperty, "KeyLabelFontSize");
            return t;
        }
        _stickerSearchText = Label("");
        var search = Action(_stickerSearchText, 4, BeginSearchTyping, "Type what you're looking for, then press Enter");
        search.HorizontalContentAlignment = HorizontalAlignment.Left;
        search.Padding = new Thickness(12, 0, 8, 0);
        search.SetResourceReference(BackgroundProperty, "KeyBg");
        Action(Label("Mine"), 1.5, ShowMyStickers, "Your stickers");
        Action(Label("▲"), 1, () => ScrollStickers(-1), "Scroll up");
        Action(Label("▼"), 1, () => ScrollStickers(1), "Scroll down");
        Action(Label("ABC"), 1.5, () => { ShowPage(_keyPage); RefreshKeys(); }, "Back to the keys");
        page.Children.Add(actions);

        _stickerList = new UniformGrid { Columns = 4, VerticalAlignment = VerticalAlignment.Top };
        _stickerScroll = new ScrollViewer
        {
            Content = _stickerList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
        };
        Grid.SetRow(_stickerScroll, 1);
        page.Children.Add(_stickerScroll);

        _stickerStatus = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20),
            IsHitTestVisible = false,
        };
        _stickerStatus.SetResourceReference(TextBlock.FontSizeProperty, "KeyLabelFontSize");
        _stickerStatus.SetResourceReference(TextBlock.ForegroundProperty, "KeySubFg");
        Grid.SetRow(_stickerStatus, 1);
        page.Children.Add(_stickerStatus);

        _stickerPreview = BuildStickerPreview();
        Grid.SetRow(_stickerPreview, 1);
        page.Children.Add(_stickerPreview);

        RefreshSearchText();
        KeyArea.Children.Add(page);
        return page;
    }

    /// <summary>A freshly made sticker, big, with Send and Back.</summary>
    Grid BuildStickerPreview()
    {
        var preview = new Grid { Visibility = Visibility.Collapsed };
        preview.SetResourceReference(BackgroundProperty, "WindowBg");
        preview.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        preview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _stickerPreviewImage = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(8) };
        RenderOptions.SetBitmapScalingMode(_stickerPreviewImage, BitmapScalingMode.HighQuality);
        preview.Children.Add(_stickerPreviewImage);

        var buttons = new UniformGrid { Rows = 1, Columns = 2 };
        Grid.SetRow(buttons, 1);
        void Add(string text, string background, Action run)
        {
            var label = new TextBlock { Text = text };
            label.SetResourceReference(TextBlock.FontSizeProperty, "KeyLabelFontSize");
            var btn = new Button { Content = label, ClickMode = ClickMode.Press };
            btn.Style = (Style)FindResource("KeyButton");
            btn.SetResourceReference(BackgroundProperty, background);
            btn.SetResourceReference(HeightProperty, "ClipItemHeight");
            btn.PreviewMouseLeftButtonDown += (_, _) => _app.Sounds.Play("space");
            btn.Click += (_, _) => run();
            buttons.Children.Add(btn);
        }
        Add("Send sticker", "AccentBg", () => { if (_previewPath != null) SendSticker(_previewPath); });
        Add("Back", "SpecialKeyBg", () => preview.Visibility = Visibility.Collapsed);
        preview.Children.Add(buttons);
        return preview;
    }

    void ShowStickerPage()
    {
        ShowPage(_stickerPage);
        if (!_stickerPageFilled) ShowMyStickers();
    }

    void ScrollStickers(int direction)
    {
        double step = Math.Max(40, _stickerScroll.ViewportHeight * 0.8);
        _stickerScroll.ScrollToVerticalOffset(_stickerScroll.VerticalOffset + direction * step);
    }

    void SetStickerStatus(string? text)
    {
        _stickerStatus.Text = text ?? "";
        _stickerStatus.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    CancellationToken NewStickerWork()
    {
        _stickerWork?.Cancel();
        _stickerWork = new CancellationTokenSource();
        return _stickerWork.Token;
    }

    // ---------- Typing a search ----------

    void BeginSearchTyping()
    {
        // Modifiers would turn letters into shortcuts sent to the app; searching doesn't want them.
        _ctrl = _alt = _win = false;
        HoldRightCtrl(false);
        _query.Clear();
        _searchTyping.Reset();
        _searchTyping.ShiftOnce = _searchTyping.CapsLock = false;
        _typing = _searchTyping;
        _searchMode = true;
        ShowPage(_lettersPage);
        RefreshKeys();
    }

    void EndSearchTyping()
    {
        if (!_searchMode) return;
        _searchMode = false;
        _typing = _app.Typing;
        _swipedWord = null;
        RefreshSearchText();
    }

    void RefreshSearchText() =>
        _stickerSearchText.Text = _query.Value.Trim() is { Length: > 0 } q ? "🔍  " + q : "🔍  Search pictures…";

    /// <summary>
    /// While searching, keys type into the search box. Returns true if the key should be handled
    /// as usual (it only touches the search's TypingController), false if it was dealt with here.
    /// </summary>
    bool SearchKey(KeyDef def)
    {
        switch (def.Kind)
        {
            case KeyKind.Char or KeyKind.Space or KeyKind.Shift or KeyKind.Page or KeyKind.Swipe:
                return true;
            case KeyKind.Backspace:
                _typing.Backspace();
                return false;
            case KeyKind.DeleteWord:
                _typing.DeleteWord();
                return false;
            case KeyKind.Enter:
                SubmitSearch();
                return false;
            case KeyKind.Clipboard:
                EndSearchTyping();
                return true;
            case KeyKind.Stickers:
            case KeyKind.Nav when def.Vk == KeySender.VK_ESCAPE:
                EndSearchTyping();
                ShowStickerPage();
                return false;
            default:
                return false; // Ctrl, Alt, ⊞, Tab, arrows: nothing to do in a search box
        }
    }

    void SubmitSearch()
    {
        string q = _query.Value.Trim();
        EndSearchTyping();
        ShowStickerPage();
        if (q.Length > 0) _ = RunSearch(q);
    }

    // ---------- Search results ----------

    async Task RunSearch(string query)
    {
        var ct = NewStickerWork();
        _stickerPageFilled = true;
        _stickerPreview.Visibility = Visibility.Collapsed;
        _stickerList.Children.Clear();
        SetStickerStatus($"Searching for “{query}”…");
        _searchQuery = query;
        _searchPage = _searchPages = 0;
        await LoadMoreResults(null, ct);
    }

    /// <summary>
    /// Adds the next page of results to the end of the list, then a "More pictures" tile if there
    /// are more. Returns false if the search couldn't be reached.
    /// </summary>
    async Task<bool> LoadMoreResults(ButtonBase? moreTile, CancellationToken ct)
    {
        int page = _searchPage + 1;
        SearchPage found;
        try
        {
            found = await StickerSearch.SearchAsync(_searchQuery, page, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (!ct.IsCancellationRequested && page == 1)
                SetStickerStatus("Couldn't reach the picture search. Check that you're connected to the internet, then try again.");
            return false;
        }
        if (ct.IsCancellationRequested) return true;
        _searchPage = page;
        _searchPages = found.Pages;
        if (page == 1)
        {
            if (found.Images.Count == 0)
            {
                SetStickerStatus($"No pictures found for “{_searchQuery}”. Try a different word.");
                return true;
            }
            SetStickerStatus(null);
            _stickerScroll.ScrollToTop();
        }
        if (moreTile != null) _stickerList.Children.Remove(moreTile);

        // Not disposed: previews still loading use it after this returns.
        var throttle = new SemaphoreSlim(4);
        foreach (var result in found.Images)
        {
            var image = new Image { Stretch = Stretch.Uniform };
            string credit = result.Creator.Length > 0 ? $" by {result.Creator}" : "";
            var btn = StickerButton(image, $"{result.Title}{credit} ({result.License.ToUpperInvariant()})\nTap to make it a sticker.");
            btn.Click += (_, _) => _ = MakeSticker(result);
            _stickerList.Children.Add(btn);
            _ = LoadThumbnail(result, image, throttle, ct);
        }
        if (_searchPage < _searchPages) _stickerList.Children.Add(MoreTile(ct));
        return true;
    }

    /// <summary>The last tile of the results: loads the next page.</summary>
    ButtonBase MoreTile(CancellationToken ct)
    {
        var label = new TextBlock
        {
            Text = "More pictures",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.FontSizeProperty, "KeyLabelFontSize");
        var tile = new Button { Content = label, ClickMode = ClickMode.Release, ToolTip = "Show the next 20 pictures" };
        tile.Style = (Style)FindResource("KeyButton");
        tile.SetResourceReference(BackgroundProperty, "SpecialKeyBg");
        tile.SetResourceReference(HeightProperty, "StickerItemHeight");
        tile.PreviewMouseLeftButtonDown += (_, _) => _app.Sounds.Play("space");
        tile.Click += async (_, _) =>
        {
            if (!tile.IsEnabled) return;
            label.Text = "Loading…";
            tile.IsEnabled = false;
            if (!await LoadMoreResults(tile, ct) && !ct.IsCancellationRequested)
            {
                label.Text = "Couldn't load more. Tap to try again";
                tile.IsEnabled = true;
            }
        };
        return tile;
    }

    static async Task LoadThumbnail(WebImage result, Image target, SemaphoreSlim throttle, CancellationToken ct)
    {
        try { await throttle.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }
        try
        {
            foreach (string url in StickerSearch.PreviewUrls(result))
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    byte[] data = await StickerSearch.DownloadAsync(url, ct);
                    target.Source = await Task.Run(() => StickerFiles.Thumbnail(data, 240), ct);
                    return;
                }
                catch (Exception)
                {
                    // That source failed (or the search moved on); try the next. If none work, the tile stays blank.
                }
            }
        }
        finally
        {
            throttle.Release();
        }
    }

    ButtonBase StickerButton(Image image, string tip)
    {
        var btn = new Button
        {
            Content = image,
            ClickMode = ClickMode.Release,
            Padding = new Thickness(4),
            ToolTip = tip,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        btn.Style = (Style)FindResource("KeyButton");
        btn.SetResourceReference(HeightProperty, "StickerItemHeight");
        btn.PreviewMouseLeftButtonDown += (_, _) => _app.Sounds.Play("space");
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return btn;
    }

    // ---------- Making a sticker ----------

    async Task MakeSticker(WebImage source)
    {
        var ct = NewStickerWork();
        SetStickerStatus("Making your sticker…");
        _stickerScroll.Visibility = Visibility.Hidden;
        try
        {
            // Some sites refuse downloads and some formats won't open, so try each copy of the picture in turn.
            Picture? photo = null;
            foreach (string url in StickerSearch.FullSizeUrls(source))
            {
                try
                {
                    byte[] data = await StickerSearch.DownloadAsync(url, ct);
                    photo = await Task.Run(() => StickerFiles.Decode(data, StickerWorkSize), ct);
                    break;
                }
                catch (Exception) when (!ct.IsCancellationRequested) { }
            }
            if (ct.IsCancellationRequested) return;
            if (photo == null)
            {
                SetStickerStatus("Couldn't download that picture. Try another one.");
                await Task.Delay(2500, ct);
                SetStickerStatus(null);
                return;
            }
            string? path = await Task.Run(() =>
            {
                var mask = Remover.Value.Mask(photo);
                int outline = Math.Max(3, (int)Math.Round(Math.Max(photo.Width, photo.Height) * 0.02));
                var sticker = StickerMaker.Cut(photo, mask, outline);
                return sticker == null ? null : StickerFiles.Save(StickerFiles.ToPng(sticker));
            }, ct);
            if (ct.IsCancellationRequested) return;
            if (path == null)
            {
                SetStickerStatus("Couldn't find a clear subject in that picture. Try another one.");
                await Task.Delay(2500, ct);
                SetStickerStatus(null);
                return;
            }
            _previewPath = path;
            _stickerPreviewImage.Source = StickerFiles.Thumbnail(await File.ReadAllBytesAsync(path, ct), 512);
            _stickerPreview.Visibility = Visibility.Visible;
            SetStickerStatus(null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Downloads, odd picture formats and timeouts all end up here; none should take the keyboard down.
            SetStickerStatus(ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException
                ? "The sticker maker couldn't start. Reinstalling Dialed Keyboard should fix it."
                : "Couldn't use that picture. Try another one.");
            await Task.Delay(2500);
            if (!ct.IsCancellationRequested) SetStickerStatus(null);
        }
        finally
        {
            if (!ct.IsCancellationRequested) _stickerScroll.Visibility = Visibility.Visible;
        }
    }

    // ---------- Your stickers ----------

    void ShowMyStickers()
    {
        var ct = NewStickerWork();
        _stickerPageFilled = true;
        _stickerPreview.Visibility = Visibility.Collapsed;
        _stickerScroll.Visibility = Visibility.Visible;
        _stickerList.Children.Clear();
        var paths = StickerFiles.All();
        SetStickerStatus(paths.Count == 0
            ? "No stickers yet. Tap 🔍 Search, type what you'd like (such as “cat”) and press Enter. " +
              "Pick a picture and its background is removed to make a sticker."
            : null);
        foreach (string path in paths)
        {
            var image = new Image { Stretch = Stretch.Uniform };
            var btn = StickerButton(image, "Tap to send. Right-click to delete.");
            btn.Click += (_, _) => SendSticker(path);
            btn.PreviewMouseRightButtonUp += (_, e) =>
            {
                StickerFiles.Delete(path);
                _stickerList.Children.Remove(btn);
                e.Handled = true;
            };
            _stickerList.Children.Add(btn);
            _ = LoadSavedSticker(path, image, ct);
        }
        _stickerScroll.ScrollToTop();
    }

    static async Task LoadSavedSticker(string path, Image target, CancellationToken ct)
    {
        try
        {
            byte[] data = await File.ReadAllBytesAsync(path, ct);
            target.Source = await Task.Run(() => StickerFiles.Thumbnail(data, 240), ct);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or NotSupportedException or FileFormatException) { }
    }

    // ---------- Sending ----------

    /// <summary>
    /// Puts the sticker on the clipboard (as a see-through PNG, plus a plain copy on white for
    /// apps that can't do transparency) and pastes it into the app being typed in.
    /// </summary>
    void SendSticker(string path)
    {
        byte[] png;
        BitmapSource image;
        try
        {
            png = File.ReadAllBytes(path);
            image = StickerFiles.Thumbnail(png, 0);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException)
        {
            SetStickerStatus("That sticker couldn't be opened.");
            return;
        }
        var data = new DataObject();
        data.SetData("PNG", new MemoryStream(png), false);
        data.SetImage(StickerFiles.OnWhite(image));
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                CtrlCombo(VK_V);
                ShowPage(_keyPage);
                RefreshKeys();
                return;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(25);
            }
        }
        SetStickerStatus("The clipboard is busy. Try again in a moment.");
    }
}
