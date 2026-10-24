using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace GameColorGuard.Controls;

internal enum TrackKind { Standard, Hue }

/// <summary>
/// Özel çizilmiş kaydırıcı: nötr noktadan değere doğru dolan iz, nötr işareti,
/// parlayan tutamaç. Çift tık nötre döndürür; ok tuşları ince ayar yapar.
/// </summary>
internal sealed class SliderTrack : FrameworkElement
{
    private const double ThumbRadius = 9;
    private const double TrackHeight = 6;

    private static readonly Brush TrackBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x26, 0x2B, 0x35)));
    private static readonly Brush TickBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x55, 0x5D, 0x6D)));
    private static readonly Brush ShadowBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)));
    private static readonly Brush HueBrush = Frozen(BuildHueBrush());

    private readonly Brush _accentBrush;
    private readonly Brush _glowBrush;
    private readonly Brush _glowStrongBrush;
    private readonly Pen _focusPen;
    private int _value;
    private bool _hover;
    private bool _dragging;

    public int Minimum { get; }
    public int Maximum { get; }
    public int Neutral { get; }
    public int Step { get; }
    public Color Accent { get; }
    public TrackKind Kind { get; }

    public event EventHandler? ValueChanged;

    public SliderTrack(int minimum, int maximum, int value, int neutral, int step, Color accent, TrackKind kind)
    {
        Minimum = minimum;
        Maximum = maximum;
        Neutral = Math.Clamp(neutral, minimum, maximum);
        Step = Math.Max(1, step);
        Accent = accent;
        Kind = kind;
        _value = Math.Clamp(value, minimum, maximum);
        _accentBrush = Frozen(new SolidColorBrush(accent));
        _glowBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x22, accent.R, accent.G, accent.B)));
        _glowStrongBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x3C, accent.R, accent.G, accent.B)));
        _focusPen = new Pen(_accentBrush, 1.5);
        _focusPen.Freeze();

        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Hand;
        MinHeight = 32;
        ToolTip = "Sürükleyin · ok tuşlarıyla ince ayar · çift tık: nötr";
    }

    public int Value
    {
        get => _value;
        set
        {
            int clamped = Math.Clamp(value, Minimum, Maximum);
            if (clamped == _value) return;
            _value = clamped;
            InvalidateVisual();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private double Left => ThumbRadius + 6;
    private double Right => Math.Max(Left + 1, ActualWidth - ThumbRadius - 6);

    private double XFor(int value) =>
        Left + (Right - Left) * (value - Minimum) / Math.Max(1.0, Maximum - Minimum);

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        if (width < ThumbRadius * 4) return;

        double cy = Math.Round(height / 2);
        var track = new Rect(Left, cy - TrackHeight / 2, Right - Left, TrackHeight);

        if (Kind == TrackKind.Hue)
        {
            dc.DrawRoundedRectangle(HueBrush, null, track, TrackHeight / 2, TrackHeight / 2);
        }
        else
        {
            dc.DrawRoundedRectangle(TrackBrush, null, track, TrackHeight / 2, TrackHeight / 2);
            double xValue = XFor(_value);
            double xNeutral = XFor(Neutral);
            double x0 = Math.Min(xValue, xNeutral);
            double x1 = Math.Max(xValue, xNeutral);
            if (x1 - x0 > 0.5)
            {
                var fillRect = new Rect(x0, track.Y, x1 - x0, TrackHeight);
                Color faded = Color.FromArgb(0x55, Accent.R, Accent.G, Accent.B);
                var fill = xValue >= xNeutral
                    ? new LinearGradientBrush(faded, Accent, 0)
                    : new LinearGradientBrush(Accent, faded, 0);
                fill.Freeze();
                dc.DrawRoundedRectangle(fill, null, fillRect, TrackHeight / 2, TrackHeight / 2);
            }
        }

        if (Neutral > Minimum && Neutral < Maximum)
        {
            double xn = Math.Round(XFor(Neutral));
            dc.DrawRoundedRectangle(TickBrush, null, new Rect(xn - 1, cy - 8, 2, 16), 1, 1);
        }

        double x = XFor(_value);
        var center = new Point(x, cy);
        Brush innerBrush = _accentBrush;
        Brush glow = _dragging || _hover || IsKeyboardFocused ? _glowStrongBrush : _glowBrush;
        if (Kind == TrackKind.Hue)
        {
            Color hue = HueColor(_value);
            innerBrush = Frozen(new SolidColorBrush(hue));
            glow = Frozen(new SolidColorBrush(Color.FromArgb((byte)(_dragging || _hover ? 0x44 : 0x26), hue.R, hue.G, hue.B)));
        }

        double glowRadius = _dragging ? 17 : (_hover || IsKeyboardFocused ? 15 : 13);
        dc.DrawEllipse(glow, null, center, glowRadius, glowRadius);
        dc.DrawEllipse(ShadowBrush, null, new Point(x, cy + 1.2), ThumbRadius + 0.8, ThumbRadius + 0.8);
        dc.DrawEllipse(Brushes.White, null, center, ThumbRadius, ThumbRadius);
        dc.DrawEllipse(innerBrush, null, center, 4.5, 4.5);

        if (IsKeyboardFocused)
            dc.DrawEllipse(null, _focusPen, center, ThumbRadius + 4.5, ThumbRadius + 4.5);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (e.ClickCount == 2)
        {
            Value = Neutral;
            e.Handled = true;
            return;
        }
        _dragging = true;
        CaptureMouse();
        SetFromX(e.GetPosition(this).X);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging && IsMouseCaptured) SetFromX(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
        InvalidateVisual();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        InvalidateVisual();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
            case Key.Down:
                Value -= Step;
                break;
            case Key.Right:
            case Key.Up:
                Value += Step;
                break;
            case Key.PageDown:
                Value -= Step * 10;
                break;
            case Key.PageUp:
                Value += Step * 10;
                break;
            case Key.Home:
                Value = Minimum;
                break;
            case Key.End:
                Value = Maximum;
                break;
            case Key.Delete:
            case Key.Back:
                Value = Neutral;
                break;
            default:
                base.OnKeyDown(e);
                return;
        }
        e.Handled = true;
    }

    private void SetFromX(double x)
    {
        double ratio = Math.Clamp((x - Left) / Math.Max(1.0, Right - Left), 0.0, 1.0);
        int raw = Minimum + (int)Math.Round((Maximum - Minimum) * ratio);
        raw = Minimum + (int)Math.Round((raw - Minimum) / (double)Step) * Step;
        Value = raw;
    }

    internal static Color HueColor(int degrees) => FromHsv(degrees % 360, 0.78, 1.0);

    internal static Color FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        double c = value * saturation;
        double x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        double m = value - c;
        (double r, double g, double b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static LinearGradientBrush BuildHueBrush()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++)
            brush.GradientStops.Add(new GradientStop(FromHsv(i * 60, 0.72, 0.95), i / 6.0));
        return brush;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
