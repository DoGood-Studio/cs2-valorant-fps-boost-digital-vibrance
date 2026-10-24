using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GameColorGuard.Core;
using GameColorGuard.Tweaks;

namespace GameColorGuard.Pages;

/// <summary>FPS Boost: sisteme göre önerilen, geri alınabilir Windows ayarları ve oyuna özel rehber.</summary>
internal sealed class BoostPage : UserControl
{
    private readonly PageContext _ctx;
    private readonly List<(ITweak Tweak, ToggleButton Switch, TextBlock State)> _rows = new();
    private TextBlock _summaryCount = null!;
    private TextBlock _summaryDetail = null!;
    private ToggleButton _alwaysOn = null!;
    private bool _updating;

    public BoostPage(PageContext ctx)
    {
        _ctx = ctx;
        Build();
        _ctx.Performance.Changed += () => Dispatcher.BeginInvoke(Refresh);
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
    }

    private TweakCatalog Catalog => _ctx.Performance.Catalog;

    private void Build()
    {
        SystemProfile system = _ctx.System;
        StackPanel page = Ui.PageStack();
        page.Children.Add(Ui.PageHeader("FPS Boost",
            "Sisteminize göre seçilmiş, güvenli ve tek tıkla geri alınabilir ayarlar. Yönetici izni gerekmez; her ayarın önceki değeri saklanır.",
            Ui.Chip($"{system.Kind} · {system.PowerText}", system.IsLaptop ? "" : "")));

        page.Children.Add(BuildSummary(system));
        foreach (TweakGroup group in Catalog.Groups) page.Children.Add(BuildGroup(group, system));
        page.Children.Add(BuildCs2Guide());
        page.Children.Add(BuildValorantGuide());
        if (system.Gpus.Any(g => g.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))) page.Children.Add(BuildNvidiaGuide());
        page.Children.Add(Ui.Card(BuildNeverDo(), 0));

        Content = Ui.PageScroll(page);
        Refresh();
    }

    private Border BuildSummary(SystemProfile system)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = "DURUM", Style = Ui.Style("Eyebrow") });
        _summaryCount = Ui.Text("", 24, FontWeights.SemiBold);
        _summaryCount.FontFamily = Ui.Res<FontFamily>("DisplayFont");
        _summaryCount.Margin = new Thickness(0, 6, 0, 0);
        left.Children.Add(_summaryCount);
        _summaryDetail = Ui.Text("", 12.5, brush: Ui.Brush("Text2Brush"));
        _summaryDetail.Margin = new Thickness(0, 4, 0, 0);
        left.Children.Add(_summaryDetail);
        string gpu = system.Gpus.FirstOrDefault(g => !SystemProfile.IsIntegrated(g)) ?? system.Gpus.FirstOrDefault() ?? "GPU bilinmiyor";
        string display = system.PrimaryDisplay is { } d ? $" · {d.Width}×{d.Height} @ {d.RefreshRate} Hz" : "";
        left.Children.Add(new TextBlock
        {
            Text = $"{(system.Model.Length > 0 ? system.Manufacturer + " " + system.Model + " · " : "")}{gpu}{display}",
            FontSize = 12, Foreground = Ui.Brush("Text3Brush"), Margin = new Thickness(0, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
        });
        grid.Children.Add(left);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(Ui.Button("Hepsini geri al", "", "SecondaryButton", (_, _) =>
        {
            int count = _ctx.Performance.RevertAll();
            _ctx.Notify(count > 0 ? $"{count} ayar önceki değerine döndürüldü" : "Geri alınacak ayar yoktu", false);
        }));
        Button apply = Ui.Button("Önerilenleri uygula", "", "PrimaryButton", (_, _) =>
        {
            int count = _ctx.Performance.ApplyRecommended(_ctx.System);
            _ctx.Notify(count > 0
                ? $"{count} önerilen ayar açıldı" + (_ctx.Performance.GameRunning || _ctx.Performance.AlwaysOn ? "" : " · 'Oyun sırasında' olanlar oyun açılınca devreye girer")
                : "Önerilen ayarların hepsi zaten açık", false);
        });
        apply.Margin = new Thickness(10, 0, 0, 0);
        buttons.Children.Add(apply);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);

        var stack = new StackPanel();
        stack.Children.Add(grid);
        stack.Children.Add(Ui.Separator(14));

        var always = new Grid();
        always.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        always.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var alwaysText = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        alwaysText.Children.Add(Ui.Text("'Oyun sırasında' ayarlarını her zaman açık tut", 13, FontWeights.SemiBold));
        alwaysText.Children.Add(Ui.Text("Kapalıyken bu ayarlar yalnız izlenen oyun açıkken uygulanır ve oyun kapanınca geri alınır (önerilen).",
            12, brush: Ui.Brush("Text3Brush")));
        always.Children.Add(alwaysText);
        _alwaysOn = Ui.Switch(_ctx.Performance.AlwaysOn, "Her zaman açık");
        _alwaysOn.Checked += (_, _) => { if (!_updating) _ctx.Performance.SetAlwaysOn(true); };
        _alwaysOn.Unchecked += (_, _) => { if (!_updating) _ctx.Performance.SetAlwaysOn(false); };
        Grid.SetColumn(_alwaysOn, 1);
        always.Children.Add(_alwaysOn);
        stack.Children.Add(always);

        Border card = Ui.Card(stack);
        card.BorderBrush = Ui.Solid(Ui.Ember, 0x55);
        return card;
    }

    private Border BuildGroup(TweakGroup group, SystemProfile system)
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.CardHeader(group.Title, group.Subtitle, null,
            group.Tweaks[0].Scope switch { TweakScope.GameRunning => "", TweakScope.GameFocused => "", _ => "" }));
        bool first = true;
        foreach (ITweak tweak in group.Tweaks)
        {
            stack.Children.Add(first ? new Border { Height = 14 } : Ui.Separator(12));
            first = false;
            stack.Children.Add(BuildRow(tweak, system));
        }
        return Ui.Card(stack);
    }

    private Grid BuildRow(ITweak tweak, SystemProfile system)
    {
        (bool recommended, string reason) = Catalog.Recommendation(tweak, system);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
        var title = new WrapPanel();
        title.Children.Add(Ui.Text(tweak.Title, 13.5, FontWeights.SemiBold, wrap: false));
        Border badge = recommended ? Ui.Badge("Önerilen", Ui.Success) : Ui.Badge("İsteğe bağlı", Color.FromRgb(0x8A, 0x93, 0xA3));
        badge.Margin = new Thickness(9, 0, 0, 0);
        badge.ToolTip = reason;
        title.Children.Add(badge);
        texts.Children.Add(title);
        TextBlock description = Ui.Text(tweak.Description, 12.5, brush: Ui.Brush("Text2Brush"));
        description.Margin = new Thickness(0, 5, 0, 0);
        texts.Children.Add(description);
        TextBlock reasonText = Ui.Text((recommended ? "Neden: " : "Not: ") + reason, 12, brush: recommended ? Ui.Solid(Ui.Success, 0xCC) : Ui.Brush("Text3Brush"));
        reasonText.Margin = new Thickness(0, 4, 0, 0);
        texts.Children.Add(reasonText);
        TextBlock state = Ui.Text("", 11.5, brush: Ui.Brush("Text3Brush"));
        state.Margin = new Thickness(0, 4, 0, 0);
        texts.Children.Add(state);
        grid.Children.Add(texts);

        ToggleButton toggle = Ui.Switch(Catalog.IsEnabled(tweak), tweak.Title);
        toggle.IsEnabled = tweak.IsSupported;
        if (!tweak.IsSupported) toggle.ToolTip = tweak.UnsupportedReason;
        toggle.Checked += (_, _) => Toggle(tweak, true);
        toggle.Unchecked += (_, _) => Toggle(tweak, false);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);

        _rows.Add((tweak, toggle, state));
        return grid;
    }

    private void Toggle(ITweak tweak, bool enabled)
    {
        if (_updating) return;
        if (!_ctx.Performance.SetEnabled(tweak, enabled, out string? error))
            _ctx.Notify($"{tweak.Title}: {error ?? "uygulanamadı"}", true);
        else
            _ctx.Notify($"{tweak.Title}: {(enabled ? "açıldı" : "kapatıldı, önceki değer geri yüklendi")}", false);
    }

    private void Refresh()
    {
        _updating = true;
        try
        {
            foreach ((ITweak tweak, ToggleButton toggle, TextBlock state) in _rows)
            {
                toggle.IsChecked = Catalog.IsEnabled(tweak);
                string when = tweak.Scope switch
                {
                    TweakScope.GameRunning when _ctx.Performance.IsAlwaysApplied(tweak) => "her zaman",
                    TweakScope.GameRunning => _ctx.Performance.GameRunning ? "oyun açık — etkin" : "oyun açılınca",
                    TweakScope.GameFocused => "oyun odaktayken",
                    _ => "kalıcı"
                };
                string read;
                try { read = _ctx.TestMode ? "—" : tweak.Read().Summary; }
                catch (Exception ex) { read = "okunamadı: " + ex.Message; }
                state.Text = $"Şu an: {read}" + (Catalog.IsEnabled(tweak) ? $"  ·  {when}" : "");
            }
            int enabled = _rows.Count(r => Catalog.IsEnabled(r.Tweak));
            int recommended = _rows.Count(r => r.Tweak.IsSupported && Catalog.Recommendation(r.Tweak, _ctx.System).Recommended);
            int recommendedOn = _rows.Count(r => Catalog.IsEnabled(r.Tweak) && Catalog.Recommendation(r.Tweak, _ctx.System).Recommended);
            _summaryCount.Text = $"{enabled} / {_rows.Count} ayar açık";
            _summaryDetail.Text = recommendedOn >= recommended
                ? "Bu sistem için önerilen ayarların hepsi açık."
                : $"Bu sistem için önerilen {recommended} ayardan {recommendedOn} tanesi açık. 'Önerilenleri uygula' ile hepsini tek tıkla açın.";
            _alwaysOn.IsChecked = _ctx.Performance.AlwaysOn;
        }
        finally { _updating = false; }
    }

    private Border BuildCs2Guide()
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.CardHeader("Counter-Strike 2 için", "Oyun içi ayarlar ve başlatma seçenekleri", Ui.Badge("CS2", Ui.Cs2), "", Ui.Cs2));
        stack.Children.Add(new Border { Height = 12 });
        stack.Children.Add(Ui.Text("Başlatma seçenekleri (Steam › Kütüphane › CS2 › Özellikler)", 12.5, FontWeights.SemiBold));
        Border code = Ui.CodeBox("-novid +fps_max 0", c => _ctx.Notify("Başlatma seçenekleri panoya kopyalandı", false));
        code.Margin = new Thickness(0, 8, 0, 6);
        stack.Children.Add(code);
        stack.Children.Add(Ui.Text("-novid açılış videosunu atlar, +fps_max 0 FPS sınırını kaldırır. Başlatma seçeneklerinin FPS etkisi küçüktür; asıl fark aşağıdaki ayarlardan gelir. " +
                                   "-threads, -tickrate gibi komutlar istemcide işe yaramaz.", 12, brush: Ui.Brush("Text3Brush")));
        stack.Children.Add(Ui.Separator(12));
        stack.Children.Add(Ui.Bullet("NVIDIA Reflex: Açık · V-Sync: Kapalı. CS2 işlemci ağırlıklıdır; FPS'i en çok bu ikisi ve arka plan yükü etkiler."));
        stack.Children.Add(Ui.Bullet("Gölgelendirici, doku ve parçacık detayı: Düşük. Global gölge kalitesi: Orta/Yüksek (rakip gölgesini görmek için düşük yapmayın)."));
        stack.Children.Add(Ui.Bullet("Oyuncu kontrastını artır: Açık. Profesyonellerin çoğu açık kullanıyor (Pro ayarlar sayfasına bakın)."));
        stack.Children.Add(Ui.Bullet("MSAA: 2x–4x (işlemciniz zayıfsa Kapalı) · FidelityFX Super Resolution: Kapalı."));
        stack.Children.Add(Ui.Bullet("Özel 'Tam ekran' en düşük gecikmeyi verir; ama Game Color Guard'ın aydınlık/kontrast/canlılık katmanı yalnız 'Tam ekran pencereli' modda görünür. Renk profili kullanıyorsanız pencereli tam ekranda kalın.",
            "", Ui.Warning));
        return Ui.Card(stack);
    }

    private Border BuildValorantGuide()
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.CardHeader("VALORANT için", "Oyun içi Video › Grafik kalitesi", Ui.Badge("VALORANT", Ui.Valorant), "", Ui.Valorant));
        stack.Children.Add(new Border { Height = 10 });
        stack.Children.Add(Ui.Bullet("Çok iş parçacıklı işleme: Açık — işlemci darboğazında en büyük FPS kazancı bu ayardan gelir."));
        stack.Children.Add(Ui.Bullet("NVIDIA Reflex: Açık + Boost · V-Sync: Kapalı · FPS sınırı: kapalı ya da ekran Hz'inizin üstünde sabit."));
        stack.Children.Add(Ui.Bullet("Malzeme, doku ve detay kalitesi: Düşük · Bloom, Bozulma, Gölge düşürme, Vinyet: Kapalı."));
        stack.Children.Add(Ui.Bullet("Kenar yumuşatma: Yok veya MSAA 2x · Anizotropik filtreleme: 1x–4x · Netliği iyileştir: Kapalı."));
        stack.Children.Add(Ui.Bullet("Riot Vanguard oyun sürecine müdahaleyi engeller; bu uygulama VALORANT'ın belleğine hiç dokunmaz. 'Yüksek öncelik' reddedilirse sessizce atlanır.",
            "", Ui.Info));
        return Ui.Card(stack);
    }

    private Border BuildNvidiaGuide()
    {
        var stack = new StackPanel();
        Button open = Ui.Button("Denetim Masasını aç", "", "SmallSecondaryButton", (_, _) =>
            Ui.Open(@"shell:AppsFolder\NVIDIACorp.NVIDIAControlPanel_56jybvzbzjz8z!NVIDIACorp.NVIDIAControlPanel"));
        stack.Children.Add(Ui.CardHeader("NVIDIA Denetim Masası", "3B ayarları › Program ayarları › oyunu seçin", open, "", Ui.Success));
        stack.Children.Add(new Border { Height = 10 });
        stack.Children.Add(Ui.Bullet("Güç yönetimi modu: Maksimum performansı tercih et (yalnız oyun için; genel ayarda bırakmayın, boşta da güç çeker)."));
        stack.Children.Add(Ui.Bullet("Düşük gecikme modu: Açık. Oyunda NVIDIA Reflex varsa Reflex bunu geçersiz kılar."));
        stack.Children.Add(Ui.Bullet("Doku filtreleme - Kalite: Yüksek performans · Dikey eşitleme: Kapalı."));
        stack.Children.Add(Ui.Bullet("Gölgelendirici önbelleği boyutu: 10 GB veya Sınırsız: harita/oyun ilk açılışındaki takılmaları azaltır."));
        return Ui.Card(stack);
    }

    private static StackPanel BuildNeverDo()
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.CardHeader("Bilerek yapmadıklarımız", "Bilgisayarınızı bozmamak ve güvenliği korumak için", null, "", Ui.Info));
        stack.Children.Add(new Border { Height = 10 });
        stack.Children.Add(Ui.Bullet("Windows hizmetlerini kapatmak, kayıt defterinde yönetici izni isteyen 'sihirli' ayarlar, bcdedit/HPET değişiklikleri: ölçülebilir faydası yok, sorun çıkarır.", "", Ui.Danger));
        stack.Children.Add(Ui.Bullet("Bellek bütünlüğü/VBS'yi kapatmak: bazı testlerde %5-15 FPS kazandırır ama bir güvenlik özelliğidir. Sistem sayfası bilgi verir, kararı size bırakır.", "", Ui.Danger));
        stack.Children.Add(Ui.Bullet("Oyun süreçlerinin belleğine dokunmak: anti-hile (VAC, Vanguard, FACEIT) açısından risklidir; uygulama yalnız Windows ayarlarını değiştirir.", "", Ui.Danger));
        stack.Children.Add(Ui.Bullet("'RAM temizleyiciler' (standby list/ISLC): yönetici izni ister, FPS'e etkisini gösteren bağımsız ölçüm yok. Gerçek kazanç için Arka plan sayfasından uygulama kapatın; RAM tek kanalsa ikinci modül takın (Sistem sayfası).", "", Ui.Danger));
        stack.Children.Add(Ui.Bullet("Zamanlayıcı çözünürlüğü, çekirdek park ayarı, oyunu belirli çekirdeklere sabitleme: oyunlar zamanlayıcıyı zaten kendileri ayarlar; Nihai/Yüksek performans planı çekirdek parkını kapatır; çekirdek sabitleme raporları çelişkili (bazı sistemlerde takılmayı artırıyor).", "", Ui.Danger));
        return stack;
    }
}
