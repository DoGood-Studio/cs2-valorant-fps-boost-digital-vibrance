using System;
using System.Collections.Generic;
using System.Linq;

namespace GameColorGuard.Core;

/// <summary>
/// FPS Boost sayfasındaki ayarların açık/kapalı durumu ve geri alma için saklanan önceki değerler.
/// Hepsi kullanıcı düzeyinde ve geri alınabilir; yönetici izni gerektiren hiçbir ayar yoktur.
/// </summary>
internal sealed class PerformanceSettings
{
    /// <summary>Kullanıcının açtığı ayarların kimlikleri (örn. "visual-effects-off").</summary>
    public List<string> Enabled { get; set; } = [];
    /// <summary>true: "oyun sırasında" ayarları oyundan bağımsız sürekli açık kalır; false: yalnız oyun çalışırken.</summary>
    public bool AlwaysOn { get; set; }
    /// <summary>Yenileme hızı ayarının geri dönülecek Hz değeri.</summary>
    public int PreviousRefreshRate { get; set; }
    /// <summary>Uyguladığımız ayarların uygulamadan önceki değerleri (kimlik → veri); geri alma bununla birebir yapılır.</summary>
    public Dictionary<string, string> CapturedPrevious { get; set; } = new();

    public bool IsEnabled(string id) => Enabled.Contains(id, StringComparer.OrdinalIgnoreCase);

    public void SetEnabled(string id, bool enabled)
    {
        Enabled.RemoveAll(e => e.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (enabled) Enabled.Add(id);
    }

    internal void Normalize()
    {
        Enabled = (Enabled ?? []).Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        CapturedPrevious ??= new();
    }
}
