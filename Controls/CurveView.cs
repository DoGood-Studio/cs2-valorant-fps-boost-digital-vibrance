using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using GameColorGuard.Core;

namespace GameColorGuard.Controls;

/// <summary>Windows gama rampasının (0-255 → çıktı) canlı ton eğrisi.</summary>
internal sealed class CurveView : FrameworkElement
{
    private static readonly Color Ember = Color.FromRgb(0xFF, 0x7A, 0x1A);
    private static readonly Color RedC = Color.FromRgb(0xFF, 0x5A, 0x5F);
    private static readonly Color GreenC = Color.FromRgb(0x3D, 0xD6, 0x8C);
    private static readonly Color BlueC = Color.FromRgb(0x4C, 0x9B, 0xFF);
    private static readonly Pen GridPen = FrozenPen(Color.FromRgb(0x1C, 0x20, 0x27), 1, null);
    private static readonly Pen DiagonalPen = FrozenPen(Color.FromRgb(0x3A, 0x40, 0x4C), 1, new DoubleCollection { 3, 3 });
    private static readonly Brush LabelBrush = FrozenBrush(Color.FromRgb(0x7E, 0x86, 0x96));
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private double[] _red = Identity();
    private double[] _green = Identity();
    private double[] _blue = Identity();

    public void SetRamp(ushort[] red, ushort[] green, ushort[] blue)
    {
        _red = red.Select(v => v / 65535.0).ToArray();
        _green = green.Select(v => v / 65535.0).ToArray();
        _blue = blue.Select(v => v / 65535.0).ToArray();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        if (width < 60 || height < 60) return;

        const double pad = 18;
        var plot = new Rect(pad, pad, width - pad * 2, height - pad * 2);

        for (int i = 1; i < 4; i++)
        {
            double x = Math.Round(plot.Left + plot.Width * i / 4) + 0.5;
            double y = Math.Round(plot.Top + plot.Height * i / 4) + 0.5;
            dc.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }
        dc.DrawLine(DiagonalPen, plot.BottomLeft, plot.TopRight);

        bool mono = Same(_red, _green) && Same(_green, _blue);
        if (mono)
        {
            var area = new LinearGradientBrush(Color.FromArgb(0x38, Ember.R, Ember.G, Ember.B),
                Color.FromArgb(0x00, Ember.R, Ember.G, Ember.B), 90);
            area.Freeze();
            dc.DrawGeometry(area, null, Build(_red, plot, closed: true));
            Geometry line = Build(_red, plot, closed: false);
            dc.DrawGeometry(null, FrozenPen(Color.FromArgb(0x30, Ember.R, Ember.G, Ember.B), 7, null), line);
            dc.DrawGeometry(null, FrozenPen(Ember, 2.2, null), line);
        }
        else
        {
            foreach ((double[] values, Color color) in new[] { (_red, RedC), (_green, GreenC), (_blue, BlueC) })
            {
                Geometry line = Build(values, plot, closed: false);
                dc.DrawGeometry(null, FrozenPen(Color.FromArgb(0x22, color.R, color.G, color.B), 6, null), line);
                dc.DrawGeometry(null, FrozenPen(Color.FromArgb(0xE6, color.R, color.G, color.B), 2, null), line);
            }
        }

        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var output = new FormattedText(Loc.T("ÇIKTI"), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 10, LabelBrush, dip);
        var input = new FormattedText(Loc.T("GİRDİ"), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 10, LabelBrush, dip);
        dc.DrawText(output, new Point(plot.Left + 6, plot.Top + 4));
        dc.DrawText(input, new Point(plot.Right - input.Width - 6, plot.Bottom - input.Height - 4));
    }

    private static Geometry Build(double[] values, Rect plot, bool closed)
    {
        var points = new Point[256];
        for (int i = 0; i < 256; i++)
        {
            double v = Math.Clamp(values[i], 0, 1);
            points[i] = new Point(plot.Left + plot.Width * i / 255.0, plot.Bottom - plot.Height * v);
        }

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            if (closed)
            {
                ctx.BeginFigure(plot.BottomLeft, true, true);
                ctx.LineTo(points[0], false, false);
                ctx.PolyLineTo(points.Skip(1).ToArray(), false, true);
                ctx.LineTo(plot.BottomRight, false, false);
            }
            else
            {
                ctx.BeginFigure(points[0], false, false);
                ctx.PolyLineTo(points.Skip(1).ToArray(), true, true);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static bool Same(double[] a, double[] b)
    {
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 0.002) return false;
        return true;
    }

    private static double[] Identity() => Enumerable.Range(0, 256).Select(i => i / 255.0).ToArray();

    private static Pen FrozenPen(Color color, double thickness, DoubleCollection? dashes)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        if (dashes is not null) pen.DashStyle = new DashStyle(dashes, 0);
        pen.Freeze();
        return pen;
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
