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
    /// <summary>Geri alması istenince başarısız olan sahte ayar (öz-test için).</summary>
    private sealed class StubbornTweak : TweakBase
    {
        public bool AllowRevert;
        public override string Id => "oz-test";
        public override string Title => "Öz-test sahte ayarı";
        public override string Description => "";
        public override TweakScope Scope => TweakScope.Persistent;
        public override TweakState Read() => new(false, "");
        protected override bool ApplyCore(out string? error) { error = null; return true; }
        protected override bool RevertCore(out string? error) { error = AllowRevert ? null : "izin verilmedi"; return AllowRevert; }
    }

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
            {
                string visible = foreground is null || GameProcessMonitor.IsTransientShellWindow(foreground)
                    ? "belirsiz (geçici kabuk)"
                    : GameProcessMonitor.IsGameVisible(game, foreground).ToString();
                report.AppendLine($"  {game}: gösteriliyor={GameProcessMonitor.IsGameShown(game)} ekranda görünür={visible} yol={SystemProfile.ProcessPath(game)}");
            }

            // Geçişlerde karanlık: çıkışta gecikmeli kararma olmasın, oyun içi kısa odak kaçışları da karartmasın.
            {
                var left = new DateTime(2026, 11, 24, 18, 0, 0);
                Check(MainWindow.RestoreDue(left, left.AddMilliseconds(350), gameRunning: true), "Oyundan çıkınca renk 0,35 sn içinde kapanıyor (sonradan kararma yok)");
                Check(!MainWindow.RestoreDue(left, left.AddMilliseconds(270), gameRunning: true), "0,27 sn'lik odak kaçışı oyunda karartma yapmıyor");
                Check(MainWindow.RestoreDue(left, left, gameRunning: false), "Oyun kapanınca renk hemen kapanıyor");
            }

            // Geri alma başarısızsa yedek ve "etkin" durumu korunmalı (sahte ayar; Windows'a dokunulmaz).
            {
                var stubborn = new StubbornTweak();
                var perf = new ColorSettings();
                perf.Performance.CapturedPrevious[stubborn.Id] = "eski-deger";
                stubborn.Apply(out _);
                Check(!PerformanceGuard.RevertAndForget(perf, stubborn, out _) && stubborn.IsActive &&
                      perf.Performance.CapturedPrevious.ContainsKey(stubborn.Id), "Geri alma başarısızsa yedek silinmiyor, ayar yeniden denenebilir");
                stubborn.AllowRevert = true;
                Check(PerformanceGuard.RevertAndForget(perf, stubborn, out _) && !stubborn.IsActive &&
                      !perf.Performance.CapturedPrevious.ContainsKey(stubborn.Id), "Geri alma başarılıysa yedek siliniyor");
            }

            // Gama katmanına yalnız gama gitmeli; aydınlık/kontrast/RGB renk matrisinde uygulanıyor (iki kez uygulanmasın).
            {
                ushort[] r = new ushort[256], g = new ushort[256], b = new ushort[256];
                DisplayGamma.ComputeRamp(ColorSettings.GammaOnly(110), r, g, b);
                Check(Math.Abs(r[128] - 35023) <= 1 && r[0] == 0 && r[255] == ushort.MaxValue,
                    $"Gama katmanı yalnız gamayı uyguluyor (1,10 için orta nokta {r[128]}, beklenen 35023; siyah {r[0]}, beyaz {r[255]})");
                DisplayGamma.ComputeRamp(ColorSettings.GammaOnly(100), r, g, b);
                Check(Enumerable.Range(0, 256).All(i => Math.Abs(r[i] - i * 257) <= 1 && r[i] == g[i] && g[i] == b[i]),
                    "Nötr gamada gama katmanı ekranı değiştirmiyor");
                Check(WindowsColorMatrix.IsNeutral(ColorSettings.GammaOnly(110)), "Yalnız gama profili renk matrisi için nötr");
            }

            // Odak dışı profil kararı (saf mantık, pencere okunmaz): Alt+Tab menüsü profili açmamalı, açıksa korumalı.
            {
                var games = new[] { "cs2.exe" };
                var altTab = new ForegroundWindow(IntPtr.Zero, 1, "explorer.exe", "XamlExplorerHostIslandWindow");
                var smallApp = new ForegroundWindow(IntPtr.Zero, 2, "discord.exe", "Chrome_WidgetWin_1");
                bool Shown(string _) => true;
                bool Covered(string _, ForegroundWindow __) => false;
                bool NotCovered(string _, ForegroundWindow __) => true;
                string? Decide(ForegroundWindow? fg, bool keep, bool hold, Func<string, ForegroundWindow, bool> vis) =>
                    GameProcessMonitor.UnfocusedProfileGame(games, fg, keep, hold, Shown, vis);

                Check(Decide(altTab, true, hold: false, NotCovered) is null,
                    "Profil kapalıyken Alt+Tab menüsü profili açmıyor (iki uygulama arası geçişte yanıp sönme yok)");
                Check(Decide(null, true, hold: false, NotCovered) is null, "Profil kapalıyken boş ön plan profili açmıyor");
                Check(Decide(altTab, true, hold: true, Covered) == "cs2.exe", "Oyundan Alt+Tab yaparken menü açıkken profil korunuyor");
                Check(Decide(altTab, false, hold: true, Covered) == "cs2.exe", "Görünürken koru kapalıyken de Alt+Tab menüsünde profil korunuyor");
                Check(Decide(smallApp, true, hold: false, Covered) is null, "Oyunu örten pencerede profil kapalı");
                Check(Decide(smallApp, true, hold: true, NotCovered) == "cs2.exe", "Oyunu örtmeyen küçük pencerede profil açık (görünürken koru)");
                Check(Decide(smallApp, false, hold: true, NotCovered) is null, "Görünürken koru kapalıyken başka pencerede profil kapalı");
                foreach (string shell in new[] { "Progman", "WorkerW", "Shell_TrayWnd", "Windows.UI.Core.CoreWindow" })
                    Check(Decide(new ForegroundWindow(IntPtr.Zero, 3, "explorer.exe", shell), false, hold: true, NotCovered) is null,
                        $"Masaüstü / görev çubuğu / Başlat ({shell}) öne geçince profil korunmuyor (görünürken koru kapalı)");
                Check(Decide(new ForegroundWindow(IntPtr.Zero, 3, "explorer.exe", "Progman"), true, hold: true, Covered) is null,
                    "Masaüstü oyunu örtünce profil kapanıyor (görünürken koru açık)");
                Check(GameProcessMonitor.UnfocusedProfileGame(games, altTab, true, true, _ => false, NotCovered) is null,
                    "Küçültülmüş oyunda Alt+Tab menüsü profili korumuyor");
            }

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
                    Check(cfg.Contains("cl_crosshair_length \"") && cfg.Contains("sensitivity \""), $"  {ProConfig.ConfigFileName(player)} komutları üretildi (yeni crosshair sistemi)");
                }
                else Check(player.Crosshair?.Code is { Length: > 0 }, "  VALORANT crosshair kodu var");
            }
            ProPlayer donk = data.Players.First(p => p.Id == "donk");
            report.AppendLine("--- örnek config (donk) ---");
            report.Append(ProConfig.BuildCs2Config(donk, settings.MouseDpi));
            settings.GamePaths.TryGetValue("cs2.exe", out string? cs2);
            report.AppendLine($"CS2 cfg klasörü: {ProConfig.FindCs2CfgFolder(cs2 ?? SystemProfile.ProcessPath("cs2.exe")) ?? "BULUNAMADI"}");
            report.AppendLine($"CS2 exe: {ProConfig.FindCs2Executable(cs2)} | VALORANT exe: {ProConfig.FindValorantExecutable(null) ?? "kurulu değil"}");

            // Kendi ayar yedeği (dosya yazılmaz; CS2'nin ayar dosyası yalnız okunur).
            report.AppendLine();
            var written = data.Players.Where(p => p.IsCs2)
                .SelectMany(p => ProConfig.BuildCs2Config(p, settings.MouseDpi).Split('\n'))
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal) && !l.StartsWith("echo", StringComparison.Ordinal))
                .Select(l => l.Split(' ')[0])
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> notBackedUp = written.Except(ProConfig.Cs2ConfigCvars, StringComparer.OrdinalIgnoreCase).ToList();
            Check(notBackedUp.Count == 0, $"Pro config'lerin yazdığı {written.Count} ayarın hepsi yedekte var" +
                (notBackedUp.Count == 0 ? "" : $" (eksik: {string.Join(", ", notBackedUp)})"));

            const string sampleVcfg = "\"config\"\n{\n\t\"convars\"\n\t{\n\t\t\"sensitivity\"\t\t\"1.5\"\n\t\t\"cl_crosshairdot\"\t\t\"false\"\n" +
                                      "\t\t\"cl_crosshair_length\"\t\t\"2; quit\"\n\t\t\"viewmodel_offset_y\"\t\t\"-2\"\n\t}\n}\n";
            string backupText = ProConfig.BuildBackupConfig(ProConfig.ParseVcfgConvars(sampleVcfg), "örnek", DateTime.Now, out List<string> missing);
            Check(backupText.Contains("sensitivity \"1.5\"") && backupText.Contains("cl_crosshairdot \"0\"") &&
                  backupText.Contains("viewmodel_offset_y \"-2\""), "Yedek: sayı ve true/false değerleri CS2 komutuna çevrildi");
            Check(!backupText.Contains("quit") && missing.Contains("cl_crosshair_length"), "Yedek: komut içeren değer reddedildi");
            Check(missing.Contains("cl_crosshair_gap") && !missing.Contains("sensitivity"), "Yedek: CS2 dosyasında olmayan ayarlar ayrı listelendi");

            // CS2'nin Eylül 2026 crosshair güncellemesinde kaldırılan adlar ("Unknown command") hiçbir config'e yazılmamalı.
            string[] removed = { "cl_crosshairsize", "cl_crosshairthickness", "cl_crosshairgap", "cl_crosshaircolor", "cl_crosshairusealpha",
                                 "cl_crosshairalpha", "cl_crosshair_outlinethickness", "cl_fixedcrosshairgap" };
            var stale = written.Intersect(removed, StringComparer.OrdinalIgnoreCase)
                .Concat(ProConfig.Cs2ConfigCvars.Intersect(removed, StringComparer.OrdinalIgnoreCase)).Distinct().ToList();
            Check(stale.Count == 0, "Pro config ve yedekte CS2'nin kaldırdığı eski crosshair komutları yok" +
                (stale.Count == 0 ? "" : $" (var: {string.Join(", ", stale)})"));
            var cs2Players = data.Players.Where(p => p.IsCs2).ToList();
            int newSystem = cs2Players.Count(ProConfig.HasCs2Crosshair);
            Check(newSystem >= cs2Players.Count * 0.8, $"{newSystem}/{cs2Players.Count} CS2 oyuncusunun crosshair'i yeni sistemde");
            ProPlayer? legacy = cs2Players.FirstOrDefault(p => p.Crosshair?.Legacy == true);
            if (legacy is not null)
            {
                string legacyCfg = ProConfig.BuildCs2Config(legacy, settings.MouseDpi);
                Check(!legacyCfg.Contains("cl_crosshair") && legacyCfg.Contains("sensitivity \""),
                    $"Eski sistemdeki oyuncuda ({legacy.Name}) crosshair yazılmıyor, hassasiyet yazılıyor");
            }

            // Açılışta otomatik yükleme: autoexec.cfg'ye tek satır (geçici klasörde; gerçek CS2 klasörüne dokunulmaz).
            string temp = Path.Combine(Path.GetTempPath(), "gcg-autoexec-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                string autoexec = Path.Combine(temp, "autoexec.cfg");
                Check(Cs2AutoLoad.EnsureAutoexecLine(temp) && Cs2AutoLoad.HasExecLine(File.ReadAllText(autoexec)),
                    "Açılış yüklemesi: autoexec.cfg yoksa tek satırla oluşturuluyor");
                byte[] own = new UTF8Encoding(false).GetBytes("// Kendi ayarlarım: şğüİ\r\nbind \"x\" \"toggle_awp\"");   // sonda satır sonu yok
                File.WriteAllBytes(autoexec, own);
                bool added = Cs2AutoLoad.EnsureAutoexecLine(temp);
                byte[] after = File.ReadAllBytes(autoexec);
                Check(added && after.Take(own.Length).SequenceEqual(own) && Cs2AutoLoad.HasExecLine(File.ReadAllText(autoexec)) &&
                      File.ReadAllText(autoexec).Contains("toggle_awp\"\r\n"),
                    "Açılış yüklemesi: kullanıcının autoexec satırları bayt bayt korunup sonuna tek satır ekleniyor");
                Check(File.Exists(Path.Combine(temp, "autoexec.cfg.gcg-yedek")) &&
                      File.ReadAllBytes(Path.Combine(temp, "autoexec.cfg.gcg-yedek")).SequenceEqual(own),
                    "Açılış yüklemesi: değiştirmeden önce autoexec.cfg'nin kopyası alınıyor");
                Check(!Cs2AutoLoad.EnsureAutoexecLine(temp) && File.ReadAllBytes(autoexec).SequenceEqual(after),
                    "Açılış yüklemesi: satır ikinci kez eklenmiyor");
                Cs2AutoLoad.Schedule(temp, "gcg_donk.cfg", "donk");
                Check(Cs2AutoLoad.IsPending(temp) && File.ReadAllText(Path.Combine(temp, Cs2AutoLoad.ActiveConfigName)).Contains("exec gcg_donk"),
                    "Açılış yüklemesi: gcg_aktif.cfg seçilen config'i bekletiyor");
                Check(!Cs2AutoLoad.HasExecLine("// exec gcg_aktif\nexec gcg_aktifx") && Cs2AutoLoad.HasExecLine("  exec gcg_aktif ; // not"),
                    "Açılış yüklemesi: yorum satırı sayılmıyor, satır sonu yorumu tanınıyor");

                DateTime scheduledAt = new(2026, 11, 23, 20, 0, 0);
                Check(!Cs2AutoLoad.ShouldConsume(scheduledAt, scheduledAt.AddMinutes(-5), scheduledAt.AddMinutes(30)) &&
                      !Cs2AutoLoad.ShouldConsume(scheduledAt, scheduledAt.AddMinutes(1), scheduledAt.AddMinutes(1.5)) &&
                      !Cs2AutoLoad.ShouldConsume(scheduledAt, null, scheduledAt.AddHours(1)) &&
                      Cs2AutoLoad.ShouldConsume(scheduledAt, scheduledAt.AddMinutes(1), scheduledAt.AddMinutes(4)),
                    "Açılış yüklemesi: yalnız yazımdan sonra başlayıp 2 dk açık kalan CS2'de tüketiliyor");
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }

            string? convars = ProConfig.FindCs2UserConvars();
            report.AppendLine($"CS2 kullanıcı ayarları: {convars ?? "BULUNAMADI"}");
            if (convars is not null)
            {
                using var stream = new FileStream(convars, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                string realBackup = ProConfig.BuildBackupConfig(ProConfig.ParseVcfgConvars(reader.ReadToEnd()), convars, DateTime.Now,
                    out List<string> realMissing);
                report.AppendLine($"--- yazılacak {ProConfig.BackupConfigName} (diske yazılmadı) ---");
                report.Append(realBackup);
                int found = ProConfig.Cs2ConfigCvars.Length - realMissing.Count;
                Check(found > 0, $"Gerçek CS2 ayarlarından {found}/{ProConfig.Cs2ConfigCvars.Length} değer yedeklenebilir" +
                    (realMissing.Count == 0 ? "" : $" (dosyada olmayan: {string.Join(", ", realMissing)})"));
            }
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
        int failures = CheckLanguageDefault(report) + CheckNotificationTexts(report) + CheckLanguages(report);
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

    /// <summary>Sayfada durmayan, değişkenli bildirim metinleri (kendi ayar yedeği) her dilde çevriliyor mu?</summary>
    private static int CheckNotificationTexts(StringBuilder report)
    {
        string cmd = ProConfig.BackupExecCommand;
        string[] samples =
        {
            "donk ayarları CS2'yi bir sonraki açışınızda otomatik yüklenecek. Oyun açıksa hemen yüklemek için konsola yapıştırın: exec gcg_donk (panoya kopyalandı)",
            "donk ayarları CS2'yi bir sonraki açışınızda otomatik yüklenecek. Oyun açıksa hemen yüklemek için konsola yapıştırın: exec gcg_donk (panoya kopyalandı). Kendi ayarlarınız yedeklenemedi",
            "CS2'nin kayıtlı ayarları bulunamadı. CS2'yi bir kez açıp kapatın ve tekrar deneyin.",
            $"CS2'yi bir sonraki açışınızda kendi ayarlarınız yüklenecek. Oyun açıksa hemen dönmek için konsola yapıştırın: {cmd} (panoya kopyalandı)",
            $"Kendi ayarlarınız yedeklendi; 2 ayar CS2 varsayılanında olduğu için yedekte yok. CS2'yi bir sonraki açışınızda kendi ayarlarınız yüklenecek. Oyun açıksa hemen dönmek için konsola yapıştırın: {cmd} (panoya kopyalandı)",
            "Yedek hazırlanamadı: Erişim reddedildi"
        };
        int failures = 0;
        try
        {
            foreach ((string code, string name) in Loc.Languages.Where(l => l.Code != "tr"))
            {
                Loc.Set(code);
                var bad = samples.Where(s => Loc.T(s) is var t && (t == s || t.Contains("yapıştırın") || t.Contains("yedeklen") || t.Contains("açışınızda"))).ToList();
                if (bad.Count > 0) failures++;
                report.AppendLine($"{(bad.Count == 0 ? "OK  " : "HATA")} Yedek bildirimleri {name} ({code}): {samples.Length - bad.Count}/{samples.Length} çevrildi" +
                                  (bad.Count == 0 ? "" : " — çevrilmeyen: " + string.Join(" | ", bad)));
            }
        }
        finally { Loc.Set("tr"); }
        return failures;
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
