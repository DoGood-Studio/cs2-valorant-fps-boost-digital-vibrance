using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace GameColorGuard.Core;

/// <summary>
/// Seçilen pro config'in (ya da kendi ayar yedeğinin) CS2 bir sonraki açıldığında kendiliğinden yüklenmesi.
/// CS2 her açılışta önce kayıtlı ayarlarını, sonra cfg\autoexec.cfg dosyasını çalıştırır (console.log: "execing autoexec.cfg");
/// autoexec.cfg'nin sonuna bir kez "exec gcg_aktif" satırı eklenir, bekleyen yükleme gcg_aktif.cfg içinde durur.
/// CS2 yeni değerleri kendi ayar dosyasına kaydettiği için yükleme tek seferliktir: oyun açıldıktan sonra gcg_aktif.cfg
/// boşaltılır, böylece oyunda yapılan sonraki değişiklikler her açılışta ezilmez. Oyunun kendisine dokunulmaz.
/// </summary>
internal static class Cs2AutoLoad
{
    public const string ActiveConfigName = "gcg_aktif.cfg";
    private const string ExecLine = "exec gcg_aktif";
    private const string AutoexecBackupName = "autoexec.cfg.gcg-yedek";

    /// <summary>CS2'nin autoexec'i çalıştırmasına yetecek süre; bu kadar açık kaldıktan sonra bekleyen yükleme tüketilmiş sayılır.</summary>
    internal static readonly TimeSpan ConsumeAfter = TimeSpan.FromMinutes(2);

    private static DateTime _lastCheck = DateTime.MinValue;

    /// <summary>
    /// <paramref name="configName"/> (ör. gcg_donk.cfg) bir sonraki CS2 açılışında yüklensin. autoexec.cfg'ye satır ilk kez
    /// eklendiyse true döner (öncesinde dosyanın kopyası alınır).
    /// </summary>
    public static bool Schedule(string cfgDir, string configName, string description)
    {
        string exec = "exec " + Path.GetFileNameWithoutExtension(configName);
        File.WriteAllText(Path.Combine(cfgDir, ActiveConfigName),
            $"// Game Color Guard — CS2 açılışında bir kez yüklenecek: {description} ({DateTime.Now:yyyy-MM-dd HH:mm})\r\n" +
            "// Yüklendikten sonra program bu dosyayı boşaltır.\r\n" +
            $"{exec}\r\n", new UTF8Encoding(false));
        bool added = EnsureAutoexecLine(cfgDir);
        AppLogger.Info($"CS2 açılış yüklemesi ayarlandı: {exec}" + (added ? " (autoexec.cfg'ye satır eklendi)" : ""));
        return added;
    }

    /// <summary>Bekleyen yükleme var mı? (gcg_aktif.cfg içinde exec satırı)</summary>
    public static bool IsPending(string cfgDir) => PendingExec(Path.Combine(cfgDir, ActiveConfigName)) is not null;

    /// <summary>
    /// autoexec.cfg sonunda "exec gcg_aktif" yoksa ekler. Yalnız ASCII baytlar eklenir (dosyanın kodlaması bozulmaz),
    /// kullanıcının kendi satırlarına dokunulmaz. Dosya yoksa yalnız bu satırla oluşturulur.
    /// </summary>
    internal static bool EnsureAutoexecLine(string cfgDir)
    {
        string path = Path.Combine(cfgDir, "autoexec.cfg");
        if (File.Exists(path))
        {
            string content = File.ReadAllText(path);
            if (HasExecLine(content)) return false;
            string backup = Path.Combine(cfgDir, AutoexecBackupName);
            if (!File.Exists(backup)) File.Copy(path, backup);
            bool endsWithNewline = content.Length == 0 || content.EndsWith('\n');
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            byte[] bytes = Encoding.ASCII.GetBytes((endsWithNewline ? "" : "\r\n") + "\r\n" + AsciiMarker + "\r\n" + ExecLine + "\r\n");
            stream.Write(bytes);
            return true;
        }
        File.WriteAllText(path, AsciiMarker + "\r\n" + ExecLine + "\r\n", new UTF8Encoding(false));
        return true;
    }

    // autoexec'e eklenen açıklama, kullanıcının dosyası hangi kodlamada olursa olsun bozulmasın diye ASCII.
    private const string AsciiMarker = "// Game Color Guard: secilen pro / kendi ayarlarinizi CS2 acilisinda bir kez yukler (silebilirsiniz)";

    internal static bool HasExecLine(string autoexec) =>
        autoexec.Split('\n').Select(l => l.Split("//")[0].Trim().TrimEnd(';').Trim())
            .Any(l => l.Equals(ExecLine, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Oyun durumu denetiminden çağrılır: CS2, bekleyen yükleme yazıldıktan sonra başlatıldıysa ve
    /// <see cref="ConsumeAfter"/> süresince açık kaldıysa gcg_aktif.cfg boşaltılır (yükleme tek seferlik).
    /// </summary>
    public static void ConsumeIfLoaded(string? knownCs2Path)
    {
        if ((DateTime.UtcNow - _lastCheck).TotalSeconds < 10) return;
        _lastCheck = DateTime.UtcNow;
        try
        {
            string? cfgDir = ProConfig.FindCs2CfgFolder(knownCs2Path);
            if (cfgDir is null) return;
            string path = Path.Combine(cfgDir, ActiveConfigName);
            string? exec = PendingExec(path);
            if (exec is null) return;
            DateTime? started = Cs2StartTime();
            if (!ShouldConsume(File.GetLastWriteTime(path), started, DateTime.Now)) return;
            File.WriteAllText(path, "// Game Color Guard — bekleyen yükleme yok.\r\n", new UTF8Encoding(false));
            AppLogger.Info($"CS2 açılışında yüklendi, bekleyen yükleme boşaltıldı: {exec}");
        }
        catch (Exception ex)
        {
            AppLogger.Write("CS2 açılış yüklemesi denetlenemedi", ex);
        }
    }

    /// <summary>Saf karar: CS2 yazımdan sonra başladı ve yeterince açık kaldı mı?</summary>
    internal static bool ShouldConsume(DateTime writtenAt, DateTime? cs2StartedAt, DateTime now) =>
        cs2StartedAt is DateTime started && started > writtenAt && now - started >= ConsumeAfter;

    private static string? PendingExec(string path)
    {
        if (!File.Exists(path)) return null;
        return File.ReadAllLines(path).Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("exec ", StringComparison.OrdinalIgnoreCase));
    }

    private static DateTime? Cs2StartTime()
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName("cs2"); }
        catch { return null; }
        try
        {
            DateTime? earliest = null;
            foreach (Process process in processes)
            {
                try
                {
                    DateTime start = process.StartTime;
                    if (earliest is null || start < earliest) earliest = start;
                }
                catch { }
            }
            return earliest;
        }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }
    }
}
