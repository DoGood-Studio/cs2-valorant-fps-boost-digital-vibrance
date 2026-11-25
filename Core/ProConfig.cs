using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameColorGuard.Core;

internal sealed class ProDataset
{
    public string Source { get; set; } = "";
    public string FetchedAt { get; set; } = "";
    public List<ProTeam> Teams { get; set; } = [];
    public List<ProPlayer> Players { get; set; } = [];
}

internal sealed class ProTeam
{
    public string Game { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string? LogoUrl { get; set; }
}

internal sealed class ProPlayer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? RealName { get; set; }
    public string? Team { get; set; }
    public string? TeamSlug { get; set; }
    public string? TeamLogoUrl { get; set; }
    public string? Country { get; set; }
    public string? CountryName { get; set; }
    public string? Role { get; set; }
    public string? PhotoUrl { get; set; }
    public string Game { get; set; } = "";
    public string Url { get; set; } = "";
    public string? LastUpdated { get; set; }
    public ProMouse Mouse { get; set; } = new();
    public ProCrosshair? Crosshair { get; set; }
    public ProVideo? Video { get; set; }
    public ProViewmodel? Viewmodel { get; set; }
    public string? LaunchOptions { get; set; }

    public bool IsCs2 => Game.Equals("cs2", StringComparison.OrdinalIgnoreCase);
    public string GameLabel => IsCs2 ? "CS2" : "VALORANT";
}

internal sealed class ProMouse
{
    public string? Model { get; set; }
    public double? Dpi { get; set; }
    public double? Sensitivity { get; set; }
    public double? Edpi { get; set; }
    public double? ZoomSensitivity { get; set; }
    public double? ScopedSensitivity { get; set; }
    public double? Hz { get; set; }
    public double? WindowsSensitivity { get; set; }
}

internal sealed class ProCrosshair
{
    /// <summary>VALORANT crosshair kodu.</summary>
    public string? Code { get; set; }
    public string? Style { get; set; }
    /// <summary>CS2: Eylül 2026 crosshair sisteminin konsol komutları (ad → değer), prosettings'in maç verisinden.</summary>
    public Dictionary<string, string>? Cvars { get; set; }
    /// <summary>CS2: veri güncellemeden önceki sisteme ait; CS2 bu komutları artık tanımıyor, crosshair yüklenmez.</summary>
    public bool Legacy { get; set; }
}

internal sealed class ProVideo
{
    public string? Resolution { get; set; }
    public string? AspectRatio { get; set; }
    public string? ScalingMode { get; set; }
    public string? DisplayMode { get; set; }
    public string? Brightness { get; set; }
}

internal sealed class ProViewmodel
{
    public double? Fov { get; set; }
    public double? OffsetX { get; set; }
    public double? OffsetY { get; set; }
    public double? OffsetZ { get; set; }
}

/// <summary>
/// Pro ayarları: gömülü veri (kaynak prosettings.net, tarihli), hassasiyet dönüşümü ve CS2 config dosyası üretimi.
/// Yalnızca yeni bir "gcg_&lt;oyuncu&gt;.cfg" yazılır; kullanıcının mevcut config dosyalarına dokunulmaz.
/// </summary>
internal static class ProConfig
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static ProDataset Load()
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProSettings.json");
            if (stream is null) return new ProDataset();
            return JsonSerializer.Deserialize<ProDataset>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new ProDataset();
        }
        catch (Exception ex)
        {
            AppLogger.Write("Pro ayar verisi okunamadı", ex);
            return new ProDataset();
        }
    }

    /// <summary>Profesyonelin eDPI'sını kullanıcının DPI'ına göre aynı el hareketine denk gelen hassasiyete çevirir.</summary>
    public static double? ConvertedSensitivity(ProPlayer player, int userDpi)
    {
        double? edpi = player.Mouse.Edpi ?? (player.Mouse.Dpi * player.Mouse.Sensitivity);
        if (edpi is not double value || userDpi <= 0) return null;
        return Math.Round(value / userDpi, 3);
    }

    public static string Number(double? value, string format = "0.###") => value is double v ? v.ToString(format, Inv) : "—";

    public static string ConfigFileName(ProPlayer player) => "gcg_" + Regex.Replace(player.Id.ToLowerInvariant(), "[^a-z0-9_]", "_") + ".cfg";

    /// <summary>CS2 konsol komutları: hassasiyet (kullanıcının DPI'ına göre), crosshair ve viewmodel.</summary>
    public static string BuildCs2Config(ProPlayer player, int userDpi)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// Game Color Guard — {player.Name}{(player.Team is null ? "" : " (" + player.Team + ")")} CS2 ayarları");
        sb.AppendLine($"// Kaynak: {player.Url}  ·  son güncelleme {player.LastUpdated ?? "?"}");
        sb.AppendLine($"// Yüklemek için CS2 konsoluna:  exec {Path.GetFileNameWithoutExtension(ConfigFileName(player))}");
        sb.AppendLine();

        if (ConvertedSensitivity(player, userDpi) is double sens)
        {
            sb.AppendLine($"// {player.Name}: {Number(player.Mouse.Dpi)} DPI × {Number(player.Mouse.Sensitivity)} = eDPI {Number(player.Mouse.Edpi)}; " +
                          $"sizin {userDpi} DPI farenizde aynı hız:");
            Cvar(sb, "sensitivity", sens);
        }
        if (player.Mouse.ZoomSensitivity is double zoom) Cvar(sb, "zoom_sensitivity_ratio", zoom);

        List<string> crosshair = Cs2CrosshairLines(player);
        if (crosshair.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"// Crosshair ({player.Crosshair?.Style ?? "?"})");
            foreach (string line in crosshair) sb.AppendLine(line);
        }
        else if (player.Crosshair?.Legacy == true)
        {
            sb.AppendLine();
            sb.AppendLine("// Crosshair yüklenmedi: bu oyuncunun verisi CS2'nin Eylül 2026 crosshair güncellemesinden önceki sisteme ait.");
        }

        if (player.Viewmodel is { } v && v.Fov is not null)
        {
            sb.AppendLine();
            sb.AppendLine("// Viewmodel (silah görünümü)");
            Cvar(sb, "viewmodel_fov", v.Fov.Value);
            if (v.OffsetX is double x) Cvar(sb, "viewmodel_offset_x", x);
            if (v.OffsetY is double y) Cvar(sb, "viewmodel_offset_y", y);
            if (v.OffsetZ is double z) Cvar(sb, "viewmodel_offset_z", z);
        }

        sb.AppendLine();
        sb.AppendLine($"echo \"Game Color Guard: {player.Name} ayarlari yuklendi\"");
        return sb.ToString();
    }

    /// <summary>
    /// CS2'nin Eylül 2026 crosshair sistemindeki komutlar (client.dll'deki adlarla doğrulandı); yazılma sırası da bu.
    /// Eski adlar (cl_crosshairsize, cl_crosshairgap, cl_crosshaircolor, cl_crosshairusealpha …) artık "Unknown command".
    /// </summary>
    internal static readonly string[] Cs2CrosshairCvars =
    {
        "cl_crosshairstyle", "cl_crosshair_length", "cl_crosshair_thickness", "cl_crosshair_gap",
        "cl_crosshair_drawoutline", "cl_crosshairoutline_r", "cl_crosshairoutline_g", "cl_crosshairoutline_b", "cl_crosshairoutline_a",
        "cl_crosshaircolor_r", "cl_crosshaircolor_g", "cl_crosshaircolor_b", "cl_crosshaircolor_a",
        "cl_crosshairdot", "cl_crosshair_t", "cl_crosshair_recoil",
        "cl_crosshair_dynamic_splitdist", "cl_crosshair_dynamic_splitalpha_innermod", "cl_crosshair_dynamic_splitalpha_outermod",
        "cl_crosshair_dynamic_maxdist_splitratio", "cl_crosshair_dynamic_spread_limit", "cl_crosshair_sniper_width",
        "cl_crosshair_screen_height", "cl_ironsight_usecrosshaircolor", "cl_ironsight_dot_scale"
    };

    /// <summary>Oyuncunun yeni sistem crosshair komutları; bilinmeyen ad ya da sayı olmayan değer yazılmaz.</summary>
    internal static List<string> Cs2CrosshairLines(ProPlayer player)
    {
        var lines = new List<string>();
        if (player.Crosshair?.Cvars is not { Count: > 0 } cvars || player.Crosshair.Legacy) return lines;
        var byName = new Dictionary<string, string>(cvars, StringComparer.OrdinalIgnoreCase);
        foreach (string name in Cs2CrosshairCvars)
            if (byName.TryGetValue(name, out string? raw) && SafeCvarValue(raw) is string value)
                lines.Add($"{name} \"{value}\"");
        return lines;
    }

    public static bool HasCs2Crosshair(ProPlayer player) => Cs2CrosshairLines(player).Count > 0;

    /// <summary>Yalnız crosshair komutları (panoya kopyalamak için, tek satır).</summary>
    public static string Cs2CrosshairCommands(ProPlayer player) => string.Join("; ", Cs2CrosshairLines(player));

    /// <summary>CS2 config dosyasını yazar; yolunu döndürür. CS2 bulunamazsa null.</summary>
    public static string? WriteCs2Config(ProPlayer player, int userDpi, string? knownCs2Path, out string? error)
    {
        error = null;
        string? cfgDir = FindCs2CfgFolder(knownCs2Path);
        if (cfgDir is null)
        {
            error = "CS2 kurulumu bulunamadı. Oyunu bir kez açın (yol otomatik öğrenilir) ve tekrar deneyin.";
            return null;
        }
        string path = Path.Combine(cfgDir, ConfigFileName(player));
        File.WriteAllText(path, BuildCs2Config(player, userDpi), new UTF8Encoding(false));
        AppLogger.Info($"Pro config yazıldı: {path}");
        return path;
    }

    // ------------------------------------------------------------------ Kullanıcının kendi ayar yedeği

    public const string BackupConfigName = "gcg_yedek.cfg";
    public static string BackupExecCommand => "exec " + Path.GetFileNameWithoutExtension(BackupConfigName);

    /// <summary>Pro config'lerin değiştirebildiği CS2 ayarları; yedek bunların kullanıcıdaki değerini saklar.</summary>
    internal static readonly string[] Cs2ConfigCvars =
        new[] { "sensitivity", "zoom_sensitivity_ratio" }
            .Concat(Cs2CrosshairCvars)
            .Concat(new[] { "viewmodel_fov", "viewmodel_offset_x", "viewmodel_offset_y", "viewmodel_offset_z" })
            .ToArray();

    /// <summary>CS2'nin "cs2_user_convars_*.vcfg" dosyasındaki "ad" "değer" satırları.</summary>
    internal static Dictionary<string, string> ParseVcfgConvars(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text, "^[ \\t]*\"([^\"\\r\\n]+)\"[ \\t]+\"([^\"\\r\\n]*)\"[ \\t]*\\r?$", RegexOptions.Multiline))
            result[match.Groups[1].Value] = match.Groups[2].Value;
        return result;
    }

    /// <summary>
    /// Yedek config metni: pro config'lerin dokunduğu ayarların kullanıcıdaki değerleri. Yalnız sayı ve true/false
    /// kabul edilir (exec edilen dosyaya komut sızmasın). CS2'nin dosyasında olmayan ayarlar (hiç değiştirilmemiş,
    /// oyunun varsayılanında) <paramref name="missing"/> listesine girer; varsayılanları tahmin edilmez.
    /// </summary>
    internal static string BuildBackupConfig(IReadOnlyDictionary<string, string> userValues, string source, DateTime createdAt,
        out List<string> missing)
    {
        missing = new List<string>();
        var sb = new StringBuilder();
        sb.AppendLine($"// Game Color Guard — kendi CS2 ayarlarınızın yedeği ({createdAt.ToString("yyyy-MM-dd HH:mm", Inv)})");
        sb.AppendLine($"// Kaynak: {source}");
        sb.AppendLine($"// Pro config yüklemeden önceki değerleriniz. Geri dönmek için CS2 konsoluna:  {BackupExecCommand}");
        sb.AppendLine("// Bu dosyayı silerseniz bir sonraki pro yüklemede o anki ayarlarınızla yeniden oluşturulur.");
        sb.AppendLine();
        foreach (string name in Cs2ConfigCvars)
        {
            if (userValues.TryGetValue(name, out string? raw) && SafeCvarValue(raw) is string value)
                sb.AppendLine($"{name} \"{value}\"");
            else
                missing.Add(name);
        }
        if (missing.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("// CS2 bu ayarları kendi dosyasına yazmamış (hiç değiştirilmemiş, oyunun varsayılanında):");
            sb.AppendLine("//   " + string.Join(", ", missing));
            sb.AppendLine("// Bir pro config bunları değiştirdiyse bu yedek eski değerlerine döndüremez; CS2 ayarlarından düzeltin.");
        }
        sb.AppendLine();
        sb.AppendLine("echo \"Game Color Guard: kendi ayarlariniz geri yuklendi\"");
        return sb.ToString();
    }

    private static string? SafeCvarValue(string raw)
    {
        string value = raw.Trim();
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) return "1";
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) return "0";
        return Regex.IsMatch(value, "^-?[0-9]+(\\.[0-9]+)?$") ? value : null;
    }

    /// <summary>
    /// Kullanıcının kendi ayar yedeği yoksa CS2'nin kayıtlı ayarlarından oluşturur; varsa dokunmaz (ilk pro yüklemeden
    /// önceki hâl korunur). CS2'nin dosyası yalnız okunur. Yedeğin yolunu, CS2'nin kayıtlı ayarları bulunamazsa null döndürür.
    /// </summary>
    public static string? EnsureCs2Backup(string cfgDir, out bool created, out int missingCount)
    {
        created = false;
        missingCount = 0;
        string path = Path.Combine(cfgDir, BackupConfigName);
        if (File.Exists(path)) return path;

        string? convars = FindCs2UserConvars();
        if (convars is null) return null;
        string text;
        using (var stream = new FileStream(convars, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
            text = reader.ReadToEnd();

        string config = BuildBackupConfig(ParseVcfgConvars(text), convars, DateTime.Now, out List<string> missing);
        File.WriteAllText(path, config, new UTF8Encoding(false));
        created = true;
        missingCount = missing.Count;
        AppLogger.Info($"Kendi CS2 ayarlarının yedeği yazıldı: {path} (kaynak {convars}; dosyada olmayan: {string.Join(", ", missing)})");
        return path;
    }

    /// <summary>Etkin Steam hesabının CS2 kullanıcı ayarları; Steam kapalıysa en son değişen hesabınki.</summary>
    public static string? FindCs2UserConvars()
    {
        string? steam = SteamPath();
        if (steam is null) return null;
        string userdata = Path.Combine(steam, "userdata");
        if (!Directory.Exists(userdata)) return null;
        string relative = Path.Combine("730", "local", "cfg", "cs2_user_convars_0_slot0.vcfg");

        uint active = 0;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            if (key?.GetValue("ActiveUser") is int id) active = unchecked((uint)id);
        }
        catch { }
        if (active != 0)
        {
            string own = Path.Combine(userdata, active.ToString(Inv), relative);
            if (File.Exists(own)) return own;
        }
        try
        {
            return Directory.EnumerateDirectories(userdata)
                .Select(dir => Path.Combine(dir, relative))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>...\game\csgo\cfg klasörü: önce öğrenilmiş cs2.exe yolundan, yoksa Steam kütüphanelerinden.</summary>
    public static string? FindCs2CfgFolder(string? knownCs2Path)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(knownCs2Path))
        {
            // ...\Counter-Strike Global Offensive\game\bin\win64\cs2.exe → ...\game\csgo\cfg
            string? game = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(knownCs2Path)));
            if (game is not null) candidates.Add(Path.Combine(game, "csgo", "cfg"));
        }
        foreach (string library in SteamLibraries())
            candidates.Add(Path.Combine(library, "steamapps", "common", "Counter-Strike Global Offensive", "game", "csgo", "cfg"));
        return candidates.FirstOrDefault(Directory.Exists);
    }

    /// <summary>Kurulu CS2'nin exe yolu (oyun simgesi için).</summary>
    public static string? FindCs2Executable(string? knownCs2Path)
    {
        if (!string.IsNullOrEmpty(knownCs2Path) && File.Exists(knownCs2Path)) return knownCs2Path;
        return SteamLibraries()
            .Select(l => Path.Combine(l, "steamapps", "common", "Counter-Strike Global Offensive", "game", "bin", "win64", "cs2.exe"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Kurulu VALORANT'ın exe yolu (oyun simgesi için); Riot'un kurulum kaydından okunur.</summary>
    public static string? FindValorantExecutable(string? knownPath)
    {
        if (!string.IsNullOrEmpty(knownPath) && File.Exists(knownPath)) return knownPath;
        try
        {
            string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Riot Games", "Metadata", "valorant.live", "valorant.live.product_settings.yaml");
            if (File.Exists(settings))
            {
                Match match = Regex.Match(File.ReadAllText(settings), "product_install_full_path:\\s*\"?([^\"\\r\\n]+)");
                if (match.Success)
                {
                    string exe = Path.Combine(match.Groups[1].Value.Trim(), "VALORANT.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
        }
        catch { }
        const string fallback = @"C:\Riot Games\VALORANT\live\VALORANT.exe";
        return File.Exists(fallback) ? fallback : null;
    }

    private static string? SteamPath()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            string? steam = (key?.GetValue("SteamPath") as string)?.Replace('/', '\\');
            return string.IsNullOrEmpty(steam) ? null : steam;
        }
        catch { return null; }
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var result = new List<string>();
        try
        {
            string? steam = SteamPath();
            if (steam is null) return result;
            result.Add(steam);
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    result.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
            }
        }
        catch { }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Cvar(StringBuilder sb, string name, double value) =>
        sb.AppendLine($"{name} \"{value.ToString("0.###", Inv)}\"");
}
