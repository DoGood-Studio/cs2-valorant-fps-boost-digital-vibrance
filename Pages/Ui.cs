using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using GameColorGuard.Core;
using GameColorGuard.Tweaks;

namespace GameColorGuard.Pages;

/// <summary>Sayfaların ortak bağlamı: ayarlar, FPS bekçisi, kayıt ve bildirim.</summary>
internal sealed class PageContext
{
    public PageContext(ColorSettings settings, PerformanceGuard performance, Action save, Action<string, bool> notify, bool testMode)
    {
        Settings = settings;
        Performance = performance;
        Save = save;
        Notify = notify;
        TestMode = testMode;
    }

    public ColorSettings Settings { get; }
    public PerformanceGuard Performance { get; }
    public Action Save { get; }
    /// <summary>Kısa bildirim (metin, hata mı).</summary>
    public Action<string, bool> Notify { get; }
    public bool TestMode { get; }

    private SystemProfile? _system;
    public SystemProfile System => _system ??= SystemProfile.Detect(Settings.GamePaths.Values);
    public void RefreshSystem() => _system = SystemProfile.Detect(Settings.GamePaths.Values);
}

/// <summary>Kodla kurulan sayfalar için tema uyumlu yapı taşları (Theme.xaml stillerini kullanır).</summary>
internal static class Ui
{
    public static readonly Color Ember = Color.FromRgb(0xFF, 0x7A, 0x1A);
    public static readonly Color Success = Color.FromRgb(0x3D, 0xD6, 0x8C);
    public static readonly Color Warning = Color.FromRgb(0xFF, 0xB5, 0x47);
    public static readonly Color Danger = Color.FromRgb(0xFF, 0x5A, 0x5F);
    public static readonly Color Info = Color.FromRgb(0x4C, 0xC3, 0xFF);
    public static readonly Color Cs2 = Color.FromRgb(0xFF, 0x9A, 0x3D);
    public static readonly Color Valorant = Color.FromRgb(0xFF, 0x46, 0x55);

    public static T Res<T>(string key) => (T)Application.Current.FindResource(key);
    public static Brush Brush(string key) => Res<Brush>(key);
    public static Style Style(string key) => Res<Style>(key);

    public static SolidColorBrush Solid(Color color, byte alpha = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    public static ScrollViewer PageScroll(UIElement content) => new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        CanContentScroll = false,
        Content = content
    };

    public static StackPanel PageStack() => new() { Margin = new Thickness(0, 0, 12, 12), MaxWidth = 1060 };

    public static Grid PageHeader(string title, string subtitle, UIElement? right = null)
    {
        var grid = new Grid { Margin = new Thickness(4, 4, 0, 18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel();
        texts.Children.Add(new TextBlock
        {
            Text = title, FontSize = 27, FontWeight = FontWeights.SemiBold,
            FontFamily = Res<FontFamily>("DisplayFont"), Foreground = Brush("TextBrush")
        });
        texts.Children.Add(new TextBlock
        {
            Text = subtitle, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("Text2Brush")
        });
        grid.Children.Add(texts);
        if (right is FrameworkElement element)
        {
            element.VerticalAlignment = VerticalAlignment.Top;
            element.Margin = new Thickness(16, 6, 0, 0);
            Grid.SetColumn(element, 1);
            grid.Children.Add(element);
        }
        return grid;
    }

    public static Border Card(UIElement child, double bottom = 14) => new()
    {
        Style = Style("Card"),
        Margin = new Thickness(0, 0, 0, bottom),
        Child = child
    };

    public static StackPanel CardHeader(string title, string? subtitle, UIElement? right = null, string? glyph = null, Color? glyphColor = null)
    {
        var panel = new StackPanel();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (glyph is not null)
        {
            Color c = glyphColor ?? Ember;
            grid.Children.Add(new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 12, 0),
                Background = Solid(c, 0x22), BorderBrush = Solid(c, 0x44), BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Top,
                Child = Icon(glyph, 15, Solid(c))
            });
        }
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = title, Style = Style("CardTitle") });
        if (subtitle is not null)
            texts.Children.Add(new TextBlock { Text = subtitle, Style = Style("CardSub"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        if (right is FrameworkElement element)
        {
            element.VerticalAlignment = VerticalAlignment.Top;
            element.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(element, 2);
            grid.Children.Add(element);
        }
        panel.Children.Add(grid);
        return panel;
    }

    public static TextBlock Text(string text, double size = 13, FontWeight? weight = null, Brush? brush = null, bool wrap = true) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = brush ?? Brush("TextBrush"),
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis
    };

    public static TextBlock Icon(string glyph, double size = 12, Brush? brush = null)
    {
        var icon = new TextBlock
        {
            Text = glyph, Style = Style("Icon"), FontSize = size,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        if (brush is not null) icon.Foreground = brush;
        return icon;
    }

    public static Border Badge(string text, Color color) => new()
    {
        CornerRadius = new CornerRadius(7),
        Padding = new Thickness(8, 2, 8, 3),
        Background = Solid(color, 0x22),
        BorderBrush = Solid(color, 0x55),
        BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Solid(color) }
    };

    public static Border Chip(string text, string? glyph = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph is not null)
        {
            row.Children.Add(Icon(glyph, 11.5, Brush("Text2Brush")));
            row.Children.Add(new Border { Width = 7 });
        }
        row.Children.Add(new TextBlock { Text = text, FontSize = 11.5, Foreground = Brush("Text2Brush"), VerticalAlignment = VerticalAlignment.Center });
        return new Border { Style = Style("Chip"), Child = row, VerticalAlignment = VerticalAlignment.Center };
    }

    public static Button Button(string text, string? glyph, string style, RoutedEventHandler click, string? tooltip = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph is not null) row.Children.Add(Icon(glyph, 12));
        if (text.Length > 0)
            row.Children.Add(new TextBlock { Text = text, Margin = new Thickness(glyph is null ? 0 : 8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Style = Style(style), Content = row };
        if (tooltip is not null) button.ToolTip = tooltip;
        System.Windows.Automation.AutomationProperties.SetName(button, text.Length > 0 ? text : tooltip ?? "");
        button.Click += click;
        return button;
    }

    public static ToggleButton Switch(bool isChecked, string name)
    {
        var toggle = new ToggleButton { Style = Style("Switch"), IsChecked = isChecked, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(toggle, name);
        return toggle;
    }

    public static Rectangle Separator(double vertical = 12) => new()
    {
        Height = 1, Fill = Brush("StrokeBrush"), Margin = new Thickness(0, vertical, 0, vertical)
    };

    /// <summary>Simge + metin satırı (madde işaretli öneri listeleri için).</summary>
    public static Grid Bullet(string text, string glyph = "", Color? color = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        TextBlock icon = Icon(glyph, 11, Solid(color ?? Success));
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.Margin = new Thickness(0, 3, 0, 0);
        grid.Children.Add(icon);
        TextBlock body = Text(text, 12.5, brush: Brush("Text2Brush"));
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    /// <summary>Kopyalanabilir tek satırlık kod kutusu.</summary>
    public static Border CodeBox(string code, Action<string> copied)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = code, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5,
            Foreground = Brush("Ember300Brush"), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 0, 8, 0)
        });
        Button copy = Button("Kopyala", "", "SmallSecondaryButton", (_, _) =>
        {
            if (TryCopy(code)) copied(code);
        });
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);
        return new Border
        {
            Background = Brush("Bg1Brush"), BorderBrush = Brush("StrokeBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(0, 4, 4, 4), Child = grid
        };
    }

    /// <summary>
    /// Uzak görsel (önbellekli): görsel gelene kadar ya da hiç gelmezse <paramref name="fallback"/> görünür.
    /// <paramref name="circle"/> true ise yuvarlak kırpılır (oyuncu fotoğrafı).
    /// </summary>
    public static Grid RemoteImage(string? url, double size, UIElement fallback, bool circle = false)
    {
        var host = new Grid { Width = size, Height = size };
        host.Children.Add(fallback);
        void Show(ImageSource image)
        {
            UIElement visual = circle
                ? new Ellipse { Width = size, Height = size, Fill = new ImageBrush(image) { Stretch = Stretch.UniformToFill } }
                : new Image { Source = image, Width = size, Height = size, Stretch = Stretch.Uniform };
            host.Children.Clear();
            host.Children.Add(visual);
        }
        if (ImageCache.TryCached(url, (int)size) is ImageSource cached) Show(cached);
        else if (!string.IsNullOrEmpty(url))
            ImageCache.GetAsync(url, (int)size).ContinueWith(t =>
            {
                if (t.Result is ImageSource image) host.Dispatcher.BeginInvoke(() => Show(image));
            }, System.Threading.Tasks.TaskScheduler.Default);
        return host;
    }

    /// <summary>Baş harf rozeti (görsel yokken).</summary>
    public static Border Monogram(string text, double size, Color color, bool circle = false) => new()
    {
        Width = size, Height = size,
        CornerRadius = new CornerRadius(circle ? size / 2 : size * 0.26),
        Background = Solid(color, 0x22), BorderBrush = Solid(color, 0x4A), BorderThickness = new Thickness(1),
        Child = new TextBlock
        {
            Text = text, FontSize = size * 0.36, FontWeight = FontWeights.Bold, Foreground = Solid(color),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        }
    };

    public static string Initials(string name)
    {
        string letters = new string(name.Where(char.IsLetterOrDigit).Take(2).ToArray());
        return letters.Length == 0 ? "?" : letters.ToUpperInvariant();
    }

    public static bool TryCopy(string text)
    {
        // Pano başka bir uygulamada kısa süre kilitli olabilir; birkaç kez denenir.
        for (int i = 0; i < 4; i++)
        {
            try { Clipboard.SetText(text); return true; }
            catch (System.Runtime.InteropServices.COMException) { System.Threading.Thread.Sleep(40); }
        }
        return false;
    }

    public static void Open(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { AppLogger.Write("Bağlantı açılamadı: " + uri, ex); }
    }
}
