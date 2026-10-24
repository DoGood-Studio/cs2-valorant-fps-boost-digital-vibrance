using System;
using System.Collections.Generic;
using GameColorGuard.Core;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GameColorGuard.Tweaks;

/// <summary>
/// Oyun sırasında Windows animasyonları ve görsel efektler kapalı: pencere küçültme/büyütme animasyonu,
/// menü/ipucu/seçim solmaları, gölgeler, pencere içi animasyonlar, görev çubuğu animasyonları.
/// SystemParametersInfo ile anında etkilidir; oyun kapanınca önceki değerler geri gelir.
/// </summary>
internal sealed class VisualEffectsTweak : TweakBase
{
    private const uint SpiGetAnimation = 0x0048, SpiSetAnimation = 0x0049;
    private const uint SpiGetUiEffects = 0x103E, SpiSetUiEffects = 0x103F;
    private const uint SpiGetClientAreaAnimation = 0x1042, SpiSetClientAreaAnimation = 0x1043;
    private const uint SpiGetDropShadow = 0x1024, SpiSetDropShadow = 0x1025;
    private const uint SendChange = 0x0002;
    private const string TaskbarKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string TaskbarValue = "TaskbarAnimations";

    private (int MinAnimate, bool UiEffects, bool ClientAnimation, bool DropShadow, object? Taskbar)? _previous;

    private sealed record Snapshot(int MinAnimate, bool UiEffects, bool ClientAnimation, bool DropShadow, int? Taskbar);

    public override string? ExportPrevious() => _previous is { } p
        ? ToJson(new Snapshot(p.MinAnimate, p.UiEffects, p.ClientAnimation, p.DropShadow, p.Taskbar is int t ? t : null))
        : null;

    protected override void ImportPreviousCore(string data)
    {
        Snapshot s = FromJson<Snapshot>(data) ?? throw new FormatException("görsel efektler");
        _previous = (s.MinAnimate, s.UiEffects, s.ClientAnimation, s.DropShadow, s.Taskbar is int t ? t : null);
    }

    public override string Id => "visual-effects-off";
    public override string Title => "Windows animasyonlarını ve görsel efektleri kapat";
    public override string Description => "Oyun çalışırken pencere aç/kapa animasyonu, menü ve ipucu solmaları, gölgeler, pencere içi animasyonlar ve görev çubuğu animasyonları kapatılır; DWM ve CPU'ya küçük ama gereksiz yükü kaldırır. Oyun kapanınca eski ayarlar geri gelir.";
    public override TweakScope Scope => TweakScope.GameRunning;

    private static (int MinAnimate, bool UiEffects, bool ClientAnimation, bool DropShadow, object? Taskbar) Current()
    {
        var animation = new AnimationInfo { Size = (uint)Marshal.SizeOf<AnimationInfo>() };
        Native.SystemParametersInfo(SpiGetAnimation, animation.Size, ref animation, 0);
        bool ui = true, client = true, shadow = true;
        Native.SystemParametersInfo(SpiGetUiEffects, 0, ref ui, 0);
        Native.SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref client, 0);
        Native.SystemParametersInfo(SpiGetDropShadow, 0, ref shadow, 0);
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(TaskbarKey);
        return (animation.MinAnimate, ui, client, shadow, key?.GetValue(TaskbarValue));
    }

    private static void Write(int minAnimate, bool uiEffects, bool clientAnimation, bool dropShadow, object? taskbar)
    {
        var animation = new AnimationInfo { Size = (uint)Marshal.SizeOf<AnimationInfo>(), MinAnimate = minAnimate };
        Native.SystemParametersInfo(SpiSetAnimation, animation.Size, ref animation, SendChange);
        Native.SystemParametersInfo(SpiSetUiEffects, 0, uiEffects ? new IntPtr(1) : IntPtr.Zero, SendChange);
        Native.SystemParametersInfo(SpiSetClientAreaAnimation, 0, clientAnimation ? new IntPtr(1) : IntPtr.Zero, SendChange);
        Native.SystemParametersInfo(SpiSetDropShadow, 0, dropShadow ? new IntPtr(1) : IntPtr.Zero, SendChange);
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(TaskbarKey, writable: true)!;
        if (taskbar is null) key.DeleteValue(TaskbarValue, throwOnMissingValue: false);
        else key.SetValue(TaskbarValue, taskbar, RegistryValueKind.DWord);
        Native.SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "TraySettings", 0x0002, 500, out _);
    }

    public override TweakState Read()
    {
        var c = Current();
        bool off = c.MinAnimate == 0 && !c.UiEffects && !c.ClientAnimation && !c.DropShadow;
        var on = new List<string>();
        if (c.MinAnimate != 0) on.Add("pencere animasyonu");
        if (c.UiEffects) on.Add("arayüz efektleri");
        if (c.ClientAnimation) on.Add("pencere içi animasyon");
        if (c.DropShadow) on.Add("gölgeler");
        if (c.Taskbar is not int t || t != 0) on.Add("görev çubuğu animasyonu");
        return new TweakState(off, off ? "animasyonlar ve efektler kapalı" : "açık: " + string.Join(", ", on));
    }

    protected override bool ApplyCore(out string? error)
    {
        error = null;
        _previous ??= Current();
        Write(0, false, false, false, 0);
        return true;
    }

    protected override bool RevertCore(out string? error)
    {
        error = null;
        // Önceki değer bilinmiyorsa Windows varsayılanı: hepsi açık.
        var p = _previous ?? (1, true, true, true, (object?)1);
        Write(p.MinAnimate, p.UiEffects, p.ClientAnimation, p.DropShadow, p.Taskbar);
        _previous = null;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AnimationInfo
    {
        public uint Size;
        public int MinAnimate;
    }

    private static class Native
    {
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SystemParametersInfo(uint action, uint param, ref AnimationInfo value, uint flags);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint flags);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
    }
}
