using System;
using System.IO;

namespace GameColorGuard.Core;

internal static class AppLogger
{
    private const long EventLogMaxBytes = 512 * 1024;

    private static readonly string LogFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CS2ColorGuard");
    internal static readonly string LogPath = Path.Combine(LogFolder, "error.log");
    internal static readonly string EventLogPath = Path.Combine(LogFolder, "olay.log");

    internal static void Write(string context, string details)
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {context}{Environment.NewLine}{details}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Günlük yazma hatası ana renk geri yükleme akışını etkilememelidir.
        }
    }

    internal static void Write(string context, Exception exception) => Write(context, exception.ToString());

    internal static void Warn(string message) => Info("UYARI: " + message);

    /// <summary>Tek satırlık olay kaydı (uygulandı / geri alındı / yeniden uygulandı); sorun bildiriminde bakılır.</summary>
    internal static void Info(string message)
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            var file = new FileInfo(EventLogPath);
            if (file.Exists && file.Length > EventLogMaxBytes) file.Delete();
            File.AppendAllText(EventLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Olay kaydı yazılamaması renk akışını durdurmamalı.
        }
    }
}
