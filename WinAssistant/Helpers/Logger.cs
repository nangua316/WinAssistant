namespace WinAssistant.Helpers;

internal static class Logger
{
    private static readonly string LogPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinAssistant_dbg.txt");
    private const long MaxSize = 1024 * 1024; // 1 MB

    public static void Log(string tag, string msg)
    {
        try
        {
            var fi = new System.IO.FileInfo(LogPath);
            if (fi.Exists && fi.Length > MaxSize)
                fi.Delete(); // truncate when exceeding 1 MB

            System.IO.File.AppendAllText(LogPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {tag}: {msg}{System.Environment.NewLine}");
        }
        catch { }
    }
}
