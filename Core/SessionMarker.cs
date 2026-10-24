using System;
using System.IO;

namespace GameColorGuard.Core;

/// <summary>
/// Profil ekrandayken bir işaret dosyası tutar. Program zorla kapatılırsa (görev yöneticisi, çökme,
/// güncelleme) bir sonraki açılış ekranda kalmış renkleri tanır ve nötre döndürür.
/// </summary>
internal static class SessionMarker
{
    private static readonly string MarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CS2ColorGuard", "profile-active.flag");

    internal static bool WasLeftActive { get; } = SafeExists();

    internal static void SetActive(bool active)
    {
        try
        {
            if (active)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
                File.WriteAllText(MarkerPath, DateTime.Now.ToString("O"));
            }
            else if (File.Exists(MarkerPath))
            {
                File.Delete(MarkerPath);
            }
        }
        catch
        {
            // İşaret dosyası yalnızca bir güvenlik ağıdır; yazılamaması renk akışını durdurmamalı.
        }
    }

    private static bool SafeExists()
    {
        try { return File.Exists(MarkerPath); }
        catch { return false; }
    }
}
