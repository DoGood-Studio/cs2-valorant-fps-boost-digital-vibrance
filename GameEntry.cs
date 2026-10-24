using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using GameColorGuard.Core;

namespace GameColorGuard;

internal enum GameRunState { Closed, Running, Focused }

/// <summary>İzlenen oyun listesindeki bir satır.</summary>
public sealed class GameEntry : INotifyPropertyChanged
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x4C, 0xC3, 0xFF),
        Color.FromRgb(0x3D, 0xD6, 0x8C),
        Color.FromRgb(0xC0, 0x84, 0xFC),
        Color.FromRgb(0xFF, 0xB5, 0x47),
        Color.FromRgb(0xF4, 0x72, 0xB6)
    };

    private static readonly Brush ClosedText = Frozen(Color.FromRgb(0x8A, 0x93, 0xA3));
    private static readonly Brush ClosedBack = Frozen(Color.FromRgb(0x1A, 0x1E, 0x25));
    private static readonly Brush RunningText = Frozen(Color.FromRgb(0x3D, 0xD6, 0x8C));
    private static readonly Brush RunningBack = Frozen(Color.FromArgb(0x1C, 0x3D, 0xD6, 0x8C));
    private static readonly Brush FocusText = Frozen(Color.FromRgb(0xFF, 0x9A, 0x3D));
    private static readonly Brush FocusBack = Frozen(Color.FromArgb(0x24, 0xFF, 0x7A, 0x1A));

    private GameRunState _state = GameRunState.Closed;
    private ImageSource? _icon;

    internal GameEntry(string executable)
    {
        Executable = executable;
        DisplayName = GameProcessMonitor.FriendlyName(executable);
        Monogram = BuildMonogram(DisplayName);

        Color accent = executable.ToLowerInvariant() switch
        {
            "cs2.exe" => Color.FromRgb(0xFF, 0x9A, 0x3D),
            "valorant-win64-shipping.exe" => Color.FromRgb(0xFF, 0x5A, 0x5F),
            _ => Palette[Math.Abs(StableHash(executable)) % Palette.Length]
        };
        AccentBrush = Frozen(accent);
        TileBrush = Frozen(Color.FromArgb(0x24, accent.R, accent.G, accent.B));
        TileBorderBrush = Frozen(Color.FromArgb(0x40, accent.R, accent.G, accent.B));
    }

    public string Executable { get; }
    public string DisplayName { get; }
    public string Monogram { get; }
    public Brush AccentBrush { get; }
    public Brush TileBrush { get; }
    public Brush TileBorderBrush { get; }

    /// <summary>Resmi oyun ikonu (kurulu exe'den ya da oyunun sitesinden); yoksa baş harf rozeti görünür.</summary>
    public ImageSource? Icon => _icon;
    public Visibility IconVisibility => _icon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility MonogramVisibility => _icon is null ? Visibility.Visible : Visibility.Collapsed;

    internal void SetIcon(ImageSource? icon)
    {
        _icon = icon;
        OnChanged(nameof(Icon));
        OnChanged(nameof(IconVisibility));
        OnChanged(nameof(MonogramVisibility));
    }

    public string StatusText => _state switch
    {
        GameRunState.Focused => "Odakta",
        GameRunState.Running => "Çalışıyor",
        _ => "Kapalı"
    };

    public Brush StatusBrush => _state switch
    {
        GameRunState.Focused => FocusText,
        GameRunState.Running => RunningText,
        _ => ClosedText
    };

    public Brush StatusBackground => _state switch
    {
        GameRunState.Focused => FocusBack,
        GameRunState.Running => RunningBack,
        _ => ClosedBack
    };

    internal void SetState(GameRunState state)
    {
        if (state == _state) return;
        _state = state;
        OnChanged(nameof(StatusText));
        OnChanged(nameof(StatusBrush));
        OnChanged(nameof(StatusBackground));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string BuildMonogram(string name)
    {
        string[] words = name.Split(new[] { ' ', '-', '_', ':', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetterOrDigit(w[0])).ToArray();
        if (words.Length >= 2 && char.IsLetter(words[1][0]))
            return (char.ToUpperInvariant(words[0][0]).ToString() + char.ToUpperInvariant(words[1][0])).Trim();
        string letters = new string(name.Where(char.IsLetterOrDigit).Take(2).ToArray());
        return letters.Length == 0 ? "?" : letters.ToUpperInvariant();
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            int hash = 23;
            foreach (char ch in value.ToLowerInvariant()) hash = hash * 31 + ch;
            return hash == int.MinValue ? 0 : hash;
        }
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
