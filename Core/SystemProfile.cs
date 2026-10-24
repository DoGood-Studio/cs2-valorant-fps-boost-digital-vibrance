using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using GameColorGuard.Tweaks;
using Microsoft.Win32;

namespace GameColorGuard.Core;

internal enum AdviceLevel { Good, Info, Warning }

/// <summary>Sistem sayfasındaki bir tespit: ne gördük, neden önemli, kullanıcı ne yapabilir.</summary>
internal sealed record Advice(AdviceLevel Level, string Title, string Detail, string? ActionText = null, string? ActionUri = null);

internal sealed record DisplayInfo(string Device, string Gpu, int Width, int Height, int RefreshRate, int MaxRefreshRate, bool Primary);

/// <summary>Oyunun kurulu olduğu sürücü: SSD mi, ne kadar boş.</summary>
internal sealed record GameDrive(string Root, bool? Ssd, double FreeGb, double TotalGb);

/// <summary>
/// Salt okunur sistem tanıma: dizüstü mü masaüstü mü, güç kaynağı, işlemci, RAM (ve XMP), ekran kartları, ekranlar,
/// güç planı, HAGS ve Bellek bütünlüğü. WMI kullanılmaz (yavaş ve ek paket gerektirir); SMBIOS tablosu, kayıt defteri
/// ve Windows API'leri okunur. Hiçbir şey değiştirmez.
/// </summary>
internal sealed class SystemProfile
{
    public string Manufacturer { get; private init; } = "";
    public string Model { get; private init; } = "";
    public int ChassisType { get; private init; }
    public bool IsLaptop { get; private init; }
    public bool HasBattery { get; private init; }
    public bool OnAcPower { get; private init; } = true;
    public int? BatteryPercent { get; private init; }
    public string Cpu { get; private init; } = "";
    public int LogicalCores { get; private init; }
    public double RamGb { get; private init; }
    public int? RamSpeed { get; private init; }
    public int? RamRatedSpeed { get; private init; }
    /// <summary>Takılı bellek modülü sayısı (SMBIOS); 1 ise bellek tek kanal çalışır.</summary>
    public int RamModules { get; private init; }
    public int RamLoadPercent { get; private init; }
    public int PhysicalCores { get; private init; }
    /// <summary>Intel 12. nesil+ gibi P/E çekirdekli işlemci.</summary>
    public bool HybridCpu { get; private init; }
    public int PerformanceCores { get; private init; }
    public int CpuBaseMhz { get; private init; }
    /// <summary>Windows'un bildirdiği anlık frekans sınırı (güç/ısı kısıtlaması varsa taban frekansın altına iner).</summary>
    public int CpuLimitMhz { get; private init; }
    public IReadOnlyList<GameDrive> GameDrives { get; private init; } = Array.Empty<GameDrive>();
    public DateTime? GpuDriverDate { get; private init; }
    public string? GpuDriverVersion { get; private init; }
    public IReadOnlyList<string> Gpus { get; private init; } = Array.Empty<string>();
    public bool HybridGraphics { get; private init; }
    public IReadOnlyList<DisplayInfo> Displays { get; private init; } = Array.Empty<DisplayInfo>();
    public DisplayInfo? PrimaryDisplay => Displays.FirstOrDefault(d => d.Primary) ?? Displays.FirstOrDefault();
    public string PowerPlan { get; private init; } = "";
    public bool HighPerformancePlan { get; private init; }
    public bool? Hags { get; private init; }
    public bool? MemoryIntegrity { get; private init; }
    public string Windows { get; private init; } = "";

    public string Kind => IsLaptop ? "Dizüstü" : ChassisType == 13 ? "Hepsi bir arada" : "Masaüstü";
    public string PowerText => !HasBattery ? "Şebeke" : (OnAcPower ? "Prizde" : "Pilde") + (BatteryPercent is int p ? $" · %{p}" : "");

    public static SystemProfile Detect(IEnumerable<string>? gamePaths = null)
    {
        Smbios smbios = Smbios.Read();
        NativeMethods.GetSystemPowerStatus(out PowerStatus power);
        bool hasBattery = power.BatteryFlag != 128 && power.BatteryFlag != 255;
        // SMBIOS kasa tipi: 8-10, 14, 30-32 taşınabilir; tip bilinmiyorsa pil varlığına bakılır.
        bool laptopChassis = smbios.Chassis is 8 or 9 or 10 or 14 or 30 or 31 or 32;
        bool desktopChassis = smbios.Chassis is 3 or 4 or 5 or 6 or 7 or 13 or 15 or 16 or 35 or 36;

        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        NativeMethods.GlobalMemoryStatusEx(ref memory);

        IReadOnlyList<string> gpus = GpuNames();
        (_, string planName, bool sliderApplies) = PowerModeTweak.ActivePlan();
        (int physical, bool hybrid, int pCores) = CoreTopology();
        (int baseMhz, int limitMhz) = CpuClocks();
        string? dedicated = gpus.FirstOrDefault(g => !IsIntegrated(g)) ?? gpus.FirstOrDefault();
        (DateTime? driverDate, string? driverVersion) = DriverInfo(dedicated);

        return new SystemProfile
        {
            Manufacturer = smbios.Manufacturer,
            Model = smbios.Product,
            ChassisType = smbios.Chassis,
            IsLaptop = laptopChassis || (!desktopChassis && hasBattery),
            HasBattery = hasBattery,
            OnAcPower = power.AcLineStatus != 0,
            BatteryPercent = hasBattery && power.BatteryLifePercent <= 100 ? power.BatteryLifePercent : null,
            Cpu = ReadString(Registry.LocalMachine, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.Trim() ?? "bilinmiyor",
            LogicalCores = Environment.ProcessorCount,
            RamGb = Math.Round(memory.TotalPhys / 1024.0 / 1024 / 1024, 1),
            RamSpeed = smbios.MemoryConfiguredSpeed,
            RamRatedSpeed = smbios.MemoryRatedSpeed,
            RamModules = smbios.MemoryModules,
            RamLoadPercent = (int)memory.MemoryLoad,
            PhysicalCores = physical,
            HybridCpu = hybrid,
            PerformanceCores = pCores,
            CpuBaseMhz = baseMhz,
            CpuLimitMhz = limitMhz,
            GameDrives = ReadGameDrives(gamePaths),
            GpuDriverDate = driverDate,
            GpuDriverVersion = driverVersion,
            Gpus = gpus,
            HybridGraphics = gpus.Count >= 2 && gpus.Any(IsIntegrated) && gpus.Any(g => !IsIntegrated(g)),
            Displays = ReadDisplays(),
            PowerPlan = planName,
            HighPerformancePlan = !sliderApplies,
            Hags = ReadInt(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode") is int h ? h == 2 : null,
            MemoryIntegrity = ReadInt(Registry.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled") is int m ? m == 1 : null,
            Windows = WindowsVersion()
        };
    }

    /// <summary>Tespitlere göre öneriler; uygulama bunların hiçbirini kendiliğinden değiştirmez.</summary>
    public IReadOnlyList<Advice> Advise()
    {
        var list = new List<Advice>();
        if (IsLaptop && HasBattery && !OnAcPower)
            list.Add(new Advice(AdviceLevel.Warning, "Dizüstü pilde çalışıyor",
                "Pildeyken işlemci ve ekran kartı güç sınırına iner; FPS yarıya kadar düşebilir. Oynarken adaptörü takın."));
        else if (IsLaptop)
            list.Add(new Advice(AdviceLevel.Good, "Dizüstü prizde", "Tam güç kullanılabilir. Üretici yazılımında (ör. Control Center) fan/performans profilini 'Performans' yapın."));

        if (HybridGraphics)
        {
            DisplayInfo? primary = PrimaryDisplay;
            bool primaryOnDedicated = primary is not null && !IsIntegrated(primary.Gpu);
            list.Add(primaryOnDedicated
                ? new Advice(AdviceLevel.Good, "Ekran doğrudan harici ekran kartına bağlı",
                    $"{primary!.Gpu} görüntüyü doğrudan veriyor; dahili GPU üzerinden kopyalama gecikmesi yok.")
                : new Advice(AdviceLevel.Info, "Hibrit grafik (iki ekran kartı)",
                    "Görüntü dahili GPU üzerinden geçiyor. Varsa üretici yazılımından MUX'u 'dGPU' moduna alın ya da harici monitörü ekran kartına bağlı porta takın. FPS Boost'taki 'GPU'ya sabitle' ayarını açın.",
                    "Grafik ayarlarını aç", "ms-settings:display-advancedgraphics"));
        }

        foreach (DisplayInfo display in Displays.Where(d => d.MaxRefreshRate > d.RefreshRate))
            list.Add(new Advice(AdviceLevel.Warning, $"Ekran {display.RefreshRate} Hz'de kalmış",
                $"{display.Width}×{display.Height} için {display.MaxRefreshRate} Hz destekleniyor. FPS Boost'tan 'en yüksek yenileme hızı' ayarını açın.",
                "Ekran ayarlarını aç", "ms-settings:display-advanced"));

        if (RamSpeed is int speed && RamRatedSpeed is int rated && rated > speed + 100)
            list.Add(new Advice(AdviceLevel.Warning, "RAM hızının altında çalışıyor",
                $"Bellek {speed} MT/s'de, desteklediği hız {rated} MT/s. " +
                (IsLaptop ? "Dizüstülerde bu genellikle üretici sınırıdır." : "BIOS'ta XMP/EXPO profilini açmak CS2'de belirgin FPS kazandırır.")));
        if (RamModules == 1)
            list.Add(new Advice(AdviceLevel.Warning, "RAM tek kanal çalışıyor (1 modül)",
                "Testlerde CS2'nin ortalama FPS'i az değişiyor ama en düşük %1'lik kareler ~%5 iyileşiyor; bant genişliğine duyarlı oyunlarda fark %30'u aşabiliyor. " +
                "Aynı hız ve boyutta ikinci bir modül takmak (çift kanal) en etkili donanım iyileştirmelerinden biri."));
        else if (RamModules >= 2)
            list.Add(new Advice(AdviceLevel.Good, $"RAM çift kanal ({RamModules} modül)", "Bellek bant genişliği tam kullanılıyor."));
        if (RamLoadPercent >= 80)
            list.Add(new Advice(AdviceLevel.Warning, $"Bellek %{RamLoadPercent} dolu",
                "Oyun belleği diske taşmak zorunda kalabilir (takılma). Arka plan sayfasından gereksiz uygulamaları kapatın."));

        if (CpuBaseMhz > 0 && CpuLimitMhz > 0 && CpuLimitMhz < CpuBaseMhz * 0.9)
            list.Add(new Advice(AdviceLevel.Warning, "İşlemci güç/ısı sınırında",
                $"Windows işlemciyi şu an {CpuLimitMhz} MHz ile sınırlıyor (taban {CpuBaseMhz} MHz). " +
                (IsLaptop ? "Prize takın, fan profilini 'Performans' yapın, havalandırma deliklerini temizleyin." : "Soğutmayı ve BIOS güç sınırlarını kontrol edin.")));
        if (HybridCpu)
            list.Add(new Advice(AdviceLevel.Info, $"P/E çekirdekli işlemci ({PerformanceCores} performans çekirdeği)",
                "Oyunu P-çekirdeklere sabitlemek ya da E-çekirdekleri kapatmak bazı sistemlerde takılmayı azaltıyor, bazılarında artırıyor (raporlar çelişkili). " +
                "Uygulama bunu kendiliğinden yapmaz; Windows Oyun Modu ve güncel BIOS/çipset sürücüsü genellikle yeterli."));

        foreach (GameDrive drive in GameDrives)
        {
            if (drive.Ssd == false)
                list.Add(new Advice(AdviceLevel.Warning, $"Oyun HDD üzerinde ({drive.Root})",
                    "Harita yüklemesi ve doku akışı sırasında takılma yapar. Oyunu bir SSD'ye taşımak (Steam › Depolama › Taşı) belirgin fark yaratır."));
            if (drive.TotalGb > 0 && drive.FreeGb / drive.TotalGb < 0.10)
                list.Add(new Advice(AdviceLevel.Warning, $"{drive.Root} neredeyse dolu ({drive.FreeGb:0} GB boş)",
                    "SSD'ler %10'un altında boş alanda yavaşlar; gölgelendirici önbelleği de yazılamayabilir."));
        }

        if (GpuDriverDate is DateTime date && (DateTime.Now - date).TotalDays > 180)
            list.Add(new Advice(AdviceLevel.Info, $"Ekran kartı sürücüsü {date:dd.MM.yyyy} tarihli",
                "Altı aydan eski. Yeni sürücüler CS2/VALORANT için performans ve kararlılık düzeltmeleri içerir (NVIDIA App / AMD Adrenalin / Intel)."));

        if (RamGb > 0 && RamGb < 15)
            list.Add(new Advice(AdviceLevel.Info, $"{RamGb:0.#} GB RAM",
                "16 GB altında oyun sırasında tarayıcı ve Discord gibi uygulamaları kapatmak takılmaları azaltır (Arka plan sayfası)."));

        if (!HighPerformancePlan && !IsLaptop)
            list.Add(new Advice(AdviceLevel.Info, $"Güç planı: {PowerPlan}",
                "FPS Boost'taki 'Güç modu' ayarı oyun sırasında en yüksek performansa alır; plan değiştirmeye gerek yok.",
                "Güç ayarlarını aç", "ms-settings:powersleep"));

        if (MemoryIntegrity == true)
            list.Add(new Advice(AdviceLevel.Info, "Bellek bütünlüğü (HVCI) açık",
                "Testlerde işlemci ağırlıklı oyunlarda %5-15 FPS kaybı görülüyor. Bu bir güvenlik özelliği olduğu için uygulama kapatmaz; karar sizin.",
                "Çekirdek yalıtımını aç", "windowsdefender://coreisolation"));
        if (Hags == false)
            list.Add(new Advice(AdviceLevel.Info, "Donanım hızlandırmalı GPU zamanlama kapalı",
                "Bazı sistemlerde kare gecikmesini azaltır, bazılarında fark etmez. Açmak yeniden başlatma ister; ölçerek karar verin.",
                "Grafik ayarlarını aç", "ms-settings:display-advancedgraphics"));

        if (list.All(a => a.Level != AdviceLevel.Warning))
            list.Insert(0, new Advice(AdviceLevel.Good, "Kritik sorun yok", "Donanım tarafında FPS'i kısan belirgin bir ayar bulunmadı."));
        return list;
    }

    /// <summary>Çalışan bir oyunun tam yolu (anti-hile korumalı süreçlerde de çalışan sınırlı sorgu hakkıyla).</summary>
    internal static string? ProcessPath(string executable)
    {
        foreach (int id in GameProcessMonitor.ProcessIdsOf(executable))
        {
            IntPtr handle = NativeMethods.OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)id);
            if (handle == IntPtr.Zero) continue;
            try
            {
                var buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                if (NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0) return buffer.ToString(0, size);
            }
            finally { NativeMethods.CloseHandle(handle); }
        }
        return null;
    }

    internal static bool IsIntegrated(string gpu) =>
        gpu.Contains("Intel", StringComparison.OrdinalIgnoreCase) && !gpu.Contains("Arc", StringComparison.OrdinalIgnoreCase) ||
        gpu.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) ||
        gpu.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase) ||
        gpu.Contains("Vega", StringComparison.OrdinalIgnoreCase) && !gpu.Contains("RX", StringComparison.OrdinalIgnoreCase);

    /// <summary>Fiziksel çekirdek sayısı, P/E ayrımı (verimlilik sınıfı) ve en yüksek sınıftaki çekirdek sayısı.</summary>
    private static (int Physical, bool Hybrid, int PerformanceCores) CoreTopology()
    {
        try
        {
            uint length = 0;
            NativeMethods.GetLogicalProcessorInformationEx(0 /* RelationProcessorCore */, IntPtr.Zero, ref length);
            if (length == 0) return (0, false, 0);
            IntPtr buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!NativeMethods.GetLogicalProcessorInformationEx(0, buffer, ref length)) return (0, false, 0);
                var classes = new List<byte>();
                for (int offset = 0; offset < length;)
                {
                    int size = Marshal.ReadInt32(buffer, offset + 4);
                    classes.Add(Marshal.ReadByte(buffer, offset + 9));   // PROCESSOR_RELATIONSHIP.EfficiencyClass
                    if (size <= 0) break;
                    offset += size;
                }
                bool hybrid = classes.Distinct().Count() > 1;
                byte top = classes.Count > 0 ? classes.Max() : (byte)0;
                return (classes.Count, hybrid, hybrid ? classes.Count(c => c == top) : classes.Count);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { return (0, false, 0); }
    }

    /// <summary>Taban frekans ve Windows'un uyguladığı anlık frekans sınırı (CallNtPowerInformation).</summary>
    private static (int BaseMhz, int LimitMhz) CpuClocks()
    {
        try
        {
            int count = Environment.ProcessorCount;
            int size = count * 24;   // PROCESSOR_POWER_INFORMATION: 6 × ULONG
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (NativeMethods.CallNtPowerInformation(11 /* ProcessorInformation */, IntPtr.Zero, 0, buffer, (uint)size) != 0) return (0, 0);
                int max = 0, limit = int.MaxValue;
                for (int i = 0; i < count; i++)
                {
                    max = Math.Max(max, Marshal.ReadInt32(buffer, i * 24 + 4));
                    limit = Math.Min(limit, Marshal.ReadInt32(buffer, i * 24 + 12));
                }
                return (max, limit == int.MaxValue ? 0 : limit);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { return (0, 0); }
    }

    /// <summary>Oyunların kurulu olduğu sürücüler: SSD mi (arama gecikmesi yok), boş alan.</summary>
    private static IReadOnlyList<GameDrive> ReadGameDrives(IEnumerable<string>? gamePaths)
    {
        var drives = new List<GameDrive>();
        var roots = (gamePaths ?? Array.Empty<string>())
            .Select(p => { try { return System.IO.Path.GetPathRoot(p); } catch { return null; } })
            .Where(r => !string.IsNullOrEmpty(r)).Select(r => r!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            try
            {
                var info = new System.IO.DriveInfo(root);
                if (!info.IsReady) continue;
                drives.Add(new GameDrive(root.TrimEnd('\\'), IsSsd(root), info.AvailableFreeSpace / 1e9, info.TotalSize / 1e9));
            }
            catch { }
        }
        return drives;
    }

    private static bool? IsSsd(string root)
    {
        // Birim, erişim hakkı istenmeden açılır (yönetici gerekmez); yalnız "arama gecikmesi var mı" sorulur.
        string volume = @"\\.\" + root.TrimEnd('\\');
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = NativeMethods.CreateFile(volume, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var query = new int[3] { 7 /* StorageDeviceSeekPenaltyProperty */, 0 /* PropertyStandardQuery */, 0 };
        var result = new byte[12];   // DEVICE_SEEK_PENALTY_DESCRIPTOR: Version, Size, IncursSeekPenalty
        bool ok = NativeMethods.DeviceIoControl(handle, 0x002D1400 /* IOCTL_STORAGE_QUERY_PROPERTY */, query, 12, result, result.Length, out _, IntPtr.Zero);
        return ok ? result[8] == 0 : null;
    }

    /// <summary>Ekran kartı sürücüsünün tarihi ve sürümü (kayıt defteri, okuma).</summary>
    private static (DateTime? Date, string? Version) DriverInfo(string? gpu)
    {
        if (gpu is null) return (null, null);
        try
        {
            using RegistryKey? cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return (null, null);
            foreach (string sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using RegistryKey? key = cls.OpenSubKey(sub);
                if (!string.Equals(key?.GetValue("DriverDesc") as string, gpu, StringComparison.OrdinalIgnoreCase)) continue;
                DateTime? date = DateTime.TryParseExact(key!.GetValue("DriverDate") as string, new[] { "M-d-yyyy", "MM-dd-yyyy" },
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime d) ? d : null;
                return (date, key.GetValue("DriverVersion") as string);
            }
        }
        catch { }
        return (null, null);
    }

    private static IReadOnlyList<string> GpuNames()
    {
        var names = new List<string>();
        var device = DisplayDevice.Create();
        for (uint i = 0; NativeMethods.EnumDisplayDevices(null, i, ref device, 0); i++)
        {
            string name = device.DeviceString?.Trim() ?? "";
            if (name.Length > 0 && !name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
            device = DisplayDevice.Create();
        }
        return names;
    }

    private static IReadOnlyList<DisplayInfo> ReadDisplays()
    {
        var list = new List<DisplayInfo>();
        var device = DisplayDevice.Create();
        for (uint i = 0; NativeMethods.EnumDisplayDevices(null, i, ref device, 0); i++)
        {
            if ((device.StateFlags & 0x1) != 0)   // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP
            {
                var current = DevMode.Create();
                if (NativeMethods.EnumDisplaySettings(device.DeviceName, -1, ref current))
                {
                    int max = current.dmDisplayFrequency;
                    var mode = DevMode.Create();
                    for (int m = 0; NativeMethods.EnumDisplaySettings(device.DeviceName, m, ref mode); m++)
                    {
                        if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight)
                            max = Math.Max(max, mode.dmDisplayFrequency);
                        mode = DevMode.Create();
                    }
                    list.Add(new DisplayInfo(device.DeviceName, device.DeviceString ?? "", current.dmPelsWidth, current.dmPelsHeight,
                        current.dmDisplayFrequency, max, (device.StateFlags & 0x4) != 0));
                }
            }
            device = DisplayDevice.Create();
        }
        return list;
    }

    private static string WindowsVersion()
    {
        const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        string build = ReadString(Registry.LocalMachine, key, "CurrentBuild") ?? "";
        string display = ReadString(Registry.LocalMachine, key, "DisplayVersion") ?? "";
        string name = int.TryParse(build, out int b) && b >= 22000 ? "Windows 11" : "Windows 10";
        return $"{name} {display} (derleme {build})".Trim();
    }

    private static string? ReadString(RegistryKey root, string path, string name)
    {
        try { using RegistryKey? key = root.OpenSubKey(path); return key?.GetValue(name) as string; }
        catch { return null; }
    }

    private static int? ReadInt(RegistryKey root, string path, string name)
    {
        try { using RegistryKey? key = root.OpenSubKey(path); return key?.GetValue(name) is int value ? value : null; }
        catch { return null; }
    }

    /// <summary>SMBIOS (DMI) tablosundan üretici/model (tip 1), kasa tipi (tip 3) ve bellek hızları (tip 17).</summary>
    private sealed record Smbios(string Manufacturer, string Product, int Chassis, int? MemoryConfiguredSpeed, int? MemoryRatedSpeed, int MemoryModules)
    {
        public static Smbios Read()
        {
            try
            {
                const uint Rsmb = 0x52534D42;   // 'RSMB'
                uint size = NativeMethods.GetSystemFirmwareTable(Rsmb, 0, IntPtr.Zero, 0);
                if (size == 0) return Empty;
                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (NativeMethods.GetSystemFirmwareTable(Rsmb, 0, buffer, size) != size) return Empty;
                    var data = new byte[size];
                    Marshal.Copy(buffer, data, 0, (int)size);
                    return Parse(data);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            catch { return Empty; }
        }

        private static readonly Smbios Empty = new("", "", 0, null, null, 0);

        private static Smbios Parse(byte[] raw)
        {
            int length = BitConverter.ToInt32(raw, 4);
            int offset = 8, end = Math.Min(raw.Length, 8 + length);
            string manufacturer = "", product = "";
            int chassis = 0;
            var configured = new List<int>();
            var rated = new List<int>();
            int modules = 0;

            while (offset + 4 <= end)
            {
                byte type = raw[offset];
                byte formatted = raw[offset + 1];
                if (formatted < 4 || offset + formatted > end) break;
                List<string> strings = ReadStrings(raw, offset + formatted, end, out int next);

                string Str(int index) => index >= offset + formatted ? "" :
                    raw[index] is byte s && s >= 1 && s <= strings.Count ? strings[s - 1].Trim() : "";
                int Word(int at) => at + 1 < offset + formatted ? BitConverter.ToUInt16(raw, at) : 0;

                if (type == 1 && manufacturer.Length == 0)
                {
                    manufacturer = Str(offset + 4);
                    product = Str(offset + 5);
                }
                else if (type == 3 && chassis == 0 && formatted > 5)
                {
                    chassis = raw[offset + 5] & 0x7F;
                }
                else if (type == 17)
                {
                    int sizeField = Word(offset + 0x0C);
                    if (sizeField != 0 && sizeField != 0xFFFF)   // boş yuva değil
                    {
                        modules++;
                        int speed = Word(offset + 0x15);
                        int configuredSpeed = Word(offset + 0x20);
                        if (speed is > 0 and < 0xFFFF) rated.Add(speed);
                        if (configuredSpeed is > 0 and < 0xFFFF) configured.Add(configuredSpeed);
                    }
                }
                else if (type == 127) break;   // tablo sonu
                offset = next;
            }
            return new Smbios(manufacturer, product, chassis,
                configured.Count > 0 ? configured.Min() : null, rated.Count > 0 ? rated.Min() : null, modules);
        }

        private static List<string> ReadStrings(byte[] raw, int start, int end, out int next)
        {
            var strings = new List<string>();
            int i = start;
            if (i + 1 < end && raw[i] == 0 && raw[i + 1] == 0) { next = i + 2; return strings; }
            while (i < end)
            {
                int s = i;
                while (i < end && raw[i] != 0) i++;
                strings.Add(Encoding.ASCII.GetString(raw, s, i - s));
                i++;
                if (i < end && raw[i] == 0) { i++; break; }
            }
            next = i;
            return strings;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
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
        public static DisplayDevice Create() => new() { cb = Marshal.SizeOf<DisplayDevice>() };
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

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")] internal static extern bool GetSystemPowerStatus(out PowerStatus status);
        [DllImport("kernel32.dll")] internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
        [DllImport("kernel32.dll")] internal static extern uint GetSystemFirmwareTable(uint provider, uint id, IntPtr buffer, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice displayDevice, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool EnumDisplaySettings(string device, int modeNum, ref DevMode mode);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
        [DllImport("powrprof.dll")] internal static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputLength, IntPtr output, uint outputLength);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, int[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
    }
}
