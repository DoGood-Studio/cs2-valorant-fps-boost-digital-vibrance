using System;
using Microsoft.Win32;

namespace GameColorGuard.Core;

internal static class StartupManager
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CS2ColorGuard";

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key is null) return;
        string? executable = Environment.ProcessPath;
        if (enabled && !string.IsNullOrWhiteSpace(executable))
            key.SetValue(ValueName, $"\"{executable}\" --minimized");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
