using System;
using System.Collections.Generic;
using System.Linq;
using GameColorGuard.Core;

namespace GameColorGuard.Tweaks;

/// <summary>FPS Boost sayfasındaki ayar grubu.</summary>
internal sealed record TweakGroup(string Title, string Subtitle, IReadOnlyList<ITweak> Tweaks);

/// <summary>Bütün ayarların listesi, grupları ve sisteme göre önerilen set.</summary>
internal sealed class TweakCatalog
{
    private readonly ColorSettings _settings;

    public TweakCatalog(ColorSettings settings)
    {
        _settings = settings;
        PowerMode = new PowerModeTweak();
        ProcessPriority = new ProcessPriorityTweak(GameProcessIds);
        Notifications = new NotificationsTweak();
        Transparency = new TransparencyTweak();
        VisualEffects = new VisualEffectsTweak();
        AccessibilityShortcuts = new AccessibilityShortcutsTweak();
        WindowsKeyLock = new WindowsKeyLockTweak();
        GameDvr = new GameDvrTweak();
        GameMode = new GameModeTweak();
        BackgroundApps = new BackgroundAppsTweak();
        MouseAcceleration = new MouseAccelerationTweak();
        KeyboardRepeat = new KeyboardRepeatTweak();
        RefreshRate = new RefreshRateTweak(() => _settings.Performance.PreviousRefreshRate, v => _settings.Performance.PreviousRefreshRate = v);
        WindowedOptimizations = new WindowedOptimizationsTweak();
        GpuPreference = new GpuPreferenceTweak(GamePaths);

        Groups = new[]
        {
            new TweakGroup("Oyun sırasında", "Oyun açılınca uygulanır, kapanınca eski hâline döner",
                new ITweak[] { VisualEffects, Transparency, Notifications, AccessibilityShortcuts, PowerMode, ProcessPriority }),
            new TweakGroup("Oyun odaktayken", "Yalnızca oyun ön plandayken etkin",
                new ITweak[] { WindowsKeyLock }),
            new TweakGroup("Windows ayarları", "Açık kaldıkça geçerli; kapatınca önceki değer geri gelir",
                new ITweak[] { GameDvr, GameMode, WindowedOptimizations, RefreshRate, GpuPreference, BackgroundApps, MouseAcceleration, KeyboardRepeat })
        };
        All = Groups.SelectMany(g => g.Tweaks).ToList();
    }

    public PowerModeTweak PowerMode { get; }
    public ProcessPriorityTweak ProcessPriority { get; }
    public NotificationsTweak Notifications { get; }
    public TransparencyTweak Transparency { get; }
    public VisualEffectsTweak VisualEffects { get; }
    public AccessibilityShortcutsTweak AccessibilityShortcuts { get; }
    public WindowsKeyLockTweak WindowsKeyLock { get; }
    public GameDvrTweak GameDvr { get; }
    public GameModeTweak GameMode { get; }
    public BackgroundAppsTweak BackgroundApps { get; }
    public MouseAccelerationTweak MouseAcceleration { get; }
    public KeyboardRepeatTweak KeyboardRepeat { get; }
    public RefreshRateTweak RefreshRate { get; }
    public WindowedOptimizationsTweak WindowedOptimizations { get; }
    public GpuPreferenceTweak GpuPreference { get; }
    public IReadOnlyList<TweakGroup> Groups { get; }
    public IReadOnlyList<ITweak> All { get; }

    public bool IsEnabled(ITweak tweak) => _settings.Performance.IsEnabled(tweak.Id);
    public void SetEnabled(ITweak tweak, bool enabled) => _settings.Performance.SetEnabled(tweak.Id, enabled);

    /// <summary>
    /// Bu sistem için önerilip önerilmediği ve nedeni. Öneriler yalnız kanıtı olan, geri alınabilir ayarlardır;
    /// masaüstü imlecinin hissini değiştiren ya da bildirim kaçırtabilecek ayarlar isteğe bırakılır.
    /// </summary>
    public (bool Recommended, string Reason) Recommendation(ITweak tweak, SystemProfile system)
    {
        return tweak.Id switch
        {
            "power-mode-best" when system.IsLaptop && !system.OnAcPower =>
                (false, "Pildesiniz: en yüksek performans pili hızla bitirir; önce prize takın."),
            "power-mode-best" => (true, system.IsLaptop ? "Prizdeyken işlemci ve ekran kartı tam güçte kalır." : "İşlemci daha agresif hızlanır."),
            "gpu-preference" when !system.HybridGraphics => (false, "Tek ekran kartı var; bu ayar hibrit (iki GPU'lu) sistemler içindir."),
            "gpu-preference" => (true, "Hibrit grafik: oyunun yanlışlıkla dahili GPU'da açılmasını önler."),
            "max-refresh-rate" when system.PrimaryDisplay is { } d && d.RefreshRate < d.MaxRefreshRate =>
                (true, $"Ekranınız {d.RefreshRate} Hz'de, {d.MaxRefreshRate} Hz destekliyor."),
            "max-refresh-rate" => (false, "Ekran zaten en yüksek yenileme hızında."),
            "mouse-acceleration-off" => (false, "CS2 ve VALORANT ham girdi kullanır; oyunda fark etmez, yalnız masaüstü imlecini değiştirir."),
            "keyboard-repeat-fast" => (false, "Oyunda etkisi yok; yalnızca yazarken tuş tekrarını hızlandırır."),
            "background-apps-off" => (false, "Store uygulamalarından bildirim alıyorsanız açık kalsın; isteğe bağlı."),
            "windows-key-lock" => (true, "Maç ortasında Başlat menüsü açılıp oyunu küçültmez."),
            _ => (true, "Güvenli ve geri alınabilir; arka plan yükünü azaltır.")
        };
    }

    private IEnumerable<int> GameProcessIds() =>
        _settings.GameExecutables.SelectMany(GameProcessMonitor.ProcessIdsOf);

    private IEnumerable<string> GamePaths() =>
        _settings.GameExecutables
            .Select(exe => _settings.GamePaths.TryGetValue(exe, out string? path) ? path : null)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!);
}
