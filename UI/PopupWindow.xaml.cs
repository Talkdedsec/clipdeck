using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClipDeck.Core;
using ClipDeck.Native;

namespace ClipDeck.UI;

public partial class PopupWindow : Window
{
    enum View { History, Pinned, Snippets, Emoji, Kaomoji, Symbols }

    enum ClipFilter { All, Text, Link, Image, Files, Code, Color, Sensitive }

    static readonly (string Name, ClipFilter Filter)[] Filters =
    [
        ("Tümü", ClipFilter.All), ("Metin", ClipFilter.Text), ("Link", ClipFilter.Link), ("Resim", ClipFilter.Image),
        ("Dosya", ClipFilter.Files), ("Kod", ClipFilter.Code), ("Renk kodu", ClipFilter.Color), ("Gizli", ClipFilter.Sensitive),
    ];

    static string ClipHint => Loc.T("Enter yapıştır · Shift+Enter düz metin · Ctrl+Space çoklu seç · Sağ tık menü");
    static string PickHint => Loc.T("Enter ekle · Shift+Enter ekle, açık kalsın · Oklarla gez · Tab bölüm");
    static readonly string[] HandTones = ["✋", "✋🏻", "✋🏼", "✋🏽", "✋🏾", "✋🏿"];
    static readonly string[] ToneNames = ["Varsayılan", "Açık", "Orta açık", "Orta", "Orta koyu", "Koyu"];

    readonly DispatcherTimer _searchTimer;
    readonly DispatcherTimer _statusTimer;
    readonly DispatcherTimer _idleTimer;
    readonly List<ClipItem> _checked = [];
    ClipFilter _filter = ClipFilter.All;
    string? _filterApp;
    PreviewWindow? _preview;
    bool PreviewOpen => _preview?.IsVisible == true;
    View _view = View.History;
    View _lastClipView = View.History;
    IntPtr _target;
    IntPtr _targetFocus;
    IntPtr _hwnd;
    bool _busy;
    bool _menuOpen;
    bool _opening;
    bool _warming;
    bool _suppress;
    int _appliedTone = -1;
    string? _pickKey;

    List<PickRow> _rows = [];
    readonly List<PickItem> _flat = [];
    readonly List<(int line, int col)> _pos = [];
    readonly List<int> _lineStart = [];
    readonly List<int> _lineRow = [];
    readonly Dictionary<PickCategory, int> _headerRow = [];
    List<PickCategory> _barCategories = [];
    int _sel = -1;
    ScrollViewer? _pickScroll;

    static App AppRef => (App)Application.Current;
    static ClipManager Manager => AppRef.Manager;
    bool IsPicker => _view >= View.Emoji;

    PickSection? Section => _view switch
    {
        View.Emoji => PickerData.Emoji,
        View.Kaomoji => PickerData.Kaomoji,
        View.Symbols => PickerData.Symbols,
        _ => null,
    };

    public PopupWindow()
    {
        InitializeComponent();
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            Refresh(false);
        };
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _statusTimer.Tick += (_, _) => ResetStatus();
        // Bitmaps, emoji renders and list visuals are only needed while the panel is up; drop them after a quiet minute.
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer.Stop();
            if (!IsVisible) ReleaseMemory();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        // The menu key is handled on release: opening on key-down lets the system's own context-menu message close it again.
        PreviewKeyUp += (_, e) =>
        {
            if (e.Key == Key.Apps && !_menuOpen && !IsPicker && SelectedClip is { } sel)
            {
                OpenMenu(sel, null);
                e.Handled = true;
            }
        };
        // Taking the foreground from another process briefly deactivates us; ignore that while opening.
        Deactivated += (_, _) =>
        {
            if (IsVisible && !_menuOpen && !_opening) HidePopup();
        };
        SourceInitialized += (_, _) => InitNative();
        DpiChanged += (_, _) => UpdateDpi();
        List.SelectionChanged += (_, _) =>
        {
            if (PreviewOpen) UpdatePreview();
        };
        ApplyViewChrome();
    }

    // ---- preview ----

    void TogglePreview()
    {
        if (PreviewOpen)
        {
            ClosePreview();
            return;
        }
        UpdatePreview();
    }

    void UpdatePreview()
    {
        if (IsPicker || SelectedClip is not { } item)
        {
            ClosePreview();
            return;
        }
        try
        {
            _preview ??= new PreviewWindow();
            _preview.ShowFor(item, Manager.GetPayload(item), _hwnd);
            Status.Text = Loc.T("Space / F3 önizlemeyi kapatır · Shift+↑↓ kaydırır");
            Status.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        }
        catch (Exception ex)
        {
            Log.Error("önizleme", ex);
            ShowStatus(Loc.T("Önizleme açılamadı."), true);
        }
    }

    void ClosePreview()
    {
        if (_preview is null) return;
        bool wasOpen = _preview.IsVisible;
        _preview.HidePreview(release: true);
        if (wasOpen && IsVisible) ResetStatus();
    }

    void InitNative()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
        ex = (ex | Win32.WS_EX_TOOLWINDOW) & ~Win32.WS_EX_APPWINDOW;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        Win32.SetDwmInt(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, 2);
    }

    public void EnsureCreated() => new WindowInteropHelper(this).EnsureHandle();

    public void Toggle(IntPtr target, HotkeyKind kind = HotkeyKind.Clipboard)
    {
        bool emoji = kind == HotkeyKind.Emoji;
        if (!IsVisible)
        {
            ShowFor(target, emoji: emoji);
            return;
        }
        if (emoji == (_view == View.Emoji)) HidePopup();
        else ChangeView(emoji ? View.Emoji : _lastClipView);
    }

    public void ShowFor(IntPtr target, bool keepState = false, bool emoji = false)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Open(target, keepState, emoji);
        }
        finally
        {
            if (timer.ElapsedMilliseconds > 120) Log.Write($"panel yavaş açıldı: {timer.ElapsedMilliseconds} ms ({Manager.Items.Count} öğe)");
        }
    }

    void Open(IntPtr target, bool keepState, bool emoji)
    {
        _idleTimer.Stop();
        if (target == _hwnd) target = IntPtr.Zero;
        _target = target;
        _targetFocus = target == IntPtr.Zero ? IntPtr.Zero : Win32.FocusOf(target);

        var s = AppRef.Settings;
        Theme.Apply(s);
        Width = s.PanelWidth;
        Height = s.PanelHeight;
        UpdatePauseButton();

        var previous = _sel >= 0 && _sel < _flat.Count ? _flat[_sel] : null;
        _searchTimer.Stop();
        if (!keepState)
        {
            _suppress = true;
            SearchBox.Text = "";
            _suppress = false;
            _filter = ClipFilter.All;
            _filterApp = null;
            ApplyFilterUi();
            SetView(emoji ? View.Emoji : View.History);
        }

        var (anchor, below) = GetAnchor(target);
        EnsureCreated();
        _opening = true;
        try
        {
            Win32.SetDwmInt(_hwnd, Win32.DWMWA_CLOAK, 1);
            Show();
            PlaceNear(anchor, below);
            UpdateLayout();
            UpdateDpi();
            ApplyBackdrop();
            Refresh(keepState);
            if (keepState && previous is not null) SelectPick(previous);
            UpdateLayout();
            Win32.SetDwmInt(_hwnd, Win32.DWMWA_CLOAK, 0);

            Win32.ForceForeground(_hwnd);
            Activate();
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.CaretIndex = SearchBox.Text.Length;
        }
        finally
        {
            _opening = false;
        }
        if (Win32.GetForegroundWindow() != _hwnd) Log.Write("panel öne alınamadı");
    }

    // The first layout of the list and emoji templates costs a few hundred ms; pay it invisibly while idle.
    public void WarmUp()
    {
        if (IsVisible) return;
        EnsureCreated();
        Win32.SetDwmInt(_hwnd, Win32.DWMWA_CLOAK, 1);
        _opening = _warming = true;
        try
        {
            Width = AppRef.Settings.PanelWidth;
            Height = AppRef.Settings.PanelHeight;
            Show();
            // Moving to a monitor with another scale factor rebuilds every visual (~1 s the first time),
            // so warm up on each monitor, ending on the one with the cursor.
            var cursor = System.Windows.Forms.Cursor.Position;
            foreach (var screen in System.Windows.Forms.Screen.AllScreens.OrderBy(s => s.Bounds.Contains(cursor) ? 1 : 0))
            {
                var wa = screen.WorkingArea;
                Win32.SetWindowPos(_hwnd, IntPtr.Zero, wa.Left + 1, wa.Top + 1, 0, 0,
                    Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
                UpdateDpi();
                foreach (var view in new[] { View.Emoji, View.History })
                {
                    SetView(view);
                    Refresh(false);
                    UpdateLayout();
                }
            }
            Hide();
        }
        finally
        {
            _opening = _warming = false;
            Win32.SetDwmInt(_hwnd, Win32.DWMWA_CLOAK, 0);
        }
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    public void HidePopup()
    {
        if (!IsVisible) return;
        ClosePreview();
        Hide();
        _statusTimer.Stop();
        ClearChecks();
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    public void RefreshIfVisible()
    {
        if (IsVisible && !IsPicker) Refresh(true);
    }

    void ReleaseMemory()
    {
        List.ItemsSource = null;
        PickList.ItemsSource = null;
        CategoryBar.ItemsSource = null;
        BgImage.Source = null;
        foreach (var it in _flat) it.IsSelected = false;
        _rows = [];
        _flat.Clear();
        _pos.Clear();
        _lineStart.Clear();
        _lineRow.Clear();
        _headerRow.Clear();
        _barCategories = [];
        _sel = -1;
        // Emoji bitmaps stay cached: they are tiny, and re-rendering them would make the next Win+. feel slow.
        Backdrop.Clear();
        Manager.ReleaseThumbnails();
        App.TrimMemory();
    }

    // ---- multi-select & filters ----

    void ToggleCheck(ClipItem item)
    {
        item.IsChecked = !item.IsChecked;
        if (item.IsChecked) _checked.Add(item);
        else _checked.Remove(item);
        UpdateCheckStatus();
    }

    void ClearChecks()
    {
        foreach (var it in _checked) it.IsChecked = false;
        _checked.Clear();
        UpdateCheckStatus();
    }

    void UpdateCheckStatus()
    {
        if (_checked.Count == 0)
        {
            ResetStatus();
            return;
        }
        _statusTimer.Stop();
        Status.Text = Loc.F("{0} öğe seçili · Enter birleştirip yapıştırır · Esc seçimi temizler", _checked.Count);
        Status.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
    }

    bool PassesFilter(ClipItem it)
    {
        bool kind = _filter switch
        {
            ClipFilter.Text => it.IsText && !it.IsLink && !it.IsCode && !it.HasColor,
            ClipFilter.Link => it.IsLink,
            ClipFilter.Image => it.IsImage,
            ClipFilter.Files => it.IsFiles,
            ClipFilter.Code => it.IsCode,
            ClipFilter.Color => it.HasColor,
            ClipFilter.Sensitive => it.IsSensitive,
            _ => true,
        };
        return kind && (_filterApp is null || it.AppName == _filterApp);
    }

    void ApplyFilterUi()
    {
        bool active = _filter != ClipFilter.All || _filterApp is not null;
        FilterLabel.Text = _filterApp ?? (active ? Loc.T(Filters.First(f => f.Filter == _filter).Name) : "");
        FilterButton.SetResourceReference(ForegroundProperty, active ? "Accent" : "TextDim");
    }

    void Filter_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = FilterButton, Placement = PlacementMode.Bottom };
        foreach (var (name, filter) in Filters)
        {
            bool on = _filter == filter && _filterApp is null;
            menu.Items.Add(Entry(Loc.T(name), on ? "✓" : null, () => SetFilter(filter, null)));
        }
        var apps = Manager.Items
            .Where(i => !i.IsSnippet && !string.IsNullOrEmpty(i.AppName))
            .GroupBy(i => i.AppName!)
            .OrderByDescending(g => g.Count())
            .Take(12)
            .Select(g => g.Key)
            .ToList();
        if (apps.Count > 0)
        {
            menu.Items.Add(new Separator());
            var sub = Entry(Loc.T("Uygulama"), null, null);
            foreach (var app in apps)
                sub.Items.Add(Entry(app, _filterApp == app ? "✓" : null, () => SetFilter(ClipFilter.All, app)));
            menu.Items.Add(sub);
        }
        ShowMenu(menu);
    }

    void SetFilter(ClipFilter filter, string? app)
    {
        _filter = filter;
        _filterApp = app;
        ApplyFilterUi();
        Refresh(false);
    }

    void ShowMenu(ContextMenu menu)
    {
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (IsVisible)
            {
                Activate();
                SearchBox.Focus();
            }
        };
        menu.IsOpen = true;
    }

    // ---- placement, DPI, background ----

    static (Win32.POINT point, int below) GetAnchor(IntPtr target)
    {
        if (target != IntPtr.Zero)
        {
            uint tid = Win32.GetWindowThreadProcessId(target, out _);
            var gi = new Win32.GUITHREADINFO { cbSize = Marshal.SizeOf<Win32.GUITHREADINFO>() };
            if (tid != 0 && Win32.GetGUIThreadInfo(tid, ref gi) && gi.hwndCaret != IntPtr.Zero)
            {
                var p = new Win32.POINT { X = gi.rcCaret.Left, Y = gi.rcCaret.Top };
                if (Win32.ClientToScreen(gi.hwndCaret, ref p) && (p.X != 0 || p.Y != 0))
                    return (p, Math.Max(gi.rcCaret.Bottom - gi.rcCaret.Top, 16) + 6);
            }
        }
        Win32.GetCursorPos(out var c);
        return (c, 14);
    }

    void PlaceNear(Win32.POINT p, int below)
    {
        var mon = Win32.MonitorFromPoint(p, Win32.MONITOR_DEFAULTTONEAREST);
        var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfoW(mon, ref mi);
        var wa = mi.rcWork;
        const uint flags = Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE;

        // First hop onto the target monitor so a per-monitor DPI change resizes the window before we measure it.
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, wa.Left + 1, wa.Top + 1, 0, 0, flags);
        Win32.GetWindowRect(_hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;

        int x = p.X;
        int y = p.Y + below;
        if (y + h > wa.Bottom - 8) y = p.Y - h - 8;
        x = Math.Clamp(x, wa.Left + 8, Math.Max(wa.Left + 8, wa.Right - w - 8));
        y = Math.Clamp(y, wa.Top + 8, Math.Max(wa.Top + 8, wa.Bottom - h - 8));
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, flags);
    }

    double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    void UpdateDpi()
    {
        double scale = DpiScale;
        int emoji = (int)Math.Round(26 * scale), icon = (int)Math.Round(20 * scale);
        if (emoji == PickerData.EmojiPixels && icon == PickerData.IconPixels) return;
        PickerData.EmojiPixels = emoji;
        PickerData.IconPixels = icon;
        PickerData.ResetEmojiImages();
        _appliedTone = -1;
        UpdateSkinToneButton();
    }

    void ApplyBackdrop()
    {
        var s = AppRef.Settings;
        var path = Backdrop.PathFor(s);
        double scale = DpiScale;
        var bmp = Backdrop.Get(path, (int)Math.Round(Width * scale), (int)Math.Round(Height * scale), s.BackgroundBlur * scale);
        if (bmp is null)
        {
            BgImage.Visibility = BgDim.Visibility = Visibility.Collapsed;
            BgImage.Source = null;
            return;
        }
        BgImage.Source = bmp;
        BgDim.Opacity = s.BackgroundDim / 100.0;
        BgImage.Visibility = BgDim.Visibility = Visibility.Visible;
    }

    // ---- views ----

    void SetView(View view)
    {
        _view = view;
        if (!IsPicker) _lastClipView = view;
        _suppress = true;
        (view switch
        {
            View.Emoji => SecEmoji,
            View.Kaomoji => SecKaomoji,
            View.Symbols => SecSymbols,
            _ => SecClipboard,
        }).IsChecked = true;
        (_lastClipView switch { View.Pinned => TabPinned, View.Snippets => TabSnippets, _ => TabHistory }).IsChecked = true;
        _suppress = false;
        ApplyViewChrome();
    }

    void ApplyViewChrome()
    {
        bool picker = IsPicker;
        ClipBar.Visibility = picker ? Visibility.Collapsed : Visibility.Visible;
        List.Visibility = picker ? Visibility.Collapsed : Visibility.Visible;
        PickList.Visibility = CategoryScroll.Visibility = picker ? Visibility.Visible : Visibility.Collapsed;
        NewSnippetButton.Visibility = _view == View.Snippets ? Visibility.Visible : Visibility.Collapsed;
        SkinToneButton.Visibility = _view == View.Emoji ? Visibility.Visible : Visibility.Collapsed;
        SearchHint.Text = picker
            ? Section?.SearchHint ?? Loc.T("Ara")
            : Loc.T("Ara — resimlerdeki yazılar dahil");
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResetStatus();
    }

    void Section_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppress || PickList is null) return;
        var view = sender == SecEmoji ? View.Emoji
            : sender == SecKaomoji ? View.Kaomoji
            : sender == SecSymbols ? View.Symbols
            : _lastClipView;
        ChangeView(view);
    }

    void ClipTab_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppress || PickList is null) return;
        ChangeView(sender == TabPinned ? View.Pinned : sender == TabSnippets ? View.Snippets : View.History);
    }

    void ChangeView(View view)
    {
        bool sectionChanged = IsPicker != (view >= View.Emoji) || (IsPicker && view != _view);
        if (sectionChanged)
        {
            _suppress = true;
            SearchBox.Text = "";
            _suppress = false;
        }
        SetView(view);
        if (IsPicker) ClosePreview();
        Refresh(false);
        SearchBox.Focus();
    }

    void Refresh(bool keepSelection)
    {
        if (List is null) return;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (IsPicker) RefreshPicker();
        else RefreshClipboard(keepSelection);
        if (timer.ElapsedMilliseconds > 50 && !_warming) Log.Write($"liste yavaş yenilendi: {timer.ElapsedMilliseconds} ms ({_view}, \"{SearchBox.Text}\")");
    }

    string[] SearchTerms => TextUtil.Normalize(SearchBox.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    // ---- clipboard view ----

    ClipItem? SelectedClip => List.SelectedItem as ClipItem;

    void RefreshClipboard(bool keepSelection)
    {
        long? keepId = keepSelection ? SelectedClip?.Id : null;
        int keepIndex = Math.Max(0, List.SelectedIndex);
        var terms = SearchTerms;
        var items = new List<ClipItem>();
        foreach (var it in Manager.Items)
        {
            bool inTab = _view switch
            {
                View.History => !it.IsSnippet,
                View.Pinned => it.Pinned && !it.IsSnippet,
                _ => it.IsSnippet,
            };
            if (!inTab || !PassesFilter(it)) continue;
            if (terms.Length > 0 && !terms.All(t => it.SearchIndex.Contains(t, StringComparison.Ordinal))) continue;
            items.Add(it);
        }

        for (int i = 0; i < items.Count; i++)
        {
            items[i].QuickIndex = i < 9 ? (i + 1).ToString() : "";
            items[i].RefreshMeta();
        }

        List.ItemsSource = items;
        if (items.Count == 0)
        {
            bool filtered = _filter != ClipFilter.All || _filterApp is not null;
            ShowEmpty(terms.Length > 0 ? Loc.T("Eşleşen sonuç yok.")
                : filtered ? Loc.T("Bu filtreye uyan öğe yok.")
                : _view switch
                {
                    View.History => Loc.T("Henüz bir şey kopyalanmadı.\nBir şey kopyala, burada görünsün."),
                    View.Pinned => Loc.T("Sabitlenmiş öğe yok.\nBir öğede Ctrl+P ile sabitle."),
                    _ => Loc.T("Snippet yok.\n+ ile yeni ekle ya da bir öğede Ctrl+S."),
                });
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;
        int index = 0;
        if (keepSelection)
        {
            int found = keepId is null ? -1 : items.FindIndex(i => i.Id == keepId);
            index = found >= 0 ? found : Math.Min(keepIndex, items.Count - 1);
        }
        List.SelectedIndex = index;
        List.ScrollIntoView(items[index]);
    }

    void ShowEmpty(string text)
    {
        EmptyText.Text = text;
        EmptyText.Visibility = Visibility.Visible;
    }

    void MoveClip(int delta)
    {
        int n = List.Items.Count;
        if (n == 0) return;
        List.SelectedIndex = Math.Clamp(List.SelectedIndex + delta, 0, n - 1);
        List.ScrollIntoView(List.SelectedItem);
    }

    // ---- picker views ----

    void RefreshPicker()
    {
        var section = Section;
        if (section is null)
        {
            PickList.ItemsSource = null;
            CategoryBar.ItemsSource = null;
            ShowEmpty(Loc.T("Yükleniyor…"));
            _ = LoadPickersThenRefresh();
            return;
        }
        ApplyToneIfNeeded();

        double avail = Math.Max(200, Width - 24 - 12);
        int cols = section.FixedColumns > 0 ? section.FixedColumns : Math.Max(4, (int)(avail / section.CellWidth));
        if (section.FixedColumns > 0) section.CellWidth = Math.Floor(avail / section.FixedColumns);

        var terms = SearchTerms;
        var recents = terms.Length == 0 ? AppRef.Settings.Recents.GetValueOrDefault(section.Key) : null;

        // Rebuilding the rows recreates every visible cell (~200 ms for emoji); reopening the same grid can keep them.
        string key = $"{section.Key}|{cols}|{_appliedTone}|{PickerData.EmojiPixels}|{string.Join(' ', terms)}|{(recents is null ? "" : string.Join('\u0001', recents))}";
        if (key == _pickKey && _flat.Count > 0 && PickList.ItemsSource == _rows)
        {
            EmptyText.Visibility = Visibility.Collapsed;
            ScrollPickTo(0);
            SetPick(0, scroll: false);
            UpdateCurrentCategory();
            return;
        }
        _pickKey = key;

        var cats = new List<PickCategory>();
        if (recents is not null && PickerData.RecentCategory(section, recents) is { } recent)
            cats.Add(recent);
        cats.AddRange(section.Categories);

        foreach (var it in _flat) it.IsSelected = false;
        _rows = [];
        _flat.Clear();
        _pos.Clear();
        _lineStart.Clear();
        _lineRow.Clear();
        _headerRow.Clear();
        _sel = -1;
        var shown = new List<PickCategory>();

        foreach (var cat in cats)
        {
            var items = terms.Length == 0
                ? cat.Items
                : cat.Items.Where(i => terms.All(t => i.SearchIndex.Contains(t, StringComparison.Ordinal))).ToList();
            if (items.Count == 0) continue;
            shown.Add(cat);
            _headerRow[cat] = _rows.Count;
            _rows.Add(new PickHeaderRow { Category = cat });
            for (int i = 0; i < items.Count; i += cols)
            {
                var chunk = items.Skip(i).Take(cols).ToArray();
                _lineStart.Add(_flat.Count);
                _lineRow.Add(_rows.Count);
                _rows.Add(new PickLineRow { Category = cat, Items = chunk });
                for (int j = 0; j < chunk.Length; j++)
                {
                    _flat.Add(chunk[j]);
                    _pos.Add((_lineRow.Count - 1, j));
                }
            }
        }

        _barCategories = shown;
        CategoryBar.ItemsSource = shown;
        PickList.ItemsSource = _rows;
        ScrollPickTo(0);

        if (_flat.Count == 0)
        {
            ShowEmpty(terms.Length > 0 ? Loc.T("Eşleşen sonuç yok.") : Loc.T("Bu bölümde gösterilecek bir şey yok."));
            ResetStatus();
            return;
        }
        EmptyText.Visibility = Visibility.Collapsed;
        SetPick(0, scroll: false);
        UpdateCurrentCategory();
    }

    async Task LoadPickersThenRefresh()
    {
        try
        {
            await PickerData.EnsureLoadedAsync();
        }
        catch (Exception ex)
        {
            Log.Error("bölüm verisi", ex);
            ShowEmpty(Loc.T("Veri yüklenemedi."));
            return;
        }
        if (IsVisible && IsPicker) Refresh(false);
    }

    void ApplyToneIfNeeded()
    {
        int tone = AppRef.Settings.SkinTone;
        if (PickerData.Emoji is not { } emoji || tone == _appliedTone) return;
        foreach (var it in emoji.AllItems) it.ApplyTone(tone);
        _appliedTone = tone;
        UpdateSkinToneButton();
    }

    void UpdateSkinToneButton()
    {
        var img = EmojiRenderer.Shared?.Render(HandTones[AppRef.Settings.SkinTone], PickerData.IconPixels);
        SkinToneImage.Source = img;
        SkinToneImage.Visibility = img is null ? Visibility.Collapsed : Visibility.Visible;
        SkinToneText.Visibility = img is null ? Visibility.Visible : Visibility.Collapsed;
    }

    void SetPick(int index, bool scroll = true)
    {
        if (_flat.Count == 0) return;
        index = Math.Clamp(index, 0, _flat.Count - 1);
        if (_sel >= 0 && _sel < _flat.Count) _flat[_sel].IsSelected = false;
        _sel = index;
        var item = _flat[index];
        item.IsSelected = true;
        if (scroll) PickList.ScrollIntoView(_rows[_lineRow[_pos[index].line]]);
        Status.Text = item.Name + "  ·  " + Loc.T("Enter ekle · Shift+Enter ekle, açık kalsın");
        Status.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
    }

    void SelectPick(PickItem item)
    {
        int i = _flat.IndexOf(item);
        if (i < 0) i = _flat.FindIndex(f => f.BaseText == item.BaseText);
        if (i >= 0) SetPick(i);
    }

    void MovePick(int dx, int dy)
    {
        if (_flat.Count == 0) return;
        if (_sel < 0)
        {
            SetPick(0);
            return;
        }
        if (dx != 0)
        {
            SetPick(_sel + dx);
            return;
        }
        var (line, col) = _pos[_sel];
        int target = Math.Clamp(line + dy, 0, _lineStart.Count - 1);
        int lineLength = (target + 1 < _lineStart.Count ? _lineStart[target + 1] : _flat.Count) - _lineStart[target];
        SetPick(_lineStart[target] + Math.Min(col, lineLength - 1));
    }

    ScrollViewer? PickScroll => _pickScroll ??= FindChild<ScrollViewer>(PickList);

    void ScrollPickTo(int row)
    {
        PickList.UpdateLayout();
        PickScroll?.ScrollToVerticalOffset(row);
    }

    void PickList_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateCurrentCategory();

    void UpdateCurrentCategory()
    {
        if (_rows.Count == 0 || PickScroll is null) return;
        int first = Math.Clamp((int)PickScroll.VerticalOffset, 0, _rows.Count - 1);
        var current = _rows[first].Category;
        foreach (var c in _barCategories) c.IsCurrent = c == current;
    }

    void Category_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PickCategory cat || !_headerRow.TryGetValue(cat, out int row)) return;
        ScrollPickTo(row);
        int line = _lineRow.FindIndex(r => r > row);
        if (line >= 0) SetPick(_lineStart[line], scroll: false);
        SearchBox.Focus();
    }

    void CategoryScroll_Wheel(object sender, MouseWheelEventArgs e)
    {
        CategoryScroll.ScrollToHorizontalOffset(CategoryScroll.HorizontalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    void Cell_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PickItem item)
            Insert(item, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    async void Insert(PickItem item, bool keepOpen)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            AppRef.Settings.AddRecent(item.Section.Key, item.BaseText);
            AppRef.SaveSettings();

            var target = _target;
            var focus = _targetFocus;
            if (target == IntPtr.Zero || !Win32.IsWindow(target))
            {
                if (ClipboardIo.Write(item.Text, null, null, null, null))
                    ShowStatus(Loc.F("{0} panoya kopyalandı.", item.Text));
                return;
            }
            Win32.Restore(target, focus);
            HidePopup();
            await PasteService.TypeAsync(target, focus, item.Text);
            if (keepOpen)
            {
                await Task.Delay(60);
                ShowFor(target, keepState: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("ekleme", ex);
        }
        finally
        {
            _busy = false;
        }
    }

    void SkinTone_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SkinToneButton, Placement = PlacementMode.Bottom };
        for (int i = 0; i < HandTones.Length; i++)
        {
            int tone = i;
            var img = EmojiRenderer.Shared?.Render(HandTones[i], PickerData.IconPixels);
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(img is null
                ? new TextBlock { Text = HandTones[i], FontFamily = new FontFamily("Segoe UI Emoji"), Width = 20 }
                : new Image { Source = img, Width = 20, Height = 20 });
            header.Children.Add(new TextBlock { Text = Loc.T(ToneNames[i]), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var mi = new MenuItem { Header = header, InputGestureText = tone == AppRef.Settings.SkinTone ? "✓" : "" };
            mi.Click += (_, _) =>
            {
                AppRef.Settings.SkinTone = tone;
                AppRef.SaveSettings();
                ApplyToneIfNeeded();
                var previous = _sel >= 0 && _sel < _flat.Count ? _flat[_sel] : null;
                double offset = PickScroll?.VerticalOffset ?? 0;
                RefreshPicker();
                PickScroll?.ScrollToVerticalOffset(offset);
                if (previous is not null) SelectPick(previous);
            };
            menu.Items.Add(mi);
        }
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (IsVisible)
            {
                Activate();
                SearchBox.Focus();
            }
        };
        menu.IsOpen = true;
    }

    // ---- keyboard ----

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_menuOpen) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool none = mods == ModifierKeys.None, ctrl = mods == ModifierKeys.Control, shift = mods == ModifierKeys.Shift;

        if (key == Key.Escape)
        {
            if (PreviewOpen) ClosePreview();
            else if (_checked.Count > 0) ClearChecks();
            else if (SearchBox.Text.Length > 0) SearchBox.Clear();
            else HidePopup();
            e.Handled = true;
            return;
        }
        if (key == Key.Tab && (none || shift))
        {
            ChangeView((View)(((int)_view + (shift ? 5 : 1)) % 6));
            e.Handled = true;
            return;
        }

        e.Handled = IsPicker ? PickerKey(key, none, shift) : ClipKey(key, none, ctrl, shift);
    }

    bool PickerKey(Key key, bool none, bool shift)
    {
        switch (key)
        {
            case Key.Left: MovePick(-1, 0); return true;
            case Key.Right: MovePick(1, 0); return true;
            case Key.Up: MovePick(0, -1); return true;
            case Key.Down: MovePick(0, 1); return true;
            case Key.PageUp: MovePick(0, -5); return true;
            case Key.PageDown: MovePick(0, 5); return true;
            case Key.Enter when (none || shift) && _sel >= 0 && _sel < _flat.Count:
                Insert(_flat[_sel], shift);
                return true;
            default:
                return false;
        }
    }

    bool ClipKey(Key key, bool none, bool ctrl, bool shift)
    {
        var sel = SelectedClip;
        switch (key)
        {
            case Key.F3 when none: TogglePreview(); return true;
            case Key.Space when none && SearchBox.Text.Length == 0: TogglePreview(); return true;
            case Key.Down when shift && PreviewOpen: _preview!.ScrollBy(60); return true;
            case Key.Up when shift && PreviewOpen: _preview!.ScrollBy(-60); return true;
            case Key.PageDown when shift && PreviewOpen: _preview!.ScrollBy(400); return true;
            case Key.PageUp when shift && PreviewOpen: _preview!.ScrollBy(-400); return true;
            case Key.Down: MoveClip(1); return true;
            case Key.Up: MoveClip(-1); return true;
            case Key.PageDown: MoveClip(5); return true;
            case Key.PageUp: MoveClip(-5); return true;
            case Key.Enter when _checked.Count > 0 && (none || shift):
                UseMany();
                return true;
            case Key.Enter when sel is not null && (none || shift):
                Use(sel, shift ? PasteMode.Plain : PasteMode.Normal);
                return true;
            case Key.Space when ctrl && sel is not null: ToggleCheck(sel); return true;
            case Key.P when ctrl && sel is { IsSnippet: false }: TogglePin(sel); return true;
            case Key.S when ctrl && sel is { IsText: true, IsSnippet: false }: SaveAsSnippet(sel); return true;
            case Key.E when ctrl && sel is { IsSnippet: true }: EditSnippet(sel); return true;
            case Key.O when ctrl && sel is { IsImage: true }: RunOcr(sel); return true;
            case Key.C when ctrl && sel is not null && SearchBox.SelectionLength == 0: CopyOnly(sel); return true;
            case Key.Delete when none && sel is not null && SearchBox.CaretIndex == SearchBox.Text.Length && SearchBox.SelectionLength == 0:
                DeleteItem(sel);
                return true;
            case Key.F10 when shift && sel is not null: OpenMenu(sel, null); return true;
            case >= Key.D1 and <= Key.D9 when ctrl: PasteAt(key - Key.D1); return true;
            case >= Key.NumPad1 and <= Key.NumPad9 when ctrl: PasteAt(key - Key.NumPad1); return true;
            default:
                return false;
        }
    }

    // ---- clipboard actions ----

    void PasteAt(int index)
    {
        if (index < List.Items.Count && List.Items[index] is ClipItem it) Use(it, PasteMode.Normal);
    }

    async void Use(ClipItem item, PasteMode mode, Func<string, string>? transform = null)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (!Manager.PutOnClipboard(item, mode, transform, out var error, out int caretBack))
            {
                Log.Write("panoya yazılamadı: " + error);
                ShowStatus(error ?? Loc.T("Panoya yazılamadı."), true);
                return;
            }
            await PasteToTargetAsync(caretBack);
        }
        catch (Exception ex)
        {
            Log.Error("yapıştırma", ex);
        }
        finally
        {
            _busy = false;
        }
    }

    async void UseMany()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (!Manager.PutManyOnClipboard(_checked.ToList(), out var error, out int skipped))
            {
                ShowStatus(error ?? Loc.T("Panoya yazılamadı."), true);
                return;
            }
            if (skipped > 0) Log.Write($"çoklu yapıştırma: {skipped} metinsiz öğe atlandı");
            await PasteToTargetAsync(0);
        }
        catch (Exception ex)
        {
            Log.Error("çoklu yapıştırma", ex);
        }
        finally
        {
            _busy = false;
        }
    }

    async Task PasteToTargetAsync(int caretBack)
    {
        var target = _target;
        bool canPaste = target != IntPtr.Zero && Win32.IsWindow(target);
        var focus = _targetFocus;
        // Hand activation back while we are still the foreground process; hiding first lets Windows pick another window.
        if (canPaste) Win32.Restore(target, focus);
        HidePopup();
        if (canPaste) await PasteService.PasteAsync(target, focus, caretBack);
        else AppRef.Tray?.Balloon(Loc.T("Panoya kopyalandı"), Loc.T("Ctrl+V ile yapıştırabilirsin."));
    }

    void CopyOnly(ClipItem item)
    {
        if (Manager.PutOnClipboard(item, PasteMode.Normal, null, out var error, out _)) HidePopup();
        else ShowStatus(error ?? Loc.T("Panoya yazılamadı."), true);
    }

    void TogglePin(ClipItem item)
    {
        Manager.SetPinned(item, !item.Pinned);
        ShowStatus(item.Pinned ? Loc.T("Sabitlendi — temizlikte silinmez.") : Loc.T("Sabitleme kaldırıldı."));
    }

    void DeleteItem(ClipItem item)
    {
        if (_checked.Remove(item)) item.IsChecked = false;
        Manager.Delete(item);
        if (_checked.Count > 0) UpdateCheckStatus();
        else ShowStatus(Loc.T("Silindi."));
    }

    async void RunOcr(ClipItem item)
    {
        ShowStatus(Loc.T("Metin çıkarılıyor…"));
        var text = await Manager.OcrAsync(item);
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowStatus(Loc.T("Resimde okunabilir metin bulunamadı."), true);
            return;
        }
        var added = Manager.AddText(text, "OCR");
        if (!IsVisible) return;
        _suppress = true;
        SearchBox.Text = "";
        _suppress = false;
        SetView(View.History);
        Refresh(false);
        List.SelectedItem = added;
        List.ScrollIntoView(added);
        ShowStatus(Loc.T("Metin çıkarıldı — Enter ile yapıştır."));
    }

    void SaveAsSnippet(ClipItem item)
    {
        var text = Manager.GetText(item);
        if (text is null) return;
        var firstLine = text.Trim().Split('\n')[0].Trim();
        if (firstLine.Length > 40) firstLine = firstLine[..40].TrimEnd() + "…";
        HidePopup();
        var dlg = new SnippetWindow(Loc.T("Snippet olarak kaydet"), firstLine, text);
        if (dlg.ShowDialog() == true) Manager.AddSnippet(dlg.SnippetTitle, dlg.SnippetText);
    }

    void EditSnippet(ClipItem item)
    {
        var text = Manager.GetText(item) ?? "";
        HidePopup();
        var dlg = new SnippetWindow(Loc.T("Snippet düzenle"), item.Title ?? "", text);
        if (dlg.ShowDialog() == true) Manager.UpdateSnippet(item, dlg.SnippetTitle, dlg.SnippetText);
    }

    void OpenMenu(ClipItem item, UIElement? at)
    {
        var anchor = at ?? List.ItemContainerGenerator.ContainerFromItem(item) as UIElement ?? List;
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = at is null ? PlacementMode.Bottom : PlacementMode.MousePoint,
        };

        menu.Items.Add(Entry(Loc.T("Yapıştır"), "Enter", () => Use(item, PasteMode.Normal)));
        if (!item.IsImage)
        {
            menu.Items.Add(Entry(Loc.T("Düz metin olarak yapıştır"), "Shift+Enter", () => Use(item, PasteMode.Plain)));
            var convert = Entry(Loc.T("Dönüştür ve yapıştır"), null, null);
            foreach (var (name, apply) in TextTransforms.All)
                convert.Items.Add(Entry(Loc.T(name), null, () => Use(item, PasteMode.Plain, apply)));
            menu.Items.Add(convert);
        }
        if (!item.IsImage && AppRef.Settings.LinkCleaning != "off" && (item.IsLink || item.Preview.Contains("http", StringComparison.OrdinalIgnoreCase)))
            menu.Items.Add(Entry(Loc.T("İzleme parametrelerini temizleyip yapıştır"), null, () => Use(item, PasteMode.Plain, LinkCleaner.CleanText)));
        menu.Items.Add(Entry(Loc.T("Önizle"), "Space", () =>
        {
            List.SelectedItem = item;
            UpdatePreview();
        }));
        menu.Items.Add(Entry(Loc.T("Sadece panoya kopyala"), "Ctrl+C", () => CopyOnly(item)));
        if (item.IsImage) menu.Items.Add(Entry(Loc.T("Metni çıkar (OCR)"), "Ctrl+O", () => RunOcr(item)));
        menu.Items.Add(new Separator());
        if (!item.IsSnippet)
            menu.Items.Add(Entry(item.Pinned ? Loc.T("Sabitlemeyi kaldır") : Loc.T("Sabitle"), "Ctrl+P", () => TogglePin(item)));
        if (item.IsText && !item.IsSnippet) menu.Items.Add(Entry(Loc.T("Snippet olarak kaydet"), "Ctrl+S", () => SaveAsSnippet(item)));
        if (item.IsSnippet) menu.Items.Add(Entry(Loc.T("Düzenle"), "Ctrl+E", () => EditSnippet(item)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Entry(Loc.T("Sil"), "Del", () => DeleteItem(item)));

        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (IsVisible)
            {
                Activate();
                SearchBox.Focus();
            }
        };
        menu.IsOpen = true;
    }

    static MenuItem Entry(string header, string? gesture, Action? action)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        if (action is not null) mi.Click += (_, _) => action();
        return mi;
    }

    void Item_LeftClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: ClipItem item }) return;
        e.Handled = true;
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            List.SelectedItem = item;
            ToggleCheck(item);
            return;
        }
        if (_checked.Count > 0)
        {
            UseMany();
            return;
        }
        Use(item, Keyboard.Modifiers == ModifierKeys.Shift ? PasteMode.Plain : PasteMode.Normal);
    }

    void Item_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: ClipItem item } container) return;
        e.Handled = true;
        List.SelectedItem = item;
        OpenMenu(item, container);
    }

    // ---- misc ----

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchHint is null) return;
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_suppress) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    void NewSnippet_Click(object sender, RoutedEventArgs e)
    {
        HidePopup();
        var dlg = new SnippetWindow(Loc.T("Yeni snippet"), "", "");
        if (dlg.ShowDialog() == true) Manager.AddSnippet(dlg.SnippetTitle, dlg.SnippetText);
    }

    void Pause_Click(object sender, RoutedEventArgs e)
    {
        AppRef.TogglePause();
        UpdatePauseButton();
        ShowStatus(Manager.Paused ? Loc.T("Kayıt duraklatıldı — kopyalananlar kaydedilmiyor.") : Loc.T("Kayıt devam ediyor."));
        SearchBox.Focus();
    }

    void UpdatePauseButton()
    {
        PauseButton.Content = Manager.Paused ? "" : "";
        PauseButton.ToolTip = Manager.Paused ? Loc.T("Kayda devam et") : Loc.T("Kaydı duraklat");
        PauseButton.SetResourceReference(ForegroundProperty, Manager.Paused ? "Danger" : "TextDim");
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        HidePopup();
        AppRef.OpenSettings();
    }

    void ShowStatus(string text, bool error = false)
    {
        Status.Text = text;
        Status.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "Accent");
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    void ResetStatus()
    {
        _statusTimer.Stop();
        if (Status is null) return;
        if (_checked.Count > 0 && !IsPicker)
        {
            UpdateCheckStatus();
            return;
        }
        bool paused = Manager.Paused && !IsPicker;
        Status.Text = paused ? Loc.T("Kayıt duraklatıldı.") : IsPicker ? PickHint : ClipHint;
        Status.SetResourceReference(TextBlock.ForegroundProperty, paused ? "Danger" : "TextDim");
    }

    static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }
}
