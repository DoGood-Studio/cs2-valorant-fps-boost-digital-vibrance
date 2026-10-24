using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameColorGuard.Core;

namespace GameColorGuard;

/// <summary>
/// "--beforeafter &lt;klasör&gt;": tanıtım görseli. Kodla çizilmiş bir sahneye uygulamanın kendi renk hesabını
/// (Windows renk matrisi + gama rampası, ekranda uygulandıkları sırayla) uygular; ekran renklerine dokunmaz.
/// Çıktılar: once.png, sonra.png ve ikisi yan yana once-sonra.png.
/// </summary>
internal static class BeforeAfter
{
    private const int Width = 1280, Height = 720;

    internal static int Run(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var profile = new ColorSettings { Brightness = 55, Contrast = 55, Gamma = 110, DigitalVibrance = 85 };
            BitmapSource before = DrawScene();
            BitmapSource after = ApplyProfile(before, profile);
            Save(before, Path.Combine(folder, "once.png"));
            Save(after, Path.Combine(folder, "sonra.png"));
            Save(Compose(before, after), Path.Combine(folder, "once-sonra.png"));
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

        var pixels = new byte[Width * Height * 4];
        source.CopyPixels(pixels, Width * 4, 0);
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
        }
        BitmapSource result = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, pixels, Width * 4);
        result.Freeze();
        return result;
    }

    /// <summary>Tozlu bir harita köşesi: gökyüzü, kumtaşı duvarlar, renkli sandıklar ve gölgede bekleyen bir siluet.</summary>
    private static BitmapSource DrawScene()
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            Brush Grad(string top, string bottom) => new LinearGradientBrush(Hex(top), Hex(bottom), 90);
            Geometry Poly(params double[] p)
            {
                var g = new StreamGeometry();
                using (StreamGeometryContext c = g.Open())
                {
                    c.BeginFigure(new Point(p[0], p[1]), true, true);
                    for (int i = 2; i < p.Length; i += 2) c.LineTo(new Point(p[i], p[i + 1]), true, false);
                }
                g.Freeze();
                return g;
            }

            dc.DrawRectangle(Grad("#8FA6BA", "#D9CBAA"), null, new Rect(0, 0, Width, 330));
            dc.DrawRectangle(Grad("#BBA57F", "#9B8763"), null, new Rect(0, 330, Width, 150));   // uzak zemin
            dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(0x90, 0xFF, 0xF1, 0xC9), Color.FromArgb(0, 0xFF, 0xF1, 0xC9)), null,
                new Point(1010, 120), 230, 160);
            // Uzak binalar
            dc.DrawRectangle(Grad("#A99273", "#8E7A5E"), null, new Rect(0, 170, 300, 200));
            dc.DrawRectangle(Grad("#B8A07D", "#9A8566"), null, new Rect(260, 120, 260, 250));
            dc.DrawRectangle(Grad("#A48C6B", "#8A7556"), null, new Rect(900, 190, 380, 180));
            // Zemin
            dc.DrawGeometry(Grad("#9B8763", "#4E4231"), null, Poly(0, 470, 1280, 450, 1280, 720, 0, 720));
            // Sol büyük duvar ve kemerli geçit (içi karanlık)
            dc.DrawGeometry(Grad("#C2A77F", "#8D7556"), null, Poly(0, 90, 470, 150, 470, 560, 0, 640));
            dc.DrawGeometry(Grad("#2B241B", "#1A1611"), null, Poly(140, 300, 330, 318, 330, 560, 140, 590));
            dc.DrawEllipse(Hex("#2B241B").ToBrush(), null, new Point(235, 309), 95, 40);
            // Gölgedeki siluet: koyu zeminden çok az ayrışır
            Brush figure = Hex("#2F2820").ToBrush();
            dc.DrawEllipse(figure, null, new Point(262, 392), 15, 17);
            dc.DrawGeometry(figure, null, Poly(240, 410, 286, 410, 294, 500, 280, 575, 266, 575, 262, 512, 256, 575, 242, 575, 236, 500));
            dc.DrawGeometry(Hex("#26201A").ToBrush(), null, Poly(282, 430, 322, 436, 322, 444, 282, 440));
            // Sağ duvar ve pencere
            dc.DrawGeometry(Grad("#B79E79", "#8A7354"), null, Poly(860, 160, 1280, 110, 1280, 600, 860, 520));
            dc.DrawRectangle(Hex("#3B5568").ToBrush(), null, new Rect(980, 250, 120, 90));
            dc.DrawRectangle(Grad("#6E8AA0", "#3B5568"), null, new Rect(990, 260, 100, 70));
            // Sandıklar, branda, varil
            dc.DrawRectangle(Grad("#8A6638", "#5E4423"), new Pen(Hex("#4A351C").ToBrush(), 4), new Rect(560, 430, 150, 130));
            dc.DrawRectangle(Grad("#7E5D33", "#553D20"), new Pen(Hex("#4A351C").ToBrush(), 4), new Rect(690, 470, 120, 100));
            dc.DrawGeometry(Grad("#4F7593", "#2F4B61"), null, Poly(520, 560, 610, 520, 700, 548, 720, 600, 520, 610));
            dc.DrawRectangle(Grad("#9A4535", "#6B2A20"), null, new Rect(820, 480, 70, 110));
            dc.DrawRectangle(Grad("#5C7A43", "#3C5229"), null, new Rect(1100, 520, 90, 80));
            // Gölge
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), null, Poly(470, 560, 860, 520, 1280, 600, 1280, 720, 0, 720, 0, 640));
        }
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    /// <summary>Aynı sahne yan yana: solda önce, sağda sonra (karanlık geçit ve siluet iki tarafta da görünür).</summary>
    private static BitmapSource Compose(BitmapSource before, BitmapSource after)
    {
        const int gap = 12, pad = 0;
        double panelWidth = (Width - gap) / 2.0, panelHeight = panelWidth * Height / Width;
        int canvasHeight = (int)Math.Ceiling(panelHeight) + pad * 2;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0A, 0x0B, 0x0E)), null, new Rect(0, 0, Width, canvasHeight));
            dc.DrawImage(before, new Rect(0, pad, panelWidth, panelHeight));
            dc.DrawImage(after, new Rect(panelWidth + gap, pad, panelWidth, panelHeight));
            Label(dc, "ÖNCE", new Point(16, 16), Color.FromArgb(0xC8, 0x14, 0x16, 0x1B));
            Label(dc, "SONRA · Game Color Guard", new Point(panelWidth + gap + 16, 16), Color.FromArgb(0xE0, 0xFF, 0x7A, 0x1A));
        }
        var bitmap = new RenderTargetBitmap(Width, canvasHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Label(DrawingContext dc, string text, Point at, Color background)
    {
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.GetCultureInfo("tr-TR"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            20, Brushes.White, 1.0);
        dc.DrawRoundedRectangle(new SolidColorBrush(background), null, new Rect(at.X, at.Y, formatted.Width + 24, formatted.Height + 12), 8, 8);
        dc.DrawText(formatted, new Point(at.X + 12, at.Y + 6));
    }

    private static void Save(BitmapSource source, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush ToBrush(this Color color) => new(color);
}
