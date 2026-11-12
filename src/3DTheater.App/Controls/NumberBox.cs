using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Theater3D.App.Controls;

/// <summary>
/// 数値の入力欄。Enter かフォーカスを外したときに確定し、Esc で元に戻す。
/// 正しくない文字は確定せず元の値に戻す。ホイールで <see cref="Step"/> ずつ増減（Shift で 1/10、Ctrl で 10 倍）。
/// 書き換えていないとき（入力の焦点があっても）は、外から変わった値（元に戻すなど）をすぐ出し、焦点を外しても確定し直さない。
/// </summary>
public sealed class NumberBox : TextBox
{
    // この欄が最後に自分で出した文字（利用者が書き換えていなければ Text と同じ）
    private string? _shownText;

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

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // Fluent の見た目は入力中に「×」（消す）ボタンを出すが、数値の欄は狭く、値の先頭（- など）が隠れるので出さない
        HideButtons(this);
    }

    private static void HideButtons(DependencyObject parent)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is System.Windows.Controls.Primitives.ButtonBase button)
            {
                button.Visibility = Visibility.Collapsed;
                continue;
            }
            HideButtons(child);
        }
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
            ShowValue(force: true);
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

    /// <summary>利用者が、この欄の出した文字を書き換えたか（入力中か）。</summary>
    private bool IsEdited => Text != _shownText;

    // 入力中（書き換えた後）は、外から値が変わっても書き換えない。書き換えていなければ、焦点があっても新しい値を出す
    // （焦点のある欄の値を、編集のメニューの「元に戻す」で戻したときなど）
    private void ShowValue(bool force = false)
    {
        if (force || !IsKeyboardFocusWithin || !IsEdited)
        {
            SetText(Value.ToString(Format, CultureInfo.CurrentCulture));
        }
    }

    private void SetText(string text)
    {
        _shownText = text;
        Text = text;
    }

    private void Commit()
    {
        // 書き換えていなければ確定し直さない（外から変わった値を、古い文字で上書きしないため）
        if (IsReadOnly || !IsEdited)
        {
            ShowValue(force: true);
            return;
        }
        string text = Text.Trim().TrimEnd('°', ' ', 'm', '%');
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double parsed)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
        {
            if (double.IsFinite(parsed))
            {
                SetAndPush(parsed);
            }
        }
        SetText(Value.ToString(Format, CultureInfo.CurrentCulture));
    }

    private void SetAndPush(double value)
    {
        Value = value;
        GetBindingExpression(ValueProperty)?.UpdateSource();
        // 受け取った側が値を丸めることがあるので、確定後の値を表示し直す
        GetBindingExpression(ValueProperty)?.UpdateTarget();
        SetText(Value.ToString(Format, CultureInfo.CurrentCulture));
    }
}
