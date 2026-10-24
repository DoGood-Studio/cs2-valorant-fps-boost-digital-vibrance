using System;
using System.Runtime.InteropServices;

namespace GameColorGuard.Core;

/// <summary>
/// Windows Büyüteç API'sinin tam ekran renk matrisi (Windows'un "Renk filtreleri" de bunu kullanır; 2.1'deki
/// "Windows" canlılık motoru). DWM birleştirme aşamasında uygulanır, ekran kartı sürücüsünden bağımsızdır:
/// gama rampası ve NVIDIA DVC'nin ekrana ulaşmadığı hibrit grafikli dizüstülerde (harici monitör) de görünür.
/// Masaüstünde ve kenarlıksız / tam ekran pencereli oyunlarda görünür, özel tam ekranda görünmez.
/// Doğrusal olduğu için aydınlık, kontrast, RGB, canlılık ve tonu yapar; gamayı (eğri) yapamaz.
/// Etki süreç kapanınca Windows tarafından kendiliğinden kaldırılır.
/// </summary>
internal sealed class WindowsColorMatrix : IDisposable
{
    private const float ReadbackTolerance = 0.002f;
    private bool _initialized;
    private bool _initFailed;
    private float[]? _applied;
    private bool _disposed;

    public bool IsAvailable => EnsureInitialized();

    /// <summary>Gama dışındaki katmanlar nötrde mi (matris kimlik olur)?</summary>
    internal static bool IsNeutral(ColorSettings s) =>
        s.Brightness == 50 && s.Contrast == 50 && s.Red == 100 && s.Green == 100 && s.Blue == 100 &&
        s.DigitalVibrance == 50 && s.Hue == 0;

    public bool Apply(ColorSettings settings, out string? error)
    {
        error = null;
        if (_disposed) { error = "Windows renk motoru kapatıldı."; return false; }
        if (!EnsureInitialized())
        {
            error = "Windows renk motoru (Magnification.dll) başlatılamadı.";
            return false;
        }

        float[] matrix = BuildMatrix(settings);
        var effect = new MagColorEffect { Transform = matrix };
        if (!NativeMethods.MagSetFullscreenColorEffect(ref effect))
        {
            error = $"Windows renk matrisi reddedildi (Win32 hata {Marshal.GetLastWin32Error()}).";
            return false;
        }
        _applied = matrix;
        return true;
    }

    /// <summary>Uygulanan matris hâlâ etkin mi? Başka bir uygulama ya da Windows sıfırladıysa false.</summary>
    public bool StillApplied()
    {
        if (_disposed || !_initialized || _applied is null) return true;
        var current = new MagColorEffect { Transform = new float[25] };
        if (!NativeMethods.MagGetFullscreenColorEffect(ref current)) return true;
        for (int i = 0; i < 25; i++)
            if (Math.Abs(current.Transform[i] - _applied[i]) > ReadbackTolerance) return false;
        return true;
    }

    public void Restore()
    {
        _applied = null;
        if (_disposed || !_initialized) return;
        var identity = new MagColorEffect { Transform = Identity() };
        NativeMethods.MagSetFullscreenColorEffect(ref identity);
    }

    /// <summary>Tanı: matristeki kırmızı→kırmızı katsayısı (kimlikte 1,00).</summary>
    internal string DiagnosticSample()
    {
        if (!_initialized) return "başlatılmadı";
        var current = new MagColorEffect { Transform = new float[25] };
        return NativeMethods.MagGetFullscreenColorEffect(ref current)
            ? current.Transform[0].ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
            : "okunamadı";
    }

    public void Dispose()
    {
        if (_disposed) return;
        Restore();
        _disposed = true;
        if (_initialized)
        {
            try { NativeMethods.MagUninitialize(); } catch { }
            _initialized = false;
        }
    }

    private bool EnsureInitialized()
    {
        if (_initialized || _initFailed || _disposed) return _initialized;
        try
        {
            _initialized = NativeMethods.MagInitialize();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            AppLogger.Write("Windows renk motoru yüklenemedi", ex);
        }
        _initFailed = !_initialized;
        return _initialized;
    }

    /// <summary>
    /// Doğrusal renk dönüşümü: önce ton döndürme ve doygunluk (canlılık, 50 nötr → çarpan 0..2; 2.1 ile aynı),
    /// sonra kontrast (0,5 etrafında), aydınlık kaydırması ve RGB kanal kazancı — gama rampasındaki sırayla.
    /// MAGCOLOREFFECT satır-vektör kuralı kullanır: çıkış = [r g b a 1] × M, yani M[giriş][çıkış].
    /// </summary>
    internal static float[] BuildMatrix(ColorSettings s)
    {
        double saturation = Math.Clamp(s.DigitalVibrance, 0, 100) / 50.0;
        double angle = Math.Clamp(s.Hue, 0, 360) * Math.PI / 180.0;
        double c = Math.Cos(angle), n = Math.Sin(angle);
        const double lr = 0.213, lg = 0.715, lb = 0.072;

        // Sütun-vektör (SVG feColorMatrix) biçiminde 3x3: out_i = Σ_j A[i][j] · in_j
        double[,] hue =
        {
            { lr + c * (1 - lr) - n * lr,     lg - c * lg - n * lg,           lb - c * lb + n * (1 - lb) },
            { lr - c * lr + n * 0.143,        lg + c * (1 - lg) + n * 0.140,  lb - c * lb - n * 0.283 },
            { lr - c * lr - n * (1 - lr),     lg - c * lg + n * lg,           lb + c * (1 - lb) + n * lb }
        };
        double[,] sat =
        {
            { lr + (1 - lr) * saturation, lg - lg * saturation,       lb - lb * saturation },
            { lr - lr * saturation,       lg + (1 - lg) * saturation, lb - lb * saturation },
            { lr - lr * saturation,       lg - lg * saturation,       lb + (1 - lb) * saturation }
        };

        var color = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                for (int k = 0; k < 3; k++)
                    color[i, j] += sat[i, k] * hue[k, j];

        double contrast = s.Contrast / 50.0;
        double offset = 0.5 * (1 - contrast) + (s.Brightness - 50) / 100.0;
        double[] gain = { s.Red / 100.0, s.Green / 100.0, s.Blue / 100.0 };

        var m = new float[25];
        for (int input = 0; input < 3; input++)
            for (int output = 0; output < 3; output++)
                m[input * 5 + output] = (float)(gain[output] * contrast * color[output, input]);   // devrik: M[giriş][çıkış]
        for (int output = 0; output < 3; output++)
            m[20 + output] = (float)(gain[output] * offset);                                        // sabit terim satırı
        m[18] = 1f;   // alfa
        m[24] = 1f;
        return m;
    }

    internal static float[] Identity()
    {
        var m = new float[25];
        m[0] = m[6] = m[12] = m[18] = m[24] = 1f;
        return m;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MagColorEffect
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)] public float[] Transform;
    }

    private static class NativeMethods
    {
        [DllImport("Magnification.dll", SetLastError = true)] internal static extern bool MagInitialize();
        [DllImport("Magnification.dll")] internal static extern bool MagUninitialize();
        [DllImport("Magnification.dll", SetLastError = true)] internal static extern bool MagSetFullscreenColorEffect(ref MagColorEffect effect);
        [DllImport("Magnification.dll")] internal static extern bool MagGetFullscreenColorEffect(ref MagColorEffect effect);
    }
}
