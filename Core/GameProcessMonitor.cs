using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace GameColorGuard.Core;

/// <summary>Ön plandaki pencere: sahibi olan süreç ve pencere sınıfı (Alt+Tab menüsü gibi geçici kabuk pencerelerini ayırmak için).</summary>
internal sealed record ForegroundWindow(IntPtr Handle, uint ProcessId, string Executable, string ClassName);

internal static class GameProcessMonitor
{
    private static readonly Dictionary<string, string> KnownGames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs2.exe"] = "Counter-Strike 2",
        ["valorant-win64-shipping.exe"] = "VALORANT",
        ["r5apex.exe"] = "Apex Legends",
        ["r5apex_dx12.exe"] = "Apex Legends",
        ["fortniteclient-win64-shipping.exe"] = "Fortnite",
        ["overwatch.exe"] = "Overwatch 2",
        ["rainbowsix.exe"] = "Rainbow Six Siege",
        ["rainbowsix_be.exe"] = "Rainbow Six Siege",
        ["tslgame.exe"] = "PUBG: Battlegrounds",
        ["league of legends.exe"] = "League of Legends",
        ["dota2.exe"] = "Dota 2",
        ["cod.exe"] = "Call of Duty",
        ["deltaforce-win64-shipping.exe"] = "Delta Force",
        ["marvel-win64-shipping.exe"] = "Marvel Rivals",
    };

    // Odağı kısa süreliğine alan ama oyunu örtmeyen kabuk pencereleri: Alt+Tab menüsü, görev çubuğu,
    // Başlat/arama/bildirim merkezi, açılır menüler. Bunlar ön plana geçince profil geri alınmaz.
    private static readonly HashSet<string> TransientShellClasses = new(StringComparer.Ordinal)
    {
        "XamlExplorerHostIslandWindow", "MultitaskingViewFrame", "TaskSwitcherWnd", "TaskSwitcherOverlayWnd",
        "ForegroundStaging", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland", "#32768", "Xaml_WindowedPopupClass",
        "tooltips_class32", "Progman", "WorkerW"
    };

    internal static IReadOnlyList<string> FindRunningGames(IEnumerable<string> executableNames)
    {
        var running = new List<string>();
        foreach (string executable in Normalized(executableNames))
        {
            if (ProcessIdsOf(executable).Count > 0) running.Add(executable);
        }
        return running;
    }

    internal static IReadOnlyList<int> ProcessIdsOf(string executable)
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)); }
        catch { return Array.Empty<int>(); }
        try { return processes.Select(p => p.Id).ToList(); }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }
    }

    internal static ForegroundWindow? Foreground()
    {
        IntPtr window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero) return null;
        NativeMethods.GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0) return null;

        var className = new StringBuilder(256);
        NativeMethods.GetClassName(window, className, className.Capacity);
        string executable;
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            executable = ColorSettings.NormalizeExecutableName(process.ProcessName);
        }
        catch
        {
            executable = "";
        }
        return new ForegroundWindow(window, processId, executable, className.ToString());
    }

    internal static string? MatchGame(ForegroundWindow? foreground, IEnumerable<string> executableNames) =>
        foreground is null || foreground.Executable.Length == 0
            ? null
            : Normalized(executableNames).FirstOrDefault(name => name.Equals(foreground.Executable, StringComparison.OrdinalIgnoreCase));

    internal static bool IsTransientShellWindow(ForegroundWindow? foreground) =>
        foreground is not null && TransientShellClasses.Contains(foreground.ClassName);

    /// <summary>
    /// Oyun ekranda görünür mü? Ana penceresi küçültülmemiş ve ön plandaki pencere oyunun bulunduğu
    /// ekranın neredeyse tamamını örtmüyorsa görünür sayılır (kenarlıksız oyun Alt+Tab'da arkada kalır).
    /// </summary>
    internal static bool IsGameVisible(string executable, ForegroundWindow? foreground)
    {
        IntPtr game = MainWindowOf(executable);
        if (game == IntPtr.Zero || !NativeMethods.IsWindowVisible(game) || NativeMethods.IsIconic(game)) return false;
        if (foreground is null || IsTransientShellWindow(foreground)) return true;
        if (foreground.Handle == game) return true;

        IntPtr monitor = NativeMethods.MonitorFromWindow(game, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info)) return true;
        if (!TryGetBounds(foreground.Handle, out Rect cover)) return true;

        Rect screen = info.Monitor;
        long screenArea = (long)(screen.Right - screen.Left) * (screen.Bottom - screen.Top);
        long overlapWidth = Math.Max(0, Math.Min(cover.Right, screen.Right) - Math.Max(cover.Left, screen.Left));
        long overlapHeight = Math.Max(0, Math.Min(cover.Bottom, screen.Bottom) - Math.Max(cover.Top, screen.Top));
        // Ekranın %90'ından fazlasını örten (büyütülmüş ya da tam ekran) bir pencere oyunu gizler.
        return screenArea <= 0 || overlapWidth * overlapHeight < screenArea * 0.9;
    }

    /// <summary>Oyun süreçlerinin görünür üst düzey pencereleri arasından en büyüğü.</summary>
    private static IntPtr MainWindowOf(string executable)
    {
        var ids = new HashSet<uint>(ProcessIdsOf(executable).Select(id => (uint)id));
        if (ids.Count == 0) return IntPtr.Zero;
        IntPtr best = IntPtr.Zero;
        long bestArea = -1;
        NativeMethods.EnumWindows((window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(window, out uint processId);
            if (!ids.Contains(processId) || !NativeMethods.IsWindowVisible(window)) return true;
            if (!NativeMethods.GetWindowRect(window, out Rect rect)) return true;
            // Küçültülmüş pencerenin dikdörtgeni anlamsızdır; yine de aday kalsın ki "küçültüldü" anlaşılsın.
            long area = NativeMethods.IsIconic(window) ? 0 : (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area > bestArea) { bestArea = area; best = window; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    private static bool TryGetBounds(IntPtr window, out Rect rect)
    {
        // Gölge kenarlarını dışarıda bırakan gerçek sınırlar; olmazsa klasik pencere dikdörtgeni.
        if (NativeMethods.DwmGetWindowAttribute(window, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out rect, Marshal.SizeOf<Rect>()) == 0)
            return true;
        return NativeMethods.GetWindowRect(window, out rect);
    }

    private static IEnumerable<string> Normalized(IEnumerable<string> executableNames) =>
        executableNames.Select(ColorSettings.NormalizeExecutableName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    internal static string FriendlyName(string executable) =>
        KnownGames.TryGetValue(executable, out string? name) ? name : Path.GetFileNameWithoutExtension(executable);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    private static class NativeMethods
    {
        internal delegate bool EnumWindowsProc(IntPtr window, IntPtr data);

        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, StringBuilder name, int size);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll")] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
    }
}

/// <summary>
/// Ön plan değişimini (EVENT_SYSTEM_FOREGROUND) ve küçültme/geri yüklemeyi anında bildirir; 250 ms yoklamayı beklemeden
/// profil oyuna dönüşte hemen uygulanır. Arayüz iş parçacığında kurulur, geri çağrı da orada gelir.
/// </summary>
internal sealed class ForegroundHook : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint WinEventOutOfContext = 0x0000;

    private readonly WinEventDelegate _callback;   // GC'ye karşı canlı tutulur
    private readonly Action _changed;
    private readonly IntPtr _foregroundHook;
    private readonly IntPtr _minimizeHook;

    public ForegroundHook(Action changed)
    {
        _changed = changed;
        _callback = (_, _, _, _, _, _, _) => _changed();
        _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _callback, 0, 0, WinEventOutOfContext);
        _minimizeHook = SetWinEventHook(EventSystemMinimizeStart, EventSystemMinimizeEnd, IntPtr.Zero, _callback, 0, 0, WinEventOutOfContext);
        if (_foregroundHook == IntPtr.Zero) AppLogger.Write("Ön plan kancası kurulamadı", "Yalnızca 250 ms yoklama kullanılacak.");
    }

    public void Dispose()
    {
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        if (_minimizeHook != IntPtr.Zero) UnhookWinEvent(_minimizeHook);
    }

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
}
