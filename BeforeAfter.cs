using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using GameColorGuard.Core;

namespace GameColorGuard;

/// <summary>
/// Tanıtım görselleri (ekran renklerine dokunmaz):
///  • "--beforeafter &lt;klasör&gt; &lt;görüntü&gt; &lt;ad&gt; [...]": gerçek oyun görüntüsüne uygulamanın kendi renk hesabını
///    (Windows renk matrisi, ardından gama rampası — ekranda uygulandıkları sırayla) uygular;
///    &lt;ad&gt;-once.jpg, &lt;ad&gt;-sonra.jpg ve yan yana &lt;ad&gt;-once-sonra.jpg üretir.
///  • "--compose &lt;önce.jpg&gt; &lt;sonra.jpg&gt; &lt;çıktı.jpg&gt; [tr|en]": hazır önce/sonra çiftini seçilen dilde etiketleyip yan yana koyar.
///  • "--fpsbanner &lt;çıktı.jpg&gt; &lt;ekran-görüntüsü.png&gt; &lt;arka-plan.jpg&gt; [tr|en]": FPS Boost tanıtım görseli.
/// </summary>
internal static class BeforeAfter
{
    private const int Width = 1280, Height = 720;
    private static readonly ColorSettings Profile = new() { Brightness = 55, Contrast = 55, Gamma = 110, DigitalVibrance = 85 };
    // Görsellerdeki sürüm programın kendi sürümünden gelir (eski sürüm numarası görselde kalmasın).
    private static readonly string AppVersion = typeof(BeforeAfter).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}" : "";

    internal static int ComposePair(string beforePath, string afterPath, string output, string language)
    {
        try
        {
            SaveJpeg(Compose(LoadCover(beforePath, Width, Height), LoadCover(afterPath, Width, Height), language), output);
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(output + ".error.txt", ex.ToString()); } catch { }
            return 1;
        }
    }

    internal static int Run(string[] args)
    {
        string folder = args[1];
        try
        {
            Directory.CreateDirectory(folder);
            for (int i = 2; i + 1 < args.Length; i += 2)
            {
                BitmapSource before = LoadCover(args[i], Width, Height);
                BitmapSource after = ApplyProfile(before, Profile);
                string name = args[i + 1];
                SaveJpeg(before, Path.Combine(folder, name + "-once.jpg"));
                SaveJpeg(after, Path.Combine(folder, name + "-sonra.jpg"));
                SaveJpeg(Compose(before, after, "tr"), Path.Combine(folder, name + "-once-sonra.jpg"));
            }
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(folder, "beforeafter.error.txt"), ex.ToString()); } catch { }
            return 1;
        }
    }

    /// <summary>Uygulamadaki sırayla: önce DWM renk matrisi (aydınlık, kontrast, canlılık), sonra gama rampası.</summary>
    private static BitmapSource ApplyProfile(BitmapSource source, ColorSettings profile)
    {
        float[] m = WindowsColorMatrix.BuildMatrix(profile);
        var red = new ushort[256];
        var green = new ushort[256];
        var blue = new ushort[256];
        DisplayGamma.ComputeRamp(new ColorSettings { Gamma = profile.Gamma }, red, green, blue);

        int w = source.PixelWidth, h = source.PixelHeight;
        var pixels = new byte[w * h * 4];
        source.CopyPixels(pixels, w * 4, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            double b = pixels[i] / 255.0, g = pixels[i + 1] / 255.0, r = pixels[i + 2] / 255.0;
            // MAGCOLOREFFECT satır-vektör: çıkış_j = Σ giriş_i · M[i*5+j] + M[20+j]
            double r2 = Math.Clamp(r * m[0] + g * m[5] + b * m[10] + m[20], 0, 1);
            double g2 = Math.Clamp(r * m[1] + g * m[6] + b * m[11] + m[21], 0, 1);
            double b2 = Math.Clamp(r * m[2] + g * m[7] + b * m[12] + m[22], 0, 1);
            pixels[i + 2] = (byte)Math.Round(red[(int)Math.Round(r2 * 255)] / 65535.0 * 255);
            pixels[i + 1] = (byte)Math.Round(green[(int)Math.Round(g2 * 255)] / 65535.0 * 255);
            pixels[i] = (byte)Math.Round(blue[(int)Math.Round(b2 * 255)] / 65535.0 * 255);
            pixels[i + 3] = 255;
        }
        BitmapSource result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        result.Freeze();
        return result;
    }

    /// <summary>Görüntüyü hedef boyuta "kaplayacak" biçimde ölçekleyip ortadan kırpar.</summary>
    private static BitmapSource LoadCover(string path, int width, int height)
    {
        BitmapDecoder decoder = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        double scale = Math.Max((double)width / frame.PixelWidth, (double)height / frame.PixelHeight);
        double w = frame.PixelWidth * scale, h = frame.PixelHeight * scale;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            dc.DrawImage(frame, new Rect((width - w) / 2, (height - h) / 2, w, h));
        }
        return ToBgra(Render(visual, width, height));
    }

    /// <summary>Aynı sahne yan yana: solda önce, sağda sonra.</summary>
    private static BitmapSource Compose(BitmapSource before, BitmapSource after, string language)
    {
        bool english = language == "en";
        const int gap = 12;
        double panelWidth = (Width - gap) / 2.0, panelHeight = panelWidth * Height / Width;
        int canvasHeight = (int)Math.Ceiling(panelHeight);
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0A, 0x0B, 0x0E)), null, new Rect(0, 0, Width, canvasHeight));
            dc.DrawImage(before, new Rect(0, 0, panelWidth, panelHeight));
            dc.DrawImage(after, new Rect(panelWidth + gap, 0, panelWidth, panelHeight));
            Label(dc, english ? "BEFORE" : "ÖNCE", new Point(16, 16), 20, Color.FromArgb(0xC8, 0x14, 0x16, 0x1B), Colors.White);
            Label(dc, (english ? "AFTER" : "SONRA") + " · Game Color Guard", new Point(panelWidth + gap + 16, 16), 20,
                Color.FromArgb(0xE6, 0xFF, 0x7A, 0x1A), Color.FromRgb(0x1A, 0x0D, 0x04));
        }
        return Render(visual, Width, canvasHeight);
    }

    /// <summary>FPS Boost tanıtım görseli: bulanık gerçek oyun arka planı, başlık, özellik çipleri ve uygulama ekranı.</summary>
    internal static int Banner(string output, string screenshot, string background, string language = "tr")
    {
        bool english = language == "en";
        try
        {
            const int w = 1600, h = 900;
            BitmapSource bg = LoadCover(background, w, h);
            BitmapDecoder shotDecoder = BitmapDecoder.Create(new Uri(Path.GetFullPath(screenshot)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapSource shot = shotDecoder.Frames[0];

            // Arka plan: bulanık oyun görüntüsü + koyu degrade + turuncu ışık
            var bgVisual = new DrawingVisual { Effect = new BlurEffect { Radius = 18, KernelType = KernelType.Gaussian } };
            using (DrawingContext dc = bgVisual.RenderOpen()) dc.DrawImage(bg, new Rect(-40, -40, w + 80, h + 80));
            BitmapSource blurred = Render(bgVisual, w, h);

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
                dc.DrawImage(blurred, new Rect(0, 0, w, h));
                dc.DrawRectangle(new LinearGradientBrush(new GradientStopCollection
                {
                    new(Color.FromArgb(0xF2, 0x0A, 0x0B, 0x0E), 0), new(Color.FromArgb(0xD8, 0x0A, 0x0B, 0x0E), 0.45),
                    new(Color.FromArgb(0x90, 0x0A, 0x0B, 0x0E), 1)
                }, 0), null, new Rect(0, 0, w, h));
                dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(0x55, 0xFF, 0x7A, 0x1A), Color.FromArgb(0, 0xFF, 0x7A, 0x1A)), null,
                    new Point(1180, 120), 700, 420);

                // Sol: başlık ve metin
                Text(dc, "FPS BOOST", new Point(80, 150), 22, FontWeights.SemiBold, Color.FromRgb(0xFF, 0xB0, 0x66));
                Text(dc, english ? "Knows your PC." : "Sisteminizi tanır.", new Point(78, 190), 62, FontWeights.Bold, Colors.White);
                Text(dc, english ? "Speeds it up safely." : "Güvenle hızlandırır.", new Point(78, 262), 62, FontWeights.Bold, Color.FromRgb(0xFF, 0x9A, 0x3D));
                Text(dc, english ? "Checks laptop or desktop, plugged in or on battery, hybrid GPU;"
                                 : "Dizüstü mü masaüstü mü, prizde mi pilde mi, hibrit GPU var mı bakar;",
                    new Point(82, 360), 21, FontWeights.Normal, Color.FromRgb(0xA3, 0xAA, 0xB7));
                Text(dc, english ? "recommends only reversible Windows settings. No admin rights needed."
                                 : "yalnızca geri alınabilir Windows ayarlarını önerir. Yönetici izni gerekmez.",
                    new Point(82, 390), 21, FontWeights.Normal, Color.FromRgb(0xA3, 0xAA, 0xB7));

                string[] chips = english
                    ? new[]
                    {
                        "Windows animations off", "Xbox Game Bar capture off", "Game Mode on", "High game priority",
                        "Highest refresh rate", "Game on the fast GPU", "Notifications muted in game", "Undo everything in one click"
                    }
                    : new[]
                    {
                        "Windows animasyonları kapalı", "Xbox Game Bar kaydı kapalı", "Oyun Modu açık", "Oyuna yüksek öncelik",
                        "En yüksek yenileme hızı", "Oyunu güçlü GPU'ya sabitle", "Bildirimler oyunda sessiz", "Tek tıkla hepsini geri al"
                    };
                double x = 82, y = 460;
                foreach (string chip in chips)
                {
                    var ft = Formatted("✓  " + chip, 17, FontWeights.SemiBold, Colors.White);
                    double cw = ft.Width + 28;
                    if (x + cw > 700) { x = 82; y += 52; }
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                        new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1), new Rect(x, y, cw, 38), 10, 10);
                    dc.DrawText(ft, new Point(x + 14, y + 8));
                    x += cw + 10;
                }
                Text(dc, $"DoGood Studio · Game Color Guard {AppVersion} · {(english ? "free" : "ücretsiz")}", new Point(82, 800), 17,
                    FontWeights.SemiBold, Color.FromRgb(0x7E, 0x86, 0x96));

                // Sağ: uygulama ekranı (gölgeli, yuvarlak köşeli)
                double sw = 780, sh = sw * shot.PixelHeight / shot.PixelWidth;
                var shotRect = new Rect(w - sw - 60, (h - sh) / 2 + 20, sw, sh);
                var shadow = new DrawingVisual { Effect = new DropShadowEffect { BlurRadius = 60, ShadowDepth = 0, Opacity = 0.75, Color = Colors.Black } };
                using (DrawingContext sdc = shadow.RenderOpen()) sdc.DrawRoundedRectangle(Brushes.Black, null, shotRect, 18, 18);
                dc.DrawImage(Render(shadow, w, h), new Rect(0, 0, w, h));
                dc.PushClip(new RectangleGeometry(shotRect, 18, 18));
                dc.DrawImage(shot, shotRect);
                dc.Pop();
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0x9A, 0x3D)), 1.5), shotRect, 18, 18);
            }
            SaveJpeg(Render(visual, w, h), output);
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(output + ".error.txt", ex.ToString()); } catch { }
            return 1;
        }
    }

    private static void Text(DrawingContext dc, string text, Point at, double size, FontWeight weight, Color color) =>
        dc.DrawText(Formatted(text, size, weight, color), at);

    private static FormattedText Formatted(string text, double size, FontWeight weight, Color color) =>
        new(text, System.Globalization.CultureInfo.GetCultureInfo("tr-TR"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size, new SolidColorBrush(color), 1.0);

    private static void Label(DrawingContext dc, string text, Point at, double size, Color background, Color foreground)
    {
        FormattedText formatted = Formatted(text, size, FontWeights.SemiBold, foreground);
        dc.DrawRoundedRectangle(new SolidColorBrush(background), null, new Rect(at.X, at.Y, formatted.Width + 24, formatted.Height + 12), 8, 8);
        dc.DrawText(formatted, new Point(at.X + 12, at.Y + 6));
    }

    private static BitmapSource Render(Visual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource ToBgra(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static void SaveJpeg(BitmapSource source, string path)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
