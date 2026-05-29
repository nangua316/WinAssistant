namespace WinAssistant.Helpers;

internal static class Logger
{
    private static readonly string LogPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinAssistant_dbg.txt");

    public static void Log(string tag, string msg)
    {
        try
        {
            System.IO.File.AppendAllText(LogPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {tag}: {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
