using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using GameColorGuard.Core;
using GameColorGuard.Tweaks;

namespace GameColorGuard;

/// <summary>
/// "--selftest &lt;rapor.txt&gt;": salt okunur doğrulama. Sistem tanıma, arka plan taraması, pro veri ve CS2 config
/// üretimi, oyun görünürlük tespiti ve ayarların okunan durumu raporlanır. Ekran renklerine ve Windows ayarlarına
/// hiçbir şey yazılmaz; hiçbir süreç kapatılmaz.
/// </summary>
internal static class SelfTest
{
    internal static int Run(string path)
    {
        var report = new StringBuilder();
        int failures = 0;
        void Check(bool ok, string message)
        {
            if (!ok) failures++;
            report.AppendLine($"{(ok ? "OK  " : "HATA")} {message}");
        }

        try
        {
            ColorSettings settings = ColorSettings.Load();

            SystemProfile system = SystemProfile.Detect(settings.GamePaths.Values);
            report.AppendLine($"Sistem: {system.Kind} | {system.Manufacturer} {system.Model} | kasa {system.ChassisType} | {system.PowerText}");
            report.AppendLine($"  CPU {system.Cpu} ({system.LogicalCores}) | RAM {system.RamGb} GB {system.RamSpeed}/{system.RamRatedSpeed} MT/s");
            report.AppendLine($"  GPU: {string.Join(" + ", system.Gpus)} | hibrit {system.HybridGraphics}");
            foreach (DisplayInfo d in system.Displays)
                report.AppendLine($"  Ekran {d.Device} {d.Width}x{d.Height} @{d.RefreshRate}/{d.MaxRefreshRate} Hz birincil={d.Primary} GPU={d.Gpu}");
            report.AppendLine($"  Plan: {system.PowerPlan} (yüksek={system.HighPerformancePlan}) | HAGS {system.Hags} | HVCI {system.MemoryIntegrity} | {system.Windows}");
            report.AppendLine($"  RAM modül {system.RamModules}, yük %{system.RamLoadPercent} | çekirdek {system.PhysicalCores}/{system.LogicalCores} hibrit={system.HybridCpu} P={system.PerformanceCores} | CPU taban {system.CpuBaseMhz} sınır {system.CpuLimitMhz} MHz");
            foreach (GameDrive drive in system.GameDrives) report.AppendLine($"  Oyun diski {drive.Root} SSD={drive.Ssd} boş {drive.FreeGb:0}/{drive.TotalGb:0} GB");
            report.AppendLine($"  GPU sürücü {system.GpuDriverVersion} {system.GpuDriverDate:yyyy-MM-dd}");
            foreach (Advice advice in system.Advise()) report.AppendLine($"  [{advice.Level}] {advice.Title}");
            Check(system.Cpu.Length > 0 && system.RamGb > 0 && system.Displays.Count > 0, "Sistem tanıma temel alanları dolu");

            report.AppendLine();
            var catalog = new TweakCatalog(settings);
            foreach (ITweak tweak in catalog.All)
            {
                (bool recommended, string reason) = catalog.Recommendation(tweak, system);
                string state;
                try { state = tweak.Read().Summary; } catch (Exception ex) { state = "OKUNAMADI " + ex.Message; failures++; }
                report.AppendLine($"  {(recommended ? "[önerilen]" : "[isteğe]  ")} {tweak.Id}: {state} — {reason}");
            }
            Check(catalog.All.Select(t => t.Id).Distinct().Count() == catalog.All.Count, $"{catalog.All.Count} ayarın kimlikleri benzersiz");

            report.AppendLine();
            var running = GameProcessMonitor.FindRunningGames(settings.GameExecutables);
            ForegroundWindow? foreground = GameProcessMonitor.Foreground();
            report.AppendLine($"Çalışan oyunlar: {string.Join(", ", running)} | ön plan: {foreground?.Executable} [{foreground?.ClassName}] " +
                              $"geçici kabuk={GameProcessMonitor.IsTransientShellWindow(foreground)}");
            foreach (string game in running)
                report.AppendLine($"  {game}: ekranda görünür={GameProcessMonitor.IsGameVisible(game, foreground)} yol={SystemProfile.ProcessPath(game)}");

            report.AppendLine();
            var apps = BackgroundScanner.Scan(settings.GameExecutables, TimeSpan.FromMilliseconds(600));
            foreach (var group in apps.GroupBy(a => a.Category))
                report.AppendLine($"  {group.Key}: {string.Join(", ", group.Select(a => $"{a.Name} ({a.MemoryBytes / 1024 / 1024} MB, %{a.CpuPercent:0.0})"))}");
            Check(apps.Count > 0, $"Arka plan taraması {apps.Count} uygulama buldu");
            Check(apps.All(a => !a.Executable.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)), "Sistem süreçleri listede yok");

            report.AppendLine();
            ProDataset data = ProConfig.Load();
            Check(data.Players.Count >= 150 && data.Teams.Count >= 30, $"Pro veri yüklendi: {data.Players.Count} oyuncu, {data.Teams.Count} takım ({data.FetchedAt})");
            Check(data.Players.All(p => p.PhotoUrl is not null), "Her oyuncunun fotoğraf adresi var");
            Check(ImageCache.GetAsync(GameIcons.OfficialIconUrl("VALORANT-Win64-Shipping.exe"), 26).GetAwaiter().GetResult() is not null,
                "Resmi VALORANT ikonu indirilip çözüldü");
            Check(ImageCache.GetAsync(GameIcons.OfficialIconUrl("cs2.exe"), 26).GetAwaiter().GetResult() is not null,
                "Resmi CS2 ikonu (ico) indirilip çözüldü");
            Check(ImageCache.GetAsync(data.Players.First(p => p.Id == "donk").PhotoUrl, 54).GetAwaiter().GetResult() is not null,
                "Oyuncu fotoğrafı (WebP) indirilip çözüldü");
            Check(ImageCache.GetAsync(data.Players.First(p => p.Id == "tenz").PhotoUrl, 54).GetAwaiter().GetResult() is not null,
                "Oyuncu fotoğrafı (PNG) indirilip çözüldü");
            foreach (ProTeam team in data.Teams.Where(t => t.LogoUrl?.EndsWith(".svg") == true))
                Check(ImageCache.GetAsync(team.LogoUrl, 18).GetAwaiter().GetResult() is not null, $"  SVG takım logosu çizildi: {team.Name} ({team.Game})");
            int rasterLogos = data.Teams.Count(t => t.LogoUrl is not null && !t.LogoUrl.EndsWith(".svg") &&
                                                   ImageCache.GetAsync(t.LogoUrl, 18).GetAwaiter().GetResult() is not null);
            Check(rasterLogos == data.Teams.Count(t => t.LogoUrl is not null && !t.LogoUrl.EndsWith(".svg")), $"  {rasterLogos} PNG takım logosu çözüldü");
            foreach (ProPlayer player in data.Players.Where(p => p.Id is "donk" or "m0nesy" or "xantares" or "woxic" or "tenz" or "cned" or "s1mple"))
            {
                double? sens = ProConfig.ConvertedSensitivity(player, settings.MouseDpi);
                Check(sens is > 0, $"{player.GameLabel} {player.Name}: {settings.MouseDpi} DPI için hassasiyet {sens}");
                if (player.IsCs2)
                {
                    string cfg = ProConfig.BuildCs2Config(player, settings.MouseDpi);
                    Check(cfg.Contains("cl_crosshairsize") && cfg.Contains("sensitivity \""), $"  {ProConfig.ConfigFileName(player)} komutları üretildi");
                }
                else Check(player.Crosshair?.Code is { Length: > 0 }, "  VALORANT crosshair kodu var");
            }
            ProPlayer donk = data.Players.First(p => p.Id == "donk");
            report.AppendLine("--- örnek config (donk) ---");
            report.Append(ProConfig.BuildCs2Config(donk, settings.MouseDpi));
            settings.GamePaths.TryGetValue("cs2.exe", out string? cs2);
            report.AppendLine($"CS2 cfg klasörü: {ProConfig.FindCs2CfgFolder(cs2 ?? SystemProfile.ProcessPath("cs2.exe")) ?? "BULUNAMADI"}");
            report.AppendLine($"CS2 exe: {ProConfig.FindCs2Executable(cs2)} | VALORANT exe: {ProConfig.FindValorantExecutable(null) ?? "kurulu değil"}");
        }
        catch (Exception ex)
        {
            failures++;
            report.AppendLine("BEKLENMEYEN HATA: " + ex);
        }

        report.AppendLine();
        failures += CheckLanguageDefault(report);
        failures += CheckLanguages(report);

        report.Insert(0, $"Game Color Guard öz-test · {DateTime.Now:yyyy-MM-dd HH:mm:ss} · {failures} hata{Environment.NewLine}");
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
        return failures == 0 ? 0 : 1;
    }

    /// <summary>"--langtest &lt;rapor.txt&gt;": yalnız dil denetimi.</summary>
    internal static int RunLanguages(string path)
    {
        var report = new StringBuilder();
        int failures = CheckLanguageDefault(report) + CheckLanguages(report);
        report.Insert(0, $"Game Color Guard dil testi · {DateTime.Now:yyyy-MM-dd HH:mm:ss} · {failures} hata{Environment.NewLine}");
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Dil varsayılanı, gerçek ayar dosyasına dokunmadan geçici dosyalarla: yeni kurulum İngilizce başlar, dil seçimi
    /// olmayan eski ayar dosyası Windows diliyle kalır, kullanıcının seçtiği dil korunur.
    /// </summary>
    private static int CheckLanguageDefault(StringBuilder report)
    {
        string temp = Path.Combine(Path.GetTempPath(), "gcg-dil-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            bool freshUnset = ColorSettings.LoadFrom(temp).Language is null;   // dosya yok: ilk açılış
            Loc.Init(null);
            bool freshEnglish = Loc.Language == "en";
            File.WriteAllText(temp, "{\"Brightness\": 60}");
            bool legacyKept = ColorSettings.LoadFrom(temp).Language == Loc.LegacyDefault();
            File.WriteAllText(temp, "{\"Language\": \"de\"}");
            bool chosenKept = ColorSettings.LoadFrom(temp).Language == "de";
            bool ok = freshUnset && freshEnglish && legacyKept && chosenKept;
            report.AppendLine($"{(ok ? "OK  " : "HATA")} Dil varsayılanı: yeni kurulum {(freshEnglish ? "İngilizce" : Loc.Language)}, " +
                              $"dil seçmemiş eski kurulum {Loc.LegacyDefault()} ile kalıyor, seçilen dil korunuyor ({legacyKept}/{chosenKept})");
            return ok ? 0 : 1;
        }
        finally
        {
            try { File.Delete(temp); } catch { }
            Loc.Set("tr");
        }
    }

    // Yalnız Türkçede bulunan harfler (ç/ö/ü Almanca ve Portekizcede de var).
    private static readonly char[] TurkishOnly = { 'ğ', 'Ğ', 'ı', 'İ', 'ş', 'Ş' };

    /// <summary>
    /// Her dilde pencereyi ve her sayfayı ekran dışında kurar (ekran renklerine ve sisteme dokunmaz); ekranda
    /// çevrilmeden kalmış kaynak metinleri hata, Türkçe harf içeren diğer metinleri uyarı olarak raporlar.
    /// </summary>
    private static int CheckLanguages(StringBuilder report)
    {
        int failures = 0;
        IReadOnlyCollection<string> sources = Loc.Sources();
        ProDataset pro = ProConfig.Load();
        var properNames = new HashSet<string>(pro.Players.SelectMany(p => new[] { p.Name, p.RealName, p.Team })
            .Where(s => !string.IsNullOrEmpty(s))!, StringComparer.Ordinal);
        foreach ((string code, string name) in Loc.Languages) properNames.Add(name);

        // Önce program Türkçe açılıp sayfalar gezilirken canlı olarak Rusçaya geçilir (gerçek açılıştaki gibi hiç
        // çeviri yapılmamış durumdan); sonra her dil kendi ayarıyla açılır.
        var runs = new List<(string Code, string Name, string[] States)>
        {
            ("tr", "Canlı geçiş TR → RU", new[] { "active", "pro", "lang:ru", "boost", "system", "apps" })
        };
        runs.AddRange(Loc.Languages.Where(l => l.Code != "tr").Select(l =>
            (l.Code, l.Name, new[] { "idle", "active", "visible", "background", "preview", "boost", "apps", "pro", "system" })));
        foreach ((string code, string name, string[] states) in runs)
        {
            var missed = new SortedSet<string>(StringComparer.Ordinal);
            var suspicious = new SortedSet<string>(StringComparer.Ordinal);
            var window = new MainWindow(testMode: true, initialSettings: new ColorSettings { Language = code })
            {
                ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -12000, Top = 0, Width = 1160, Height = 800
            };
            window.Show();
            foreach (string state in states)
            {
                window.ApplyTestState(state);
                for (int i = 0; i < 4; i++)
                {
                    Thread.Sleep(40);
                    window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                }
                if (Loc.IsSource) continue;   // canlı geçişten önceki Türkçe adımlar denetlenmez
                foreach (string text in ShownTexts(window))
                {
                    string trimmed = text.Trim();
                    if (trimmed.Length == 0 || properNames.Contains(trimmed)) continue;
                    if (sources.Contains(trimmed) && Loc.T(trimmed) != trimmed) missed.Add($"[{state}] {trimmed}");
                    else if (trimmed.IndexOfAny(TurkishOnly) >= 0 && !properNames.Any(n => trimmed.Contains(n, StringComparison.Ordinal)))
                        suspicious.Add($"[{state}] {trimmed}");
                }
            }
            window.CloseForTest();

            failures += missed.Count;
            report.AppendLine($"{(missed.Count == 0 ? "OK  " : "HATA")} {name} ({code}): {missed.Count} çevrilmemiş metin, {suspicious.Count} Türkçe harfli metin");
            foreach (string item in missed) report.AppendLine("    çevrilmedi: " + item);
            foreach (string item in suspicious) report.AppendLine("    Türkçe?   : " + item);
        }
        Loc.Set("tr");
        return failures;
    }

    /// <summary>Ekrandaki tüm metinler: TextBlock/Run, ipuçları ve erişilebilirlik adları (yazı kutuları hariç).</summary>
    private static IEnumerable<string> ShownTexts(DependencyObject root)
    {
        var visited = new HashSet<DependencyObject>();
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            DependencyObject node = stack.Pop();
            if (!visited.Add(node) || node is TextBox) continue;
            if (node is TextBlock block)
            {
                var runs = block.Inlines.OfType<Run>().ToList();
                if (runs.Count > 1) foreach (Run run in runs) yield return run.Text;
                else yield return block.Text;
            }
            if (node is FrameworkElement element)
            {
                if (element.ToolTip is string tip) yield return tip;
                string automation = AutomationProperties.GetName(element);
                if (!string.IsNullOrEmpty(automation)) yield return automation;
            }
            foreach (object child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject d) stack.Push(d);
            if (node is Visual)
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) stack.Push(VisualTreeHelper.GetChild(node, i));
        }
    }
}
