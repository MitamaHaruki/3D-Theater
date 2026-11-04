using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Theater3D.App.Controls;

/// <summary>
/// 数値の入力欄。Enter かフォーカスを外したときに確定し、Esc で元に戻す。
/// 正しくない文字は確定せず元の値に戻す。ホイールで <see cref="Step"/> ずつ増減（Shift で 1/10、Ctrl で 10 倍）。
/// </summary>
public sealed class NumberBox : TextBox
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(NumberBox),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged)
        {
            DefaultUpdateSourceTrigger = UpdateSourceTrigger.Explicit,
        });

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(NumberBox), new PropertyMetadata(0.1d));

    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
        nameof(Format), typeof(string), typeof(NumberBox), new PropertyMetadata("0.000", (d, _) => ((NumberBox)d).ShowValue()));

    public NumberBox()
    {
        MinHeight = 0;
        Padding = new Thickness(4, 1, 4, 1);
        HorizontalContentAlignment = HorizontalAlignment.Right;
        ShowValue();
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public string Format
    {
        get => (string)GetValue(FormatProperty);
        set => SetValue(FormatProperty, value);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Commit();
            SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ShowValue();
            SelectAll();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        Commit();
        base.OnLostKeyboardFocus(e);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        SelectAll();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (IsReadOnly || !IsEnabled)
        {
            base.OnMouseWheel(e);
            return;
        }
        double step = Step;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            step /= 10;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            step *= 10;
        }
        SetAndPush(Value + (Math.Sign(e.Delta) * step));
        e.Handled = true;
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((NumberBox)d).ShowValue();

    private void ShowValue()
    {
        if (!IsKeyboardFocusWithin)
        {
            Text = Value.ToString(Format, CultureInfo.CurrentCulture);
        }
    }

    private void Commit()
    {
        if (IsReadOnly)
        {
            ShowValue();
            return;
        }
        string text = Text.Trim().TrimEnd('°', ' ', 'm');
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double parsed)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
        {
            if (double.IsFinite(parsed))
            {
                SetAndPush(parsed);
            }
        }
        Text = Value.ToString(Format, CultureInfo.CurrentCulture);
    }

    private void SetAndPush(double value)
    {
        Value = value;
        GetBindingExpression(ValueProperty)?.UpdateSource();
        // 受け取った側が値を丸めることがあるので、確定後の値を表示し直す
        GetBindingExpression(ValueProperty)?.UpdateTarget();
        Text = Value.ToString(Format, CultureInfo.CurrentCulture);
    }
}
