using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GameColorGuard.Core;

internal sealed class DisplayGamma : IDisposable
{
    private readonly Dictionary<string, GammaRamp> _originals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GammaRamp> _appliedReadback = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private readonly ColorSettings? _profile;
    private readonly bool _leftoverSuspected;

    /// <param name="profile">Kayıtlı profil: açılışta ekranda bu profil duruyorsa "orijinal" sayılmaz.</param>
    /// <param name="leftoverSuspected">Önceki oturum profil ekrandayken kapandıysa true.</param>
    public DisplayGamma(ColorSettings? profile = null, bool leftoverSuspected = false)
    {
        _profile = profile;
        _leftoverSuspected = leftoverSuspected;
        CaptureOriginals();
    }

    public int DisplayCount => _originals.Count;

    public bool Apply(ColorSettings settings, out string? error)
    {
        error = null;
        var ramp = BuildRamp(settings);
        int success = 0;

        foreach (string device in EnumerateDisplayDevices())
        {
            IntPtr dc = NativeMethods.CreateDC("DISPLAY", device, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) continue;
            try
            {
                if (NativeMethods.SetDeviceGammaRamp(dc, ref ramp)) success++;
            }
            finally { NativeMethods.DeleteDC(dc); }
        }

        // Bazı sürücüler monitör DC'sinde reddedip masaüstü DC'sinde kabul eder.
        if (success == 0)
        {
            IntPtr desktopDc = NativeMethods.GetDC(IntPtr.Zero);
            if (desktopDc != IntPtr.Zero)
            {
                try
                {
                    if (NativeMethods.SetDeviceGammaRamp(desktopDc, ref ramp)) success++;
                }
                finally { NativeMethods.ReleaseDC(IntPtr.Zero, desktopDc); }
            }
        }

        if (success == 0)
        {
            error = "Windows gama ayarı HDR, Gece ışığı veya ekran sürücüsü tarafından engellendi.";
            return false;
        }
        CaptureReadback(ramp);
        return true;
    }

    public void Restore()
    {
        _appliedReadback.Clear();
        foreach (var pair in _originals)
        {
            IntPtr dc = NativeMethods.CreateDC("DISPLAY", pair.Key, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) continue;
            try
            {
                var ramp = pair.Value;
                NativeMethods.SetDeviceGammaRamp(dc, ref ramp);
            }
            finally { NativeMethods.DeleteDC(dc); }
        }
    }

    /// <summary>
    /// Profil uygulandıktan hemen sonra sürücünün bildirdiği rampa ile şu anki rampa aynı mı?
    /// Windows, sürücü veya oyun rampayı sıfırladığında false döner; çağıran profili yeniden uygular.
    /// (Sürücünün kendi dönüşümleri, karşılaştırma okunan değer üzerinden yapıldığı için yanlış alarm üretmez.)
    /// </summary>
    public bool StillApplied()
    {
        foreach (var pair in _appliedReadback)
        {
            IntPtr dc = NativeMethods.CreateDC("DISPLAY", pair.Key, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) continue;
            try
            {
                var current = new GammaRamp(true);
                if (!NativeMethods.GetDeviceGammaRamp(dc, ref current)) continue;
                if (!Close(current, pair.Value)) return false;
            }
            finally { NativeMethods.DeleteDC(dc); }
        }
        return true;
    }

    /// <summary>Tanı: ilk ekranın rampasında orta noktanın (128) kırmızı değeri; kimlik rampada 32896.</summary>
    internal string DiagnosticSample()
    {
        foreach (string device in EnumerateDisplayDevices())
        {
            IntPtr dc = NativeMethods.CreateDC("DISPLAY", device, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) continue;
            try
            {
                var ramp = new GammaRamp(true);
                return NativeMethods.GetDeviceGammaRamp(dc, ref ramp) ? ramp.Red[128].ToString() : "okunamadı";
            }
            finally { NativeMethods.DeleteDC(dc); }
        }
        return "yok";
    }

    private void CaptureReadback(GammaRamp desired)
    {
        _appliedReadback.Clear();
        foreach (string device in EnumerateDisplayDevices())
        {
            IntPtr dc = NativeMethods.CreateDC("DISPLAY", device, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) continue;
            try
            {
                var ramp = new GammaRamp(true);
                // Okunan rampa istenenden belirgin farklıysa (yazma ile okuma arasında sıfırlandıysa)
                // istenen rampa esas alınır; böylece bir sonraki denetim sıfırlamayı yakalar.
                if (NativeMethods.GetDeviceGammaRamp(dc, ref ramp))
                    _appliedReadback[device] = Close(ramp, desired) ? ramp : desired;
            }
            finally { NativeMethods.DeleteDC(dc); }
        }
    }

    private static bool Close(GammaRamp a, GammaRamp b)
    {
        const int tolerance = 0x300;
        for (int i = 0; i < 256; i++)
        {
            if (Math.Abs(a.Red[i] - b.Red[i]) > tolerance ||
                Math.Abs(a.Green[i] - b.Green[i]) > tolerance ||
                Math.Abs(a.Blue[i] - b.Blue[i]) > tolerance) return false;
        }
        return true;
    }

    private static GammaRamp IdentityRamp()
    {
        var ramp = new GammaRamp(true);
        for (int i = 0; i < 256; i++)
        {
            ushort v = (ushort)Math.Round(i / 255.0 * ushort.MaxValue);
            ramp.Red[i] = v;
            ramp.Green[i] = v;
            ramp.Blue[i] = v;
        }
        return ramp;
    }

    // Açılışta yakalanan rampa kimlik rampasından belirgin biçimde sapıyorsa, büyük olasılıkla
    // önceki bir oturumun kapanmadan bıraktığı profildir; geri yükleme için kimlik rampası kullanılır.
    // Monitör kalibrasyonu gibi küçük sapmalar korunur.
    private bool IsLeftover(GammaRamp ramp)
    {
        if (_profile is not null && Close(ramp, BuildRamp(_profile)) && !Close(ramp, IdentityRamp())) return true;
        int threshold = _leftoverSuspected ? 0x0200 : 0x0A00; // ~%0.8 / ~%3.9
        var identity = IdentityRamp();
        for (int i = 0; i < 256; i++)
        {
            if (Math.Abs(ramp.Red[i] - identity.Red[i]) > threshold ||
                Math.Abs(ramp.Green[i] - identity.Green[i]) > threshold ||
                Math.Abs(ramp.Blue[i] - identity.Blue[i]) > threshold) return true;
        }
        return false;
    }

    private void CaptureOriginals()
    {
        foreach (string device in EnumerateDisplayDevices())
        {
            IntPtr dc = NativeMethods.CreateDC("DISPLAY", device, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) continue;
            try
            {
                var ramp = new GammaRamp(true);
                if (NativeMethods.GetDeviceGammaRamp(dc, ref ramp))
                    _originals[device] = IsLeftover(ramp) ? IdentityRamp() : ramp;
            }
            finally { NativeMethods.DeleteDC(dc); }
        }
    }

    private static IEnumerable<string> EnumerateDisplayDevices()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (monitor, _, _, _) =>
            {
                var info = new MonitorInfoEx();
                info.Size = Marshal.SizeOf<MonitorInfoEx>();
                if (NativeMethods.GetMonitorInfo(monitor, ref info) && !string.IsNullOrWhiteSpace(info.DeviceName))
                    result.Add(info.DeviceName);
                return true;
            }, IntPtr.Zero);

        if (result.Count == 0) result.Add("DISPLAY");
        return result;
    }

    private static GammaRamp BuildRamp(ColorSettings s)
    {
        var ramp = new GammaRamp(true);
        ComputeRamp(s, ramp.Red, ramp.Green, ramp.Blue);
        return ramp;
    }

    /// <summary>Uygulanacak gama rampasını hesaplar; arayüzdeki ton eğrisi de aynı değerleri çizer.</summary>
    internal static void ComputeRamp(ColorSettings s, ushort[] red, ushort[] green, ushort[] blue)
    {
        // NVIDIA Denetim Masası ile aynı kullanıcı anlamı: 50 nötrdür.
        double brightnessOffset = (s.Brightness - 50) / 100.0;
        double contrast = s.Contrast / 50.0;
        double gamma = s.Gamma / 100.0;
        double[] channels = { s.Red / 100.0, s.Green / 100.0, s.Blue / 100.0 };

        for (int i = 0; i < 256; i++)
        {
            double input = i / 255.0;
            double adjusted = Math.Pow(input, 1.0 / Math.Max(0.1, gamma));
            adjusted = ((adjusted - 0.5) * contrast + 0.5) + brightnessOffset;

            red[i] = ToSafeUShort(adjusted * channels[0], i);
            green[i] = ToSafeUShort(adjusted * channels[1], i);
            blue[i] = ToSafeUShort(adjusted * channels[2], i);
        }
    }

    private static ushort ToSafeUShort(double value, int index)
    {
        int requested = (int)Math.Round(Math.Clamp(value, 0.0, 1.0) * ushort.MaxValue);
        int identity = (int)Math.Round(index / 255.0 * ushort.MaxValue);
        // Windows okunamaz ekranları önlemek için kimlik rampasından 32768'den
        // fazla uzaklaşan girdileri reddedebilir. Her girdiyi güvenli koridorda tut.
        return (ushort)Math.Clamp(requested,
            Math.Max(0, identity - 32767), Math.Min(ushort.MaxValue, identity + 32767));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Restore();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GammaRamp
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Red;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Green;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Blue;

        public GammaRamp(bool _) => (Red, Green, Blue) = (new ushort[256], new ushort[256], new ushort[256]);
    }

    private static class NativeMethods
    {
        internal delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateDC(string driver, string? device, string? output, IntPtr initData);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern bool GetDeviceGammaRamp(IntPtr dc, ref GammaRamp ramp);
        [DllImport("gdi32.dll")] internal static extern bool SetDeviceGammaRamp(IntPtr dc, ref GammaRamp ramp);
        [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
    }
}
