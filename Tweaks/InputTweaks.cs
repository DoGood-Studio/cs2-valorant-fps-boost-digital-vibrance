using System;
using System.Linq;
using GameColorGuard.Core;
using System.Runtime.InteropServices;

namespace GameColorGuard.Tweaks;

/// <summary>SystemParametersInfo sarmalayıcıları.</summary>
internal static class Spi
{
    public const uint GetMouse = 0x0003, SetMouse = 0x0004;
    public const uint GetKeyboardSpeed = 0x000A, SetKeyboardSpeed = 0x000B;
    public const uint GetKeyboardDelay = 0x0016, SetKeyboardDelay = 0x0017;
    public const uint GetMouseSpeed = 0x0070, SetMouseSpeed = 0x0071;
    public const uint GetStickyKeys = 0x003A, SetStickyKeys = 0x003B;
    public const uint GetToggleKeys = 0x0034, SetToggleKeys = 0x0035;
    public const uint GetFilterKeys = 0x0032, SetFilterKeys = 0x0033;
    public const uint UpdateIniFile = 0x0001, SendChange = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint action, uint param, int[] value, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint action, uint param, ref AccessibilityFlags value, uint flags);

    /// <summary>STICKYKEYS / TOGGLEKEYS / FILTERKEYS yapılarının ortak başı (cbSize + dwFlags); FILTERKEYS'in kalan alanları için yer ayrılır.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AccessibilityFlags
    {
        public uint Size;
        public uint Flags;
        public uint WaitMSec;
        public uint DelayMSec;
        public uint RepeatMSec;
        public uint BounceMSec;
    }

    public const uint HotkeyActive = 0x00000004;   // SKF_HOTKEYACTIVE, TKF_HOTKEYACTIVE, FKF_HOTKEYACTIVE aynı bit
    public const uint ConfirmHotkey = 0x00000008;
}

/// <summary>"İşaretçi kesinliğini artır" (fare ivmesi) kapalı ve hız 6/11.</summary>
internal sealed class MouseAccelerationTweak : TweakBase
{
    private int[]? _previousMouse;
    private int _previousSpeed;

    public override string? ExportPrevious() => _previousMouse is null ? null : ToJson(new[] { _previousMouse[0], _previousMouse[1], _previousMouse[2], _previousSpeed });
    protected override void ImportPreviousCore(string data)
    {
        int[] v = FromJson<int[]>(data) ?? throw new FormatException("fare");
        _previousMouse = new[] { v[0], v[1], v[2] };
        _previousSpeed = v[3];
    }

    public override string Id => "mouse-acceleration-off";
    public override string Title => "Fare ivmesi kapalı, hız 6/11";
    public override string Description => "Windows'un 'İşaretçi kesinliğini artır' ivmesini kapatır ve hızı ölçeklemesiz 6/11 değerine alır: imleç hareketi fare hareketiyle birebir olur (nişan tutarlılığı). Oyun içi hassasiyetinizi buna göre ayarlayın.";
    public override TweakScope Scope => TweakScope.Persistent;

    private static (int[] Mouse, int Speed) Current()
    {
        var mouse = new int[3];
        Spi.SystemParametersInfo(Spi.GetMouse, 0, mouse, 0);
        int speed = 0;
        Spi.SystemParametersInfo(Spi.GetMouseSpeed, 0, ref speed, 0);
        return (mouse, speed);
    }

    public override TweakState Read()
    {
        (int[] mouse, int speed) = Current();
        bool accelOff = mouse[2] == 0;
        return new TweakState(accelOff && speed == 10, $"ivme {(accelOff ? "kapalı" : "açık")}, hız {speed}/20 ({(speed == 10 ? "6/11, ölçeklemesiz" : "ölçekli")})");
    }

    protected override bool ApplyCore(out string? error)
    {
        error = null;
        if (_previousMouse is null) (_previousMouse, _previousSpeed) = Current();
        if (!Spi.SystemParametersInfo(Spi.SetMouse, 0, new[] { 0, 0, 0 }, Spi.UpdateIniFile | Spi.SendChange)) { error = "SPI_SETMOUSE başarısız"; return false; }
        if (!Spi.SystemParametersInfo(Spi.SetMouseSpeed, 0, new IntPtr(10), Spi.UpdateIniFile | Spi.SendChange)) { error = "SPI_SETMOUSESPEED başarısız"; return false; }
        return true;
    }

    protected override bool RevertCore(out string? error)
    {
        error = null;
        // Önceki değer bilinmiyorsa Windows varsayılanı: ivme açık (6/10/1), hız 10.
        int[] mouse = _previousMouse ?? new[] { 6, 10, 1 };
        int speed = _previousMouse is null ? 10 : _previousSpeed;
        Spi.SystemParametersInfo(Spi.SetMouse, 0, mouse, Spi.UpdateIniFile | Spi.SendChange);
        Spi.SystemParametersInfo(Spi.SetMouseSpeed, 0, new IntPtr(speed), Spi.UpdateIniFile | Spi.SendChange);
        _previousMouse = null;
        return true;
    }
}

/// <summary>Klavye tekrar gecikmesi en kısa, tekrar hızı en yüksek.</summary>
internal sealed class KeyboardRepeatTweak : TweakBase
{
    private int _previousDelay = -1;
    private int _previousSpeed = -1;

    public override string? ExportPrevious() => _previousDelay < 0 ? null : ToJson(new[] { _previousDelay, _previousSpeed });
    protected override void ImportPreviousCore(string data)
    {
        int[] v = FromJson<int[]>(data) ?? throw new FormatException("klavye");
        _previousDelay = v[0];
        _previousSpeed = v[1];
    }

    public override string Id => "keyboard-repeat-fast";
    public override string Title => "Klavye tekrarı: en kısa gecikme, en yüksek hız";
    public override string Description => "Basılı tutulan tuşun tekrar etmeye başlama gecikmesini en kısa (250 ms), tekrar hızını en yüksek değere alır. Oyun içi tuş tepkisini değil, Windows'taki tekrarlamayı etkiler.";
    public override TweakScope Scope => TweakScope.Persistent;

    private static (int Delay, int Speed) Current()
    {
        int delay = 0, speed = 0;
        Spi.SystemParametersInfo(Spi.GetKeyboardDelay, 0, ref delay, 0);
        Spi.SystemParametersInfo(Spi.GetKeyboardSpeed, 0, ref speed, 0);
        return (delay, speed);
    }

    public override TweakState Read()
    {
        (int delay, int speed) = Current();
        return new TweakState(delay == 0 && speed == 31, $"gecikme {delay}/3, hız {speed}/31");
    }

    protected override bool ApplyCore(out string? error)
    {
        error = null;
        if (_previousDelay < 0) (_previousDelay, _previousSpeed) = Current();
        bool ok = Spi.SystemParametersInfo(Spi.SetKeyboardDelay, 0, IntPtr.Zero, Spi.UpdateIniFile | Spi.SendChange)
               & Spi.SystemParametersInfo(Spi.SetKeyboardSpeed, 31, IntPtr.Zero, Spi.UpdateIniFile | Spi.SendChange);
        if (!ok) error = "SPI_SETKEYBOARD* başarısız";
        return ok;
    }

    protected override bool RevertCore(out string? error)
    {
        error = null;
        // Önceki değer bilinmiyorsa Windows varsayılanı: gecikme 1 (500 ms), hız 31.
        uint delay = _previousDelay < 0 ? 1u : (uint)_previousDelay;
        uint speed = _previousDelay < 0 ? 31u : (uint)_previousSpeed;
        Spi.SystemParametersInfo(Spi.SetKeyboardDelay, delay, IntPtr.Zero, Spi.UpdateIniFile | Spi.SendChange);
        Spi.SystemParametersInfo(Spi.SetKeyboardSpeed, speed, IntPtr.Zero, Spi.UpdateIniFile | Spi.SendChange);
        _previousDelay = -1;
        return true;
    }
}

/// <summary>Yapışkan / Filtre / Geçiş tuşları kısayolları (Shift×5, sağ Shift 8 sn, Num Lock 5 sn) oyun sırasında kapalı.</summary>
internal sealed class AccessibilityShortcutsTweak : TweakBase
{
    // cbSize yapıya göre değişir: STICKYKEYS/TOGGLEKEYS 8 bayt, FILTERKEYS 24 bayt.
    private readonly (uint Get, uint Set, uint Size)[] _pairs =
    {
        (Spi.GetStickyKeys, Spi.SetStickyKeys, 8),
        (Spi.GetToggleKeys, Spi.SetToggleKeys, 8),
        (Spi.GetFilterKeys, Spi.SetFilterKeys, 24)
    };
    private uint[]? _previousFlags;

    public override string? ExportPrevious() => _previousFlags is null ? null : ToJson(_previousFlags);
    protected override void ImportPreviousCore(string data) => _previousFlags = FromJson<uint[]>(data) ?? throw new FormatException("erişilebilirlik");

    public override string Id => "accessibility-shortcuts-off";
    public override string Title => "Erişilebilirlik kısayollarını kapat";
    public override string Description => "Maç ortasında Shift'e beş kez basınca çıkan 'Yapışkan Tuşlar' penceresi ve benzerleri (Filtre/Geçiş tuşları) oyun süresince devre dışı kalır; oyun kapanınca eski ayar geri gelir.";
    public override TweakScope Scope => TweakScope.GameRunning;

    private uint[] CurrentFlags()
    {
        var flags = new uint[_pairs.Length];
        for (int i = 0; i < _pairs.Length; i++)
        {
            var data = new Spi.AccessibilityFlags { Size = _pairs[i].Size };
            Spi.SystemParametersInfo(_pairs[i].Get, data.Size, ref data, 0);
            flags[i] = data.Flags;
        }
        return flags;
    }

    private void WriteFlags(uint[] flags)
    {
        for (int i = 0; i < _pairs.Length; i++)
        {
            var data = new Spi.AccessibilityFlags { Size = _pairs[i].Size };
            Spi.SystemParametersInfo(_pairs[i].Get, data.Size, ref data, 0);
            data.Flags = flags[i];
            Spi.SystemParametersInfo(_pairs[i].Set, data.Size, ref data, Spi.SendChange);
        }
    }

    public override TweakState Read()
    {
        uint[] flags = CurrentFlags();
        bool anyHotkey = flags.Any(f => (f & Spi.HotkeyActive) != 0);
        return new TweakState(!anyHotkey, anyHotkey ? "kısayollar açık (Windows varsayılanı)" : "kısayollar kapalı");
    }

    protected override bool ApplyCore(out string? error)
    {
        error = null;
        _previousFlags ??= CurrentFlags();
        WriteFlags(_previousFlags.Select(f => f & ~(Spi.HotkeyActive | Spi.ConfirmHotkey)).ToArray());
        return true;
    }

    protected override bool RevertCore(out string? error)
    {
        error = null;
        if (_previousFlags is not null) WriteFlags(_previousFlags);
        _previousFlags = null;
        return true;
    }
}

/// <summary>Oyun ön plandayken Windows tuşunu yutar (alt düzey klavye kancası; yalnızca bu süreçte, oyuna müdahale yok).</summary>
internal sealed class WindowsKeyLockTweak : TweakBase
{
    private const int WhKeyboardLl = 13;
    private const int VkLWin = 0x5B, VkRWin = 0x5C;
    private readonly Native.HookProc _callback;   // GC'ye karşı canlı tutulur
    private IntPtr _hook;

    public WindowsKeyLockTweak() => _callback = HookCallback;

    public override string Id => "windows-key-lock";
    public override string Title => "Oyunda Windows tuşunu kilitle";
    public override string Description => "Oyun ön plandayken yanlışlıkla basılan Windows tuşu Başlat menüsünü açmaz. Alt+Tab ve diğer tuşlar etkilenmez; oyundan çıkınca kilit kalkar.";
    public override TweakScope Scope => TweakScope.GameFocused;

    public override TweakState Read() => new(_hook != IntPtr.Zero, _hook != IntPtr.Zero ? "kilit açık" : "kilit kapalı");

    protected override bool ApplyCore(out string? error)
    {
        error = null;
        if (_hook != IntPtr.Zero) return true;
        _hook = Native.SetWindowsHookEx(WhKeyboardLl, _callback, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) { error = $"kanca kurulamadı (Win32 {Marshal.GetLastWin32Error()})"; return false; }
        return true;
    }

    protected override bool RevertCore(out string? error)
    {
        error = null;
        if (_hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        return true;
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int vk = Marshal.ReadInt32(lParam);   // KBDLLHOOKSTRUCT.vkCode
            if (vk is VkLWin or VkRWin) return new IntPtr(1);
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static class Native
    {
        internal delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);
        [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string? module);
    }
}
