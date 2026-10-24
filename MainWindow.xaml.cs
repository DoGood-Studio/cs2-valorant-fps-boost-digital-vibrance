using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameColorGuard.Controls;
using GameColorGuard.Core;
using GameColorGuard.Pages;
using GameColorGuard.Tweaks;

namespace GameColorGuard;

public partial class MainWindow : Window
{
    private enum Tone { Neutral, Success, Warning, Error, Info }
    private enum HeroState { Idle, Active, Background, Preview, Error }

    private const double PreviewSeconds = 10;
    private const double PreviewClickGuardMs = 1200;
    // Oyun gizlendikten sonra renkleri geri almadan önceki bekleme: hızlı Alt+Tab ve kısa odak kaçışları ekranı kırpıştırmasın.
    private static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(700);
    // Oyuna dönüşte profil bu anlarda yeniden yazılır: sürücü/oyun tam ekran geçişinde rampayı sıfırlarsa geri gelir.
    private static readonly int[] ReassertAfterFocusMs = { 250, 900, 2500 };

    private static readonly Color Ember = Color.FromRgb(0xFF, 0x7A, 0x1A);
    private static readonly Color Muted = Color.FromRgb(0x8A, 0x93, 0xA3);
    private static readonly Color SuccessColor = Color.FromRgb(0x3D, 0xD6, 0x8C);
    private static readonly Color WarningColor = Color.FromRgb(0xFF, 0xB5, 0x47);
    private static readonly Color DangerColor = Color.FromRgb(0xFF, 0x5A, 0x5F);
    private static readonly Color InfoColor = Color.FromRgb(0x4C, 0xC3, 0xFF);

    private readonly ColorSettings _settings;
    private readonly DisplayGamma _displayGamma;
    private readonly NvidiaColorControl _nvidiaColor;
    private readonly WindowsColorMatrix _colorMatrix = new();
    private readonly TrayIcon? _tray;
    private readonly bool _testMode;
    private readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _saveDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _liveApplyTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _saveFlashTimer = new() { Interval = TimeSpan.FromMilliseconds(1600) };
    private readonly ObservableCollection<GameEntry> _games = new();
    private readonly List<ProfileSlider> _sliders = new();
    private readonly ushort[] _rampRed = new ushort[256];
    private readonly ushort[] _rampGreen = new ushort[256];
    private readonly ushort[] _rampBlue = new ushort[256];

    private DateTime _previewEndsAt;
    private DateTime _lastPreviewToggle = DateTime.MinValue;
    private bool _gameHasFocus;
    /// <summary>Oyun odakta ya da ekranda görünür: profil bu süre boyunca oyuna aittir.</summary>
    private bool _gameProfileOn;
    private DateTime? _restorePendingSince;
    private DateTime _focusGainedAt;
    private int _reassertStage = ReassertAfterFocusMs.Length;
    private ForegroundHook? _foregroundHook;
    private readonly PerformanceGuard? _performance;
    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    // Pencere tepsideyken birkaç dakikada bir çalışma kümesi kırpılır (Görev Yöneticisi'ndeki bellek).
    private readonly DispatcherTimer _trimTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private PageContext? _pageContext;
    private bool _profileApplied;
    private bool _previewActive;
    private bool _previewTransition;
    private bool _reallyExit;
    private bool _trayHintShown;
    private bool _windowsLayerActive;
    private bool _matrixLayerActive;
    private int _verifyCounter;
    private DateTime _lastReapplyLog = DateTime.MinValue;
    private HeroState? _heroState;

    internal bool StartsHidden { get; }

    internal MainWindow(bool testMode, ColorSettings? initialSettings, bool forceHidden = false)
    {
        _testMode = testMode;
        _settings = initialSettings ?? ColorSettings.Load();
        // Önceki oturum profil ekrandayken kapandıysa (zorla kapatma, güncelleme) açılışta
        // yakalanan renkler "orijinal" sayılmaz; nötr/varsayılan değerlere dönülür.
        bool leftoverSuspected = !testMode && SessionMarker.WasLeftActive;
        _displayGamma = new DisplayGamma(_settings, leftoverSuspected);
        _nvidiaColor = new NvidiaColorControl(_settings.DigitalVibrance, _settings.Hue, leftoverSuspected);

        Loc.Init(_settings.Language);
        InitializeComponent();
        FitToWorkArea();
        BuildLanguagePicker();

        // FPS Boost ayarları; test/çizim modunda sisteme hiçbir şey uygulanmaz.
        var catalog = new TweakCatalog(_settings);
        _performance = new PerformanceGuard(_settings, catalog, () => SaveQuietly());
        _pageContext = new PageContext(_settings, _performance, () => SaveQuietly(), (text, error) => ShowToast(text, error), testMode);
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        _trimTimer.Tick += (_, _) => { if (!IsVisible) TrimMemory(); };

        BuildSliders();
        GameItems.ItemsSource = _games;
        // Satır şablonundaki ipuçları satırlar oluşunca çevrilir.
        GameItems.ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (GameItems.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Loc.Translate(GameItems)));
        };
        RefreshGameList();
        BuildStaticStrips();
        UpdateVisualPreview();
        UpdateHardwareChips();

        KeepVisibleToggle.IsChecked = _settings.KeepWhileVisible;
        KeepVisibleToggle.Checked += KeepVisible_Changed;
        KeepVisibleToggle.Unchecked += KeepVisible_Changed;
        UpdateColorSubtitle();
        StartWithWindowsToggle.IsChecked = _settings.StartWithWindows;
        StartMinimizedToggle.IsChecked = _settings.StartMinimized;
        StartWithWindowsToggle.Checked += StartWithWindows_Changed;
        StartWithWindowsToggle.Unchecked += StartWithWindows_Changed;
        StartMinimizedToggle.Checked += StartMinimized_Changed;
        StartMinimizedToggle.Unchecked += StartMinimized_Changed;

        _watchTimer.Tick += (_, _) =>
        {
            CheckGameState();
            ReassertAfterFocus();
            VerifyAppliedProfile();
        };
        _previewTimer.Tick += (_, _) => PreviewTick();
        _saveDebounceTimer.Tick += (_, _) =>
        {
            _saveDebounceTimer.Stop();
            SaveQuietly();
        };
        _liveApplyTimer.Tick += (_, _) =>
        {
            _liveApplyTimer.Stop();
            LiveApplyDuringPreview();
        };
        _saveFlashTimer.Tick += (_, _) =>
        {
            _saveFlashTimer.Stop();
            SaveGlyph.Text = "";
            Loc.SetText(SaveText, "Profili kaydet");
        };

        SourceInitialized += (_, _) => ApplyWindowAttributes();
        StateChanged += (_, _) =>
        {
            UpdateMaximizeState();
            // Simge durumuna küçültülünce de kullanılmayan bellek geri verilir.
            if (WindowState == WindowState.Minimized && !_testMode)
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(TrimMemory));
        };
        PreviewKeyDown += OnWindowPreviewKeyDown;

        SetStatus(ReadyText(), Tone.Neutral);
        SetHero(HeroState.Idle, "Beklemede", IdleDetail(), -1);
        Loc.Translate(this);

        if (!testMode)
        {
            _tray = new TrayIcon();
            _tray.OpenRequested += (_, _) => ShowFromTray();
            _tray.RestoreRequested += (_, _) =>
            {
                RestoreColors();
                SetStatus("Renkler elle geri yüklendi", Tone.Neutral);
            };
            _tray.ExitRequested += (_, _) => ExitApplication();
            Application.Current.SessionEnding += OnSessionEnding;

            if (_settings.StartWithWindows)
            {
                // Program taşındıysa başlangıç kaydındaki yolu güncel tut.
                try { StartupManager.SetEnabled(true); }
                catch (Exception ex) { AppLogger.Write("Başlangıç kaydı güncellenemedi", ex); }
            }

            // Önceki bir oturumdan kalmış olabilecek renkleri temizle; oyun odakta değilken ekran nötr olmalı.
            RestoreColors();
            _performance.Start();
            _watchTimer.Start();
            _foregroundHook = new ForegroundHook(CheckGameState);
            CheckGameState();
        }

        StartsHidden = !testMode && (_settings.StartMinimized || forceHidden);
        if (StartsHidden)
        {
            _trimTimer.Start();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(TrimMemory));
        }
    }

    // ------------------------------------------------------------------ Sayfalar

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string page }) ShowPage(page);
    }

    internal void ShowPage(string page)
    {
        if (_pageContext is null) return;
        // Sayfa koddan açıldığında da (tepsi, test) üstteki sekme vurgusu eşleşsin.
        foreach (RadioButton tab in NavBar.Children.OfType<RadioButton>())
            if (tab.Tag as string == page && tab.IsChecked != true) tab.IsChecked = true;
        ColorPage.Visibility = page == "color" ? Visibility.Visible : Visibility.Collapsed;
        if (page != "color" && !_pages.ContainsKey(page))
        {
            // Sayfalar ilk açılışta kurulur: açılış hızlı kalır, kullanılmayan sayfa sistemi taramaz.
            FrameworkElement created = page switch
            {
                "boost" => new BoostPage(_pageContext),
                "background" => new BackgroundPage(_pageContext),
                "pro" => new ProPage(_pageContext),
                _ => new SystemPage(_pageContext)
            };
            created.Visibility = Visibility.Collapsed;
            _pages[page] = created;
            PageHost.Children.Add(created);
        }
        // Yalnız açık sayfa bellekte kalır: diğerleri (özellikle fotoğraflı Pro sayfası) bırakılır, dönünce yeniden kurulur.
        foreach (string key in _pages.Keys.Where(k => k != page).ToList())
        {
            PageHost.Children.Remove(_pages[key]);
            _pages.Remove(key);
            if (key == "pro") ImageCache.ReleaseMemory();
        }
        foreach ((string key, FrameworkElement element) in _pages)
            element.Visibility = key == page ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Tepsiye inince açılmış sayfaları ve indirilmiş görselleri bırakır; tekrar açılınca yeniden kurulur.</summary>
    private void ReleasePages()
    {
        if (_pages.Count == 0) return;
        NavColor.IsChecked = true;
        PageHost.Children.Clear();
        _pages.Clear();
        ImageCache.ReleaseMemory();
    }

    /// <summary>
    /// Kullanılmayan belleği geri verir: çöp toplama + çalışma kümesini kırpma. Windows sayfaları gerektiğinde
    /// geri getirir; oyunu ve renk takibini etkilemez, Görev Yöneticisi'ndeki bellek düşer.
    /// </summary>
    private static void TrimMemory()
    {
        try
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch (Exception ex)
        {
            AppLogger.Write("Bellek kırpılamadı", ex);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

    private void ShowToast(string text, bool error)
    {
        Loc.SetText(ToastText, text);
        ToastDot.Fill = Solid(error ? DangerColor : SuccessColor);
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(error || text.Length > 70 ? 8 : 5);
        _toastTimer.Start();
        _tray?.SetText(Loc.T(text));
    }

    private void KeepVisible_Changed(object sender, RoutedEventArgs e)
    {
        _settings.KeepWhileVisible = KeepVisibleToggle.IsChecked == true;
        SaveQuietly();
        UpdateColorSubtitle();
        CheckGameState();
        SetStatus(_settings.KeepWhileVisible
            ? "Alt+Tab'da profil korunacak: oyun ekranda göründükçe renkler açık kalır"
            : "Profil yalnızca oyun odaktayken uygulanacak", Tone.Neutral);
    }

    private void UpdateColorSubtitle() => Loc.SetText(ColorPageSubtitle, _settings.KeepWhileVisible
        ? "İzlenen oyun açıkken ve ekranda görünürken uygulanır; oyunu küçültünce ya da başka bir pencere örtünce eski haline döner."
        : "İzlenen oyun odaktayken uygulanır; Alt+Tab yaptığınızda ekranınız eski haline döner.");

    // ------------------------------------------------------------------ Dil

    private void BuildLanguagePicker()
    {
        foreach ((string code, string name) in Loc.Languages)
        {
            var item = new RadioButton
            {
                Style = (Style)FindResource("FilterTab"),
                GroupName = "Language",
                Content = name,
                Tag = code,
                IsChecked = code == Loc.Language
            };
            AutomationProperties.SetName(item, name);
            item.Checked += Language_Checked;
            LanguagePanel.Children.Add(item);
        }
        LanguageButtonText.Text = Loc.Languages.First(l => l.Code == Loc.Language).Name;
    }

    private void LanguageButton_Click(object sender, RoutedEventArgs e) => LanguagePopup.IsOpen = !LanguagePopup.IsOpen;

    /// <summary>Dili yeniden başlatmadan değiştirir: pencere yeniden çevrilir, açık sayfa yeni dilde kurulur.</summary>
    private void Language_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string code } || code == Loc.Language) return;
        LanguagePopup.IsOpen = false;
        LanguageButtonText.Text = Loc.Languages.First(l => l.Code == code).Name;
        _settings.Language = code;
        SaveQuietly();
        Loc.Set(code);
        Loc.Translate(this);
        _tray?.Retranslate();
        RefreshGameList();
        CheckGameState();
        Curve.InvalidateVisual();
        string? openPage = _pages.Keys.FirstOrDefault();
        if (openPage is not null)
        {
            PageHost.Children.Clear();
            _pages.Clear();
            ShowPage(openPage);
        }
        SetStatus("Dil değiştirildi", Tone.Neutral);
    }

    // ------------------------------------------------------------------ Kurulum

    private void BuildSliders()
    {
        AddSlider(BasicSliders, "Aydınlık", 0, 100, _settings.Brightness, 50, Rgb(0xFF, 0xB0, 0x66),
            v => _settings.Brightness = v);
        AddSlider(BasicSliders, "Kontrast", 0, 100, _settings.Contrast, 50, Rgb(0xFF, 0x8A, 0x2A),
            v => _settings.Contrast = v);
        AddSlider(BasicSliders, "Gama", 50, 200, _settings.Gamma, 100, Rgb(0xFF, 0x6A, 0x3D),
            v => _settings.Gamma = v, divisor: 100, decimals: 2);

        AddSlider(VibranceSliders, "Digital Vibrance", 0, 100, _settings.DigitalVibrance, 50, SuccessColor,
            v => _settings.DigitalVibrance = v, unit: "%");
        AddSlider(VibranceSliders, "Renk tonu", 0, 360, _settings.Hue, 0, InfoColor,
            v => _settings.Hue = v, unit: "°", kind: TrackKind.Hue);

        AddSlider(RgbSliders, "Kırmızı", 50, 150, _settings.Red, 100, Rgb(0xFF, 0x5A, 0x5F),
            v => _settings.Red = v, unit: "%");
        AddSlider(RgbSliders, "Yeşil", 50, 150, _settings.Green, 100, Rgb(0x3D, 0xD6, 0x8C),
            v => _settings.Green = v, unit: "%");
        AddSlider(RgbSliders, "Mavi", 50, 150, _settings.Blue, 100, Rgb(0x4C, 0x9B, 0xFF),
            v => _settings.Blue = v, unit: "%");
    }

    private void AddSlider(Panel host, string caption, int min, int max, int value, int neutral, Color accent,
        Action<int> setter, string unit = "", int divisor = 1, int decimals = 0, TrackKind kind = TrackKind.Standard)
    {
        var slider = new ProfileSlider(caption, min, max, value, neutral, accent, unit, divisor, decimals, kind);
        slider.ValueChanged += (_, _) =>
        {
            setter(slider.Value);
            OnProfileEdited();
        };
        _sliders.Add(slider);
        host.Children.Add(slider);
    }

    private void FitToWorkArea()
    {
        Rect area = SystemParameters.WorkArea;
        if (MinHeight > area.Height - 16) MinHeight = Math.Max(480, area.Height - 16);
        if (MinWidth > area.Width - 16) MinWidth = Math.Max(760, area.Width - 16);
        if (Height > area.Height - 24) Height = Math.Max(MinHeight, area.Height - 24);
        if (Width > area.Width - 24) Width = Math.Max(MinWidth, area.Width - 24);
    }

    private void UpdateHardwareChips()
    {
        Loc.SetText(DisplayChipText, $"{Math.Max(1, _displayGamma.DisplayCount)} ekran");
        // Test/çizim modunda Büyüteç API'si başlatılmaz; motor hazır gösterilir.
        if (_testMode || _colorMatrix.IsAvailable)
        {
            NvidiaChipDot.Fill = Solid(SuccessColor);
            Loc.SetText(NvidiaChipText, "Renk motoru hazır");
            Loc.SetText(NvidiaLayerText, "WINDOWS RENK MOTORU");
            NvidiaLayerText.Foreground = (Brush)FindResource("Text2Brush");
            Loc.SetText(VibranceSubText, "Digital Vibrance ve renk tonu");
        }
        else
        {
            NvidiaChipDot.Fill = Solid(WarningColor);
            Loc.SetText(NvidiaChipText, "Renk motoru yok");
            Loc.SetText(NvidiaLayerText, "RENK MOTORU YOK");
            NvidiaLayerText.Foreground = Solid(WarningColor);
            Loc.SetText(VibranceSubText, "Windows renk motoru başlatılamadı; bu ayarlar uygulanamaz");
        }
    }

    // ------------------------------------------------------------------ Oyun listesi

    private void RefreshGameList()
    {
        _games.Clear();
        foreach (string executable in _settings.GameExecutables)
        {
            var entry = new GameEntry(executable);
            // Resmi ikon: kurulu exe'den hemen; oyun kurulu değilse resmi siteden indirilip gelince gösterilir.
            ImageSource? icon = GameIcons.Resolve(executable, _settings.GamePaths, 26, out var pending);
            if (icon is not null) entry.SetIcon(icon);
            else pending?.ContinueWith(t =>
            {
                if (t.Result is ImageSource image) Dispatcher.BeginInvoke(() => entry.SetIcon(image));
            }, System.Threading.Tasks.TaskScheduler.Default);
            _games.Add(entry);
        }
        GamesCountText.Text = _games.Count.ToString();
        GamesEmptyHint.Visibility = _games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Oyunun çalıştırılabilir dosyasını seçin"),
            Filter = Loc.T("Oyun uygulaması (*.exe)|*.exe"),
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        string name = GameProcessMonitor.FriendlyName(Path.GetFileName(dialog.FileName));
        if (AddGameExecutable(dialog.FileName))
            SetStatus($"{name} izleme listesine eklendi", Tone.Info);
        else
            SetStatus($"{name} zaten izleme listesinde", Tone.Neutral);
    }

    private bool AddGameExecutable(string path)
    {
        string executable = ColorSettings.NormalizeExecutableName(path);
        if (string.IsNullOrWhiteSpace(executable) ||
            _settings.GameExecutables.Contains(executable, StringComparer.OrdinalIgnoreCase)) return false;

        _settings.GameExecutables.Add(executable);
        if (File.Exists(path)) _settings.GamePaths[executable] = path;   // ikon ve GPU tercihi için
        _settings.Normalize();
        RefreshGameList();
        SaveQuietly();
        CheckGameState();
        return true;
    }

    private void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameEntry entry) return;
        _settings.GameExecutables.RemoveAll(name => name.Equals(entry.Executable, StringComparison.OrdinalIgnoreCase));
        RefreshGameList();
        SaveQuietly();
        CheckGameState();
        SetStatus($"{entry.DisplayName} izleme listesinden çıkarıldı", Tone.Neutral);
    }

    // ------------------------------------------------------------------ Oyun durumu

    private void CheckGameState()
    {
        if (_testMode)
        {
            UpdateGameState(Array.Empty<string>(), null);
            return;
        }

        IReadOnlyList<string> runningGames = GameProcessMonitor.FindRunningGames(_settings.GameExecutables);
        ForegroundWindow? foreground = GameProcessMonitor.Foreground();
        string? foregroundGame = GameProcessMonitor.MatchGame(foreground, _settings.GameExecutables);

        // Odak oyunda değilse: "görünürken koru" açıksa ya da odak yalnızca Alt+Tab menüsü gibi geçici bir kabuk
        // penceresindeyse, oyun ekranda görünür kaldığı sürece profil oyuna ait sayılır.
        string? visibleGame = null;
        if (foregroundGame is null && runningGames.Count > 0 &&
            (_settings.KeepWhileVisible || GameProcessMonitor.IsTransientShellWindow(foreground)))
        {
            visibleGame = runningGames.FirstOrDefault(game => GameProcessMonitor.IsGameVisible(game, foreground));
        }
        LearnGamePaths(runningGames);
        UpdateGameState(runningGames, foregroundGame, visibleGame, foreground);
        // Oyun yokken saniyede bir yoklamak yeterli (ön plan değişimi zaten kancayla anında gelir); süreç taraması az tutulur.
        TimeSpan interval = TimeSpan.FromMilliseconds(runningGames.Count > 0 ? 250 : 1000);
        if (_watchTimer.Interval != interval) _watchTimer.Interval = interval;
    }

    private void UpdateGameState(IReadOnlyList<string> runningGames, string? foregroundGame,
        string? visibleGame = null, ForegroundWindow? foreground = null)
    {
        bool running = runningGames.Count > 0;
        bool hasFocus = !string.IsNullOrWhiteSpace(foregroundGame);
        string? profileGame = foregroundGame ?? visibleGame;
        string gameNames = string.Join(", ", runningGames.Select(GameProcessMonitor.FriendlyName));

        foreach (GameEntry entry in _games)
        {
            GameRunState state =
                hasFocus && entry.Executable.Equals(foregroundGame, StringComparison.OrdinalIgnoreCase) ? GameRunState.Focused :
                runningGames.Contains(entry.Executable, StringComparer.OrdinalIgnoreCase) ? GameRunState.Running :
                GameRunState.Closed;
            entry.SetState(state);
        }

        if (profileGame is not null)
        {
            _restorePendingSince = null;
            string name = GameProcessMonitor.FriendlyName(profileGame);
            if (!_gameProfileOn)
            {
                _gameProfileOn = true;
                StopPreviewState();
                bool applied = ApplyProfile(out string? message);
                _profileApplied = applied;
                LogEvent($"{name} {(hasFocus ? "odağa geldi" : "ekranda görünür")}: profil {(applied ? "uygulandı" : "uygulanamadı")}" +
                    (message is null ? "" : $" ({message})") + LayerSummary());
                if (applied)
                    SetStatus(message ?? $"{name} — oyun profili etkin", message is null ? Tone.Success : Tone.Warning);
                else
                {
                    SetHero(HeroState.Error, "Uygulanamadı", message ?? "Profil uygulanamadı.", -1);
                    SetStatus(message ?? "Profil uygulanamadı", Tone.Error);
                }
            }
            else if (hasFocus != _gameHasFocus)
            {
                LogEvent(hasFocus
                    ? $"{name} yeniden odakta: profil zaten etkindi"
                    : $"Odak {Describe(foreground)} penceresine geçti; {name} ekranda görünür, profil korunuyor");
            }

            if (hasFocus && !_gameHasFocus)
            {
                _focusGainedAt = DateTime.UtcNow;
                _reassertStage = 0;
            }
            if (_profileApplied)
            {
                SetHero(HeroState.Active, "Profil etkin", hasFocus
                    ? $"{name} odakta. " + (_settings.KeepWhileVisible
                        ? "Oyun ekranda göründükçe profil korunur; küçültünce ya da başka pencere örtünce geri alınır."
                        : "Alt+Tab yaptığınızda renkler geri alınır.")
                    : $"{name} arka planda ama ekranda görünür; profil korunuyor.", -1);
            }
        }
        else if (_gameProfileOn)
        {
            // Oyun gizlendi (küçültüldü, örtüldü, kapandı ya da odaktan çıktı): kısa beklemeden sonra geri al.
            _restorePendingSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - _restorePendingSince.Value >= RestoreDelay || !running)
            {
                _gameProfileOn = false;
                _restorePendingSince = null;
                RestoreColors();
                LogEvent((running
                    ? $"Oyun gizlendi (odak: {Describe(foreground)}): renkler geri alındı"
                    : "Oyun kapandı: renkler geri alındı") + LayerSummary());
                if (running)
                {
                    SetHero(HeroState.Background, "Arka planda", $"{gameNames} çalışıyor. Oyun gizli olduğu için renkler kapalı.", -1);
                    SetStatus($"{gameNames} arka planda — ekran renkleri geri yüklendi", Tone.Neutral);
                }
                else
                {
                    SetHero(HeroState.Idle, "Beklemede", IdleDetail(), -1);
                    SetStatus("Oyun kapandı — önceki ekran renkleri geri yüklendi", Tone.Neutral);
                }
            }
        }
        else if (!_previewActive)
        {
            if (running)
                SetHero(HeroState.Background, "Arka planda", $"{gameNames} çalışıyor. Oyun gizli olduğu için renkler kapalı.", -1);
            else if (_heroState != HeroState.Error)
                SetHero(HeroState.Idle, "Beklemede", IdleDetail(), -1);
        }
        _gameHasFocus = hasFocus;
        // Çizim/test modu Windows ayarlarına asla dokunmaz.
        if (!_testMode) _performance?.Update(running, hasFocus);
    }

    private static string Describe(ForegroundWindow? window) =>
        window is null ? "bilinmeyen" : $"{(window.Executable.Length > 0 ? window.Executable : "pid " + window.ProcessId)} [{window.ClassName}]";

    /// <summary>Çalışan oyunların tam yolunu öğrenir (GPU tercihi ve oyun simgesi için).</summary>
    private void LearnGamePaths(IReadOnlyList<string> runningGames)
    {
        foreach (string game in runningGames)
        {
            if (_settings.GamePaths.ContainsKey(game)) continue;
            string? path = SystemProfile.ProcessPath(game);
            if (string.IsNullOrEmpty(path)) continue;
            _settings.GamePaths[game] = path;
            LogEvent($"{GameProcessMonitor.FriendlyName(game)} yolu öğrenildi: {path}");
            SaveQuietly();
            _performance?.GamesChanged();
        }
    }

    /// <summary>Oyuna dönüşten sonra profili birkaç kez yeniden yazar (tam ekran geçişinde sürücü sıfırlamasına karşı).</summary>
    private void ReassertAfterFocus()
    {
        if (_testMode || !_profileApplied || !_gameHasFocus || _reassertStage >= ReassertAfterFocusMs.Length) return;
        if ((DateTime.UtcNow - _focusGainedAt).TotalMilliseconds < ReassertAfterFocusMs[_reassertStage]) return;
        _reassertStage++;
        try
        {
            if (_windowsLayerActive) _displayGamma.Apply(GammaOnlySettings(), out _);
            if (_matrixLayerActive) _colorMatrix.Apply(_settings, out _);
        }
        catch (Exception ex)
        {
            AppLogger.Write("Profil yeniden yazılamadı", ex);
        }
    }

    // ------------------------------------------------------------------ Önizleme

    private void Preview_Click(object sender, RoutedEventArgs e) => TogglePreview();

    private void TogglePreview()
    {
        if (_previewTransition) return;
        // Aynı düğme hem başlatır hem bitirir: çift tık ya da odaktaki düğmede Boşluk/Enter tekrarı
        // önizlemeyi yarım saniyede kapatıyordu. Son tıklamadan hemen sonraki tıklamalar yok sayılır.
        if ((DateTime.UtcNow - _lastPreviewToggle).TotalMilliseconds < PreviewClickGuardMs)
        {
            LogEvent("Önizleme: art arda tıklama yok sayıldı");
            return;
        }
        _lastPreviewToggle = DateTime.UtcNow;
        if (_previewActive)
        {
            EndPreview();
            return;
        }

        _previewTransition = true;
        PreviewButton.IsEnabled = false;
        try
        {
            if (_gameProfileOn)
            {
                if (ApplyProfile(out string? gameMessage))
                    SetStatus(gameMessage ?? "Yeni ayarlar çalışan oyuna uygulandı",
                        gameMessage is null ? Tone.Success : Tone.Warning);
                else
                    SetStatus(gameMessage ?? "Profil uygulanamadı", Tone.Error);
                return;
            }

            bool applied = ApplyProfile(out string? message);
            LogEvent($"Önizleme başlatıldı: profil {(applied ? "uygulandı" : "uygulanamadı")}" +
                (message is null ? "" : $" ({message})") + LayerSummary());
            if (applied)
            {
                _previewActive = true;
                _previewEndsAt = DateTime.UtcNow.AddSeconds(PreviewSeconds);
                _previewTimer.Start();
                UpdatePreviewUi();
                SetStatus(message is null
                    ? "Önizleme etkin — kaydırıcılardaki değişiklikler canlı uygulanır"
                    : "Önizleme etkin — " + message, Tone.Warning);
            }
            else
            {
                SetHero(HeroState.Error, "Uygulanamadı", message ?? "Önizleme başlatılamadı.", -1);
                SetStatus(message ?? "Önizleme başlatılamadı", Tone.Error);
            }
        }
        catch (Exception ex)
        {
            HandlePreviewFailure("Önizleme başlatılırken hata", ex);
        }
        finally
        {
            PreviewButton.IsEnabled = true;
            _previewTransition = false;
        }
    }

    private void PreviewTick()
    {
        if (!_previewActive)
        {
            _previewTimer.Stop();
            return;
        }
        if (DateTime.UtcNow >= _previewEndsAt)
        {
            EndPreview();
            return;
        }
        UpdatePreviewUi();
    }

    private void UpdatePreviewUi()
    {
        double remaining = Math.Max(0, (_previewEndsAt - DateTime.UtcNow).TotalSeconds);
        int seconds = (int)Math.Ceiling(remaining);
        PreviewGlyph.Text = "";
        Loc.SetText(PreviewText, $"Önizlemeyi bitir · {seconds}");
        SetHero(HeroState.Preview, "Önizleme",
            $"Profil şu an ekranınızda. {seconds} sn sonra eski haline döner.", remaining / PreviewSeconds);
    }

    private void EndPreview()
    {
        if (_previewTransition) return;
        _previewTransition = true;
        PreviewButton.IsEnabled = false;
        try
        {
            bool wasActive = _previewActive;
            StopPreviewState();
            if (!_gameProfileOn)
            {
                RestoreColors();
                LogEvent("Önizleme bitti: renkler geri alındı" + LayerSummary());
                if (wasActive) SetStatus("Önizleme bitti — ekran renkleri geri yüklendi", Tone.Neutral);
            }
            SetHero(HeroState.Idle, "Beklemede", IdleDetail(), -1);
            CheckGameState();
        }
        catch (Exception ex)
        {
            HandlePreviewFailure("Önizleme bitirilirken hata", ex);
        }
        finally
        {
            PreviewButton.IsEnabled = true;
            _previewTransition = false;
        }
    }

    private void StopPreviewState()
    {
        _previewTimer.Stop();
        _liveApplyTimer.Stop();
        _previewActive = false;
        PreviewGlyph.Text = "";
        Loc.SetText(PreviewText, "10 sn önizle");
    }

    private void LiveApplyDuringPreview()
    {
        if (!_previewActive && !_gameProfileOn) return;
        try
        {
            if (!ApplyProfile(out string? message))
                SetStatus(message ?? "Önizleme güncellenemedi", Tone.Error);
        }
        catch (Exception ex)
        {
            HandlePreviewFailure("Canlı önizleme hatası", ex);
        }
    }

    private void HandlePreviewFailure(string context, Exception exception)
    {
        StopPreviewState();
        AppLogger.Write(context, exception);
        RestoreColors();
        SetHero(HeroState.Error, "Durduruldu", "Önizleme güvenli biçimde durduruldu. Ayrıntılar error.log dosyasında.", -1);
        SetStatus("Önizleme güvenli biçimde durduruldu — ayrıntı error.log dosyasında", Tone.Error);
    }

    // ------------------------------------------------------------------ Renk uygulama

    private void RestoreColors()
    {
        StopPreviewState();
        _profileApplied = false;
        _windowsLayerActive = false;
        _matrixLayerActive = false;
        if (_testMode) return;
        try { _colorMatrix.Restore(); }
        catch (Exception ex) { AppLogger.Write("Windows renk motoru geri yüklenemedi", ex); }
        try { _nvidiaColor.Restore(); }
        catch (Exception ex) { AppLogger.Write("NVIDIA renkleri geri yüklenemedi", ex); }
        try { _displayGamma.Restore(); }
        catch (Exception ex) { AppLogger.Write("Windows gama tablosu geri yüklenemedi", ex); }
        SessionMarker.SetActive(false);
    }

    private bool ApplyProfile(out string? message)
    {
        if (_testMode)
        {
            message = null;
            return true;
        }

        // Gama (eğri) Windows gama rampasıyla, geri kalan her şey (aydınlık, kontrast, RGB, canlılık, ton)
        // Windows renk matrisiyle uygulanır. NVIDIA DVC kullanılmaz: hibrit grafikli dizüstünde harici monitörde
        // sürücü DVC'yi ve gama rampasını kabul edip ekrana yansıtmıyordu (kullanıcıyla gözle doğrulandı,
        // 2026-09-24); renk matrisi ise her denemede göründü. DVC her geri yüklemede nötrde tutulur.
        bool gammaRequested = _settings.Gamma != 100;
        bool gammaApplied = !gammaRequested;
        string? gammaError = null;
        if (gammaRequested)
        {
            ColorSettings gammaOnly = GammaOnlySettings();
            gammaApplied = _displayGamma.Apply(gammaOnly, out gammaError);
            if (!gammaApplied)
            {
                Thread.Sleep(60);
                gammaApplied = _displayGamma.Apply(gammaOnly, out gammaError);
            }
        }
        else
        {
            // Nötr gamaya dönüldüyse rampayı başlangıç değerine çek.
            try { _displayGamma.Restore(); }
            catch (Exception ex) { AppLogger.Write("Windows gama tablosu geri yüklenemedi", ex); }
        }

        bool matrixRequested = !WindowsColorMatrix.IsNeutral(_settings);
        bool matrixApplied = !matrixRequested;
        string? matrixError = null;
        if (matrixRequested)
        {
            matrixApplied = _colorMatrix.Apply(_settings, out matrixError);
            if (!matrixApplied)
            {
                Thread.Sleep(60);
                matrixApplied = _colorMatrix.Apply(_settings, out matrixError);
            }
        }
        else
        {
            try { _colorMatrix.Restore(); }
            catch (Exception ex) { AppLogger.Write("Windows renk motoru geri yüklenemedi", ex); }
        }

        _windowsLayerActive = gammaRequested && gammaApplied;
        _matrixLayerActive = matrixRequested && matrixApplied;
        SessionMarker.SetActive(_windowsLayerActive || _matrixLayerActive);

        bool anythingRequested = gammaRequested || matrixRequested;
        bool anythingApplied = _windowsLayerActive || _matrixLayerActive;
        if (anythingRequested && !anythingApplied)
        {
            message = string.Join(" ", new[] { gammaError, matrixError }.Where(value => !string.IsNullOrWhiteSpace(value)));
            RestoreColors();
            AppLogger.Write("Profil uygulanamadı", message);
            return false;
        }

        if (gammaRequested && !gammaApplied)
        {
            message = "Renkler uygulandı • gama HDR/sürücü nedeniyle atlandı";
            AppLogger.Write("Profil kısmen uygulandı", gammaError ?? "Gama katmanı atlandı");
            return true;
        }
        if (matrixRequested && !matrixApplied)
        {
            message = "Gama uygulandı • Windows renk motoru kullanılamadı";
            AppLogger.Write("Profil kısmen uygulandı", matrixError ?? "Windows renk motoru atlandı");
            return true;
        }

        message = null;
        return true;
    }

    /// <summary>Gama rampasına yalnız gama gider; aydınlık/kontrast/RGB renk matrisinde, iki kez uygulanmasın.</summary>
    private ColorSettings GammaOnlySettings() => new() { Gamma = _settings.Gamma };

    /// <summary>
    /// Profil etkinken (oyun odakta veya önizleme) yarım saniyede bir ekrandaki değerleri denetler;
    /// Windows, sürücü ya da oyun renkleri sıfırladıysa profili sessizce yeniden uygular.
    /// </summary>
    private void VerifyAppliedProfile()
    {
        if (_testMode || !(_profileApplied || _previewActive)) return;
        if (!_windowsLayerActive && !_matrixLayerActive) return;
        if (++_verifyCounter % 2 != 0) return;

        try
        {
            bool gammaOk = !_windowsLayerActive || _displayGamma.StillApplied();
            bool matrixOk = !_matrixLayerActive || _colorMatrix.StillApplied();
            if (gammaOk && matrixOk) return;

            if (!gammaOk) _displayGamma.Apply(GammaOnlySettings(), out _);
            if (!matrixOk) _colorMatrix.Apply(_settings, out _);

            if ((DateTime.UtcNow - _lastReapplyLog).TotalSeconds >= 5)
            {
                _lastReapplyLog = DateTime.UtcNow;
                string layers = string.Join(" ve ", new[] { gammaOk ? null : "gama rampası", matrixOk ? null : "Windows renk motoru" }
                    .Where(v => v is not null));
                LogEvent($"Profil yeniden uygulandı: {layers} katmanı dışarıdan sıfırlanmıştı" + LayerSummary());
            }
        }
        catch (Exception ex)
        {
            AppLogger.Write("Profil denetimi başarısız", ex);
        }
    }

    private void LogEvent(string message)
    {
        if (!_testMode) AppLogger.Info(message);
    }

    /// <summary>Olay kaydı için ekrandaki gerçek değerler (gama orta nokta, renk matrisi R→R, NVIDIA DVC/ton).</summary>
    private string LayerSummary()
    {
        if (_testMode) return "";
        try
        {
            return $" [ekran: gama128={_displayGamma.DiagnosticSample()}, matris={_colorMatrix.DiagnosticSample()}, " +
                $"NVIDIA={_nvidiaColor.DiagnosticSample()}]";
        }
        catch { return ""; }
    }

    // ------------------------------------------------------------------ Profil düzenleme / kayıt

    private void OnProfileEdited()
    {
        UpdateVisualPreview();
        ScheduleAutoSave();
        // Önizlemede ya da oyun profili ekrandayken kaydırıcılar canlı uygulanır.
        if (_previewActive) _previewEndsAt = DateTime.UtcNow.AddSeconds(PreviewSeconds);
        if (_previewActive || _gameProfileOn)
        {
            _liveApplyTimer.Stop();
            _liveApplyTimer.Start();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveSettings(showMessage: true);

    private void SaveSettings(bool showMessage)
    {
        _saveDebounceTimer.Stop();
        if (!SaveQuietly()) return;
        if (_gameProfileOn) _profileApplied = ApplyProfile(out _);
        if (showMessage)
        {
            SetStatus("Profil kaydedildi", Tone.Success);
            SaveGlyph.Text = "";
            Loc.SetText(SaveText, "Kaydedildi");
            _saveFlashTimer.Stop();
            _saveFlashTimer.Start();
        }
    }

    private bool SaveQuietly()
    {
        _saveDebounceTimer.Stop();
        if (_testMode) return true;
        try
        {
            _settings.Save();
            Loc.SetText(AutoSaveText, $"Kaydedildi · {DateTime.Now:HH:mm}");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Write("Ayarlar kaydedilemedi", ex);
            SetStatus("Ayarlar kaydedilemedi — ayrıntı error.log dosyasında", Tone.Error);
            return false;
        }
    }

    private void ScheduleAutoSave()
    {
        if (_testMode) return;
        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Start();
    }

    /// <summary>Önerilen profil (DoGood Studio): aydınlık 55, kontrast 55, gama 1,10, canlılık 85; kullanıcı sonra değiştirebilir.</summary>
    private static readonly int[] RecommendedValues = { 55, 55, 110, 85, 0, 100, 100, 100 };

    private void Recommended_Click(object sender, RoutedEventArgs e)
    {
        for (int i = 0; i < _sliders.Count && i < RecommendedValues.Length; i++) _sliders[i].Value = RecommendedValues[i];
        if (_gameProfileOn) _profileApplied = ApplyProfile(out _);
        SetStatus("Önerilen renk ayarları uygulandı; istediğiniz gibi değiştirebilirsiniz", Tone.Success);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        foreach (ProfileSlider slider in _sliders) slider.Value = slider.NeutralValue;
        if (_gameProfileOn) _profileApplied = ApplyProfile(out _);
        SetStatus("Tüm değerler nötre döndürüldü", Tone.Neutral);
    }

    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        _settings.StartWithWindows = StartWithWindowsToggle.IsChecked == true;
        if (!_testMode)
        {
            try { StartupManager.SetEnabled(_settings.StartWithWindows); }
            catch (Exception ex)
            {
                AppLogger.Write("Başlangıç ayarı yazılamadı", ex);
                SetStatus("Başlangıç ayarı yazılamadı", Tone.Error);
                return;
            }
        }
        SaveQuietly();
        SetStatus(_settings.StartWithWindows ? "Windows ile başlatma açıldı" : "Windows ile başlatma kapatıldı", Tone.Neutral);
    }

    private void StartMinimized_Changed(object sender, RoutedEventArgs e)
    {
        _settings.StartMinimized = StartMinimizedToggle.IsChecked == true;
        SaveQuietly();
        SetStatus(_settings.StartMinimized ? "Program tepside başlayacak" : "Program pencereyle başlayacak", Tone.Neutral);
    }

    // ------------------------------------------------------------------ Görsel önizleme (eğri + şeritler)

    private void BuildStaticStrips()
    {
        GrayBefore.Background = Strip(x => ((byte)x, (byte)x, (byte)x));
        ColorBefore.Background = Strip(x =>
        {
            Color c = SliderTrack.FromHsv(x / 256.0 * 360.0, 0.72, 0.95);
            return (c.R, c.G, c.B);
        });
    }

    private void UpdateVisualPreview()
    {
        DisplayGamma.ComputeRamp(_settings, _rampRed, _rampGreen, _rampBlue);
        Curve.SetRamp(_rampRed, _rampGreen, _rampBlue);

        GrayAfter.Background = Strip(x => (Ramp(_rampRed, x), Ramp(_rampGreen, x), Ramp(_rampBlue, x)));

        // Windows renk matrisiyle aynı çarpan (50 nötr → 1, 100 → 2).
        double saturationScale = _settings.DigitalVibrance / 50.0;
        double hueShift = _settings.Hue;
        ColorAfter.Background = Strip(x =>
        {
            double saturation = Math.Clamp(0.72 * saturationScale, 0, 1);
            Color c = SliderTrack.FromHsv(x / 256.0 * 360.0 + hueShift, saturation, 0.95);
            return (Ramp(_rampRed, c.R), Ramp(_rampGreen, c.G), Ramp(_rampBlue, c.B));
        });
    }

    private static byte Ramp(ushort[] ramp, int index) => (byte)Math.Round(ramp[Math.Clamp(index, 0, 255)] / 65535.0 * 255);

    private static ImageBrush Strip(Func<int, (byte R, byte G, byte B)> pixel)
    {
        const int rows = 16;
        var bitmap = new WriteableBitmap(256, rows, 96, 96, PixelFormats.Bgr32, null);
        var pixels = new int[256 * rows];
        for (int x = 0; x < 256; x++)
        {
            (byte r, byte g, byte b) = pixel(x);
            int value = (r << 16) | (g << 8) | b;
            for (int y = 0; y < rows; y++) pixels[y * 256 + x] = value;
        }
        bitmap.WritePixels(new Int32Rect(0, 0, 256, rows), pixels, 256 * 4, 0);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    // ------------------------------------------------------------------ Durum göstergeleri

    private void SetStatus(string text, Tone tone)
    {
        Loc.SetText(FooterText, text);
        FooterDot.Fill = Solid(ToneColor(tone));
        _tray?.SetText(Loc.T(text));
    }

    private void SetHero(HeroState state, string title, string detail, double progress)
    {
        (Color color, string glyph, bool emphasis) = state switch
        {
            HeroState.Active => (Ember, "", true),
            HeroState.Background => (InfoColor, "", false),
            HeroState.Preview => (WarningColor, "", true),
            HeroState.Error => (DangerColor, "", true),
            _ => (Muted, "", false)
        };

        if (HeroTitle.Text != Loc.T(title)) Loc.SetText(HeroTitle, title);
        if (HeroDetail.Text != Loc.T(detail)) Loc.SetText(HeroDetail, detail);
        HeroRing.Set(color, glyph, progress, emphasis);

        if (_heroState == state) return;
        _heroState = state;

        HeroTitle.Foreground = state == HeroState.Idle ? (Brush)FindResource("TextBrush") : Solid(Lighten(color));
        var glow = new RadialGradientBrush
        {
            Center = new Point(1.0, 0.0),
            GradientOrigin = new Point(1.0, 0.0),
            RadiusX = 1.1,
            RadiusY = 1.3
        };
        byte alpha = state == HeroState.Idle ? (byte)0x0C : (byte)0x2A;
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, color.R, color.G, color.B), 0));
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
        glow.Freeze();
        HeroGlow.Fill = glow;

        HeroCard.BorderBrush = state == HeroState.Idle
            ? (Brush)FindResource("CardBorder")
            : Solid(Color.FromArgb(0x66, color.R, color.G, color.B));
    }

    private string IdleDetail() => _settings.GameExecutables.Count == 0
        ? "İzleme listesi boş. Profilin uygulanması için bir oyun ekleyin."
        : $"{_settings.GameExecutables.Count} oyun izleniyor. Biri odağa geldiğinde profil otomatik uygulanır.";

    private string ReadyText()
    {
        string engine = _testMode || _colorMatrix.IsAvailable ? "Windows renk motoru" : "renk motoru yok";
        return $"Hazır · {Math.Max(1, _displayGamma.DisplayCount)} ekran · {engine}";
    }

    private static Color ToneColor(Tone tone) => tone switch
    {
        Tone.Success => SuccessColor,
        Tone.Warning => WarningColor,
        Tone.Error => DangerColor,
        Tone.Info => InfoColor,
        _ => Muted
    };

    private static Color Lighten(Color c) => Color.FromRgb(
        (byte)Math.Min(255, c.R + (255 - c.R) * 0.25),
        (byte)Math.Min(255, c.G + (255 - c.G) * 0.25),
        (byte)Math.Min(255, c.B + (255 - c.B) * 0.25));

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ------------------------------------------------------------------ Pencere

    private void ApplyWindowAttributes()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        try
        {
            int dark = 1;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));          // DWMWA_USE_IMMERSIVE_DARK_MODE
            int round = 2;
            DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));         // DWMWA_WINDOW_CORNER_PREFERENCE
            int border = 0x00392F2A;                                           // #2A2F39 (COLORREF: 0x00BBGGRR)
            DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));        // DWMWA_BORDER_COLOR
        }
        catch
        {
            // Windows 10'da desteklenmeyen öznitelikler yok sayılır.
        }
    }

    private void UpdateMaximizeState()
    {
        bool maximized = WindowState == WindowState.Maximized;
        RootBorder.Margin = maximized ? new Thickness(7) : new Thickness(0);
        MaxRestoreGlyph.Text = maximized ? "" : "";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxRestore_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SaveSettings(showMessage: true);
            e.Handled = true;
        }
    }

    private void ShowFromTray()
    {
        _trimTimer.Stop();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _reallyExit = true;
        Close();
    }

    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        _saveDebounceTimer.Stop();
        SaveQuietly();
        RestoreColors();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_testMode)
        {
            _saveDebounceTimer.Stop();
            SaveQuietly();
        }
        if (!_testMode && !_reallyExit)
        {
            e.Cancel = true;
            Hide();
            ReleasePages();
            _trimTimer.Start();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(TrimMemory));
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray?.ShowBalloon("Game Color Guard", Loc.T("Program arka planda seçtiğiniz oyunları izlemeye devam ediyor."));
            }
            return;
        }
        RestoreColors();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _watchTimer.Stop();
        _previewTimer.Stop();
        _saveDebounceTimer.Stop();
        _liveApplyTimer.Stop();
        _saveFlashTimer.Stop();
        _toastTimer.Stop();
        _trimTimer.Stop();
        _foregroundHook?.Dispose();
        // Oyuna bağlı geçici ayarlar geri alınır; kullanıcının açık bıraktığı kalıcı ayarlar kalır.
        if (!_testMode) _performance?.Dispose();
        if (!_testMode) Application.Current.SessionEnding -= OnSessionEnding;
        _tray?.Dispose();
        // Test/çizim modu ekran renklerine hiç dokunmaz: Dispose geri yükleme yapar ve o sırada oyunda
        // uygulanmış profili (gerçek programın profilini) nötre çekiyordu.
        if (!_testMode)
        {
            try { _colorMatrix.Dispose(); } catch (Exception ex) { AppLogger.Write("Windows renk motoru kapatılamadı", ex); }
            try { _nvidiaColor.Dispose(); } catch (Exception ex) { AppLogger.Write("NVIDIA kapatılamadı", ex); }
            try { _displayGamma.Dispose(); } catch (Exception ex) { AppLogger.Write("Gama kapatılamadı", ex); }
        }
        base.OnClosed(e);
        if (!_testMode) Application.Current.Shutdown();
    }

    // ------------------------------------------------------------------ Test / ekran görüntüsü

    internal FrameworkElement RenderRoot => RootBorder;

    internal void ApplyTestState(string state)
    {
        switch (state.ToLowerInvariant())
        {
            case "active":
                _gameHasFocus = false;
                UpdateGameState(new[] { "cs2.exe" }, "cs2.exe");
                break;
            case "visible":
                _gameHasFocus = true;
                _gameProfileOn = true;
                _profileApplied = true;
                UpdateGameState(new[] { "cs2.exe" }, null, visibleGame: "cs2.exe");
                break;
            case "background":
                _gameHasFocus = true;
                _gameProfileOn = true;
                _restorePendingSince = DateTime.UtcNow - RestoreDelay;
                UpdateGameState(new[] { "cs2.exe" }, null);
                break;
            case "preview":
                _previewActive = true;
                _previewEndsAt = DateTime.UtcNow.AddSeconds(6.4);
                UpdatePreviewUi();
                SetStatus("Önizleme etkin — kaydırıcılardaki değişiklikler canlı uygulanır", Tone.Warning);
                break;
            case "scrolled":
                MainScroll.ScrollToVerticalOffset(10000);
                break;
            case "boost-on":
                ShowPage("boost");
                break;
            case "boost":
            case "pro":
            case "system":
                ShowPage(state.ToLowerInvariant());
                break;
            case "apps":
                ShowPage("background");
                ((BackgroundPage)_pages["background"]).ShowSample();
                break;
            case { } s when s.StartsWith("lang:", StringComparison.Ordinal):
                // Dil seçicideki öğeyi seçer: canlı dil değişimi gerçek tıklamadaki yoldan geçer.
                LanguagePanel.Children.OfType<RadioButton>().First(r => (string)r.Tag == s[5..]).IsChecked = true;
                break;
            default:
                UpdateGameState(Array.Empty<string>(), null);
                break;
        }
    }

    internal void CloseForTest()
    {
        _reallyExit = true;
        Close();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
