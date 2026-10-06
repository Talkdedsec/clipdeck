namespace ClipDeck.Core;

internal sealed class ClipManager
{
    readonly ClipStore _store;
    readonly Crypto _crypto;
    readonly AppSettings _settings;
    bool _capturing, _again;

    public List<ClipItem> Items { get; } = [];
    public bool Paused { get; set; }
    public event Action? Changed;

    public ClipManager(ClipStore store, Crypto crypto, AppSettings settings)
    {
        _store = store;
        _crypto = crypto;
        _settings = settings;
    }

    public void Load()
    {
        ClipItem.MaskSensitive = _settings.SensitiveMode == "mask";
        Items.Clear();
        foreach (var it in _store.LoadAll())
        {
            it.ThumbLoader = LoadThumb;
            Items.Add(it);
        }
        Trim();
    }

    System.Windows.Media.ImageSource? LoadThumb(ClipItem it)
    {
        var bytes = _store.LoadThumb(it.Id);
        return bytes is null ? null : ImageUtil.ToImageSource(bytes);
    }

    public void ReleaseThumbnails()
    {
        foreach (var it in Items) it.ReleaseThumbnail();
    }

    public async Task CaptureAsync()
    {
        if (_capturing)
        {
            _again = true;
            return;
        }
        _capturing = true;
        try
        {
            do
            {
                _again = false;
                if (Paused) return;
                var s = _settings;
                bool cleaned = false;
                var cap = await Task.Run(() =>
                {
                    var raw = ClipboardIo.Read(s.RespectPrivacyFlags, s.CaptureImages, s.CaptureFiles);
                    if (raw is null || s.IsExcluded(raw.ProcessName)) return null;
                    if (s.SensitiveMode == "skip" && raw.Text is not null && Sensitive.Detect(raw.Text) != SensitiveKind.None) return null;
                    if (s.LinkCleaning == "auto" && raw.Text is not null && LinkCleaner.CleanBareLink(raw.Text) is { } clean)
                    {
                        raw.Text = clean;
                        raw.Html = raw.Rtf = null;
                        cleaned = true;
                    }
                    return Capture.Process(raw, _crypto);
                });
                if (cap is not null)
                {
                    // The copying app still holds the tracked link, so the clipboard is replaced with the clean one.
                    if (cleaned && cap.Text is not null) ClipboardIo.Write(cap.Text, null, null, null, null);
                    Add(cap);
                }
            } while (_again);
        }
        catch (Exception ex)
        {
            Log.Error("yakalama", ex);
        }
        finally
        {
            _capturing = false;
        }
    }

    ClipItem Add(Captured cap)
    {
        long now = TextUtil.Now();
        var existing = Items.FirstOrDefault(i => !i.IsSnippet && i.Hash == cap.Hash);
        if (existing is not null)
        {
            existing.Used = now;
            _store.Touch(existing.Id, now);
            MoveToFront(existing);
            Changed?.Invoke();
            return existing;
        }

        var item = new ClipItem
        {
            Kind = cap.Kind,
            Created = now,
            Used = now,
            Hash = cap.Hash,
            Width = cap.Width,
            Height = cap.Height,
            AppName = cap.AppName,
        };
        item.SetContent(cap.Text, cap.Files);
        item.Id = _store.Insert(item, cap.Text, cap.Html, cap.Rtf, cap.Png, cap.Thumb, cap.Files);
        item.ThumbLoader = LoadThumb;
        Items.Insert(0, item);
        Trim();
        Changed?.Invoke();

        if (item.IsImage && _settings.AutoOcr && cap.Png is not null)
            _ = OcrAndStoreAsync(item, cap.Png);
        return item;
    }

    public ClipItem AddText(string text, string? appName)
    {
        return Add(new Captured
        {
            Kind = ClipKind.Text,
            Text = text,
            Hash = _crypto.Hash("T\n" + text),
            AppName = appName,
        });
    }

    void MoveToFront(ClipItem it)
    {
        Items.Remove(it);
        Items.Insert(0, it);
    }

    public void Bump(ClipItem it)
    {
        it.Used = TextUtil.Now();
        _store.Touch(it.Id, it.Used);
        MoveToFront(it);
        Changed?.Invoke();
    }

    public void Trim()
    {
        var drop = new HashSet<ClipItem>();
        var history = Items.Where(i => !i.Pinned && !i.IsSnippet).ToList();
        if (_settings.MaxItems > 0 && history.Count > _settings.MaxItems)
            drop.UnionWith(history.Skip(_settings.MaxItems));
        if (_settings.MaxAgeDays > 0)
        {
            long cutoff = TextUtil.Now() - _settings.MaxAgeDays * 86_400_000L;
            drop.UnionWith(history.Where(i => i.Used < cutoff));
        }
        if (drop.Count == 0) return;
        _store.Delete(drop.Select(i => i.Id));
        Items.RemoveAll(drop.Contains);
        Changed?.Invoke();
    }

    public void SetPinned(ClipItem it, bool pinned)
    {
        _store.SetPinned(it.Id, pinned);
        it.Pinned = pinned;
        Changed?.Invoke();
    }

    public void Delete(ClipItem it)
    {
        _store.Delete([it.Id]);
        Items.Remove(it);
        Changed?.Invoke();
    }

    public void ClearHistory()
    {
        _store.ClearHistory();
        Items.RemoveAll(i => !i.Pinned && !i.IsSnippet);
        Changed?.Invoke();
    }

    public ClipItem AddSnippet(string title, string text)
    {
        long now = TextUtil.Now();
        var item = new ClipItem
        {
            Kind = ClipKind.Text,
            Created = now,
            Used = now,
            IsSnippet = true,
            Hash = _crypto.Hash("S\n" + Guid.NewGuid()),
        };
        item.Title = title;
        item.SetContent(text, null);
        item.Id = _store.Insert(item, text, null, null, null, null, null);
        Items.Insert(0, item);
        Changed?.Invoke();
        return item;
    }

    public void UpdateSnippet(ClipItem it, string title, string text)
    {
        _store.UpdateSnippet(it.Id, title, text);
        it.Title = title;
        it.SetContent(text, null);
        Changed?.Invoke();
    }

    public ClipPayload GetPayload(ClipItem it) => _store.LoadPayload(it.Id);

    public string? GetText(ClipItem it)
    {
        var p = _store.LoadPayload(it.Id);
        return p.Text ?? (p.Files is null ? it.OcrText : string.Join("\r\n", p.Files));
    }

    public bool PutOnClipboard(ClipItem it, PasteMode mode, Func<string, string>? transform, out string? error, out int caretBack)
    {
        error = null;
        caretBack = 0;
        ClipPayload p;
        try
        {
            p = _store.LoadPayload(it.Id);
        }
        catch (Exception ex)
        {
            Log.Error("öğe okunamadı", ex);
            error = Loc.T("Öğe okunamadı.");
            return false;
        }

        if (it.IsSnippet && p.Text is not null)
        {
            (p.Text, caretBack) = SnippetVars.Expand(p.Text, ClipboardIo.ReadCurrentText);
            mode = PasteMode.Plain;
        }

        bool ok;
        if (transform is not null || (mode == PasteMode.Plain && it.Kind != ClipKind.Image))
        {
            var text = p.Text ?? (p.Files is null ? null : string.Join("\r\n", p.Files));
            if (text is null)
            {
                error = Loc.T("Bu öğede metin yok.");
                return false;
            }
            if (transform is not null)
            {
                try
                {
                    text = transform(text);
                }
                catch (Exception ex)
                {
                    error = TextTransforms.Explain(ex);
                    return false;
                }
            }
            ok = ClipboardIo.Write(text, null, null, null, null);
        }
        else
        {
            ok = ClipboardIo.Write(p.Text, p.Html, p.Rtf, p.Image, p.Files);
        }

        if (!ok)
        {
            error = Loc.T("Pano şu an başka bir uygulamada kilitli, tekrar dene.");
            return false;
        }
        if (_settings.MoveUsedToTop) Bump(it);
        return true;
    }

    // Joins the text of several items (in the order they were picked) into one plain-text paste.
    public bool PutManyOnClipboard(IReadOnlyList<ClipItem> items, out string? error, out int skipped)
    {
        error = null;
        skipped = 0;
        var parts = new List<string>();
        foreach (var it in items)
        {
            var p = _store.LoadPayload(it.Id);
            var text = p.Text ?? (p.Files is null ? null : string.Join("\r\n", p.Files));
            if (text is null)
            {
                skipped++;
                continue;
            }
            if (it.IsSnippet) text = SnippetVars.Expand(text, ClipboardIo.ReadCurrentText).Text;
            parts.Add(text);
        }
        if (parts.Count == 0)
        {
            error = Loc.T("Seçilen öğelerde metin yok.");
            return false;
        }
        if (!ClipboardIo.Write(string.Join("\r\n", parts), null, null, null, null))
        {
            error = Loc.T("Pano şu an başka bir uygulamada kilitli, tekrar dene.");
            return false;
        }
        return true;
    }

    public async Task<string?> OcrAsync(ClipItem it)
    {
        if (it.OcrText is not null) return it.OcrText;
        var png = _store.LoadPayload(it.Id).Image;
        if (png is null) return null;
        return await OcrAndStoreAsync(it, png);
    }

    async Task<string?> OcrAndStoreAsync(ClipItem it, byte[] png)
    {
        try
        {
            var text = await Task.Run(() => OcrService.RecognizeAsync(png));
            if (string.IsNullOrWhiteSpace(text) || !Items.Contains(it)) return text;
            it.OcrText = text;
            it.RebuildIndex(null);
            _store.SetOcr(it.Id, text);
            return text;
        }
        catch (Exception ex)
        {
            Log.Error("ocr", ex);
            return null;
        }
    }

    public (int count, long bytes) Stats() => (Items.Count, _store.SizeOnDisk());

    // ---- backup ----

    // Backups run on a worker thread over their own connection, so the panel stays usable meanwhile.
    public Task<int> ExportAsync(string path, string password) => Task.Run(() =>
    {
        using var store = ClipStore.OpenSecondary(_store.FilePath, _crypto);
        return Backup.Write(path, store.ReadForBackup(), password);
    });

    public async Task<(int added, int skipped)> ImportAsync(string path, string password)
    {
        var hashes = Items.Where(i => !i.IsSnippet).Select(i => i.Hash).ToHashSet();
        var snippets = Items.Where(i => i.IsSnippet).Select(i => (i.Title ?? "") + "\n" + GetText(i)).ToHashSet();
        var result = await Task.Run(() =>
        {
            using var reader = Backup.Open(path, password);
            using var store = ClipStore.OpenSecondary(_store.FilePath, _crypto);
            return ImportItems(store, reader.Items(), hashes, snippets);
        });
        Load();
        Changed?.Invoke();
        return result;
    }

    (int added, int skipped) ImportItems(ClipStore store, IEnumerable<BackupItem> items, HashSet<string> hashes, HashSet<string> snippets)
    {
        int added = 0, skipped = 0;
        using var e = items.GetEnumerator();
        bool more = true;
        // Committed in batches: one transaction over the whole file would lock the panel's own writes out until the end.
        while (more)
        {
            store.InTransaction(() =>
            {
                long bytes = 0;
                for (int n = 0; n < 200 && bytes < 32 << 20 && (more = e.MoveNext()); n++)
                {
                    var b = e.Current;
                    bytes += (b.Image?.Length ?? 0) + (b.Html?.Length ?? 0) + (b.Rtf?.Length ?? 0) + (b.Text?.Length ?? 0) * 2L;
                    if (ImportOne(store, b, hashes, snippets)) added++;
                    else skipped++;
                }
            });
        }
        return (added, skipped);
    }

    bool ImportOne(ClipStore store, BackupItem b, HashSet<string> hashes, HashSet<string> snippets)
    {
        var kind = (ClipKind)b.Kind;
        string hash;
        if (b.Snippet)
        {
            if (b.Text is null || !snippets.Add((b.Title ?? "") + "\n" + b.Text)) return false;
            hash = _crypto.Hash("S\n" + Guid.NewGuid());
        }
        else
        {
            hash = kind switch
            {
                ClipKind.Files when b.Files is not null => _crypto.Hash("F\n" + string.Join("\n", b.Files)),
                ClipKind.Image when b.Image is not null => _crypto.Hash(b.Image),
                ClipKind.Text when b.Text is not null => _crypto.Hash("T\n" + b.Text),
                _ => "",
            };
            if (hash.Length == 0 || !hashes.Add(hash)) return false;
        }

        var item = new ClipItem
        {
            Kind = kind,
            Created = b.Created,
            Used = b.Used,
            Hash = hash,
            IsSnippet = b.Snippet,
            Width = b.Width,
            Height = b.Height,
            AppName = b.App,
            OcrText = b.Ocr,
        };
        item.Pinned = b.Pinned;
        item.Title = b.Title;
        item.SetContent(b.Text, b.Files);
        store.Insert(item, b.Text, b.Html, b.Rtf, b.Image, b.Thumb, b.Files);
        return true;
    }
}
