using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace GameColorGuard.Controls;

/// <summary>Canlı durum halkası: durum rengi, simge ve (önizlemede) geri sayım yayı.</summary>
internal sealed class StatusRing : FrameworkElement
{
    private static readonly Typeface IconFace = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Pen TrackPen = CreatePen(Color.FromRgb(0x26, 0x2B, 0x35), 3.5);

    private Color _color = Color.FromRgb(0x8A, 0x93, 0xA3);
    private double _progress = -1;
    private string _glyph = "";
    private bool _emphasis;

    public StatusRing()
    {
        SnapsToDevicePixels = false;
        UpdateEffect();
    }

    /// <param name="progress">-1: tam halka; 0..1: geri sayım yayı.</param>
    public void Set(Color color, string glyph, double progress, bool emphasis)
    {
        bool colorChanged = color != _color || emphasis != _emphasis;
        if (!colorChanged && glyph == _glyph && Math.Abs(progress - _progress) < 0.001) return;
        _color = color;
        _glyph = glyph;
        _progress = progress;
        _emphasis = emphasis;
        if (colorChanged) UpdateEffect();
        InvalidateVisual();
    }

    private void UpdateEffect()
    {
        Effect = new DropShadowEffect
        {
            Color = _color,
            BlurRadius = _emphasis ? 26 : 14,
            ShadowDepth = 0,
            Opacity = _emphasis ? 0.65 : 0.25
        };
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 16) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = size / 2 - 3;

        var fill = new SolidColorBrush(Color.FromArgb(0x1C, _color.R, _color.G, _color.B));
        fill.Freeze();
        dc.DrawEllipse(fill, null, center, radius - 5, radius - 5);
        dc.DrawEllipse(null, TrackPen, center, radius, radius);

        Pen arcPen = CreatePen(_color, 3.5);
        if (_progress < 0 || _progress >= 0.999)
        {
            dc.DrawEllipse(null, arcPen, center, radius, radius);
        }
        else if (_progress > 0.001)
        {
            double start = -Math.PI / 2;
            double end = start + Math.PI * 2 * _progress;
            var p0 = new Point(center.X + radius * Math.Cos(start), center.Y + radius * Math.Sin(start));
            var p1 = new Point(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end));
            var arc = new StreamGeometry();
            using (StreamGeometryContext ctx = arc.Open())
            {
                ctx.BeginFigure(p0, false, false);
                ctx.ArcTo(p1, new Size(radius, radius), 0, _progress > 0.5, SweepDirection.Clockwise, true, false);
            }
            arc.Freeze();
            dc.DrawGeometry(null, arcPen, arc);
        }

        var brush = new SolidColorBrush(_color);
        brush.Freeze();
        var text = new FormattedText(_glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            IconFace, Math.Round(size * 0.34), brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }

    private static Pen CreatePen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        return pen;
    }
}
