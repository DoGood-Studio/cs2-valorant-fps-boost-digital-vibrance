using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GameColorGuard.Core;

internal sealed class NvidiaColorControl : IDisposable
{
    private const int Ok = 0;
    private const uint NvApiInitializeId = 0x0150E828;
    private const uint NvApiUnloadId = 0xD22BDD7E;
    private const uint EnumDisplayHandleId = 0x9ABDD40D;
    private const uint GetDvcInfoExId = 0x0E45002D;
    private const uint SetDvcLevelExId = 0x4A82C2B1;
    private const uint GetHueInfoId = 0x95B64341;
    private const uint SetHueAngleId = 0xF5A0F22C;

    private readonly List<DisplayState> _displays = new();
    private readonly Dictionary<IntPtr, (int? Level, uint? Hue)> _appliedReadback = new();
    private NvApiUnload? _unload;
    private NvApiSetDvcLevelEx? _setDvc;
    private NvApiSetHueAngle? _setHue;
    private NvApiGetDvcInfoEx? _getDvc;
    private NvApiGetHueInfo? _getHue;
    private bool _disposed;

    public bool IsAvailable => _displays.Count > 0;
    public int DisplayCount => _displays.Count;

    private readonly int? _profileVibrance;
    private readonly int? _profileHue;
    private readonly bool _leftoverSuspected;

    /// <param name="profileVibrance">Kayıtlı profilin Digital Vibrance değeri (açılışta ekranda kalmışsa tanımak için).</param>
    /// <param name="profileHue">Kayıtlı profilin renk tonu değeri.</param>
    /// <param name="leftoverSuspected">Önceki oturum profil ekrandayken kapandıysa true.</param>
    public NvidiaColorControl(int? profileVibrance = null, int? profileHue = null, bool leftoverSuspected = false)
    {
        _profileVibrance = profileVibrance;
        _profileHue = profileHue;
        _leftoverSuspected = leftoverSuspected;
        try
        {
            var initialize = GetDelegate<NvApiInitialize>(NvApiInitializeId);
            _unload = GetDelegate<NvApiUnload>(NvApiUnloadId);
            var enumerate = GetDelegate<NvApiEnumNvidiaDisplayHandle>(EnumDisplayHandleId);
            var getDvc = GetDelegate<NvApiGetDvcInfoEx>(GetDvcInfoExId);
            _getDvc = getDvc;
            _setDvc = GetDelegate<NvApiSetDvcLevelEx>(SetDvcLevelExId);
            var getHue = GetDelegate<NvApiGetHueInfo>(GetHueInfoId);
            _getHue = getHue;
            _setHue = GetDelegate<NvApiSetHueAngle>(SetHueAngleId);

            if (initialize is null || enumerate is null || initialize() != Ok) return;

            for (int index = 0; index < 64; index++)
            {
                if (enumerate(index, out IntPtr handle) != Ok || handle == IntPtr.Zero) break;

                DvcInfoEx dvc = DvcInfoEx.Create();
                HueInfo hue = HueInfo.Create();
                bool dvcSupported = getDvc is not null && getDvc(handle, 0, ref dvc) == Ok;
                bool hueSupported = getHue is not null && getHue(handle, 0, ref hue) == Ok;
                if (dvcSupported || hueSupported)
                    _displays.Add(new DisplayState(handle, dvcSupported ? dvc : null, hueSupported ? hue : null));
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (BadImageFormatException) { }
    }

    public bool Apply(int vibrance, int hue, out string? error)
    {
        error = null;
        if (!IsAvailable)
        {
            error = "NVIDIA renk denetimi bulunamadı; yalnızca Windows renkleri uygulandı.";
            return false;
        }

        int successfulDisplays = 0;
        foreach (DisplayState display in _displays)
        {
            bool attempted = false;
            bool success = true;

            if (display.OriginalDvc is DvcInfoEx originalDvc && _setDvc is not null)
            {
                attempted = true;
                DvcInfoEx value = DvcInfoEx.Create();
                value.CurrentLevel = Math.Clamp(vibrance, originalDvc.MinLevel, originalDvc.MaxLevel);
                success &= _setDvc(display.Handle, 0, ref value) == Ok;
            }

            if (display.OriginalHue is not null && _setHue is not null)
            {
                attempted = true;
                success &= _setHue(display.Handle, 0, (uint)Math.Clamp(hue, 0, 360)) == Ok;
            }

            if (attempted && success) successfulDisplays++;
        }

        if (successfulDisplays > 0)
        {
            // Denetim, yazdığımız hedef değerle yapılır (2.1'de sahada çalışan yöntem). Hemen ardından
            // okunan değer kullanılırsa, yazmayla okuma arasında sürücü sıfırlarsa bu hiç fark edilmez.
            _appliedReadback.Clear();
            foreach (DisplayState display in _displays)
            {
                int? level = display.OriginalDvc is DvcInfoEx dvc
                    ? Math.Clamp(vibrance, dvc.MinLevel, dvc.MaxLevel)
                    : null;
                uint? angle = display.OriginalHue is not null ? (uint)Math.Clamp(hue, 0, 360) : null;
                _appliedReadback[display.Handle] = (level, angle);
            }
            return true;
        }
        error = "NVIDIA sürücüsü Digital Vibrance/Renk Tonu ayarını kabul etmedi.";
        return false;
    }

    public void Restore()
    {
        _appliedReadback.Clear();
        foreach (DisplayState display in _displays)
        {
            if (display.OriginalDvc is DvcInfoEx originalDvc && _setDvc is not null)
            {
                DvcInfoEx value = DvcInfoEx.Create();
                value.CurrentLevel = BaselineVibrance(originalDvc);
                _setDvc(display.Handle, 0, ref value);
            }

            if (display.OriginalHue is HueInfo originalHue && _setHue is not null)
                _setHue(display.Handle, 0, BaselineHue(originalHue));
        }
    }

    /// <summary>
    /// Profil uygulandıktan hemen sonra sürücünün bildirdiği değerler hâlâ yerinde mi?
    /// Sürücü veya oyun renkleri sıfırladığında false döner; çağıran profili yeniden uygular.
    /// </summary>
    public bool StillApplied()
    {
        foreach (var pair in _appliedReadback)
        {
            (int? level, uint? hue) = Read(pair.Key);
            if (pair.Value.Level.HasValue && level.HasValue && level != pair.Value.Level) return false;
            if (pair.Value.Hue.HasValue && hue.HasValue && hue != pair.Value.Hue) return false;
        }
        return true;
    }

    /// <summary>Tanı: ilk ekranın şu anki DVC seviyesi ve renk tonu.</summary>
    internal string DiagnosticSample()
    {
        if (_displays.Count == 0) return "yok";
        (int? level, uint? hue) = Read(_displays[0].Handle);
        return $"DVC {level?.ToString() ?? "?"} / ton {hue?.ToString() ?? "?"}";
    }

    private (int? Level, uint? Hue) Read(IntPtr handle)
    {
        int? level = null;
        uint? hue = null;
        if (_getDvc is not null)
        {
            DvcInfoEx dvc = DvcInfoEx.Create();
            if (_getDvc(handle, 0, ref dvc) == Ok) level = dvc.CurrentLevel;
        }
        if (_getHue is not null)
        {
            HueInfo info = HueInfo.Create();
            if (_getHue(handle, 0, ref info) == Ok) hue = info.CurrentHueAngle;
        }
        return (level, hue);
    }

    // Açılışta yakalanan değer, önceki bir oturumun kapanmadan bıraktığı profil olabilir
    // (değer kayıtlı profille aynıysa ya da oturum profil ekrandayken kapandıysa). O durumda
    // sürücünün varsayılanına dönülür; aksi halde kullanıcının kendi ayarı korunur.
    private int BaselineVibrance(DvcInfoEx info)
    {
        bool saneDefault = info.DefaultLevel > info.MinLevel && info.DefaultLevel < info.MaxLevel;
        bool leftover = _leftoverSuspected || (_profileVibrance.HasValue &&
            info.CurrentLevel == Math.Clamp(_profileVibrance.Value, info.MinLevel, info.MaxLevel));
        return saneDefault && leftover && info.CurrentLevel != info.DefaultLevel ? info.DefaultLevel : info.CurrentLevel;
    }

    private uint BaselineHue(HueInfo info)
    {
        bool leftover = _leftoverSuspected || (_profileHue.HasValue && info.CurrentHueAngle == (uint)Math.Clamp(_profileHue.Value, 0, 360));
        return leftover && info.DefaultHueAngle <= 360 ? info.DefaultHueAngle : info.CurrentHueAngle;
    }

    internal NvidiaTestResult TestCurrentValues()
    {
        int dvcReads = 0;
        int hueReads = 0;
        int dvcWrites = 0;
        int hueWrites = 0;
        var values = new List<object>();

        foreach (DisplayState display in _displays)
        {
            if (display.OriginalDvc is DvcInfoEx dvc && _setDvc is not null)
            {
                dvcReads++;
                DvcInfoEx same = DvcInfoEx.Create();
                same.CurrentLevel = dvc.CurrentLevel;
                if (_setDvc(display.Handle, 0, ref same) == Ok) dvcWrites++;
            }
            if (display.OriginalHue is HueInfo hue && _setHue is not null)
            {
                hueReads++;
                if (_setHue(display.Handle, 0, hue.CurrentHueAngle) == Ok) hueWrites++;
            }

            values.Add(new
            {
                Vibrance = display.OriginalDvc?.CurrentLevel,
                VibranceMin = display.OriginalDvc?.MinLevel,
                VibranceMax = display.OriginalDvc?.MaxLevel,
                Hue = display.OriginalHue?.CurrentHueAngle
            });
        }

        return new NvidiaTestResult(
            IsAvailable && dvcReads > 0 && dvcWrites == dvcReads && hueReads > 0 && hueWrites == hueReads,
            _displays.Count, dvcReads, dvcWrites, hueReads, hueWrites, values);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Restore();
        _unload?.Invoke();
    }

    private static T? GetDelegate<T>(uint id) where T : Delegate
    {
        IntPtr pointer = NativeMethods.QueryInterface(id);
        return pointer == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    private sealed record DisplayState(IntPtr Handle, DvcInfoEx? OriginalDvc, HueInfo? OriginalHue);

    [StructLayout(LayoutKind.Sequential)]
    private struct DvcInfoEx
    {
        public uint Version;
        public int CurrentLevel;
        public int MinLevel;
        public int MaxLevel;
        public int DefaultLevel;

        public static DvcInfoEx Create() => new() { Version = (uint)(Marshal.SizeOf<DvcInfoEx>() | (1 << 16)) };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HueInfo
    {
        public uint Version;
        public uint CurrentHueAngle;
        public uint DefaultHueAngle;

        public static HueInfo Create() => new() { Version = (uint)(Marshal.SizeOf<HueInfo>() | (1 << 16)) };
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvApiInitialize();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvApiUnload();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvApiEnumNvidiaDisplayHandle(int index, out IntPtr displayHandle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvApiGetDvcInfoEx(IntPtr displayHandle, uint outputId, ref DvcInfoEx info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvApiSetDvcLevelEx(IntPtr displayHandle, uint outputId, ref DvcInfoEx info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvApiGetHueInfo(IntPtr displayHandle, uint outputId, ref HueInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvApiSetHueAngle(IntPtr displayHandle, uint outputId, uint hueAngle);

    private static class NativeMethods
    {
        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr QueryInterface(uint functionId);
    }
}

internal sealed record NvidiaTestResult(
    bool Passed,
    int DisplayCount,
    int DvcReads,
    int DvcWrites,
    int HueReads,
    int HueWrites,
    IReadOnlyList<object> Values);
