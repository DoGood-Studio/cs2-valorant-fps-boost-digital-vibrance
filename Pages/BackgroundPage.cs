using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using GameColorGuard.Core;

namespace GameColorGuard.Pages;

/// <summary>Arka plan uygulamaları: tarar, önerir; yalnız kullanıcının seçtiklerini kapatır.</summary>
internal sealed class BackgroundPage : UserControl
{
    private static readonly (AppCategory Category, string Title, string Subtitle, string Glyph, System.Windows.Media.Color Color)[] Sections =
    {
        (AppCategory.Suggested, "Önerilen", "Oyun sırasında gereksiz; kapatması güvenli", "", Ui.Success),
        (AppCategory.Caution, "Dikkat", "Bir işlevi var; kullanmıyorsanız kapatın", "", Ui.Warning),
        (AppCategory.Other, "Diğer uygulamalar", "Tanınmayan uygulamalar; ne olduğunu biliyorsanız seçin", "", Ui.Info),
        (AppCategory.Keep, "Oyun için gerekli", "Oyun, anti-hile, Steam/Riot ve sürücüler: kapatılamaz", "", Ui.Ember)
    };

    private readonly PageContext _ctx;
    private readonly StackPanel _list = new();
    private readonly TextBlock _scanInfo;
    private readonly TextBlock _selectionInfo;
    private readonly Button _scanButton;
    private readonly Button _closeButton;
    private IReadOnlyList<BackgroundApp> _apps = Array.Empty<BackgroundApp>();
    private bool _scanning;
    private bool _scannedOnce;

    public BackgroundPage(PageContext ctx)
    {
        _ctx = ctx;
        StackPanel page = Ui.PageStack();
        _scanButton = Ui.Button("Yeniden tara", "", "SecondaryButton", async (_, _) => await ScanAsync());
        page.Children.Add(Ui.PageHeader("Arka plan uygulamaları",
            "Oyun sırasında işlemci ve RAM tüketen uygulamaları bulur. Hiçbir şey sizin seçiminiz olmadan kapatılmaz; pencereli uygulamalar düzgünce kapatılır, kaydedilmemiş işiniz varsa sorarlar.",
            _scanButton));

        var toolbar = new Grid();
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _scanInfo = Ui.Text("Henüz taranmadı", 13.5, FontWeights.SemiBold);
        _selectionInfo = Ui.Text("", 12.5, brush: Ui.Brush("Text2Brush"));
        _selectionInfo.Margin = new Thickness(0, 4, 0, 0);
        infoStack.Children.Add(_scanInfo);
        infoStack.Children.Add(_selectionInfo);
        toolbar.Children.Add(infoStack);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(Ui.Button("Seçimi temizle", null, "GhostButton", (_, _) => Select(_ => false)));
        Button suggest = Ui.Button("Önerilenleri seç", "", "SecondaryButton", (_, _) => Select(a => a.Category == AppCategory.Suggested));
        suggest.Margin = new Thickness(6, 0, 0, 0);
        actions.Children.Add(suggest);
        _closeButton = Ui.Button("Seçilenleri kapat", "", "PrimaryButton", (_, _) => CloseSelected());
        _closeButton.Margin = new Thickness(10, 0, 0, 0);
        actions.Children.Add(_closeButton);
        Grid.SetColumn(actions, 1);
        toolbar.Children.Add(actions);
        Border toolbarCard = Ui.Card(toolbar);
        toolbarCard.BorderBrush = Ui.Solid(Ui.Ember, 0x55);
        page.Children.Add(toolbarCard);
        page.Children.Add(_list);

        Content = Ui.PageScroll(page);
        UpdateSelection();
        // İlk görünüşte bir kez tarar (sayfa açılmadıkça arka planda tarama yapılmaz).
        IsVisibleChanged += async (_, _) => { if (IsVisible && !_scannedOnce && !_ctx.TestMode) await ScanAsync(); };
    }

    private async Task ScanAsync()
    {
        if (_scanning) return;
        _scanning = true;
        _scannedOnce = true;
        _scanButton.IsEnabled = false;
        _scanInfo.Text = "Taranıyor… (işlemci kullanımı 1 saniye ölçülüyor)";
        try
        {
            var games = _ctx.Settings.GameExecutables.ToList();
            _apps = await Task.Run(() => BackgroundScanner.Scan(games, TimeSpan.FromSeconds(1)));
            Render();
            long total = _apps.Where(a => a.CanClose).Sum(a => a.MemoryBytes);
            _scanInfo.Text = $"{DateTime.Now:HH:mm} · {_apps.Count} uygulama · kapatılabilenler toplam {Megabytes(total)} RAM kullanıyor";
        }
        catch (Exception ex)
        {
            AppLogger.Write("Arka plan taraması başarısız", ex);
            _scanInfo.Text = "Tarama başarısız: " + ex.Message;
        }
        finally
        {
            _scanButton.IsEnabled = true;
            _scanning = false;
        }
    }

    /// <summary>Test/çizim modu: gerçek süreçlere dokunmadan örnek liste.</summary>
    internal void ShowSample()
    {
        _scannedOnce = true;
        var sample = new List<BackgroundApp>
        {
            Sample("chrome.exe", "Google Chrome", AppCategory.Suggested, "Sekmeler RAM ve işlemci tüketir", 2.4, 1_450, true),
            Sample("onedrive.exe", "OneDrive", AppCategory.Suggested, "Dosya eşitleme diski ve ağı kullanır", 0.3, 120, false),
            Sample("spotify.exe", "Spotify", AppCategory.Suggested, "Müzik dinlemiyorsanız gereksiz", 0.8, 310, false),
            Sample("discord.exe", "Discord", AppCategory.Caution, "Sesli sohbet kullanıyorsanız açık kalsın", 1.1, 420, false),
            Sample("steam.exe", "Steam", AppCategory.Keep, "CS2 için gerekli", 0.4, 260, false),
            Sample("cs2.exe", "Counter-Strike 2", AppCategory.Keep, "İzlenen oyun", 31.0, 2_800, true)
        };
        sample[0].IsSelected = true;
        _apps = sample;
        Render();
        _scanInfo.Text = "12:00 · 6 uygulama · kapatılabilenler toplam 2,3 GB RAM kullanıyor";
    }

    private static BackgroundApp Sample(string exe, string name, AppCategory category, string reason, double cpu, long mb, bool window)
    {
        var app = new BackgroundApp(exe, name, category, reason) { CpuPercent = cpu, MemoryBytes = mb * 1024 * 1024, HasWindow = window };
        app.ProcessIds.Add(0);
        return app;
    }

    private void Render()
    {
        _list.Children.Clear();
        foreach (var section in Sections)
        {
            var apps = _apps.Where(a => a.Category == section.Category).ToList();
            if (apps.Count == 0) continue;
            var stack = new StackPanel();
            stack.Children.Add(Ui.CardHeader($"{section.Title} · {apps.Count}", section.Subtitle, null, section.Glyph, section.Color));
            stack.Children.Add(new Border { Height = 8 });
            foreach (BackgroundApp app in apps) stack.Children.Add(BuildRow(app));
            _list.Children.Add(Ui.Card(stack));
        }
        UpdateSelection();
    }

    private Border BuildRow(BackgroundApp app)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });

        var check = new CheckBox { Style = Ui.Style("Check"), IsChecked = app.IsSelected, IsEnabled = app.CanClose };
        var texts = new StackPanel();
        var titleRow = new WrapPanel();
        titleRow.Children.Add(Ui.Text(app.Name, 13, FontWeights.SemiBold, wrap: false));
        titleRow.Children.Add(new TextBlock
        {
            Text = "  " + app.Executable + (app.ProcessIds.Count > 1 ? $" ×{app.ProcessIds.Count}" : "") + (app.HasWindow ? " · pencere açık" : ""),
            FontSize = 11.5, Foreground = Ui.Brush("Text3Brush"), VerticalAlignment = VerticalAlignment.Center
        });
        texts.Children.Add(titleRow);
        TextBlock reason = Ui.Text(app.Reason, 12, brush: Ui.Brush("Text2Brush"));
        reason.Margin = new Thickness(0, 2, 0, 0);
        texts.Children.Add(reason);
        check.Content = texts;
        check.Checked += (_, _) => { app.IsSelected = true; UpdateSelection(); };
        check.Unchecked += (_, _) => { app.IsSelected = false; UpdateSelection(); };
        app.PropertyChanged += (_, _) => check.IsChecked = app.IsSelected;
        grid.Children.Add(check);

        TextBlock cpu = Metric($"%{app.CpuPercent:0.0}", "işlemci", app.CpuPercent >= 5 ? Ui.Warning : null);
        Grid.SetColumn(cpu, 1);
        grid.Children.Add(cpu);
        TextBlock ram = Metric(Megabytes(app.MemoryBytes), "RAM", app.MemoryBytes >= 800L * 1024 * 1024 ? Ui.Warning : null);
        Grid.SetColumn(ram, 2);
        grid.Children.Add(ram);

        var row = new Border { Padding = new Thickness(10, 9, 10, 9), CornerRadius = new CornerRadius(10), Child = grid, Background = System.Windows.Media.Brushes.Transparent };
        row.MouseEnter += (_, _) => row.Background = Ui.Brush("Surface2Brush");
        row.MouseLeave += (_, _) => row.Background = System.Windows.Media.Brushes.Transparent;
        return row;
    }

    private static TextBlock Metric(string value, string label, System.Windows.Media.Color? color)
    {
        var text = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        text.Inlines.Add(new System.Windows.Documents.Run(value)
        {
            FontWeight = FontWeights.SemiBold, FontSize = 13,
            Foreground = color is { } c ? Ui.Solid(c) : Ui.Brush("TextBrush")
        });
        text.Inlines.Add(new System.Windows.Documents.LineBreak());
        text.Inlines.Add(new System.Windows.Documents.Run(label) { FontSize = 11, Foreground = Ui.Brush("Text3Brush") });
        return text;
    }

    private void Select(Func<BackgroundApp, bool> predicate)
    {
        foreach (BackgroundApp app in _apps) app.IsSelected = app.CanClose && predicate(app);
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = _apps.Where(a => a.IsSelected).ToList();
        _selectionInfo.Text = selected.Count == 0
            ? "Kapatmak istediklerinizi işaretleyin ya da 'Önerilenleri seç'e basın."
            : $"{selected.Count} uygulama seçili · {Megabytes(selected.Sum(a => a.MemoryBytes))} RAM boşalacak";
        _closeButton.IsEnabled = selected.Count > 0;
    }

    private async void CloseSelected()
    {
        var selected = _apps.Where(a => a.IsSelected && a.CanClose).ToList();
        if (selected.Count == 0 || _ctx.TestMode) return;
        string names = string.Join("\n• ", selected.Select(a => a.Name));
        MessageBoxResult answer = MessageBox.Show(Window.GetWindow(this)!,
            $"Şu uygulamalar kapatılacak:\n\n• {names}\n\nPencereli olanlar düzgünce kapatılır; kaydedilmemiş işiniz varsa uygulama sorar. Devam edilsin mi?",
            "Game Color Guard", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        _closeButton.IsEnabled = false;
        _scanInfo.Text = "Kapatılıyor…";
        IReadOnlyList<string> problems = await Task.Run(() => BackgroundScanner.Close(selected));
        int closed = selected.Count - problems.Count;
        _ctx.Notify(problems.Count == 0
            ? $"{closed} uygulama kapatıldı"
            : $"{closed} uygulama kapatıldı · {problems.Count} sorun: {string.Join("; ", problems)}", problems.Count > 0);
        await ScanAsync();
    }

    private static string Megabytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / 1024.0 / 1024 / 1024:0.0} GB" : $"{bytes / 1024 / 1024} MB";
}
