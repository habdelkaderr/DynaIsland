using System.IO;

namespace DynaIsland;

/// <summary>Tiny local file logger (%LOCALAPPDATA%\DynaIsland\log.txt).</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynaIsland", "log.txt");

    public static void Error(string area, Exception ex) => Write($"[{area}] {ex}");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 512 * 1024) info.Delete();
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
