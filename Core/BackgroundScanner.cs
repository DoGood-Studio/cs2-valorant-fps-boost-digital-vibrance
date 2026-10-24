using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace GameColorGuard.Core;

internal enum AppCategory
{
    /// <summary>Oyun sırasında gereksiz ve kapatması güvenli (tarayıcı, bulut eşitleme, başlatıcılar...).</summary>
    Suggested,
    /// <summary>Kapatılabilir ama bir işlevi var (sesli sohbet, fare/RGB yazılımı, kayıt).</summary>
    Caution,
    /// <summary>Oyun, anti-hile, Steam/Riot istemcisi, ekran kartı ve ses: listede gösterilir, kapatılamaz.</summary>
    Keep,
    /// <summary>Tanınmayan uygulama: karar kullanıcının.</summary>
    Other
}

/// <summary>Arka plan sayfasındaki bir satır: aynı exe'ye ait süreçlerin toplamı.</summary>
internal sealed class BackgroundApp : INotifyPropertyChanged
{
    private bool _selected;

    public BackgroundApp(string executable, string name, AppCategory category, string reason)
    {
        Executable = executable;
        Name = name;
        Category = category;
        Reason = reason;
    }

    public string Executable { get; }
    public string Name { get; }
    public AppCategory Category { get; }
    public string Reason { get; }
    public List<int> ProcessIds { get; } = new();
    public double CpuPercent { get; set; }
    public long MemoryBytes { get; set; }
    public bool HasWindow { get; set; }
    public bool CanClose => Category != AppCategory.Keep;

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value || !CanClose) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Kullanıcı oturumundaki uygulamaları tarar ve kullanıcının seçtiklerini kapatır. Sistem süreçleri, hizmetler
/// (oturum 0) ve Windows klasöründeki programlar hiç listelenmez; oyun, anti-hile ve sürücü süreçleri listelenir
/// ama kapatılamaz. Hiçbir şey kullanıcı seçmeden kapatılmaz.
/// </summary>
internal static class BackgroundScanner
{
    private static readonly HashSet<string> System32Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost",
        "fontdrvhost", "dwm", "explorer", "sihost", "taskhostw", "ctfmon", "runtimebroker", "searchhost", "searchindexer",
        "searchapp", "startmenuexperiencehost", "shellexperiencehost", "textinputhost", "applicationframehost",
        "securityhealthsystray", "securityhealthservice", "msmpeng", "nissrv", "smartscreen", "audiodg", "conhost",
        "dllhost", "wmiprvse", "spoolsv", "memory compression", "lockapp", "useroobebroker", "dashost", "wudfhost",
        "sgrmbroker", "systemsettings", "widgetservice", "crossdeviceresume", "shellhost", "backgroundtaskhost"
    };

    private static readonly Dictionary<string, (AppCategory Category, string Name, string Reason)> Known =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Oyun ve oyunla ilgili: kapatılamaz.
        ["steam"] = (AppCategory.Keep, "Steam", "CS2 için gerekli"),
        ["steamwebhelper"] = (AppCategory.Keep, "Steam (web yardımcısı)", "Steam arayüzü ve oyun içi katman"),
        ["steamservice"] = (AppCategory.Keep, "Steam hizmeti", "Steam için gerekli"),
        ["gameoverlayui64"] = (AppCategory.Keep, "Steam oyun içi katmanı", "Steam için gerekli"),
        ["cs2"] = (AppCategory.Keep, "Counter-Strike 2", "Oyun"),
        ["riotclientservices"] = (AppCategory.Keep, "Riot Client", "VALORANT için gerekli"),
        ["riotclientux"] = (AppCategory.Keep, "Riot Client", "VALORANT için gerekli"),
        ["riotclientuxrender"] = (AppCategory.Keep, "Riot Client", "VALORANT için gerekli"),
        ["riotclientcrashhandler"] = (AppCategory.Keep, "Riot Client", "VALORANT için gerekli"),
        ["vgtray"] = (AppCategory.Keep, "Riot Vanguard", "Anti-hile: kapatılırsa VALORANT açılmaz"),
        ["valorant"] = (AppCategory.Keep, "VALORANT", "Oyun"),
        ["valorant-win64-shipping"] = (AppCategory.Keep, "VALORANT", "Oyun"),
        ["faceit"] = (AppCategory.Keep, "FACEIT", "Anti-hile / maç istemcisi"),
        ["faceitclient"] = (AppCategory.Keep, "FACEIT", "Anti-hile / maç istemcisi"),
        ["faceitservice"] = (AppCategory.Keep, "FACEIT hizmeti", "Anti-hile"),
        ["easyanticheat"] = (AppCategory.Keep, "Easy Anti-Cheat", "Anti-hile"),
        ["beservice"] = (AppCategory.Keep, "BattlEye", "Anti-hile"),
        ["exitlag"] = (AppCategory.Keep, "ExitLag", "Ağ yönlendirme: oyun bağlantısı için kullanılıyor olabilir"),
        ["nvcontainer"] = (AppCategory.Keep, "NVIDIA sürücü hizmeti", "Ekran kartı sürücüsü"),
        ["nvdisplay.container"] = (AppCategory.Keep, "NVIDIA ekran hizmeti", "Ekran kartı sürücüsü"),
        ["nvsphelper64"] = (AppCategory.Keep, "NVIDIA katmanı", "Ekran kartı sürücüsü"),
        ["igfxem"] = (AppCategory.Keep, "Intel grafik", "Ekran kartı sürücüsü"),
        ["igfxemn"] = (AppCategory.Keep, "Intel grafik", "Ekran kartı sürücüsü"),
        ["rtkauduservice64"] = (AppCategory.Keep, "Realtek ses", "Ses sürücüsü"),
        ["gamecolorguard"] = (AppCategory.Keep, "Game Color Guard", "Bu uygulama"),

        // Bir işlevi var: kullanıcı bilerek seçsin.
        ["discord"] = (AppCategory.Caution, "Discord", "Sesli sohbet kullanıyorsanız açık kalsın"),
        ["obs64"] = (AppCategory.Caution, "OBS Studio", "Yayın/kayıt yapıyorsanız açık kalsın"),
        ["lghub"] = (AppCategory.Caution, "Logitech G HUB", "Fare DPI/tuş ayarları bu yazılımdan geliyorsa açık kalsın"),
        ["lghub_agent"] = (AppCategory.Caution, "Logitech G HUB", "Fare DPI/tuş ayarları bu yazılımdan geliyorsa açık kalsın"),
        ["razer synapse 3"] = (AppCategory.Caution, "Razer Synapse", "Fare/klavye ayarları bu yazılımdan geliyorsa açık kalsın"),
        ["razercentralservice"] = (AppCategory.Caution, "Razer Synapse", "Fare/klavye ayarları bu yazılımdan geliyorsa açık kalsın"),
        ["icue"] = (AppCategory.Caution, "Corsair iCUE", "Çevre birimi ayarları; RGB kapanabilir"),
        ["steelseriesgg"] = (AppCategory.Caution, "SteelSeries GG", "Çevre birimi ayarları"),
        ["armourycrate.service"] = (AppCategory.Caution, "Armoury Crate", "Fan/performans profili bu yazılımdan geliyorsa açık kalsın"),
        ["nvidia app"] = (AppCategory.Caution, "NVIDIA App", "Katman ve kayıt; sürücü için gerekli değil"),
        ["nvidia share"] = (AppCategory.Caution, "NVIDIA katmanı (ShadowPlay)", "Anlık kayıt kullanıyorsanız açık kalsın"),
        ["msedgewebview2"] = (AppCategory.Other, "Edge WebView2", "Başka uygulamaların penceresi olabilir"),

        // Oyun sırasında gereksiz: önerilen.
        ["onedrive"] = (AppCategory.Suggested, "OneDrive", "Dosya eşitleme diski ve ağı kullanır"),
        ["chrome"] = (AppCategory.Suggested, "Google Chrome", "Sekmeler RAM ve işlemci tüketir"),
        ["msedge"] = (AppCategory.Suggested, "Microsoft Edge", "Sekmeler RAM ve işlemci tüketir"),
        ["firefox"] = (AppCategory.Suggested, "Firefox", "Sekmeler RAM ve işlemci tüketir"),
        ["opera"] = (AppCategory.Suggested, "Opera", "Sekmeler RAM ve işlemci tüketir"),
        ["opera_gx"] = (AppCategory.Suggested, "Opera GX", "Sekmeler RAM ve işlemci tüketir"),
        ["brave"] = (AppCategory.Suggested, "Brave", "Sekmeler RAM ve işlemci tüketir"),
        ["vivaldi"] = (AppCategory.Suggested, "Vivaldi", "Sekmeler RAM ve işlemci tüketir"),
        ["ms-teams"] = (AppCategory.Suggested, "Microsoft Teams", "Arka planda sürekli çalışır"),
        ["teams"] = (AppCategory.Suggested, "Microsoft Teams", "Arka planda sürekli çalışır"),
        ["spotify"] = (AppCategory.Suggested, "Spotify", "Müzik dinlemiyorsanız gereksiz"),
        ["epicgameslauncher"] = (AppCategory.Suggested, "Epic Games Launcher", "CS2/VALORANT için gerekmez"),
        ["epicwebhelper"] = (AppCategory.Suggested, "Epic Games Launcher", "CS2/VALORANT için gerekmez"),
        ["eadesktop"] = (AppCategory.Suggested, "EA app", "CS2/VALORANT için gerekmez"),
        ["battle.net"] = (AppCategory.Suggested, "Battle.net", "CS2/VALORANT için gerekmez"),
        ["upc"] = (AppCategory.Suggested, "Ubisoft Connect", "CS2/VALORANT için gerekmez"),
        ["ubisoftconnect"] = (AppCategory.Suggested, "Ubisoft Connect", "CS2/VALORANT için gerekmez"),
        ["galaxyclient"] = (AppCategory.Suggested, "GOG Galaxy", "CS2/VALORANT için gerekmez"),
        ["dropbox"] = (AppCategory.Suggested, "Dropbox", "Dosya eşitleme diski ve ağı kullanır"),
        ["googledrivefs"] = (AppCategory.Suggested, "Google Drive", "Dosya eşitleme diski ve ağı kullanır"),
        ["skype"] = (AppCategory.Suggested, "Skype", "Arka planda sürekli çalışır"),
        ["zoom"] = (AppCategory.Suggested, "Zoom", "Arka planda sürekli çalışır"),
        ["slack"] = (AppCategory.Suggested, "Slack", "Arka planda sürekli çalışır"),
        ["whatsapp"] = (AppCategory.Suggested, "WhatsApp", "Mesaj bildirimleri oyunu böler"),
        ["telegram"] = (AppCategory.Suggested, "Telegram", "Mesaj bildirimleri oyunu böler"),
        ["phoneexperiencehost"] = (AppCategory.Suggested, "Telefon Bağlantısı", "Arka planda telefonla eşitleme yapar"),
        ["creative cloud"] = (AppCategory.Suggested, "Adobe Creative Cloud", "Güncelleme ve eşitleme arka planda çalışır"),
        ["ccxprocess"] = (AppCategory.Suggested, "Adobe Creative Cloud", "Güncelleme ve eşitleme arka planda çalışır"),
        ["adobecollabsync"] = (AppCategory.Suggested, "Adobe Acrobat eşitleme", "Arka planda çalışır"),
        ["wallpaper64"] = (AppCategory.Suggested, "Wallpaper Engine", "Hareketli duvar kâğıdı GPU kullanır"),
        ["wallpaper32"] = (AppCategory.Suggested, "Wallpaper Engine", "Hareketli duvar kâğıdı GPU kullanır"),
        ["rainmeter"] = (AppCategory.Suggested, "Rainmeter", "Masaüstü araçları sürekli güncellenir"),
        ["olk"] = (AppCategory.Suggested, "Outlook", "Posta eşitleme arka planda çalışır"),
        ["outlook"] = (AppCategory.Suggested, "Outlook", "Posta eşitleme arka planda çalışır"),
        ["widgets"] = (AppCategory.Suggested, "Windows Pencere Öğeleri", "Haber/hava durumu arka planda yenilenir"),
    };

    /// <summary>İşlemci kullanımını ölçmek için kısa örnekleme yapar; arayüz iş parçacığında çağırmayın.</summary>
    public static IReadOnlyList<BackgroundApp> Scan(IEnumerable<string> watchedGames, TimeSpan sample)
    {
        int session = Process.GetCurrentProcess().SessionId;
        int ownId = Environment.ProcessId;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var games = new HashSet<string>(watchedGames.Select(Path.GetFileNameWithoutExtension).OfType<string>(), StringComparer.OrdinalIgnoreCase);

        var apps = new Dictionary<string, BackgroundApp>(StringComparer.OrdinalIgnoreCase);
        var firstCpu = new Dictionary<int, TimeSpan>();
        var members = new List<(BackgroundApp App, int Id)>();

        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == ownId || process.SessionId != session) continue;
                string name = process.ProcessName;
                if (System32Names.Contains(name)) continue;
                string? path = ImagePath(process.Id);
                if (path is not null && path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;

                if (!apps.TryGetValue(name, out BackgroundApp? app))
                {
                    app = Classify(name, path, games);
                    apps[name] = app;
                }
                app.ProcessIds.Add(process.Id);
                try { app.MemoryBytes += process.WorkingSet64; } catch { }
                try { if (process.MainWindowHandle != IntPtr.Zero) app.HasWindow = true; } catch { }
                try { firstCpu[process.Id] = process.TotalProcessorTime; } catch { }
                members.Add((app, process.Id));
            }
            catch
            {
                // Erişilemeyen süreç: atla.
            }
            finally { process.Dispose(); }
        }

        var clock = Stopwatch.StartNew();
        Thread.Sleep(sample);
        double elapsedMs = Math.Max(1, clock.Elapsed.TotalMilliseconds) * Environment.ProcessorCount;
        foreach ((BackgroundApp app, int id) in members)
        {
            if (!firstCpu.TryGetValue(id, out TimeSpan before)) continue;
            try
            {
                using Process process = Process.GetProcessById(id);
                app.CpuPercent += (process.TotalProcessorTime - before).TotalMilliseconds / elapsedMs * 100;
            }
            catch { }
        }

        return apps.Values
            .OrderBy(a => a.Category switch { AppCategory.Suggested => 0, AppCategory.Caution => 1, AppCategory.Other => 2, _ => 3 })
            .ThenByDescending(a => a.MemoryBytes)
            .ToList();
    }

    /// <summary>
    /// Seçilen uygulamaları kapatır. Penceresi olanlar düzgünce kapatılmaya çağrılır (kaydedilmemiş iş varsa uygulama
    /// sorabilir; zorla kapatılmaz). Penceresiz (tepsi/arka plan) olanlar sonlandırılır.
    /// </summary>
    public static IReadOnlyList<string> Close(IEnumerable<BackgroundApp> selected)
    {
        var problems = new List<string>();
        foreach (BackgroundApp app in selected.Where(a => a.CanClose))
        {
            var processes = new List<Process>();
            foreach (int id in app.ProcessIds)
            {
                try { processes.Add(Process.GetProcessById(id)); } catch { }
            }
            try
            {
                var windowed = processes.Where(p => SafeHasWindow(p)).ToList();
                foreach (Process process in windowed)
                {
                    try { process.CloseMainWindow(); } catch { }
                }

                if (windowed.Count > 0)
                {
                    var wait = Stopwatch.StartNew();
                    while (wait.ElapsedMilliseconds < 4000 && windowed.Any(p => !SafeExited(p))) Thread.Sleep(150);
                    if (windowed.Any(p => !SafeExited(p)))
                    {
                        problems.Add($"{app.Name}: pencere kapanmadı (kaydedilmemiş iş için onay bekliyor olabilir)");
                        continue;
                    }
                }

                foreach (Process process in processes.Where(p => !SafeExited(p)))
                {
                    try { process.Kill(entireProcessTree: false); }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        if (!SafeExited(process)) { problems.Add($"{app.Name}: kapatılamadı ({ex.Message})"); break; }
                    }
                }
                AppLogger.Info($"Arka plan: {app.Name} kapatıldı ({app.ProcessIds.Count} süreç)");
            }
            finally
            {
                foreach (Process process in processes) process.Dispose();
            }
        }
        return problems;
    }

    private static BackgroundApp Classify(string name, string? path, HashSet<string> games)
    {
        if (games.Contains(name)) return new BackgroundApp(name + ".exe", GameProcessMonitor.FriendlyName(name + ".exe"), AppCategory.Keep, "İzlenen oyun");
        if (Known.TryGetValue(name, out var known)) return new BackgroundApp(name + ".exe", known.Name, known.Category, known.Reason);
        string display = name;
        try
        {
            if (path is not null && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description) display = description;
        }
        catch { }
        return new BackgroundApp(name + ".exe", display, AppCategory.Other, "Tanınmayan uygulama — ne olduğunu biliyorsanız kapatabilirsiniz");
    }

    private static bool SafeHasWindow(Process process)
    {
        try { return process.MainWindowHandle != IntPtr.Zero; } catch { return false; }
    }

    private static bool SafeExited(Process process)
    {
        try { return process.HasExited; } catch { return true; }
    }

    private static string? ImagePath(int processId)
    {
        IntPtr handle = NativeMethods.OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0 ? buffer.ToString(0, size) : null;
        }
        finally { NativeMethods.CloseHandle(handle); }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
    }
}
