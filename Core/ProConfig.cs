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
    public string? Code { get; set; }
    public string? Style { get; set; }
    public double? Length { get; set; }
    public double? Thickness { get; set; }
    public double? Gap { get; set; }
    public bool? Outline { get; set; }
    public double? OutlineThickness { get; set; }
    public int? ColorR { get; set; }
    public int? ColorG { get; set; }
    public int? ColorB { get; set; }
    public int? Alpha { get; set; }
    public bool? Dot { get; set; }
    public bool? TStyle { get; set; }
    public bool? FollowRecoil { get; set; }
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

        if (player.Crosshair is { } c)
        {
            sb.AppendLine();
            sb.AppendLine($"// Crosshair ({c.Style ?? "statik"})");
            Cvar(sb, "cl_crosshairstyle", 4);   // klasik statik: profesyonellerin neredeyse tamamı
            if (c.Length is double length) Cvar(sb, "cl_crosshairsize", length);
            if (c.Thickness is double thickness) Cvar(sb, "cl_crosshairthickness", thickness);
            if (c.Gap is double gap) Cvar(sb, "cl_crosshairgap", gap);
            if (c.Outline is bool outline)
            {
                Cvar(sb, "cl_crosshair_drawoutline", outline ? 1 : 0);
                if (outline && c.OutlineThickness is double ot) Cvar(sb, "cl_crosshair_outlinethickness", ot);
            }
            if (c.ColorR is int r && c.ColorG is int g && c.ColorB is int b)
            {
                Cvar(sb, "cl_crosshaircolor", 5);
                Cvar(sb, "cl_crosshaircolor_r", r);
                Cvar(sb, "cl_crosshaircolor_g", g);
                Cvar(sb, "cl_crosshaircolor_b", b);
            }
            if (c.Alpha is int alpha)
            {
                Cvar(sb, "cl_crosshairusealpha", 1);
                Cvar(sb, "cl_crosshairalpha", alpha);
            }
            if (c.Dot is bool dot) Cvar(sb, "cl_crosshairdot", dot ? 1 : 0);
            if (c.TStyle is bool t) Cvar(sb, "cl_crosshair_t", t ? 1 : 0);
            if (c.FollowRecoil is bool recoil) Cvar(sb, "cl_crosshair_recoil", recoil ? 1 : 0);
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

    /// <summary>Yalnız crosshair komutları (panoya kopyalamak için, tek satır).</summary>
    public static string Cs2CrosshairCommands(ProPlayer player)
    {
        string config = BuildCs2Config(player, 800);
        IEnumerable<string> lines = config.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("cl_crosshair", StringComparison.Ordinal));
        return string.Join("; ", lines);
    }

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

    private static IEnumerable<string> SteamLibraries()
    {
        var result = new List<string>();
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            string? steam = (key?.GetValue("SteamPath") as string)?.Replace('/', '\\');
            if (string.IsNullOrEmpty(steam)) return result;
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
