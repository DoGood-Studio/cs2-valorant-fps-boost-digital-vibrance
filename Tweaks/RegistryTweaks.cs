using System;
using System.Collections.Generic;
using System.Linq;
using GameColorGuard.Core;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GameColorGuard.Tweaks;

/// <summary>HKCU altında bir veya birkaç değeri yazan, öncekileri (yoksa "yok" olarak) saklayan ayar.</summary>
internal abstract class RegistryTweak : TweakBase
{
    protected sealed record Entry(string KeyPath, string ValueName, object AppliedValue, RegistryValueKind Kind);

    private readonly Dictionary<string, object?> _previous = new(StringComparer.OrdinalIgnoreCase);

    protected abstract IReadOnlyList<Entry> Entries { get; }

    protected static object? ReadValue(string keyPath, string valueName)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(valueName);
    }

    protected static bool ValueEquals(object? current, object applied) =>
        current is not null && string.Equals(Convert.ToString(current), Convert.ToString(applied), StringComparison.OrdinalIgnoreCase);

    public override TweakState Read()
    {
        bool all = Entries.All(e => ValueEquals(ReadValue(e.KeyPath, e.ValueName), e.AppliedValue));
        return new TweakState(all, DescribeState(all));
    }

    protected virtual string DescribeState(bool applied) => applied ? "uygulanmış" : "uygulanmamış";

    protected override bool ApplyCore(out string? error)
    {
        error = null;
        foreach (Entry entry in Entries)
        {
            string id = entry.KeyPath + "|" + entry.ValueName;
            if (!_previous.ContainsKey(id)) _previous[id] = ReadValue(entry.KeyPath, entry.ValueName);
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(entry.KeyPath, writable: true)
                ?? throw new InvalidOperationException("anahtar açılamadı: " + entry.KeyPath);
            key.SetValue(entry.ValueName, entry.AppliedValue, entry.Kind);
        }
        AfterChange();
        return true;
    }

    protected override bool RevertCore(out string? error)
    {
        error = null;
        foreach (Entry entry in Entries)
        {
            string id = entry.KeyPath + "|" + entry.ValueName;
            // Önceki değer bilinmiyorsa (başka oturumda uygulanmış) değeri silmek Windows varsayılanına döndürür.
            _previous.TryGetValue(id, out object? previous);
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(entry.KeyPath, writable: true);
            if (key is null) continue;
            if (previous is null) key.DeleteValue(entry.ValueName, throwOnMissingValue: false);
            else key.SetValue(entry.ValueName, previous, entry.Kind);
        }
        _previous.Clear();
        AfterChange();
        return true;
    }

    protected virtual void AfterChange() { }

    public override string? ExportPrevious()
    {
        if (_previous.Count == 0) return null;
        var map = new Dictionary<string, string?>();
        foreach ((string id, object? value) in _previous)
            map[id] = value switch { null => null, int i => "d:" + i, string s => "s:" + s, _ => "s:" + Convert.ToString(value) };
        return ToJson(map);
    }

    protected override void ImportPreviousCore(string data)
    {
        var map = FromJson<Dictionary<string, string?>>(data) ?? new();
        _previous.Clear();
        foreach ((string id, string? value) in map)
            _previous[id] = value is null ? null : value.StartsWith("d:") && int.TryParse(value[2..], out int i) ? i : value.Length >= 2 ? value[2..] : value;
    }

    /// <summary>Kabuk ve DWM'ye ayar değişikliğini duyurur (saydamlık gibi anında yansıması gereken değerler için).</summary>
    protected static void BroadcastSettingChange(string area)
    {
        Native.SendMessageTimeout(new IntPtr(0xFFFF), 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, area,
            0x0002 /* SMTO_ABORTIFHUNG */, 500, out _);
    }

    private static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
    }
}

/// <summary>Xbox Game Bar arka plan kaydı (Game DVR) kapalı: GPU/CPU'yu sessizce tüketen en yaygın etken.</summary>
internal sealed class GameDvrTweak : RegistryTweak
{
    private static readonly Entry[] Items =
    {
        new(@"System\GameConfigStore", "GameDVR_Enabled", 0, RegistryValueKind.DWord),
        new(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, RegistryValueKind.DWord)
    };
    public override string Id => "game-dvr-off";
    public override string Title => "Xbox Game Bar arka plan kaydı kapalı";
    public override string Description => "Game DVR'ın oyunları arka planda sürekli kaydetmesini durdurur; GPU ve disk yükünü azaltır. Klip almak istiyorsanız kapatmayın.";
    public override TweakScope Scope => TweakScope.Persistent;
    protected override IReadOnlyList<Entry> Entries => Items;
    protected override string DescribeState(bool applied) => applied ? "arka plan kaydı kapalı" : "arka plan kaydı açık (Windows varsayılanı)";
}

/// <summary>Microsoft Store uygulamalarının arka planda çalışma izni kapalı (Windows ayarlarındaki genel anahtar).</summary>
internal sealed class BackgroundAppsTweak : RegistryTweak
{
    private static readonly Entry[] Items =
    {
        new(@"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1, RegistryValueKind.DWord)
    };
    public override string Id => "background-apps-off";
    public override string Title => "Store uygulamalarının arka planda çalışmasını durdur";
    public override string Description => "Microsoft Store'dan gelen uygulamaların (Fotoğraflar, Telefon Bağlantısı, Xbox vb.) arka plan görevlerini kapatır. Bu uygulamalardan bildirim alıyorsanız (örn. Store sürümü WhatsApp) açık bırakın.";
    public override TweakScope Scope => TweakScope.Persistent;
    protected override IReadOnlyList<Entry> Entries => Items;
    protected override string DescribeState(bool applied) => applied ? "arka plan izni kapalı" : "arka plan izni açık (Windows varsayılanı)";
}

/// <summary>Windows Oyun Modu açık.</summary>
internal sealed class GameModeTweak : RegistryTweak
{
    private static readonly Entry[] Items = { new(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, RegistryValueKind.DWord) };
    public override string Id => "game-mode-on";
    public override string Title => "Windows Oyun Modu açık";
    public override string Description => "Oyun çalışırken Windows Update ve sürücü bildirimlerini erteler, arka plan işlerini kısar. Windows'ta varsayılan olarak açıktır.";
    public override TweakScope Scope => TweakScope.Persistent;
    protected override IReadOnlyList<Entry> Entries => Items;
    public override TweakState Read()
    {
        object? value = ReadValue(Items[0].KeyPath, Items[0].ValueName);
        bool on = value is null || Convert.ToInt32(value) != 0;
        return new TweakState(on, on ? (value is null ? "açık (varsayılan)" : "açık") : "kapalı");
    }
}

/// <summary>Oyun sırasında bildirim balonları kapalı.</summary>
internal sealed class NotificationsTweak : RegistryTweak
{
    private static readonly Entry[] Items = { new(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", 0, RegistryValueKind.DWord) };
    public override string Id => "mute-notifications";
    public override string Title => "Bildirimleri sustur";
    public override string Description => "Oyun çalıştığı sürece Windows bildirim balonları gösterilmez; oyun kapanınca eski ayar geri gelir.";
    public override TweakScope Scope => TweakScope.GameRunning;
    protected override IReadOnlyList<Entry> Entries => Items;
    protected override string DescribeState(bool applied) => applied ? "bildirimler kapalı" : "bildirimler açık";
    protected override void AfterChange() => BroadcastSettingChange("ToastEnabled");
}

/// <summary>Oyun sırasında Windows saydamlık efektleri kapalı (DWM yükü).</summary>
internal sealed class TransparencyTweak : RegistryTweak
{
    private static readonly Entry[] Items = { new(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, RegistryValueKind.DWord) };
    public override string Id => "transparency-off";
    public override string Title => "Saydamlık efektlerini kapat";
    public override string Description => "Görev çubuğu/menü saydamlığını oyun süresince kapatır; kenarlıksız modda oyunu DWM birleştirdiği için küçük bir yük azaltmasıdır.";
    public override TweakScope Scope => TweakScope.GameRunning;
    protected override IReadOnlyList<Entry> Entries => Items;
    protected override string DescribeState(bool applied) => applied ? "saydamlık kapalı" : "saydamlık açık";
    protected override void AfterChange() => BroadcastSettingChange("ImmersiveColorSet");
}

/// <summary>Windows 11 "Pencereli oyunlar için iyileştirmeler" + değişken yenileme hızı.</summary>
internal sealed class WindowedOptimizationsTweak : TweakBase
{
    private const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string ValueName = "DirectXUserGlobalSettings";
    private string? _previous;
    private bool _captured;

    public override string? ExportPrevious() => _captured ? ToJson(new[] { _previous ?? "" }) : null;
    protected override void ImportPreviousCore(string data) { _previous = (FromJson<string[]>(data) ?? new[] { "" })[0]; _captured = true; }

    public override string Id => "windowed-optimizations";
    public override string Title => "Pencereli oyun iyileştirmeleri + VRR";
    public override string Description => "Kenarlıksız/pencereli oyunlarda daha düşük gecikmeli sunum (flip model) ve değişken yenileme hızı. Oyun yeniden başlatılınca etkilidir.";
    public override TweakScope Scope => TweakScope.Persistent;

    private static Dictionary<string, string> Parse(string? raw)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in (raw ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = token.IndexOf('=');
            if (eq > 0) map[token[..eq].Trim()] = token[(eq + 1)..].Trim();
        }
        return map;
    }

    private static string Current()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) as string ?? string.Empty;
    }

    public override TweakState Read()
    {
        var map = Parse(Current());
        bool swap = map.TryGetValue("SwapEffectUpgradeEnable", out string? s) && s == "1";
        bool vrr = map.TryGetValue("VRROptimizeEnable", out string? v) && v == "1";
        return new TweakState(swap && vrr, $"pencereli iyileştirme {(swap ? "açık" : "kapalı")}, VRR {(vrr ? "açık" : "kapalı")}");
    }

    protected override bool ApplyCore(out string? error)
    {
        error = null;
        if (!_captured) { _previous = Current(); _captured = true; }
        var map = Parse(_previous);
        map["SwapEffectUpgradeEnable"] = "1";
        map["VRROptimizeEnable"] = "1";
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)!;
        key.SetValue(ValueName, string.Join(";", map.Select(p => p.Key + "=" + p.Value)) + ";", RegistryValueKind.String);
        return true;
    }

    protected override bool RevertCore(out string? error)
    {
        error = null;
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key is not null)
        {
            if (_captured)
            {
                if (string.IsNullOrEmpty(_previous)) key.DeleteValue(ValueName, throwOnMissingValue: false);
                else key.SetValue(ValueName, _previous, RegistryValueKind.String);
            }
            else
            {
                // Önceki değer bilinmiyor: yalnızca bizim iki anahtarımızı çıkar, diğer ayarlara dokunma.
                var map = Parse(Current());
                map.Remove("SwapEffectUpgradeEnable");
                map.Remove("VRROptimizeEnable");
                if (map.Count == 0) key.DeleteValue(ValueName, throwOnMissingValue: false);
                else key.SetValue(ValueName, string.Join(";", map.Select(p => p.Key + "=" + p.Value)) + ";", RegistryValueKind.String);
            }
        }
        _captured = false;
        return true;
    }
}

/// <summary>İzlenen oyunları "yüksek performanslı GPU"ya sabitler (hibrit dizüstülerde oyunun iGPU'da açılmasını önler).</summary>
internal sealed class GpuPreferenceTweak : TweakBase
{
    private const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private readonly Func<IEnumerable<string>> _paths;
    private readonly Dictionary<string, string?> _previous = new(StringComparer.OrdinalIgnoreCase);

    public GpuPreferenceTweak(Func<IEnumerable<string>> gamePaths) => _paths = gamePaths;

    public override string? ExportPrevious() => _previous.Count == 0 ? null : ToJson(_previous);
    protected override void ImportPreviousCore(string data)
    {
        _previous.Clear();
        foreach ((string path, string? value) in FromJson<Dictionary<string, string?>>(data) ?? new()) _previous[path] = value;
    }

    public override string Id => "gpu-preference";
    public override string Title => "Oyunları yüksek performanslı GPU'ya sabitle";
    public override string Description => "Windows grafik ayarındaki 'Yüksek performans' tercihini izlenen oyunlara yazar. Yolu bilinen oyunlar için geçerlidir (dosya seçilerek eklenen ya da bir kez çalışmış oyunlar).";
    public override TweakScope Scope => TweakScope.Persistent;

    private IReadOnlyList<string> KnownPaths => _paths().Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public override TweakState Read()
    {
        var paths = KnownPaths;
        if (paths.Count == 0) return new TweakState(false, "yolu bilinen oyun yok");
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
        int set = paths.Count(p => (key?.GetValue(p) as string ?? string.Empty).Contains("GpuPreference=2", StringComparison.OrdinalIgnoreCase));
        return new TweakState(set == paths.Count, $"{set}/{paths.Count} oyun yüksek performanslı GPU'da");
    }

    protected override bool ApplyCore(out string? error)
    {
        error = null;
        var paths = KnownPaths;
        if (paths.Count == 0) { error = "yolu bilinen oyun yok"; return false; }
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)!;
        foreach (string path in paths)
        {
            if (!_previous.ContainsKey(path)) _previous[path] = key.GetValue(path) as string;
            key.SetValue(path, "GpuPreference=2;", RegistryValueKind.String);
        }
        return true;
    }

    protected override bool RevertCore(out string? error)
    {
        error = null;
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key is not null)
        {
            if (_previous.Count == 0)
                foreach (string path in KnownPaths) key.DeleteValue(path, throwOnMissingValue: false);
            foreach ((string path, string? previous) in _previous)
            {
                if (previous is null) key.DeleteValue(path, throwOnMissingValue: false);
                else key.SetValue(path, previous, RegistryValueKind.String);
            }
        }
        _previous.Clear();
        return true;
    }
}
