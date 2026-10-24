using System;
using System.Collections.Generic;
using System.Linq;
using GameColorGuard.Core;

namespace GameColorGuard.Tweaks;

/// <summary>
/// FPS Boost ayarlarının zamanlaması (v2.1'den taşındı):
///  • "Oyun sırasında" ayarları ilk izlenen oyun başlayınca uygulanır, sonuncusu kapanınca geri alınır
///    ("Her zaman açık" seçiliyse oyundan bağımsız sürekli kalır).
///  • "Oyun odaktayken" ayarları (Windows tuşu kilidi) yalnız oyun ön plandayken etkindir.
///  • "Windows ayarları" kullanıcı açtıkça kalır.
/// Uyguladığımız her ayarın önceki değeri ayar dosyasında saklanır; geri alma (çökme sonrası dahil) birebir
/// bu değere döner. Windows zaten istenen durumdaysa (kullanıcının kendi ayarı) hiç dokunulmaz.
/// </summary>
internal sealed class PerformanceGuard : IDisposable
{
    private readonly ColorSettings _settings;
    private readonly TweakCatalog _catalog;
    private readonly Action _save;
    private bool _running;
    private bool _focused;
    private bool _disposed;

    public PerformanceGuard(ColorSettings settings, TweakCatalog catalog, Action save)
    {
        _settings = settings;
        _catalog = catalog;
        _save = save;
    }

    /// <summary>Bir ayarın durumu değişti (arayüz satırlarını tazelemek için).</summary>
    public event Action? Changed;

    public TweakCatalog Catalog => _catalog;
    public bool GameRunning => _running;
    public bool AlwaysOn => _settings.Performance.AlwaysOn;

    /// <summary>"Oyun sırasında" ayarı, her-zaman kipinde sürekli uygulanır (süreç önceliği hariç: oyun gerektirir).</summary>
    public bool IsAlwaysApplied(ITweak tweak) =>
        AlwaysOn && tweak.Scope == TweakScope.GameRunning && tweak is not ProcessPriorityTweak;

    private bool ShouldStayApplied(ITweak tweak) =>
        _catalog.IsEnabled(tweak) && tweak.IsSupported && (tweak.Scope == TweakScope.Persistent || IsAlwaysApplied(tweak));

    public void Start()
    {
        Guarded(() =>
        {
            // Önceki oturumda uyguladıklarımızın "önceki değer"lerini yükle: ayar etkin sayılır, geri alma birebir olur.
            foreach ((string id, string data) in _settings.Performance.CapturedPrevious.ToList())
            {
                ITweak? tweak = _catalog.All.FirstOrDefault(t => t.Id == id);
                if (tweak is null) { _settings.Performance.CapturedPrevious.Remove(id); continue; }
                tweak.ImportPrevious(data);
            }

            bool changed = false;
            foreach (ITweak tweak in _catalog.All)
            {
                if (!tweak.IsActive || ShouldStayApplied(tweak)) continue;
                // Önceki oturumdan kalan geçici (oyuna bağlı) ayar: çökme ya da kip değişimi — geri al.
                Revert(tweak, out _);
                AppLogger.Warn($"Önceki oturumdan kalan ayar geri alındı: {tweak.Title}");
                changed = true;
            }

            foreach (ITweak tweak in _catalog.All.Where(ShouldStayApplied))
            {
                if (tweak.IsActive || tweak.Read().IsApplied) continue;   // zaten öyle: kullanıcının kendi ayarı
                Apply(tweak, out _);
                changed = true;
            }
            if (changed) _save();
        });
    }

    /// <summary>Kip değişimi: her-zaman açılınca oyun süresi ayarları hemen uygulanır; kapanınca (oyun yoksa) geri alınır.</summary>
    public void SetAlwaysOn(bool alwaysOn)
    {
        if (_settings.Performance.AlwaysOn == alwaysOn) return;
        _settings.Performance.AlwaysOn = alwaysOn;
        foreach (ITweak tweak in _catalog.All.Where(t => t.Scope == TweakScope.GameRunning && t is not ProcessPriorityTweak))
        {
            if (alwaysOn)
            {
                if (_catalog.IsEnabled(tweak) && tweak.IsSupported && !tweak.IsActive && !tweak.Read().IsApplied) Apply(tweak, out _);
            }
            else if (!_running && tweak.IsActive)
            {
                Revert(tweak, out _);
            }
        }
        _save();
        AppLogger.Info(alwaysOn ? "Performans ayarları: her zaman açık" : "Performans ayarları: yalnızca oyun sırasında");
        Changed?.Invoke();
    }

    /// <summary>Arayüzden gelen aç/kapat: ayarı kaydeder; gerekiyorsa hemen uygular/geri alır.</summary>
    public bool SetEnabled(ITweak tweak, bool enabled, out string? error)
    {
        error = null;
        _catalog.SetEnabled(tweak, enabled);
        bool ok = true;
        if (ShouldApplyNow(tweak))
        {
            if (enabled)
            {
                if (!(tweak.Read().IsApplied && !tweak.IsActive)) ok = Apply(tweak, out error);   // Windows zaten böyleyse dokunma
                if (!ok) _catalog.SetEnabled(tweak, false);
            }
            else ok = Revert(tweak, out error);
        }
        _save();
        Changed?.Invoke();
        return ok;
    }

    /// <summary>Sisteme göre önerilen ayarları açar; önerilmeyenlere dokunmaz. Uygulanan ayar sayısını döndürür.</summary>
    public int ApplyRecommended(SystemProfile system)
    {
        int count = 0;
        foreach (ITweak tweak in _catalog.All.Where(t => t.IsSupported && !_catalog.IsEnabled(t)))
        {
            if (!_catalog.Recommendation(tweak, system).Recommended) continue;
            if (SetEnabled(tweak, true, out _)) count++;
        }
        return count;
    }

    /// <summary>Bizim uyguladığımız her ayarı kapatıp önceki değerine döndürür (kullanıcının kendi ayarlarına dokunmaz).</summary>
    public int RevertAll()
    {
        int count = 0;
        foreach (ITweak tweak in _catalog.All)
        {
            if (!_catalog.IsEnabled(tweak) && !tweak.IsActive) continue;
            _catalog.SetEnabled(tweak, false);
            if (tweak.IsActive && Revert(tweak, out _)) count++;
        }
        _save();
        AppLogger.Info($"FPS Boost: {count} ayar önceki değerine döndürüldü");
        Changed?.Invoke();
        return count;
    }

    /// <summary>Oyun listesi/yolları değişti: GPU tercihi gibi yola bağlı ayarları tazeler.</summary>
    public void GamesChanged()
    {
        if (_catalog.IsEnabled(_catalog.GpuPreference) && ShouldApplyNow(_catalog.GpuPreference))
            Apply(_catalog.GpuPreference, out _);
        Changed?.Invoke();
    }

    /// <summary>Oyun durumu (250 ms'de bir ve ön plan değişiminde) bildirilir.</summary>
    public void Update(bool running, bool focused)
    {
        if (_disposed) return;
        Guarded(() =>
        {
            bool changed = false;
            if (running && !_running)
            {
                _running = true;
                AppLogger.Info("Oyun başladı — oyun süresi ayarları uygulanıyor");
                foreach (ITweak tweak in Enabled(TweakScope.GameRunning))
                    if (!tweak.IsActive && !(tweak is not ProcessPriorityTweak && tweak.Read().IsApplied)) Apply(tweak, out _);
                changed = true;
            }
            else if (!running && _running)
            {
                _running = false;
                AppLogger.Info("Oyun kapandı — oyun süresi ayarları geri alınıyor");
                foreach (ITweak tweak in _catalog.All.Where(t => t.Scope == TweakScope.GameRunning && !IsAlwaysApplied(t) && t.IsActive))
                    Revert(tweak, out _);
                changed = true;
            }
            else if (running && _catalog.IsEnabled(_catalog.ProcessPriority) && _catalog.ProcessPriority.IsActive)
            {
                _catalog.ProcessPriority.RaiseNewProcesses();   // oyun yeni süreç açmış olabilir
            }

            if (focused && !_focused)
            {
                _focused = true;
                foreach (ITweak tweak in Enabled(TweakScope.GameFocused)) if (!tweak.IsActive) Apply(tweak, out _);
                changed = true;
            }
            else if (!focused && _focused)
            {
                _focused = false;
                foreach (ITweak tweak in _catalog.All.Where(t => t.Scope == TweakScope.GameFocused && t.IsActive)) Revert(tweak, out _);
                changed = true;
            }

            if (changed) { _save(); Changed?.Invoke(); }
        });
    }

    private bool ShouldApplyNow(ITweak tweak) =>
        tweak.Scope == TweakScope.Persistent || IsAlwaysApplied(tweak) ||
        (tweak.Scope == TweakScope.GameRunning && _running) ||
        (tweak.Scope == TweakScope.GameFocused && _focused);

    private bool Apply(ITweak tweak, out string? error)
    {
        bool ok = tweak.Apply(out error);
        if (ok)
        {
            string? previous = tweak.ExportPrevious();
            if (previous is not null) _settings.Performance.CapturedPrevious[tweak.Id] = previous;
        }
        return ok;
    }

    private bool Revert(ITweak tweak, out string? error)
    {
        bool ok = tweak.Revert(out error);
        _settings.Performance.CapturedPrevious.Remove(tweak.Id);
        return ok;
    }

    private IEnumerable<ITweak> Enabled(TweakScope scope) =>
        _catalog.All.Where(t => t.Scope == scope && t.IsSupported && _catalog.IsEnabled(t));

    /// <summary>Çıkışta geçici (oyuna bağlı) ayarları geri al; kalıcı ve her-zaman kipindekiler kullanıcı tercihi olarak kalır.</summary>
    private void RevertAllTransient()
    {
        foreach (ITweak tweak in _catalog.All.Where(t => t.Scope != TweakScope.Persistent && t.IsActive && !IsAlwaysApplied(t)))
            Revert(tweak, out _);
        // Windows tuşu kilidi süreçle yaşar: her kipte kaldırılır.
        if (_catalog.WindowsKeyLock.IsActive) Revert(_catalog.WindowsKeyLock, out _);
        _running = false;
        _focused = false;
        _save();
    }

    private static void Guarded(Action action)
    {
        try { action(); }
        catch (Exception ex) { AppLogger.Write("FPS Boost bekçisi hatası", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Guarded(RevertAllTransient);
        _disposed = true;
    }
}
