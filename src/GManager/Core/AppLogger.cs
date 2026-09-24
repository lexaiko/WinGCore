using System.IO;

namespace GManager.Core;

public static class AppLogger
{
    private static readonly string LogFilePath;
    private static readonly object LockObj = new();

    static AppLogger()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localAppData, "GManager");
        Directory.CreateDirectory(dir);
        LogFilePath = Path.Combine(dir, "gmanager.log");
    }

    public static void Log(string tag, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{tag}] {message}";
        System.Diagnostics.Debug.WriteLine(line);

        lock (LockObj)
        {
            try
            {
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
            catch { }
        }
    }

    public static void LogError(string tag, string message, Exception? ex = null)
    {
        var exDetails = ex != null ? $"\nException: {ex.GetType().FullName}: {ex.Message}\nStackTrace:\n{ex.StackTrace}" : "";
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{tag}] [ERROR] {message}{exDetails}";
        System.Diagnostics.Debug.WriteLine(line);

        lock (LockObj)
        {
            try
            {
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
            catch { }
        }
    }
}
