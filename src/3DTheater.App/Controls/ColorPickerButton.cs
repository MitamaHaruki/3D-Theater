using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using Theater3D.Editor;

namespace Theater3D.App.Controls;

/// <summary>
/// 色の見本のボタン（設計判断記録 87 章）。押すとパレットを出し、選んだ色を <see cref="Target"/> の欄（「#RRGGBB」を打つ欄）に入れる。
/// パレットは、よく使う色の見本（<see cref="ColorPalette.Swatches"/>）と、色合い・鮮やかさ・明るさのつまみ。
/// 見本は、欄の色に合わせて塗る（読めない字の間は、前の色のまま）。パレットの外を押すと閉じる。
/// </summary>
public sealed class ColorPickerButton : Button
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(TextBox), typeof(ColorPickerButton), new PropertyMetadata(null, OnTargetChanged));

    private readonly Border _swatch;
    private Popup? _popup;
    private Slider? _hue;
    private Slider? _saturation;
    private Slider? _value;
    private Border? _preview;
    private TextBlock? _previewText;
    private bool _updatingSliders;
    private DateTime _closedAt;

    public ColorPickerButton()
    {
        Width = 30;
        Height = 24;
        MinWidth = 0;
        MinHeight = 0;
        Padding = new Thickness(3);
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalContentAlignment = HorizontalAlignment.Stretch; // 見本をボタンいっぱいに塗る
        VerticalContentAlignment = VerticalAlignment.Stretch;
        _swatch = new Border
        {
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
        };
        _swatch.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Content = _swatch;
        ToolTip = "パレットから色を選びます";
        AutomationProperties.SetName(this, "色を選ぶ");
    }

    /// <summary>色を入れる欄（「#RRGGBB」）。</summary>
    public TextBox? Target
    {
        get => (TextBox?)GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    /// <summary>パレットを開いているか。</summary>
    public bool IsPaletteOpen => _popup?.IsOpen == true;

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var button = (ColorPickerButton)d;
        if (e.OldValue is TextBox oldBox)
        {
            oldBox.TextChanged -= button.OnTargetTextChanged;
        }
        if (e.NewValue is TextBox newBox)
        {
            newBox.TextChanged += button.OnTargetTextChanged;
        }
        button.ShowTargetColor();
    }

    private void OnTargetTextChanged(object sender, TextChangedEventArgs e) => ShowTargetColor();

    private Vector3? TargetColor => Target is { } box && ImageExport.TryParseColor(box.Text, out Vector3 color) ? color : null;

    private void ShowTargetColor()
    {
        if (TargetColor is not { } color)
        {
            return; // 打ちかけの間は、前の色のまま
        }
        _swatch.Background = BrushOf(color);
        if (_preview is not null && _previewText is not null)
        {
            _preview.Background = BrushOf(color);
            _previewText.Text = ImageExport.FormatColor(color);
        }
        if (IsPaletteOpen && !_updatingSliders)
        {
            ShowSliders(color);
        }
    }

    protected override void OnClick()
    {
        base.OnClick();
        if (Target is null)
        {
            return;
        }
        if (_popup is { IsOpen: true })
        {
            _popup.IsOpen = false; // もう一度押すと閉じる
            return;
        }
        if (DateTime.UtcNow - _closedAt < TimeSpan.FromMilliseconds(300))
        {
            return; // このボタンを押してパレットが閉じたところ（外を押すと閉じるため）。開き直さない
        }
        _popup ??= BuildPopup();
        Vector3 color = TargetColor ?? Vector3.One;
        ShowSliders(color);
        _preview!.Background = BrushOf(color);
        _previewText!.Text = ImageExport.FormatColor(color);
        _popup.IsOpen = true;
    }

    // 選んだ色を欄へ入れる（欄の決まりで、場面へ入る）
    private void Pick(Vector3 color)
    {
        if (Target is not { IsEnabled: true, IsReadOnly: false } box)
        {
            return;
        }
        box.Text = ImageExport.FormatColor(color);
    }

    private Popup BuildPopup()
    {
        var panel = new StackPanel { Margin = new Thickness(10), Width = 8 * 26 };

        panel.Children.Add(Caption("パレット"));
        var grid = new UniformGrid { Columns = ColorPalette.SwatchesPerRow };
        foreach (ColorPalette.Swatch swatch in ColorPalette.Swatches)
        {
            Vector3 color = ImageExport.TryParseColor(swatch.Color, out Vector3 parsed) ? parsed : Vector3.One;
            var chip = new Button
            {
                Width = 24,
                Height = 24,
                MinWidth = 0,
                MinHeight = 0,
                Margin = new Thickness(1),
                Padding = new Thickness(2),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Content = new Border { Background = BrushOf(color), CornerRadius = new CornerRadius(2) },
                ToolTip = $"{swatch.Name}（{swatch.Color}）",
            };
            AutomationProperties.SetName(chip, $"パレットの色: {swatch.Name}");
            chip.Click += (_, _) => Pick(color);
            grid.Children.Add(chip);
        }
        panel.Children.Add(grid);

        panel.Children.Add(Caption("細かく選ぶ"));
        _hue = Knob("色合い", 0, 360, panel, HueBrush());
        _saturation = Knob("鮮やかさ", 0, 100, panel, null);
        _value = Knob("明るさ", 0, 100, panel, new LinearGradientBrush(Colors.Black, Colors.White, 0));

        var current = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        _preview = new Border { Width = 40, Height = 22, CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1) };
        _preview.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        _previewText = new TextBlock { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_previewText, "選んでいる色");
        current.Children.Add(_preview);
        current.Children.Add(_previewText);
        panel.Children.Add(current);

        var frame = new Border { Child = panel, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
        frame.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        frame.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        AutomationProperties.SetName(frame, "色のパレット");
        var popup = new Popup
        {
            Child = frame,
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
        };
        popup.Closed += (_, _) => _closedAt = DateTime.UtcNow;
        frame.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                popup.IsOpen = false; // Esc で閉じる
                e.Handled = true;
            }
        };
        return popup;
    }

    private static TextBlock Caption(string text)
    {
        var caption = new TextBlock { Text = text, Margin = new Thickness(0, 4, 0, 4), FontSize = 11.5 };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        return caption;
    }

    // つまみ 1 本（名前・つまみ・値）。動かすと、3 本の値から色を作って欄へ入れる
    private Slider Knob(string name, double minimum, double maximum, Panel parent, Brush? track)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5 };
        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            SmallChange = 1,
            LargeChange = 10,
            IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(slider, $"パレットの{name}");
        var number = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, FontSize = 11.5 };
        slider.ValueChanged += (_, _) =>
        {
            number.Text = slider.Value.ToString("0", CultureInfo.InvariantCulture);
            if (!_updatingSliders)
            {
                PickFromSliders();
            }
        };
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(number, 2);
        row.Children.Add(label);
        row.Children.Add(slider);
        row.Children.Add(number);
        if (track is not null)
        {
            // つまみの下に、選べる色の帯を敷く
            var strip = new Border { Height = 4, VerticalAlignment = VerticalAlignment.Bottom, Background = track, CornerRadius = new CornerRadius(2), IsHitTestVisible = false };
            Grid.SetColumn(strip, 1);
            row.Children.Insert(0, strip);
        }
        parent.Children.Add(row);
        return slider;
    }

    private void PickFromSliders()
    {
        if (_hue is null || _saturation is null || _value is null)
        {
            return;
        }
        _updatingSliders = true;
        try
        {
            Pick(ColorPalette.FromHsv((float)_hue.Value, (float)(_saturation.Value / 100), (float)(_value.Value / 100)));
        }
        finally
        {
            _updatingSliders = false;
        }
    }

    private void ShowSliders(Vector3 color)
    {
        if (_hue is null || _saturation is null || _value is null)
        {
            return;
        }
        var (hue, saturation, value) = ColorPalette.ToHsv(color);
        _updatingSliders = true;
        try
        {
            if (saturation > 0f && value > 0f)
            {
                _hue.Value = hue; // 灰色・黒では色合いが決まらないので、つまみを動かさない
            }
            if (value > 0f)
            {
                _saturation.Value = saturation * 100;
            }
            _value.Value = value * 100;
        }
        finally
        {
            _updatingSliders = false;
        }
    }

    private static LinearGradientBrush HueBrush()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++)
        {
            Vector3 c = ColorPalette.FromHsv(i * 60f, 1f, 1f);
            brush.GradientStops.Add(new GradientStop(ColorOf(c), i / 6.0));
        }
        brush.Freeze();
        return brush;
    }

    private static Color ColorOf(Vector3 c) =>
        Color.FromRgb((byte)MathF.Round(c.X * 255f), (byte)MathF.Round(c.Y * 255f), (byte)MathF.Round(c.Z * 255f));

    private static SolidColorBrush BrushOf(Vector3 c)
    {
        var brush = new SolidColorBrush(ColorOf(c));
        brush.Freeze();
        return brush;
    }
}
