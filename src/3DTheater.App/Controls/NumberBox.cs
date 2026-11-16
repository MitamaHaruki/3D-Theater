using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Theater3D.App.Controls;

/// <summary>
/// 数値の入力欄。Enter かフォーカスを外したときに確定し、Esc で元に戻す。
/// 正しくない文字は確定せず元の値に戻す。ホイールで <see cref="Step"/> ずつ増減（Shift で 1/10、Ctrl で 10 倍）。
/// 字を打っていないときは、左右にドラッグして値を変えられる（右で増え、左で減る。Shift で 1/10、Ctrl で 10 倍。設計判断記録 87 章）。
/// 押して動かさずに離すと、字を打つ欄になる。
/// 書き換えていないとき（入力の焦点があっても）は、外から変わった値（元に戻すなど）をすぐ出し、焦点を外しても確定し直さない。
/// </summary>
public sealed class NumberBox : TextBox
{
    // この欄が最後に自分で出した文字（利用者が書き換えていなければ Text と同じ）
    private string? _shownText;

    // 左右のドラッグ（押した所と、そのときの値。動かし始めたら _dragging）
    private Point? _pressPoint;
    private double _dragStart;
    private bool _dragging;

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
        // 文字の欄が I の形に決めた後で、形を決め直す（決め終わった知らせも受け取る）
        AddHandler(Mouse.QueryCursorEvent, new QueryCursorEventHandler(OnQueryCursorChanged), handledEventsToo: true);
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

    /// <summary>Fluent の見た目の、文字の欄の中のボタン（「×」など）を隠す。</summary>
    internal static void HideButtons(DependencyObject parent)
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

    /// <summary>ドラッグで値を変えられるか（字を打っている間・変えられないときは、ふつうの文字の欄）。</summary>
    private bool CanDrag => !IsReadOnly && IsEnabled && !IsKeyboardFocusWithin;

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (!CanDrag || e.Handled)
        {
            return;
        }
        _pressPoint = e.GetPosition(this);
        _dragStart = Value;
        _dragging = false;
        CaptureMouse();
        e.Handled = true; // 押しただけでは字の欄にしない（離したときに決める）
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (_pressPoint is not { } start || !IsMouseCaptured)
        {
            return; // 押している間（マウスをつかんでいる間）だけ。離すと、つかむのをやめる
        }
        double dx = e.GetPosition(this).X - start.X;
        if (!_dragging && Math.Abs(dx) < Theater3D.Editor.NumberDrag.Threshold)
        {
            return;
        }
        _dragging = true;
        ModifierKeys modifiers = Keyboard.Modifiers;
        double next = Theater3D.Editor.NumberDrag.ValueAt(_dragStart, dx, Step,
            fine: modifiers.HasFlag(ModifierKeys.Shift), coarse: modifiers.HasFlag(ModifierKeys.Control));
        if (next != Value)
        {
            SetAndPush(next);
        }
        e.Handled = true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (_pressPoint is null)
        {
            return;
        }
        bool dragged = _dragging;
        EndDrag();
        if (!dragged)
        {
            Focus(); // 動かさずに離したら、字を打つ（全部を選ぶ）
        }
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _pressPoint = null;
        _dragging = false;
    }

    // 字を打っていないときは、左右の矢印の形にして、ドラッグできることを見せる
    private void OnQueryCursorChanged(object sender, QueryCursorEventArgs e)
    {
        if (CanDrag || _dragging)
        {
            e.Cursor = Cursors.SizeWE;
            e.Handled = true;
        }
    }

    private void EndDrag()
    {
        _pressPoint = null;
        _dragging = false;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
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
