using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Theater3D.Editor;

namespace Theater3D.App.Controls;

/// <summary>
/// 色（#RRGGBB）の入力欄（設計判断記録 87 章）。読める色になったら、その場で入れる（打ちながら色が変わる）。
/// 打っている途中の読めない字（「#FF00」など）は、そのまま残す（元の色に書き戻さない）。Enter かフォーカスを外したときに
/// 表示を整え、読めない字なら元の色に戻す。Esc で元に戻す。書き換えていないときは、外から変わった色（元に戻すなど）をすぐ出す。
/// 横に <see cref="ColorPickerButton"/> を置くと、パレットからも選べる。
/// </summary>
public sealed class ColorBox : TextBox
{
    // この欄が最後に自分で出した文字（利用者が書き換えていなければ Text と同じ）
    private string? _shownText;
    private bool _showing;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(ColorBox),
        new FrameworkPropertyMetadata("#FFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged)
        {
            DefaultUpdateSourceTrigger = UpdateSourceTrigger.Explicit,
        });

    public ColorBox()
    {
        SetResourceReference(StyleProperty, typeof(TextBox)); // ふつうの文字の欄と同じ見た目
        MinHeight = 0;
        VerticalContentAlignment = VerticalAlignment.Center;
        ToolTip = "#RRGGBB（白 = #FFFFFF）。左の見本を押すと、パレットから選べます";
        ShowValue();
    }

    /// <summary>今の色（「#RRGGBB」）。</summary>
    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        NumberBox.HideButtons(this); // Fluent の「×」（消す）ボタンは出さない（狭い欄で字が隠れる）
    }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        base.OnTextChanged(e);
        if (_showing || IsReadOnly)
        {
            return;
        }
        if (ImageExport.TryParseColor(Text, out Vector3 color))
        {
            Push(ImageExport.FormatColor(color)); // 読める色になったら入れる（字はそのまま）
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ShowValue(force: true);
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
        ShowValue(force: true); // 読めない字は元の色に戻し、読める字は「#RRGGBB」に整える
        base.OnLostKeyboardFocus(e);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ColorBox)d).ShowValue();

    private bool IsEdited => Text != _shownText;

    private void ShowValue(bool force = false)
    {
        if (force || !IsKeyboardFocusWithin || !IsEdited)
        {
            _showing = true;
            try
            {
                _shownText = Value;
                Text = Value;
            }
            finally
            {
                _showing = false;
            }
        }
    }

    private void Push(string value)
    {
        Value = value;
        GetBindingExpression(ValueProperty)?.UpdateSource();
        GetBindingExpression(ValueProperty)?.UpdateTarget(); // 受け取った側が入れなかった（ロック中など）ときは、元の色に戻る
    }
}
