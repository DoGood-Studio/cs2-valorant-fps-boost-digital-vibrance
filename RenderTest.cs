using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameColorGuard.Core;

namespace GameColorGuard;

/// <summary>
/// "--render &lt;png&gt; [idle|active|background|preview|scrolled]": arayüzü ekran renklerine
/// dokunmadan, örnek bir profille PNG olarak çizer (tasarım doğrulaması için).
/// </summary>
internal static class RenderTest
{
    internal static int Run(string path, string state, string? language = null)
    {
        try
        {
            var settings = new ColorSettings
            {
                Brightness = 55,
                Contrast = 55,
                Gamma = 110,
                DigitalVibrance = 85,
                Hue = 0,
                StartWithWindows = true,
                Language = language
            };
            // Tanıtım görseli: FPS Boost sayfası önerilen ayarlar açık hâliyle (yalnız görünüm; sisteme bir şey uygulanmaz).
            if (state.Equals("boost-on", StringComparison.OrdinalIgnoreCase))
                settings.Performance.Enabled.AddRange(new[]
                {
                    "visual-effects-off", "transparency-off", "mute-notifications", "accessibility-shortcuts-off", "power-mode-best",
                    "process-priority-high", "windows-key-lock", "game-dvr-off", "game-mode-on", "windowed-optimizations", "gpu-preference"
                });
            var window = new MainWindow(testMode: true, initialSettings: settings)
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -12000,
                Top = 0,
                Width = 1160,
                Height = 800
            };
            window.Show();
            Pump(window.Dispatcher);
            // Birden çok durum virgülle sırayla uygulanabilir (örn. "pro,lang:en").
            foreach (string step in state.Split(','))
            {
                window.ApplyTestState(step);
                for (int i = 0; i < 6; i++)
                {
                    System.Threading.Thread.Sleep(70);
                    Pump(window.Dispatcher);
                }
            }

            FrameworkElement root = window.RenderRoot;
            const double scale = 1.0;
            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(root);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(path)) encoder.Save(stream);

            window.CloseForTest();
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(path + ".error.txt", ex.ToString()); } catch { }
            return 1;
        }
    }

    private static void Pump(Dispatcher dispatcher) =>
        dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
}
