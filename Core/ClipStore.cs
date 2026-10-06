using Microsoft.Data.Sqlite;

namespace ClipDeck.Core;

internal sealed class ClipPayload
{
    public string? Text;
    public byte[]? Html, Rtf, Image;
    public string[]? Files;
}

// Only kind/time/flags/hash are stored in the clear; every content column is encrypted.
// Large blobs live in their own table: inside the items row they would sit in front of the small columns,
// and SQLite would walk every image's overflow pages just to list the history.
internal sealed class ClipStore : IDisposable
{
    const int SchemaVersion = 2;
    static readonly string[] BlobColumns = ["html", "rtf", "image", "thumb"];

    readonly SqliteConnection _db;
    readonly Crypto _c;
    public string FilePath { get; }

    public ClipStore(string path, Crypto crypto) : this(path, crypto, setup: true)
    {
    }

    // A second connection for backups on a worker thread; WAL lets it work next to the main one.
    public static ClipStore OpenSecondary(string path, Crypto crypto) => new(path, crypto, setup: false);

    ClipStore(string path, Crypto crypto, bool setup)
    {
        FilePath = path;
        _c = crypto;
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        if (setup) cs.Mode = SqliteOpenMode.ReadWriteCreate;
        _db = new SqliteConnection(cs.ToString());
        _db.Open();
        Exec("PRAGMA synchronous=NORMAL;");
        if (!setup) return;
        Exec("PRAGMA journal_mode=WAL;");
        Exec(ItemsTable("items"));
        Exec("CREATE TABLE IF NOT EXISTS payloads(id INTEGER PRIMARY KEY, html BLOB, rtf BLOB, image BLOB, thumb BLOB);");
        if (HasColumn("items", "image")) MigrateBlobsOut();
        Exec("CREATE INDEX IF NOT EXISTS ix_items_used ON items(used DESC);");
        Exec("CREATE TRIGGER IF NOT EXISTS items_delete_payload AFTER DELETE ON items BEGIN DELETE FROM payloads WHERE id = old.id; END;");
        Exec($"PRAGMA user_version={SchemaVersion};");
    }

    bool HasColumn(string table, string column)
    {
        using var cmd = Command();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $c";
        P(cmd, "$c", column);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    const string SmallColumns = "id, kind, created, used, pinned, snippet, hash, source, title, text, ocr, files, width, height, length";

    static string ItemsTable(string name) => $"""
        CREATE TABLE IF NOT EXISTS {name}(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            kind INTEGER NOT NULL,
            created INTEGER NOT NULL,
            used INTEGER NOT NULL,
            pinned INTEGER NOT NULL DEFAULT 0,
            snippet INTEGER NOT NULL DEFAULT 0,
            hash TEXT NOT NULL,
            source BLOB, title BLOB, text BLOB, ocr BLOB, files BLOB,
            width INTEGER NOT NULL DEFAULT 0,
            height INTEGER NOT NULL DEFAULT 0,
            length INTEGER NOT NULL DEFAULT 0);
        """;

    // A version 1 database (blobs inside items) needs a one-off rebuild that can take a while on a big history.
    public static bool NeedsMigration(string path)
    {
        if (!File.Exists(path)) return false;
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var db = new SqliteConnection(cs.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('items') WHERE name = 'image'";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    // Version 1 kept blobs inside items; copy them out and rebuild items once, then give the space back.
    void MigrateBlobsOut()
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        InTransaction(() =>
        {
            Exec($"""
                INSERT OR REPLACE INTO payloads(id, {string.Join(", ", BlobColumns)})
                SELECT id, {string.Join(", ", BlobColumns)} FROM items
                WHERE {string.Join(" OR ", BlobColumns.Select(c => c + " IS NOT NULL"))};
                """);
            Exec(ItemsTable("items_v2"));
            Exec($"INSERT INTO items_v2({SmallColumns}) SELECT {SmallColumns} FROM items;");
            Exec("DROP TABLE items;");
            Exec("ALTER TABLE items_v2 RENAME TO items;");
        });
        Compact();
        Log.Write($"veritabanı yeni biçime taşındı: {timer.ElapsedMilliseconds} ms");
    }

    // VACUUM in WAL mode leaves a log as large as the database; checkpoint it away.
    void Compact()
    {
        Exec("VACUUM;");
        Exec("PRAGMA wal_checkpoint(TRUNCATE);");
    }

    SqliteTransaction? _tx;

    SqliteCommand Command()
    {
        var cmd = _db.CreateCommand();
        cmd.Transaction = _tx;
        return cmd;
    }

    // Thousands of single inserts are each a disk sync; one transaction turns a bulk import into one.
    public void InTransaction(Action work)
    {
        if (_tx is not null)
        {
            work();
            return;
        }
        using var tx = _db.BeginTransaction();
        _tx = tx;
        try
        {
            work();
            tx.Commit();
        }
        finally
        {
            _tx = null;
        }
    }

    void Exec(string sql)
    {
        using var cmd = Command();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static void P(SqliteCommand cmd, string name, object? value) =>
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    static byte[]? Blob(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : (byte[])r.GetValue(i);

    static string[]? SplitFiles(string? s) =>
        string.IsNullOrEmpty(s) ? null : s.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    sealed record Row(long Id, int Kind, long Created, long Used, bool Pinned, bool Snippet, string Hash,
        byte[]? Source, byte[]? Title, byte[]? Text, byte[]? Ocr, byte[]? Files, int Width, int Height, int Length);

    // Reading is sequential, but decrypting and building previews/search indexes is pure CPU work,
    // so it is spread over all cores (a 10k-item history otherwise takes seconds to open).
    public List<ClipItem> LoadAll()
    {
        var rows = new List<Row>();
        using (var cmd = Command())
        {
            cmd.CommandText = "SELECT id, kind, created, used, pinned, snippet, hash, source, title, text, ocr, files, width, height, length FROM items ORDER BY used DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new Row(r.GetInt64(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4) != 0, r.GetInt64(5) != 0,
                    r.GetString(6), Blob(r, 7), Blob(r, 8), Blob(r, 9), Blob(r, 10), Blob(r, 11), r.GetInt32(12), r.GetInt32(13), r.GetInt32(14)));
        }

        var items = new ClipItem?[rows.Count];
        Parallel.For(0, rows.Count, i =>
        {
            var row = rows[i];
            try
            {
                var it = new ClipItem
                {
                    Id = row.Id,
                    Kind = (ClipKind)row.Kind,
                    Created = row.Created,
                    Used = row.Used,
                    IsSnippet = row.Snippet,
                    Hash = row.Hash,
                    Width = row.Width,
                    Height = row.Height,
                    Length = row.Length,
                    AppName = _c.DecText(row.Source),
                    OcrText = _c.DecText(row.Ocr),
                };
                it.Pinned = row.Pinned;
                it.Title = _c.DecText(row.Title);
                it.SetContent(_c.DecText(row.Text), SplitFiles(_c.DecText(row.Files)));
                items[i] = it;
            }
            catch (Exception ex)
            {
                Log.Error("kayıt okunamadı", ex);
            }
        });
        return items.Where(i => i is not null).ToList()!;
    }

    // Every row with its payload, one at a time, so a backup of any size streams straight to disk.
    public IEnumerable<BackupItem> ReadForBackup()
    {
        using var cmd = Command();
        cmd.CommandText = """
            SELECT i.kind, i.created, i.used, i.pinned, i.snippet, i.source, i.title, i.text, i.ocr, i.files, i.width, i.height,
                   p.html, p.rtf, p.image, p.thumb
            FROM items i LEFT JOIN payloads p ON p.id = i.id ORDER BY i.used DESC
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (BackupRow(r) is { } item) yield return item;
    }

    BackupItem? BackupRow(SqliteDataReader r)
    {
        try
        {
            return new BackupItem
            {
                Kind = r.GetInt32(0),
                Created = r.GetInt64(1),
                Used = r.GetInt64(2),
                Pinned = r.GetInt64(3) != 0,
                Snippet = r.GetInt64(4) != 0,
                App = _c.DecText(Blob(r, 5)),
                Title = _c.DecText(Blob(r, 6)),
                Text = _c.DecText(Blob(r, 7)),
                Ocr = _c.DecText(Blob(r, 8)),
                Files = SplitFiles(_c.DecText(Blob(r, 9))),
                Width = r.GetInt32(10),
                Height = r.GetInt32(11),
                Html = _c.DecBytes(Blob(r, 12)),
                Rtf = _c.DecBytes(Blob(r, 13)),
                Image = _c.DecBytes(Blob(r, 14)),
                Thumb = _c.DecBytes(Blob(r, 15)),
            };
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            Log.Error("yedek: kayıt okunamadı", ex);
            return null;
        }
    }

    public long Insert(ClipItem it, string? text, byte[]? html, byte[]? rtf, byte[]? image, byte[]? thumb, string[]? files)
    {
        long id = 0;
        InTransaction(() =>
        {
            using (var cmd = Command())
            {
                cmd.CommandText = """
                    INSERT INTO items(kind, created, used, pinned, snippet, hash, source, title, text, ocr, files, width, height, length)
                    VALUES($kind, $created, $used, $pinned, $snippet, $hash, $source, $title, $text, $ocr, $files, $w, $h, $len)
                    RETURNING id;
                    """;
                P(cmd, "$kind", (int)it.Kind);
                P(cmd, "$created", it.Created);
                P(cmd, "$used", it.Used);
                P(cmd, "$pinned", it.Pinned ? 1 : 0);
                P(cmd, "$snippet", it.IsSnippet ? 1 : 0);
                P(cmd, "$hash", it.Hash);
                P(cmd, "$source", _c.EncText(it.AppName));
                P(cmd, "$title", _c.EncText(it.Title));
                P(cmd, "$text", _c.EncText(text));
                P(cmd, "$ocr", _c.EncText(it.OcrText));
                P(cmd, "$files", _c.EncText(files is null ? null : string.Join("\n", files)));
                P(cmd, "$w", it.Width);
                P(cmd, "$h", it.Height);
                P(cmd, "$len", it.Length);
                id = Convert.ToInt64(cmd.ExecuteScalar());
            }
            if (html is null && rtf is null && image is null && thumb is null) return;
            using var blobs = Command();
            blobs.CommandText = "INSERT OR REPLACE INTO payloads(id, html, rtf, image, thumb) VALUES($id, $html, $rtf, $image, $thumb);";
            P(blobs, "$id", id);
            P(blobs, "$html", _c.EncBytes(html));
            P(blobs, "$rtf", _c.EncBytes(rtf));
            P(blobs, "$image", _c.EncBytes(image));
            P(blobs, "$thumb", _c.EncBytes(thumb));
            blobs.ExecuteNonQuery();
        });
        return id;
    }

    public ClipPayload LoadPayload(long id)
    {
        using var cmd = Command();
        cmd.CommandText = "SELECT i.text, p.html, p.rtf, p.image, i.files FROM items i LEFT JOIN payloads p ON p.id = i.id WHERE i.id=$id";
        P(cmd, "$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new ClipPayload();
        return new ClipPayload
        {
            Text = _c.DecText(Blob(r, 0)),
            Html = _c.DecBytes(Blob(r, 1)),
            Rtf = _c.DecBytes(Blob(r, 2)),
            Image = _c.DecBytes(Blob(r, 3)),
            Files = SplitFiles(_c.DecText(Blob(r, 4))),
        };
    }

    public byte[]? LoadThumb(long id)
    {
        using var cmd = Command();
        cmd.CommandText = "SELECT thumb FROM payloads WHERE id=$id";
        P(cmd, "$id", id);
        return _c.DecBytes(cmd.ExecuteScalar() as byte[]);
    }

    public void Touch(long id, long used) => Update("UPDATE items SET used=$v WHERE id=$id", id, used);
    public void SetPinned(long id, bool pinned) => Update("UPDATE items SET pinned=$v WHERE id=$id", id, pinned ? 1 : 0);
    public void SetOcr(long id, string text) => Update("UPDATE items SET ocr=$v WHERE id=$id", id, _c.EncText(text));

    public void UpdateSnippet(long id, string title, string text)
    {
        using var cmd = Command();
        cmd.CommandText = "UPDATE items SET title=$t, text=$x, length=$l WHERE id=$id";
        P(cmd, "$t", _c.EncText(title));
        P(cmd, "$x", _c.EncText(text));
        P(cmd, "$l", text.Length);
        P(cmd, "$id", id);
        cmd.ExecuteNonQuery();
    }

    void Update(string sql, long id, object? value)
    {
        using var cmd = Command();
        cmd.CommandText = sql;
        P(cmd, "$v", value);
        P(cmd, "$id", id);
        cmd.ExecuteNonQuery();
    }

    public void Delete(IEnumerable<long> ids) => InTransaction(() =>
    {
        using var cmd = Command();
        cmd.CommandText = "DELETE FROM items WHERE id=$id";
        var p = cmd.Parameters.Add("$id", SqliteType.Integer);
        foreach (var id in ids)
        {
            p.Value = id;
            cmd.ExecuteNonQuery();
        }
    });

    public void ClearHistory()
    {
        Exec("DELETE FROM items WHERE pinned=0 AND snippet=0;");
        Compact();
    }

    public long SizeOnDisk()
    {
        long total = 0;
        foreach (var f in new[] { FilePath, FilePath + "-wal" })
            if (File.Exists(f)) total += new FileInfo(f).Length;
        return total;
    }

    public void Dispose() => _db.Dispose();
}
