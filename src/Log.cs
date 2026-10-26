namespace GodanOverlay;

public static class Log
{
    static readonly object Gate = new();
    static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "overlay.log");

    static Log()
    {
        // 前回分が大きくなりすぎていたら作り直す
        try { if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 2_000_000) File.Delete(FilePath); } catch { }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
        lock (Gate)
        {
            Console.WriteLine(line);
            try { File.AppendAllText(FilePath, line + Environment.NewLine); } catch { }
        }
    }
}
