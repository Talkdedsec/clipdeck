namespace ClipDeck.Core;

internal static class Log
{
    static readonly object Gate = new();
    public static string? FilePath;

    public static void Write(string message)
    {
        if (FilePath is null) return;
        try
        {
            lock (Gate)
            {
                var fi = new FileInfo(FilePath);
                // Keep the previous log as .1 so the lines leading up to a problem survive the rotation.
                if (fi.Exists && fi.Length > 1_000_000) File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    public static void Error(string where, Exception ex) => Write($"HATA [{where}] {ex}");
}
