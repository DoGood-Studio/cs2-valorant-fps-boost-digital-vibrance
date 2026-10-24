using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GameColorGuard.Core;

internal sealed class ColorSettings
{
    public int SettingsVersion { get; set; } = 2;
    // Yeni kurulumlar önerilen profille başlar (aydınlık 55, kontrast 55, gama 1,10, canlılık 85); kayıtlı ayar dosyası varsa o kullanılır.
    public int Brightness { get; set; } = 55;
    public int Contrast { get; set; } = 55;
    public int Gamma { get; set; } = 110;
    public int DigitalVibrance { get; set; } = 85;
    public int Hue { get; set; } = 0;
    public int Red { get; set; } = 100;
    public int Green { get; set; } = 100;
    public int Blue { get; set; } = 100;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    /// <summary>
    /// true: oyun odaktan çıksa da ekranda görünür kaldıkça (küçültülmedi, başka pencere örtmedi) profil korunur.
    /// false: yalnızca oyun odaktayken uygulanır (Alt+Tab'da geri alınır).
    /// </summary>
    public bool KeepWhileVisible { get; set; } = true;
    public List<string> GameExecutables { get; set; } =
    [
        "cs2.exe",
        "VALORANT-Win64-Shipping.exe"
    ];
    /// <summary>Oyunların tam yolu (exe adı → yol); GPU tercihi ve oyun simgesi için, oyun ilk çalıştığında öğrenilir.</summary>
    public Dictionary<string, string> GamePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public PerformanceSettings Performance { get; set; } = new();
    /// <summary>Pro ayar sayfasında hassasiyet dönüşümü için kullanıcının fare DPI'ı.</summary>
    public int MouseDpi { get; set; } = 800;
    /// <summary>Arayüz dili (tr, en, ru, ar, de, es, pt); boşsa Windows dilinden seçilir.</summary>
    public string? Language { get; set; }

    // Önceki sürümle aynı klasör: kullanıcının kayıtlı profili aynen korunur.
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CS2ColorGuard");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    public static ColorSettings Load()
    {
        try
        {
            return LoadFrom(FilePath);
        }
        catch
        {
            return new ColorSettings();
        }
    }

    public void Save() => SaveTo(FilePath);

    internal static ColorSettings LoadFrom(string path)
    {
        if (!File.Exists(path)) return new ColorSettings();
        ColorSettings settings = JsonSerializer.Deserialize<ColorSettings>(File.ReadAllText(path)) ?? new ColorSettings();
        settings.Normalize();
        return settings;
    }

    internal void SaveTo(string path)
    {
        Normalize();
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal void Normalize()
    {
        SettingsVersion = 2;
        Brightness = Math.Clamp(Brightness, 0, 100);
        Contrast = Math.Clamp(Contrast, 0, 100);
        Gamma = Math.Clamp(Gamma, 50, 200);
        DigitalVibrance = Math.Clamp(DigitalVibrance, 0, 100);
        Hue = Math.Clamp(Hue, 0, 360);
        Red = Math.Clamp(Red, 50, 150);
        Green = Math.Clamp(Green, 50, 150);
        Blue = Math.Clamp(Blue, 50, 150);
        GameExecutables = (GameExecutables ?? [])
            .Select(NormalizeExecutableName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // JSON okuyucu sözlüğü büyük/küçük harfe duyarlı kurar; anahtarlar exe adı olduğu için duyarsız olmalı.
        GamePaths = new Dictionary<string, string>(GamePaths ?? new(), StringComparer.OrdinalIgnoreCase);
        Performance ??= new PerformanceSettings();
        Performance.Normalize();
        MouseDpi = Math.Clamp(MouseDpi, 100, 32000);
    }

    internal static string NormalizeExecutableName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string fileName = Path.GetFileName(value.Trim());
        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".exe";
    }
}
