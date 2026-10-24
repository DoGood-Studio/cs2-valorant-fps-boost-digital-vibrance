using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using GameColorGuard.Core;

namespace GameColorGuard.Pages;

/// <summary>
/// Pro oyuncu ayarları: oyun ve takım seçimi, arama, oyuncu fotoğraflı kartlar; tek tıkla CS2'ye yükleme ve crosshair kopyalama.
/// Fotoğraf ve logolar prosettings.net'ten çalışırken indirilip önbelleğe alınır; depoda görsel yoktur.
/// </summary>
internal sealed class ProPage : UserControl
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    private readonly PageContext _ctx;
    private readonly ProDataset _data;
    private readonly UniformGrid _cards = new() { Columns = 2 };
    private readonly WrapPanel _teamBar = new();
    private readonly TextBlock _countText;
    private readonly TextBox _dpiBox;
    private readonly TextBox _searchBox;
    private string _game = "cs2";
    private string? _team;

    public ProPage(PageContext ctx)
    {
        _ctx = ctx;
        _data = ProConfig.Load();

        StackPanel page = Ui.PageStack();
        string fetched = DateTime.TryParse(_data.FetchedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime f)
            ? f.ToString("d MMMM yyyy", Tr) : _data.FetchedAt;
        Button source = Ui.Button("prosettings.net", "", "SmallSecondaryButton", (_, _) => Ui.Open("https://prosettings.net/"));
        page.Children.Add(Ui.PageHeader("Pro ayarlar",
            $"Top takımların {_data.Players.Count} oyuncusunun güncel fare, crosshair ve görüntü ayarları. Veri: prosettings.net, {fetched}. Hassasiyet sizin fare DPI'nıza çevrilir.",
            source));

        // Üst çubuk: oyun, arama, DPI
        var toolbar = new Grid();
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var games = new StackPanel { Orientation = Orientation.Horizontal };
        foreach ((string key, string label, string exe) in new[] { ("cs2", "Counter-Strike 2", "cs2.exe"), ("valorant", "VALORANT", "VALORANT-Win64-Shipping.exe") })
        {
            var tab = new RadioButton { Style = Ui.Style("FilterTab"), GroupName = "ProGame", IsChecked = key == _game };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(GameIcon(exe, 18));
            int count = _data.Players.Count(p => p.Game.Equals(key, StringComparison.OrdinalIgnoreCase));
            content.Children.Add(new TextBlock { Text = $"{label}  {count}", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            tab.Content = content;
            tab.Checked += (_, _) => { _game = key; _team = null; BuildTeamBar(); RenderCards(); };
            games.Children.Add(tab);
        }
        toolbar.Children.Add(new Border
        {
            Background = Ui.Brush("Bg1Brush"), BorderBrush = Ui.Brush("StrokeBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(2), Child = games, VerticalAlignment = VerticalAlignment.Center
        });

        _searchBox = new TextBox
        {
            Style = Ui.Style("ValueBox"), Width = double.NaN, MinWidth = 160, MaxWidth = 260, HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(14, 0, 14, 0), FontWeight = FontWeights.Normal, FontSize = 13, ToolTip = "Oyuncu ya da takım ara"
        };
        System.Windows.Automation.AutomationProperties.SetName(_searchBox, "Oyuncu ara");
        _searchBox.TextChanged += (_, _) => RenderCards();
        var search = new Grid { HorizontalAlignment = HorizontalAlignment.Right };
        search.Children.Add(_searchBox);
        var hint = new StackPanel
        {
            Orientation = Orientation.Horizontal, IsHitTestVisible = false,
            Margin = new Thickness(26, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
        };
        hint.Children.Add(Ui.Icon("", 12, Ui.Brush("Text3Brush")));
        hint.Children.Add(Ui.Text("Oyuncu veya takım ara…", 12.5, brush: Ui.Brush("Text3Brush"), wrap: false));
        ((TextBlock)hint.Children[1]).Margin = new Thickness(8, 0, 0, 0);
        _searchBox.TextChanged += (_, _) => hint.Visibility = _searchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        search.Children.Add(hint);
        Grid.SetColumn(search, 1);
        toolbar.Children.Add(search);

        var dpi = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        dpi.Children.Add(Ui.Text("Fare DPI'nız", 12.5, FontWeights.SemiBold, Ui.Brush("Text2Brush"), wrap: false));
        _dpiBox = new TextBox { Style = Ui.Style("ValueBox"), Text = _ctx.Settings.MouseDpi.ToString(CultureInfo.InvariantCulture), Margin = new Thickness(10, 0, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(_dpiBox, "Fare DPI");
        _dpiBox.LostKeyboardFocus += (_, _) => CommitDpi();
        _dpiBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) CommitDpi(); };
        dpi.Children.Add(_dpiBox);
        Grid.SetColumn(dpi, 2);
        toolbar.Children.Add(dpi);

        // Takım seçici
        var teamStack = new StackPanel();
        teamStack.Children.Add(toolbar);
        teamStack.Children.Add(Ui.Separator(12));
        var teamHead = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        teamHead.Children.Add(new TextBlock { Text = "TAKIM", Style = Ui.Style("Eyebrow"), VerticalAlignment = VerticalAlignment.Center });
        _countText = new TextBlock { FontSize = 11.5, Foreground = Ui.Brush("Text3Brush"), HorizontalAlignment = HorizontalAlignment.Right };
        teamHead.Children.Add(_countText);
        teamStack.Children.Add(teamHead);
        teamStack.Children.Add(_teamBar);
        page.Children.Add(Ui.Card(teamStack));

        page.Children.Add(_cards);
        Content = Ui.PageScroll(page);
        SizeChanged += (_, e) => _cards.Columns = e.NewSize.Width >= 1180 ? 3 : e.NewSize.Width >= 640 ? 2 : 1;
        BuildTeamBar();
        RenderCards();
    }

    private void BuildTeamBar()
    {
        _teamBar.Children.Clear();
        _teamBar.Children.Add(TeamChip(null, "Tüm takımlar", null));
        foreach (ProTeam team in _data.Teams.Where(t => t.Game.Equals(_game, StringComparison.OrdinalIgnoreCase)))
            _teamBar.Children.Add(TeamChip(team.Slug, team.Name, team.LogoUrl));
        // Takımı olmayan (serbest/ listede olmayan takım) oyuncular
        if (_data.Players.Any(p => p.Game.Equals(_game, StringComparison.OrdinalIgnoreCase) && p.TeamSlug is null))
            _teamBar.Children.Add(TeamChip("", "Diğer", null));
    }

    private RadioButton TeamChip(string? slug, string name, string? logo)
    {
        var chip = new RadioButton { Style = Ui.Style("FilterTab"), GroupName = "ProTeam", IsChecked = slug == _team, ToolTip = name };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (slug is { Length: > 0 })
        {
            content.Children.Add(Ui.RemoteImage(logo, 18, Ui.Monogram(Ui.Initials(name), 18, Color.FromRgb(0x8A, 0x93, 0xA3))));
            content.Children.Add(new Border { Width = 7 });
        }
        content.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        chip.Content = content;
        chip.Checked += (_, _) => { _team = slug; RenderCards(); };
        return chip;
    }

    private void CommitDpi()
    {
        if (!int.TryParse(_dpiBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int dpi) || dpi < 100 || dpi > 32000)
        {
            _dpiBox.Text = _ctx.Settings.MouseDpi.ToString(CultureInfo.InvariantCulture);
            return;
        }
        if (dpi == _ctx.Settings.MouseDpi) return;
        _ctx.Settings.MouseDpi = dpi;
        _ctx.Save();
        RenderCards();
    }

    private void RenderCards()
    {
        string query = _searchBox.Text.Trim();
        var players = _data.Players
            .Where(p => p.Game.Equals(_game, StringComparison.OrdinalIgnoreCase))
            .Where(p => _team is null || (_team.Length == 0 ? p.TeamSlug is null : p.TeamSlug == _team))
            .Where(p => query.Length == 0 ||
                        p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        (p.Team?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (p.RealName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
        _countText.Text = $"{players.Count} oyuncu";
        _cards.Children.Clear();
        foreach (ProPlayer player in players) _cards.Children.Add(BuildCard(player));
    }

    private Border BuildCard(ProPlayer player)
    {
        Color accent = player.IsCs2 ? Ui.Cs2 : Ui.Valorant;
        var stack = new StackPanel();

        // Üst: fotoğraf (takım logosu rozetiyle), ad, takım · rol · ülke, oyun rozeti
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var avatar = new Grid { Width = 58, Height = 58, Margin = new Thickness(0, 0, 14, 0) };
        avatar.Children.Add(new Ellipse { Fill = Ui.Solid(accent, 0x18), Stroke = Ui.Solid(accent, 0x55), StrokeThickness = 1.5 });
        Grid photo = Ui.RemoteImage(player.PhotoUrl, 54, Ui.Monogram(Ui.Initials(player.Name), 54, accent, circle: true), circle: true);
        avatar.Children.Add(photo);
        if (player.TeamLogoUrl is not null)
        {
            var badge = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(7), Background = Ui.Brush("Surface3Brush"),
                BorderBrush = Ui.Brush("StrokeStrongBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(3),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -4, -3),
                Child = Ui.RemoteImage(player.TeamLogoUrl, 16, new Border()), ToolTip = player.Team
            };
            avatar.Children.Add(badge);
        }
        head.Children.Add(avatar);

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock
        {
            Text = player.Name, FontSize = 18, FontWeight = FontWeights.SemiBold, ToolTip = player.RealName,
            FontFamily = Ui.Res<FontFamily>("DisplayFont"), Foreground = Ui.Brush("TextBrush")
        });
        names.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", new[] { player.Team, RoleName(player.Role), CountryName(player) }.Where(s => !string.IsNullOrEmpty(s))),
            FontSize = 12, Foreground = Ui.Brush("Text2Brush"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(names, 1);
        head.Children.Add(names);
        var gameBadge = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Background = Ui.Solid(accent, 0x1E),
            BorderBrush = Ui.Solid(accent, 0x4A), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top,
            Child = GameIcon(player.IsCs2 ? "cs2.exe" : "VALORANT-Win64-Shipping.exe", 16), ToolTip = player.IsCs2 ? "Counter-Strike 2" : "VALORANT"
        };
        Grid.SetColumn(gameBadge, 2);
        head.Children.Add(gameBadge);
        stack.Children.Add(head);

        // Değerler
        var stats = new UniformGrid { Columns = 3, Margin = new Thickness(0, 14, 0, 0) };
        stats.Children.Add(Stat("DPI", ProConfig.Number(player.Mouse.Dpi)));
        stats.Children.Add(Stat("Hassasiyet", ProConfig.Number(player.Mouse.Sensitivity)));
        stats.Children.Add(Stat("eDPI", ProConfig.Number(player.Mouse.Edpi)));
        stats.Children.Add(Stat("Çözünürlük", player.Video?.Resolution ?? "—"));
        stats.Children.Add(Stat(player.IsCs2 ? "Oran" : "Görüntü", player.IsCs2
            ? $"{player.Video?.AspectRatio} {Shorten(player.Video?.ScalingMode)}".Trim() is { Length: > 0 } ratio ? ratio : "—"
            : Shorten(player.Video?.DisplayMode) ?? "—"));
        stats.Children.Add(Stat("Yoklama", player.Mouse.Hz is double hz ? $"{hz:0} Hz" : "—"));
        stack.Children.Add(stats);

        double? converted = ProConfig.ConvertedSensitivity(player, _ctx.Settings.MouseDpi);
        var yours = new Border
        {
            Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(10),
            Background = Ui.Solid(Ui.Ember, 0x14), BorderBrush = Ui.Solid(Ui.Ember, 0x33), BorderThickness = new Thickness(1)
        };
        var yoursText = new TextBlock { FontSize = 12.5, Foreground = Ui.Brush("Text2Brush"), TextWrapping = TextWrapping.Wrap };
        yoursText.Inlines.Add(new System.Windows.Documents.Run($"Sizin {_ctx.Settings.MouseDpi} DPI farenizde aynı hız: "));
        yoursText.Inlines.Add(new System.Windows.Documents.Run(ProConfig.Number(converted))
            { FontWeight = FontWeights.Bold, Foreground = Ui.Brush("Ember300Brush") });
        if (!player.IsCs2 && player.Mouse.ScopedSensitivity is double scoped)
            yoursText.Inlines.Add(new System.Windows.Documents.Run($"  ·  nişan çarpanı {ProConfig.Number(scoped)}"));
        yours.Child = yoursText;
        stack.Children.Add(yours);

        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (player.IsCs2)
        {
            Button apply = Ui.Button("CS2'ye yükle", "", "SmallPrimaryButton", (_, _) => ApplyCs2(player),
                "Hassasiyet, crosshair ve viewmodel ayarlarını CS2 config klasörüne yeni bir dosya olarak yazar");
            apply.Margin = new Thickness(0, 0, 8, 0);
            actions.Children.Add(apply);
            Button cross = Ui.Button("Crosshair", "", "SmallSecondaryButton", (_, _) =>
            {
                if (Ui.TryCopy(ProConfig.Cs2CrosshairCommands(player)))
                    _ctx.Notify($"{player.Name} crosshair komutları kopyalandı — CS2 konsoluna yapıştırın", false);
            }, "Crosshair konsol komutlarını panoya kopyalar");
            cross.IsEnabled = player.Crosshair?.Length is not null;
            cross.Margin = new Thickness(0, 0, 8, 0);
            actions.Children.Add(cross);
        }
        else
        {
            Button cross = Ui.Button("Crosshair kodu", "", "SmallPrimaryButton", (_, _) =>
            {
                if (player.Crosshair?.Code is string code && Ui.TryCopy(code))
                    _ctx.Notify($"{player.Name} crosshair kodu kopyalandı — VALORANT › Ayarlar › Nişangah › Profil › İçe aktar", false);
            });
            cross.IsEnabled = player.Crosshair?.Code is not null;
            cross.Margin = new Thickness(0, 0, 8, 0);
            actions.Children.Add(cross);
            Button sens = Ui.Button("Hassasiyet", "", "SmallSecondaryButton", (_, _) =>
            {
                if (converted is double value && Ui.TryCopy(value.ToString("0.###", CultureInfo.InvariantCulture)))
                    _ctx.Notify($"Hassasiyet {value.ToString("0.###", CultureInfo.InvariantCulture)} kopyalandı ({_ctx.Settings.MouseDpi} DPI için)", false);
            });
            sens.Margin = new Thickness(0, 0, 8, 0);
            actions.Children.Add(sens);
        }
        actions.Children.Add(Ui.Button("", "", "SmallSecondaryButton", (_, _) => Ui.Open(player.Url), $"{player.Name} sayfasını prosettings.net'te aç"));
        stack.Children.Add(actions);

        string updated = DateTime.TryParse(player.LastUpdated, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d)
            ? d.ToString("d MMM yyyy", Tr) : "bilinmiyor";
        stack.Children.Add(new TextBlock
        {
            Text = $"{player.Mouse.Model ?? "Fare bilinmiyor"} · güncelleme {updated}", FontSize = 11, Foreground = Ui.Brush("Text3Brush"),
            Margin = new Thickness(0, 10, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = player.Mouse.Model
        });

        Border card = Ui.Card(stack);
        card.Margin = new Thickness(0, 0, 14, 14);
        card.Padding = new Thickness(18, 16, 18, 16);
        return card;
    }

    private void ApplyCs2(ProPlayer player)
    {
        if (_ctx.TestMode) return;
        try
        {
            _ctx.Settings.GamePaths.TryGetValue("cs2.exe", out string? cs2);
            string? path = ProConfig.WriteCs2Config(player, _ctx.Settings.MouseDpi, cs2, out string? error);
            if (path is null)
            {
                _ctx.Notify(error ?? "CS2 bulunamadı", true);
                return;
            }
            string command = "exec " + System.IO.Path.GetFileNameWithoutExtension(path);
            Ui.TryCopy(command);
            _ctx.Notify($"{player.Name} config'i yazıldı. CS2 konsoluna yapıştırın: {command} (panoya kopyalandı)", false);
        }
        catch (Exception ex)
        {
            AppLogger.Write("Pro config yazılamadı", ex);
            _ctx.Notify("Config yazılamadı: " + ex.Message, true);
        }
    }

    /// <summary>Resmi oyun ikonu: kurulu oyunun exe'sinden, yoksa resmi siteden; o da yoksa rozet.</summary>
    private FrameworkElement GameIcon(string exe, double size)
    {
        bool cs2 = exe.Equals("cs2.exe", StringComparison.OrdinalIgnoreCase);
        Border fallback = Ui.Monogram(cs2 ? "CS" : "V", size, cs2 ? Ui.Cs2 : Ui.Valorant);
        ImageSource? icon = GameIcons.Resolve(exe, _ctx.Settings.GamePaths, (int)size, out var pending);
        if (icon is not null) return new Image { Source = icon, Width = size, Height = size };
        return Ui.RemoteImage(GameIcons.OfficialIconUrl(exe), size, fallback);
    }

    private static StackPanel Stat(string label, string value)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 8, 8) };
        stack.Children.Add(new TextBlock { Text = label.ToUpper(Tr), Style = Ui.Style("Eyebrow") });
        stack.Children.Add(new TextBlock
        {
            Text = value, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 0),
            Foreground = Ui.Brush("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis
        });
        return stack;
    }

    private static string? RoleName(string? role) => role switch
    {
        null or "" => null,
        "Sniper" => "AWP'ci",
        "Rifler" => "Rifler",
        "IGL" or "In-game leader" => "Lider (IGL)",
        "Coach" => "Koç",
        _ => role
    };

    private static string? Shorten(string? value) => value switch
    {
        "Stretched" => "Uzatılmış",
        "Native" => "Doğal",
        "Black Bars" => "Siyah bant",
        "Fullscreen" => "Tam ekran",
        "Windowed Fullscreen" => "Pencereli tam",
        _ => value
    };

    private static string? CountryName(ProPlayer player) => player.Country?.ToUpperInvariant() switch
    {
        null => player.CountryName,
        "TR" => "Türkiye",
        "RU" => "Rusya",
        "FR" => "Fransa",
        "UA" => "Ukrayna",
        "BA" => "Bosna-Hersek",
        "EE" => "Estonya",
        "CA" => "Kanada",
        "BR" => "Brezilya",
        "FI" => "Finlandiya",
        "US" => "ABD",
        "DE" => "Almanya",
        "DK" => "Danimarka",
        "SE" => "İsveç",
        "PL" => "Polonya",
        "MN" => "Moğolistan",
        "KR" => "Güney Kore",
        "JP" => "Japonya",
        "CN" => "Çin",
        "GB" => "Birleşik Krallık",
        "ES" => "İspanya",
        "BE" => "Belçika",
        "NL" => "Hollanda",
        "IL" => "İsrail",
        "KZ" => "Kazakistan",
        "LV" => "Letonya",
        "SK" => "Slovakya",
        "HU" => "Macaristan",
        "PT" => "Portekiz",
        "AR" => "Arjantin",
        "CL" => "Şili",
        _ => player.CountryName
    };
}
