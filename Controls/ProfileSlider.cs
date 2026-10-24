using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GameColorGuard.Controls;

/// <summary>Başlık + aralık bilgisi, özel iz ve düzenlenebilir değer kutusundan oluşan satır.</summary>
internal sealed class ProfileSlider : Grid
{
    private readonly SliderTrack _track;
    private readonly TextBox _input;
    private readonly int _divisor;
    private readonly int _decimals;

    public string Caption { get; }
    public int NeutralValue => _track.Neutral;
    public event EventHandler? ValueChanged;

    public ProfileSlider(string caption, int minimum, int maximum, int value, int neutral, Color accent,
        string unit = "", int divisor = 1, int decimals = 0, TrackKind kind = TrackKind.Standard)
    {
        Caption = caption;
        _divisor = Math.Max(1, divisor);
        _decimals = Math.Max(0, decimals);

        Height = 64;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(188) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 9, 0)
        });
        header.Children.Add(new TextBlock
        {
            Text = caption,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("TextBrush")
        });
        labels.Children.Add(header);
        labels.Children.Add(new TextBlock
        {
            Text = $"{Format(minimum)}{unit} – {Format(maximum)}{unit}  ·  nötr {Format(neutral)}{unit}",
            FontSize = 11.5,
            Foreground = Res<Brush>("Text3Brush"),
            Margin = new Thickness(15, 3, 0, 0)
        });
        Children.Add(labels);

        int step = Math.Max(1, decimals > 0 ? 1 : 1);
        _track = new SliderTrack(minimum, maximum, value, neutral, step, accent, kind)
        {
            Margin = new Thickness(8, 0, 18, 0),
            Height = 40,
            VerticalAlignment = VerticalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetName(_track, caption);
        SetColumn(_track, 1);
        Children.Add(_track);

        _input = new TextBox
        {
            Style = Res<Style>("ValueBox"),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Değer yazıp Enter'a basın"
        };
        System.Windows.Automation.AutomationProperties.SetName(_input, caption + " değeri");
        SetColumn(_input, 2);
        Children.Add(_input);

        UpdateInputText();

        _track.ValueChanged += (_, _) =>
        {
            if (!_input.IsKeyboardFocused) UpdateInputText();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        };
        _input.PreviewKeyDown += OnInputKeyDown;
        _input.LostKeyboardFocus += (_, _) => CommitInput();
        _input.GotKeyboardFocus += (_, _) => _input.Dispatcher.BeginInvoke(new Action(_input.SelectAll));
    }

    public int Value
    {
        get => _track.Value;
        set
        {
            _track.Value = value;
            UpdateInputText();
        }
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                CommitInput();
                _track.Focus();
                e.Handled = true;
                break;
            case Key.Escape:
                UpdateInputText();
                _track.Focus();
                e.Handled = true;
                break;
            case Key.Up:
            case Key.Down:
                CommitInput();
                _track.Value += e.Key == Key.Up ? _track.Step : -_track.Step;
                UpdateInputText();
                _input.SelectAll();
                e.Handled = true;
                break;
        }
    }

    private void CommitInput()
    {
        string cleaned = new string(_input.Text.Where(ch => char.IsDigit(ch) || ch is '.' or ',' or '-').ToArray())
            .Replace(',', '.');
        if (decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal displayed))
        {
            int internalValue = (int)Math.Round(displayed * _divisor, MidpointRounding.AwayFromZero);
            _track.Value = internalValue;
        }
        UpdateInputText();
    }

    private void UpdateInputText() => _input.Text = Format(_track.Value);

    private string Format(int internalValue)
    {
        decimal displayed = internalValue / (decimal)_divisor;
        string format = _decimals > 0 ? $"F{_decimals}" : "0";
        return displayed.ToString(format, CultureInfo.InvariantCulture);
    }

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);
}
