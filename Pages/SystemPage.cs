using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GameColorGuard.Core;

namespace GameColorGuard.Pages;

/// <summary>Sistem: donanım özeti ve tespitlere göre öneriler. Bu sayfa hiçbir ayarı değiştirmez.</summary>
internal sealed class SystemPage : UserControl
{
    private readonly PageContext _ctx;
    private readonly StackPanel _body = new();

    public SystemPage(PageContext ctx)
    {
        _ctx = ctx;
        StackPanel page = Ui.PageStack();
        Button refresh = Ui.Button("Yenile", "", "SecondaryButton", (_, _) =>
        {
            _ctx.RefreshSystem();
            Render();
            _ctx.Notify("Sistem bilgisi yenilendi", false);
        });
        page.Children.Add(Ui.PageHeader("Sistem",
            "Bilgisayarınızın oyun açısından durumu. Uygulama burada hiçbir şeyi değiştirmez; önerilerin yanındaki düğme ilgili Windows ayarını açar.",
            refresh));
        page.Children.Add(_body);
        Content = Ui.PageScroll(page);
        Render();
    }

    private void Render()
    {
        SystemProfile s = _ctx.System;
        _body.Children.Clear();

        var tiles = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -14, 0) };
        tiles.Children.Add(Tile("", "Cihaz", s.Kind, s.Model.Length > 0 ? $"{s.Manufacturer} {s.Model}" : "Model okunamadı"));
        tiles.Children.Add(Tile("", "Güç", s.PowerText, $"Plan: {s.PowerPlan}",
            s.IsLaptop && s.HasBattery && !s.OnAcPower ? Ui.Warning : null));
        string cores = s.PhysicalCores > 0
            ? $"{s.PhysicalCores} çekirdek / {s.LogicalCores} iş parçacığı" + (s.HybridCpu ? $" · {s.PerformanceCores} P-çekirdek" : "")
            : $"{s.LogicalCores} iş parçacığı";
        bool limited = s.CpuBaseMhz > 0 && s.CpuLimitMhz > 0 && s.CpuLimitMhz < s.CpuBaseMhz * 0.9;
        tiles.Children.Add(Tile("", "İşlemci", Short(s.Cpu), cores + (limited ? $" · sınır {s.CpuLimitMhz} MHz" : ""), limited ? Ui.Warning : null));
        string channel = s.RamModules switch { 0 => "", 1 => "1 modül · tek kanal", _ => $"{s.RamModules} modül · çift kanal" };
        tiles.Children.Add(Tile("", "Bellek", $"{s.RamGb:0.#} GB · %{s.RamLoadPercent} dolu",
            string.Join(" · ", new[] { channel, s.RamSpeed is int speed ? $"{speed} MT/s" + (s.RamRatedSpeed is int rated && rated > speed ? $" (desteklenen {rated})" : "") : "" }
                .Where(t => t.Length > 0)),
            s.RamModules == 1 || s.RamLoadPercent >= 80 ? Ui.Warning : null));
        string dedicated = s.Gpus.FirstOrDefault(g => !SystemProfile.IsIntegrated(g)) ?? s.Gpus.FirstOrDefault() ?? "bilinmiyor";
        tiles.Children.Add(Tile("", "Ekran kartı", Short(dedicated),
            s.HybridGraphics ? "Hibrit: " + string.Join(" + ", s.Gpus.Select(Short)) : (s.Gpus.Count > 1 ? string.Join(", ", s.Gpus) : "Tek GPU")));
        DisplayInfo? primary = s.PrimaryDisplay;
        tiles.Children.Add(Tile("", "Ekran", primary is null ? "bilinmiyor" : $"{primary.Width}×{primary.Height} @ {primary.RefreshRate} Hz",
            primary is null ? "" : $"{s.Displays.Count} ekran · en yüksek {primary.MaxRefreshRate} Hz · {Short(primary.Gpu)}",
            primary is not null && primary.MaxRefreshRate > primary.RefreshRate ? Ui.Warning : null));
        int buildAt = s.Windows.IndexOf(" (", StringComparison.Ordinal);
        tiles.Children.Add(Tile("", "Windows", buildAt > 0 ? s.Windows[..buildAt] : s.Windows,
            buildAt > 0 ? s.Windows[(buildAt + 2)..].TrimEnd(')') : ""));
        tiles.Children.Add(Tile("", "Bellek bütünlüğü", s.MemoryIntegrity switch { true => "Açık", false => "Kapalı", _ => "bilinmiyor" },
            "Güvenlik özelliği; bilgi amaçlı"));
        tiles.Children.Add(Tile("", "GPU zamanlama (HAGS)", s.Hags switch { true => "Açık", false => "Kapalı", _ => "bilinmiyor" },
            "Değiştirmek yeniden başlatma ister"));
        foreach (GameDrive drive in s.GameDrives)
            tiles.Children.Add(Tile("", $"Oyun diski {drive.Root}", $"{(drive.Ssd switch { true => "SSD", false => "HDD", _ => "Disk" })} · {drive.FreeGb:0} GB boş",
                $"toplam {drive.TotalGb:0} GB", drive.Ssd == false || (drive.TotalGb > 0 && drive.FreeGb / drive.TotalGb < 0.10) ? Ui.Warning : null));
        tiles.Children.Add(Tile("", "GPU sürücüsü", s.GpuDriverDate is DateTime date ? date.ToString("dd.MM.yyyy") : "bilinmiyor",
            s.GpuDriverVersion ?? "", s.GpuDriverDate is DateTime old && (DateTime.Now - old).TotalDays > 180 ? Ui.Warning : null));
        _body.Children.Add(tiles);

        var advice = new StackPanel();
        advice.Children.Add(Ui.CardHeader("Öneriler", "Bu bilgisayar için tespit edilenler", null, "", Ui.Ember));
        advice.Children.Add(new Border { Height = 8 });
        bool first = true;
        foreach (Advice item in s.Advise())
        {
            if (!first) advice.Children.Add(Ui.Separator(10));
            first = false;
            advice.Children.Add(AdviceRow(item));
        }
        Border card = Ui.Card(advice, 0);
        _body.Children.Add(card);
    }

    private static Grid AdviceRow(Advice item)
    {
        (string glyph, Color color) = item.Level switch
        {
            AdviceLevel.Good => ("", Ui.Success),
            AdviceLevel.Warning => ("", Ui.Warning),
            _ => ("", Ui.Info)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 12, 0),
            Background = Ui.Solid(color, 0x22), VerticalAlignment = VerticalAlignment.Top, Child = Ui.Icon(glyph, 12, Ui.Solid(color))
        });
        var texts = new StackPanel();
        texts.Children.Add(Ui.Text(item.Title, 13.5, FontWeights.SemiBold));
        TextBlock detail = Ui.Text(item.Detail, 12.5, brush: Ui.Brush("Text2Brush"));
        detail.Margin = new Thickness(0, 3, 0, 0);
        texts.Children.Add(detail);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        if (item.ActionText is not null && item.ActionUri is not null)
        {
            Button action = Ui.Button(item.ActionText, "", "SmallSecondaryButton", (_, _) => Ui.Open(item.ActionUri));
            action.Margin = new Thickness(12, 0, 0, 0);
            action.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(action, 2);
            grid.Children.Add(action);
        }
        return grid;
    }

    private static Border Tile(string glyph, string label, string value, string detail, Color? alert = null)
    {
        var stack = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Ui.Icon(glyph, 12, alert is { } a ? Ui.Solid(a) : Ui.Brush("Text3Brush")));
        head.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), Style = Ui.Style("Eyebrow"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(head);
        stack.Children.Add(new TextBlock
        {
            Text = value, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0),
            Foreground = alert is { } c ? Ui.Solid(c) : Ui.Brush("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = value
        });
        if (detail.Length > 0)
            stack.Children.Add(new TextBlock
            {
                Text = detail, FontSize = 11.5, Foreground = Ui.Brush("Text3Brush"), Margin = new Thickness(0, 4, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = detail
            });
        Border card = Ui.Card(stack);
        card.Padding = new Thickness(16, 14, 16, 14);
        card.Margin = new Thickness(0, 0, 14, 14);
        return card;
    }

    private static string Short(string value) => value
        .Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "")
        .Replace("NVIDIA GeForce ", "").Replace(" CPU", "").Replace("11th Gen ", "").Replace("  ", " ").Trim();
}
