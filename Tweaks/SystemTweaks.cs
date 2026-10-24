using System;
using System.Collections.Generic;
using System.Linq;
using GameColorGuard.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameColorGuard.Tweaks;

/// <summary>
/// Windows güç modu kaydırıcısı ("En yüksek performans") oyun süresince. Etkin güç planı zaten Yüksek/Nihai
/// performans ise kaydırıcı Windows tarafından geçersiz sayılır; o durumda yapılacak bir şey yoktur ve bu dürüstçe bildirilir.
/// </summary>
internal sealed class PowerModeTweak : TweakBase
{
    private static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");
    private static readonly Guid BetterPerformance = new("3af9b8d9-7c97-431d-ad78-34a8bfea439f");
    private static readonly Guid BetterBattery = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid HighPerformancePlan = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid UltimatePerformancePlan = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    private Guid? _previous;
    private readonly bool _supported;

    public override string? ExportPrevious() => _previous?.ToString();
    protected override void ImportPreviousCore(string data) => _previous = Guid.Parse(data);
    private readonly string? _reason;

    public PowerModeTweak()
    {
        try
        {
            _supported = Native.PowerGetActualOverlayScheme(out _) == 0;
            if (!_supported) _reason = "Güç modu API'si bu sistemde yanıt vermiyor.";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _supported = false;
            _reason = "powrprof.dll güç modu API'si yok (Windows 10 1709+ gerekir).";
        }
    }

    public override string Id => "power-mode-best";
    public override string Title => "Güç modu: En yüksek performans";
    public override string Description => "Windows güç kaydırıcısını oyun süresince 'En yüksek performans'a alır (işlemci daha agresif hızlanır); oyun kapanınca önceki moda döner. Güç planınız zaten Yüksek/Nihai performans ise kaydırıcı geçersizdir ve bir şey yapılmaz. Dizüstünde ısı ve pil tüketimi artar, donanım sınırları içinde kalır.";
    public override TweakScope Scope => TweakScope.GameRunning;
    public override bool IsSupported => _supported;
    public override string? UnsupportedReason => _reason;

    private static string Name(Guid mode) =>
        mode == BestPerformance ? "En yüksek performans" :
        mode == BetterPerformance ? "Daha iyi performans" :
        mode == BetterBattery ? "Daha iyi pil" :
        mode == Guid.Empty ? "Dengeli" : mode.ToString();

    /// <summary>Etkin güç planı ve kaydırıcının bu planda geçerli olup olmadığı.</summary>
    public static (Guid Plan, string PlanName, bool SliderApplies) ActivePlan()
    {
        IntPtr pointer = IntPtr.Zero;
        try
        {
            if (Native.PowerGetActiveScheme(IntPtr.Zero, out pointer) != 0 || pointer == IntPtr.Zero) return (Guid.Empty, "bilinmiyor", true);
            Guid plan = Marshal.PtrToStructure<Guid>(pointer);
            string name = ReadPlanName(plan);
            bool highPlan = plan == HighPerformancePlan || plan == UltimatePerformancePlan ||
                name.Contains("performans", StringComparison.OrdinalIgnoreCase) || name.Contains("performance", StringComparison.OrdinalIgnoreCase);
            return (plan, name, !highPlan);
        }
        finally { if (pointer != IntPtr.Zero) Native.LocalFree(pointer); }
    }

    private static string ReadPlanName(Guid plan)
    {
        uint size = 0;
        Native.PowerReadFriendlyName(IntPtr.Zero, ref plan, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
        if (size == 0) return plan.ToString();
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.PowerReadFriendlyName(IntPtr.Zero, ref plan, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0) return plan.ToString();
            return Marshal.PtrToStringUni(buffer) ?? plan.ToString();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public override TweakState Read()
    {
        if (!_supported) return new TweakState(false, _reason ?? "desteklenmiyor");
        (_, string planName, bool sliderApplies) = ActivePlan();
        if (!sliderApplies) return new TweakState(true, $"plan '{planName}' zaten en yüksek performans; kaydırıcı bu planda geçersiz");
        Native.PowerGetEffectiveOverlayScheme(out Guid effective);
        return new TweakState(effective == BestPerformance, $"plan '{planName}', güç modu: {Name(effective)}");
    }

    protected override bool ApplyCore(out string? error)
    {
        error = null;
        if (!ActivePlan().SliderApplies) return true;   // yapılacak bir şey yok
        if (_previous is null)
        {
            Native.PowerGetActualOverlayScheme(out Guid current);
            _previous = current;
        }
        Guid target = BestPerformance;
        uint result = Native.PowerSetActiveOverlayScheme(ref target);
        if (result != 0) { error = $"PowerSetActiveOverlayScheme hata {result}"; return false; }
        Native.PowerGetEffectiveOverlayScheme(out Guid effective);
        if (effective != BestPerformance) { error = "sürücü/plan güç modunu kabul etmedi"; return false; }
        return true;
    }

    protected override bool RevertCore(out string? error)
    {
        error = null;
        if (_previous is Guid previous)
        {
            Guid target = previous;
            Native.PowerSetActiveOverlayScheme(ref target);
        }
        _previous = null;
        return true;
    }

    private static class Native
    {
        [DllImport("powrprof.dll")] internal static extern uint PowerGetActualOverlayScheme(out Guid scheme);
        [DllImport("powrprof.dll")] internal static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);
        [DllImport("powrprof.dll")] internal static extern uint PowerSetActiveOverlayScheme(ref Guid scheme);
        [DllImport("powrprof.dll")] internal static extern uint PowerGetActiveScheme(IntPtr rootKey, out IntPtr scheme);
        [DllImport("powrprof.dll")] internal static extern uint PowerReadFriendlyName(IntPtr rootKey, ref Guid scheme, IntPtr subGroup, IntPtr setting, IntPtr buffer, ref uint size);
        [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr memory);
    }
}
/// <summary>Çalışan oyun süreçlerine "Yüksek" öncelik (Gerçek Zamanlı değil).</summary>
internal sealed class ProcessPriorityTweak : TweakBase
{
    private readonly Func<IEnumerable<int>> _gameProcessIds;
    private readonly HashSet<int> _raised = new();

    public ProcessPriorityTweak(Func<IEnumerable<int>> gameProcessIds) => _gameProcessIds = gameProcessIds;

    public override string Id => "process-priority-high";
    public override string Title => "Oyun sürecine yüksek öncelik";
    public override string Description => "Oyun çalışırken işlemci zamanlayıcısında oyunu arka plan işlerinin önüne alır ('Yüksek'; sistemi kilitleyebilen 'Gerçek Zamanlı' kullanılmaz). Anti-hile korumalı bazı oyunlar buna izin vermez; o zaman atlanır.";
    public override TweakScope Scope => TweakScope.GameRunning;

    public override TweakState Read()
    {
        var ids = _gameProcessIds().ToList();
        if (ids.Count == 0) return new TweakState(false, "oyun çalışmıyor");
        int high = 0;
        foreach (int id in ids)
        {
            try { using var p = Process.GetProcessById(id); if (p.PriorityClass == ProcessPriorityClass.High) high++; }
            catch { }
        }
        return new TweakState(high == ids.Count, $"{high}/{ids.Count} oyun süreci yüksek öncelikte");
    }

    /// <summary>Oyun çalışırken yeni süreçler doğabilir; her değişimde yeniden çağrılır.</summary>
    public void RaiseNewProcesses()
    {
        foreach (int id in _gameProcessIds())
        {
            if (_raised.Contains(id)) continue;
            try
            {
                using var process = Process.GetProcessById(id);
                if (process.PriorityClass != ProcessPriorityClass.High) process.PriorityClass = ProcessPriorityClass.High;
                _raised.Add(id);
            }
            catch (Exception ex)
            {
                _raised.Add(id);   // tekrar denemeyelim
                AppLogger.Warn($"Süreç önceliği değiştirilemedi (pid {id}): {ex.Message}");
            }
        }
    }

    protected override bool ApplyCore(out string? error)
    {
        error = null;
        RaiseNewProcesses();
        return true;
    }

    protected override bool RevertCore(out string? error)
    {
        error = null;
        foreach (int id in _raised)
        {
            try { using var p = Process.GetProcessById(id); p.PriorityClass = ProcessPriorityClass.Normal; } catch { }
        }
        _raised.Clear();
        return true;
    }
}

/// <summary>Birincil ekranı, mevcut çözünürlükte desteklenen en yüksek yenileme hızına alır.</summary>
internal sealed class RefreshRateTweak : TweakBase
{
    private const int EnumCurrentSettings = -1;
    private const int DmPelsWidth = 0x00080000, DmPelsHeight = 0x00100000, DmDisplayFrequency = 0x00400000;
    private const uint CdsUpdateRegistry = 0x00000001;
    private readonly Func<int> _getStored;
    private readonly Action<int> _setStored;

    /// <param name="getStoredPrevious">Ayar dosyasında saklanan önceki Hz (oturumlar arası geri alma için).</param>
    public RefreshRateTweak(Func<int> getStoredPrevious, Action<int> setStoredPrevious)
    {
        _getStored = getStoredPrevious;
        _setStored = setStoredPrevious;
    }

    private int _previousFrequency
    {
        get => _getStored();
        set => _setStored(value);
    }

    public override string Id => "max-refresh-rate";
    public override string Title => "Ekranı en yüksek yenileme hızına al";
    public override string Description => "Birincil ekranın mevcut çözünürlükte desteklediği en yüksek Hz değerini ayarlar (örn. 60 Hz'de kalmış 144/165 Hz paneller). Kapatınca önceki değere döner.";
    public override TweakScope Scope => TweakScope.Persistent;

    public (string Device, int Width, int Height, int Current, int Max) Query()
    {
        string device = PrimaryDevice();
        var current = DevMode.Create();
        if (!Native.EnumDisplaySettings(device, EnumCurrentSettings, ref current)) return (device, 0, 0, 0, 0);
        int max = current.dmDisplayFrequency;
        var mode = DevMode.Create();
        for (int i = 0; Native.EnumDisplaySettings(device, i, ref mode); i++)
        {
            if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight &&
                mode.dmBitsPerPel == current.dmBitsPerPel && mode.dmDisplayFrequency > max)
                max = mode.dmDisplayFrequency;
            mode = DevMode.Create();
        }
        return (device, current.dmPelsWidth, current.dmPelsHeight, current.dmDisplayFrequency, max);
    }

    public override TweakState Read()
    {
        var q = Query();
        if (q.Current == 0) return new TweakState(false, "ekran modu okunamadı");
        return new TweakState(q.Current >= q.Max, $"{q.Width}×{q.Height} @ {q.Current} Hz (en yüksek {q.Max} Hz)");
    }

    protected override bool ApplyCore(out string? error)
    {
        error = null;
        var q = Query();
        if (q.Current == 0) { error = "ekran modu okunamadı"; return false; }
        if (q.Current >= q.Max) { _previousFrequency = q.Current; return true; }
        if (_previousFrequency == 0) _previousFrequency = q.Current;
        return SetFrequency(q.Device, q.Max, out error);
    }

    protected override bool RevertCore(out string? error)
    {
        error = null;
        if (_previousFrequency == 0) return true;
        bool ok = SetFrequency(PrimaryDevice(), _previousFrequency, out error);
        _previousFrequency = 0;
        return ok;
    }

    private static bool SetFrequency(string device, int frequency, out string? error)
    {
        error = null;
        var mode = DevMode.Create();
        if (!Native.EnumDisplaySettings(device, EnumCurrentSettings, ref mode)) { error = "ekran modu okunamadı"; return false; }
        mode.dmDisplayFrequency = frequency;
        mode.dmFields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency;
        int result = Native.ChangeDisplaySettingsEx(device, ref mode, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);
        if (result != 0) { error = $"ChangeDisplaySettingsEx sonucu {result}"; return false; }
        return true;
    }

    private static string PrimaryDevice()
    {
        var device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; Native.EnumDisplayDevices(null, i, ref device, 0); i++)
        {
            if ((device.StateFlags & 0x4) != 0) return device.DeviceName;   // DISPLAY_DEVICE_PRIMARY_DEVICE
            device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        }
        return @"\\.\DISPLAY1";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;

        public static DevMode Create() => new() { dmSize = (ushort)Marshal.SizeOf<DevMode>() };
    }

    private static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool EnumDisplaySettings(string device, int modeNum, ref DevMode mode);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int ChangeDisplaySettingsEx(string device, ref DevMode mode, IntPtr hwnd, uint flags, IntPtr param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice displayDevice, uint flags);
    }
}
