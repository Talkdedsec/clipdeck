using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClipDeck.Core;
using Forms = System.Windows.Forms;
using IOPath = System.IO.Path;

namespace ClipDeck.UI;

public partial class SettingsWindow : Window
{
    static App AppRef => (App)Application.Current;

    readonly DispatcherTimer _previewTimer;
    string? _accent;
    string? _imagePath;
    bool _imageChanged;
    bool _loaded;

    public SettingsWindow()
    {
        InitializeComponent();
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            RenderPreview();
        };
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this);
        Load();
        _loaded = true;
        Loaded += (_, _) =>
        {
            Native.Win32.FitToWorkArea(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            RenderPreview();
        };
    }

    void Load()
    {
        var s = AppRef.Settings;
        TakeOverWinV.IsChecked = s.TakeOverWinV;
        TakeOverWinPeriod.IsChecked = s.TakeOverWinPeriod;
        StartWithWindows.IsChecked = s.StartWithWindows;
        MoveUsedToTop.IsChecked = s.MoveUsedToTop;
        RunAsAdmin.IsChecked = s.RunAsAdmin;
        CheckUpdates.IsChecked = s.CheckUpdates;
        (s.Language == "en" ? LangEn : LangTr).IsChecked = true;
        CaptureImages.IsChecked = s.CaptureImages;
        AutoOcr.IsChecked = s.AutoOcr;
        CaptureFiles.IsChecked = s.CaptureFiles;
        RespectPrivacy.IsChecked = s.RespectPrivacyFlags;
        MaxItems.Text = s.MaxItems.ToString();
        MaxAgeDays.Text = s.MaxAgeDays.ToString();
        (s.SensitiveMode switch { "skip" => SensSkip, "off" => SensOff, _ => SensMask }).IsChecked = true;
        (s.LinkCleaning switch { "auto" => LinkAuto, "off" => LinkOff, _ => LinkMenu }).IsChecked = true;
        ExcludedApps.Text = string.Join(Environment.NewLine, s.ExcludedApps);
        (s.Theme switch { "dark" => ThemeDark, "light" => ThemeLight, _ => ThemeSystem }).IsChecked = true;
        ShortcutHelp.Text = string.Join("\n",
            Loc.T("Win+V pano · Win+. emoji · yaz ara · Tab bölüm değiştir (Geçmiş → Sabitli → Snippet → Emoji → Kaomoji → Semboller)"),
            Loc.T("Pano: ↑↓ seç · Enter yapıştır · Shift+Enter düz metin · Space / F3 önizleme · Ctrl+Space / Ctrl+tık çoklu seç · Ctrl+1…9 hızlı yapıştır · Ctrl+P sabitle · Ctrl+S snippet yap · Ctrl+E snippet düzenle · Ctrl+O resimden metin · Ctrl+C sadece kopyala · Del sil · Menü tuşu / Shift+F10 menü"),
            Loc.T("Snippet değişkenleri: ") + SnippetVars.Help,
            Loc.T("Emoji / Kaomoji / Semboller: oklarla gez · Enter ekle · Shift+Enter ekle ve panel açık kalsın · Esc kapat"));

        _accent = s.AccentColor;
        _imagePath = Backdrop.PathFor(s);
        BlurSlider.Value = s.BackgroundBlur;
        DimSlider.Value = s.BackgroundDim;
        CardSlider.Value = s.CardOpacity;
        WidthSlider.Value = s.PanelWidth;
        HeightSlider.Value = s.PanelHeight;
        BuildAccentPanel();
        UpdateImageLabel();

        Version.Text = "clipdeck " + Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
            + (Autostart.IsElevated ? " · " + Loc.T("yönetici") : "");
        UpdateStats();
    }

    void UpdateStats()
    {
        var (count, bytes) = AppRef.Manager.Stats();
        Stats.Text = Loc.F("{0:N0} öğe · {1:N1} MB", count, bytes / 1024.0 / 1024.0);
    }

    void Warn(string text) => MessageBox.Show(this, text, "clipdeck", MessageBoxButton.OK, MessageBoxImage.Warning);

    // ---- appearance ----

    string SelectedTheme => ThemeDark.IsChecked == true ? "dark" : ThemeLight.IsChecked == true ? "light" : "system";

    bool PreviewDark => SelectedTheme switch
    {
        "dark" => true,
        "light" => false,
        _ => Theme.IsDark,
    };

    Color PreviewAccent => Theme.ParseColor(_accent) ?? Theme.SystemAccent(PreviewDark);

    void BuildAccentPanel()
    {
        AccentPanel.Children.Clear();
        AccentPanel.Children.Add(Swatch(null, Loc.T("Sistem rengi")));
        foreach (var hex in Theme.AccentPresets) AccentPanel.Children.Add(Swatch(hex, hex));

        bool custom = _accent is not null && !Theme.AccentPresets.Contains(_accent, StringComparer.OrdinalIgnoreCase);
        if (custom) AccentPanel.Children.Add(Swatch(_accent, Loc.T("Özel: ") + _accent));

        var pick = new Button
        {
            Style = (Style)FindResource("DialogButton"),
            Content = Loc.T("Özel…"),
            MinWidth = 0,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(4, 2, 0, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        pick.Click += CustomAccent_Click;
        AccentPanel.Children.Add(pick);
    }

    Button Swatch(string? hex, string tooltip)
    {
        bool selected = string.Equals(hex, _accent, StringComparison.OrdinalIgnoreCase);
        var fill = hex is null ? Theme.SystemAccent(PreviewDark) : Theme.ParseColor(hex) ?? Colors.Gray;
        var grid = new Grid { Width = 26, Height = 26 };
        grid.Children.Add(new Ellipse
        {
            Fill = Theme.Brush(fill),
            Stroke = selected ? (Brush)FindResource("Text") : Brushes.Transparent,
            StrokeThickness = 2,
        });
        if (hex is null)
            grid.Children.Add(new TextBlock
            {
                Text = "A",
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Brush(Theme.Contrast(fill)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        else if (selected)
            grid.Children.Add(new TextBlock
            {
                Text = "",
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 11,
                Foreground = Theme.Brush(Theme.Contrast(fill)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });

        var b = new Button
        {
            Content = grid,
            ToolTip = tooltip,
            Margin = new Thickness(0, 2, 6, 2),
            Cursor = Cursors.Hand,
            Template = SwatchTemplate(),
        };
        b.Click += (_, _) =>
        {
            _accent = hex;
            BuildAccentPanel();
            SchedulePreview();
        };
        return b;
    }

    static ControlTemplate SwatchTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        t.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
        return t;
    }

    void CustomAccent_Click(object sender, RoutedEventArgs e)
    {
        var current = PreviewAccent;
        using var dlg = new Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };
        if (dlg.ShowDialog() != Forms.DialogResult.OK) return;
        _accent = Theme.ToHex(Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
        BuildAccentPanel();
        SchedulePreview();
    }

    void PickImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Arka plan fotoğrafı seç"),
            Filter = Loc.T("Resimler") + "|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff|" + Loc.T("Tüm dosyalar") + "|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        if (Backdrop.Render(dlg.FileName, 64, 64, 0) is null)
        {
            Warn(Loc.T("Bu dosya resim olarak açılamadı."));
            return;
        }
        _imagePath = dlg.FileName;
        _imageChanged = true;
        UpdateImageLabel();
        SchedulePreview();
    }

    void RemoveImage_Click(object sender, RoutedEventArgs e)
    {
        _imagePath = null;
        _imageChanged = true;
        UpdateImageLabel();
        SchedulePreview();
    }

    void UpdateImageLabel()
    {
        ImageName.Text = _imagePath is null ? Loc.T("Yok — düz renk")
            : _imageChanged ? IOPath.GetFileName(_imagePath)
            : Loc.T("Seçili fotoğraf");
        RemoveImageButton.IsEnabled = _imagePath is not null;
        bool hasImage = _imagePath is not null;
        BlurSlider.IsEnabled = DimSlider.IsEnabled = hasImage;
        BlurSlider.Opacity = DimSlider.Opacity = hasImage ? 1 : 0.45;
    }

    void Appearance_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        if (sender is RadioButton) BuildAccentPanel();
        SchedulePreview();
    }

    void SchedulePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    void RenderPreview()
    {
        bool dark = PreviewDark;
        var accent = PreviewAccent;
        byte cardAlpha = (byte)Math.Round(CardSlider.Value * 2.55);

        double panelW = WidthSlider.Value, panelH = HeightSlider.Value;
        double h = PreviewArea.ActualHeight > 0 ? PreviewArea.ActualHeight - 20 : 190;
        double w = h * panelW / panelH;
        PreviewPanel.Width = w;
        PreviewPanel.Height = h;

        Color bg = dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
        Color card = dark ? Color.FromArgb(cardAlpha, 0x2B, 0x2B, 0x2B) : Color.FromArgb(cardAlpha, 0xFF, 0xFF, 0xFF);
        Color line = dark ? Color.FromRgb(0x3D, 0x3D, 0x3D) : Color.FromRgb(0xDD, 0xDD, 0xDD);
        Color text = dark ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Color.FromRgb(0x1B, 0x1B, 0x1B);
        Color dim = dark ? Color.FromRgb(0x9E, 0x9E, 0x9E) : Color.FromRgb(0x5F, 0x5F, 0x5F);

        PreviewPanel.Background = Theme.Brush(bg);
        PreviewPanel.BorderBrush = Theme.Brush(line);
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        BitmapSource? image = _imagePath is null
            ? null
            : Backdrop.Render(_imagePath, (int)Math.Round(w * scale), (int)Math.Round(h * scale), BlurSlider.Value * scale * w / panelW);
        PreviewImage.Source = image;
        PreviewDim.Fill = Theme.Brush(bg);
        PreviewDim.Opacity = image is null ? 0 : DimSlider.Value / 100.0;

        PreviewTab.Background = Theme.Brush(card);
        PreviewSearch.Background = Theme.Brush(card);
        PreviewSearch.BorderBrush = Theme.Brush(line);
        PreviewCard1.Background = PreviewCard2.Background = PreviewCard3.Background = Theme.Brush(card);
        PreviewCard1.BorderBrush = Theme.Brush(accent);
        PreviewLine1.Background = Theme.Brush(text);
        PreviewLine2.Background = Theme.Brush(dim);
    }

    // ---- save ----

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MaxItems.Text.Trim(), out int max) || max < 0)
        {
            Warn(Loc.T("Geçmiş sınırı 0 ya da pozitif bir sayı olmalı."));
            MaxItems.Focus();
            return;
        }
        if (!int.TryParse(MaxAgeDays.Text.Trim(), out int days) || days < 0)
        {
            Warn(Loc.T("Otomatik silme süresi 0 ya da pozitif bir gün sayısı olmalı."));
            MaxAgeDays.Focus();
            return;
        }

        var s = AppRef.Settings;
        if (_imageChanged && !SaveImage(s)) return;

        string oldLanguage = s.Language;
        bool oldAdmin = s.RunAsAdmin;

        s.TakeOverWinV = TakeOverWinV.IsChecked == true;
        s.TakeOverWinPeriod = TakeOverWinPeriod.IsChecked == true;
        s.StartWithWindows = StartWithWindows.IsChecked == true;
        s.MoveUsedToTop = MoveUsedToTop.IsChecked == true;
        s.RunAsAdmin = RunAsAdmin.IsChecked == true;
        s.CheckUpdates = CheckUpdates.IsChecked == true;
        s.Language = LangEn.IsChecked == true ? "en" : "tr";
        s.CaptureImages = CaptureImages.IsChecked == true;
        s.AutoOcr = AutoOcr.IsChecked == true;
        s.CaptureFiles = CaptureFiles.IsChecked == true;
        s.RespectPrivacyFlags = RespectPrivacy.IsChecked == true;
        s.MaxItems = max;
        s.MaxAgeDays = days;
        s.SensitiveMode = SensSkip.IsChecked == true ? "skip" : SensOff.IsChecked == true ? "off" : "mask";
        s.LinkCleaning = LinkAuto.IsChecked == true ? "auto" : LinkOff.IsChecked == true ? "off" : "menu";
        s.ExcludedApps = ExcludedApps.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        s.Theme = SelectedTheme;
        s.AccentColor = _accent;
        s.BackgroundBlur = (int)BlurSlider.Value;
        s.BackgroundDim = (int)DimSlider.Value;
        s.CardOpacity = (int)CardSlider.Value;
        s.PanelWidth = (int)WidthSlider.Value;
        s.PanelHeight = (int)HeightSlider.Value;
        s.Normalize();

        AppRef.ApplySettings();
        // Turned off from a normal process: the elevated logon task would still start clipdeck, so remove it (one UAC prompt).
        if (!s.RunAsAdmin && !Autostart.IsElevated && Autostart.TaskExists())
            Task.Run(Autostart.DeleteTaskElevated);

        bool needsElevation = s.RunAsAdmin && !oldAdmin && !Autostart.IsElevated;
        bool languageChanged = s.Language != oldLanguage;
        if (needsElevation || languageChanged)
        {
            var answer = MessageBox.Show(this,
                needsElevation
                    ? Loc.T("Yönetici modu için clipdeck yeniden başlatılmalı (UAC onayı istenecek). Şimdi yeniden başlatılsın mı?")
                    : Loc.T("Dil değişikliği yeniden başlatınca uygulanır. Şimdi yeniden başlatılsın mı?"),
                "clipdeck", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes && (needsElevation ? AppRef.RelaunchElevated() : AppRef.Restart()))
            {
                AppRef.Quit();
                return;
            }
        }
        else if (!s.RunAsAdmin && oldAdmin && Autostart.IsElevated)
        {
            MessageBox.Show(this, Loc.T("clipdeck bir sonraki açılışta normal modda çalışacak."), "clipdeck",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        Close();
    }

    // The photo is copied into the data folder so moving or deleting the original does not break the panel.
    bool SaveImage(AppSettings s)
    {
        try
        {
            var old = Backdrop.PathFor(s);
            if (_imagePath is null)
            {
                s.BackgroundImage = null;
                if (old is not null && File.Exists(old)) File.Delete(old);
                return true;
            }
            var name = "arkaplan" + IOPath.GetExtension(_imagePath).ToLowerInvariant();
            var dest = IOPath.Combine(App.DataDir, name);
            if (!string.Equals(IOPath.GetFullPath(_imagePath), IOPath.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            {
                if (old is not null && File.Exists(old) && !string.Equals(old, dest, StringComparison.OrdinalIgnoreCase)) File.Delete(old);
                File.Copy(_imagePath, dest, overwrite: true);
                File.SetLastWriteTimeUtc(dest, DateTime.UtcNow);
            }
            s.BackgroundImage = name;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("arka plan kaydı", ex);
            Warn(Loc.T("Fotoğraf kaydedilemedi: ") + ex.Message);
            return false;
        }
    }

    async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        CheckNowButton.Content = Loc.T("Denetleniyor…");
        try
        {
            var info = await UpdateChecker.CheckAsync();
            if (info is null)
            {
                MessageBox.Show(this, Loc.F("clipdeck güncel ({0}).", UpdateChecker.Current.ToString(3)), "clipdeck",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            AppRef.AnnounceUpdate(info);
            var answer = MessageBox.Show(this,
                Loc.F("clipdeck {0} çıktı; sen {1} kullanıyorsun. Sürüm sayfası açılsın mı?", info.Version.ToString(3), UpdateChecker.Current.ToString(3)),
                "clipdeck", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes) Process.Start(new ProcessStartInfo(info.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("güncelleme denetlenemedi: " + ex.Message);
            Warn(Loc.T("Güncelleme denetlenemedi: ") + ex.Message);
        }
        finally
        {
            CheckNowButton.IsEnabled = true;
            CheckNowButton.Content = Loc.T("Şimdi denetle");
        }
    }

    // ---- data ----

    void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{App.DataDir}\"") { UseShellExecute = true });

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            Loc.T("Sabitlenmemiş tüm geçmiş silinsin mi?\nSabitli öğeler ve snippet'ler kalır. Bu işlem geri alınamaz."),
            Loc.T("Geçmişi temizle"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        AppRef.Manager.ClearHistory();
        UpdateStats();
    }

    async void Backup_Click(object sender, RoutedEventArgs e)
    {
        var pass = new PasswordWindow(Loc.T("Yedeği şifrele"),
            Loc.T("Yedek bu parolayla şifrelenir. Parolayı unutursan yedek açılamaz."), confirm: true) { Owner = this };
        if (pass.ShowDialog() != true) return;
        var save = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("Yedeği kaydet"),
            Filter = Loc.T("clipdeck yedeği") + "|*" + Backup.Extension,
            FileName = $"clipdeck-{DateTime.Now:yyyyMMdd-HHmm}{Backup.Extension}",
        };
        if (save.ShowDialog(this) != true) return;

        Busy(Loc.T("Yedekleniyor…"));
        try
        {
            int count = await AppRef.Manager.ExportAsync(save.FileName, pass.Password);
            NotBusy();
            MessageBox.Show(this, Loc.F("Yedek kaydedildi: {0} öğe.", count), "clipdeck",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error("yedek", ex);
            Warn(Loc.T("Yedek kaydedilemedi: ") + ex.Message);
        }
        finally
        {
            NotBusy();
        }
    }

    void Busy(string what)
    {
        IsEnabled = false;
        Cursor = Cursors.Wait;
        Stats.Text = what;
    }

    void NotBusy()
    {
        IsEnabled = true;
        Cursor = null;
        UpdateStats();
    }

    async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var open = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Yedeği aç"),
            Filter = Loc.T("clipdeck yedeği") + "|*" + Backup.Extension + "|" + Loc.T("Tüm dosyalar") + "|*.*",
        };
        if (open.ShowDialog(this) != true) return;
        var pass = new PasswordWindow(Loc.T("Yedeği aç"), Loc.T("Yedeği oluştururken kullandığın parolayı gir."), confirm: false) { Owner = this };
        if (pass.ShowDialog() != true) return;

        Busy(Loc.T("Geri yükleniyor…"));
        try
        {
            var (added, skipped) = await AppRef.Manager.ImportAsync(open.FileName, pass.Password);
            NotBusy();
            MessageBox.Show(this, Loc.F("{0} öğe geri yüklendi, {1} öğe zaten vardı.", added, skipped), "clipdeck",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or InvalidDataException)
        {
            Warn(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("geri yükleme", ex);
            Warn(Loc.T("Yedek açılamadı: ") + ex.Message);
        }
        finally
        {
            NotBusy();
        }
    }
}
