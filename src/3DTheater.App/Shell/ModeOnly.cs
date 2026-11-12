using System.Windows;
using Theater3D.Editor;

namespace Theater3D.App.Shell;

/// <summary>
/// 片方のモードだけで使う部品の印。XAML で <c>shell:ModeOnly.Edit="True"</c>（エディットだけ）・<c>shell:ModeOnly.Live="True"</c>（ライブだけ）。
/// 起動したときのモード（<see cref="CurrentMode"/>。画面を作る前に決める）と違えば、作られるときに隠す。
/// テンプレートの中の部品（モーフ欄の行の「◆」など）にも効く。表示を結び付けている部品は、読み込まれたときにもう一度隠す
/// （後から結び付いた表示の切り替えに勝つため）。
/// </summary>
public static class ModeOnly
{
    public static readonly DependencyProperty EditProperty =
        DependencyProperty.RegisterAttached("Edit", typeof(bool), typeof(ModeOnly), new PropertyMetadata(false, OnEditChanged));

    public static readonly DependencyProperty LiveProperty =
        DependencyProperty.RegisterAttached("Live", typeof(bool), typeof(ModeOnly), new PropertyMetadata(false, OnLiveChanged));

    /// <summary>この起動のモード（App が画面を作る前に決める）。</summary>
    public static AppMode CurrentMode { get; set; } = AppMode.Edit;

    public static bool GetEdit(DependencyObject element) => (bool)element.GetValue(EditProperty);

    public static void SetEdit(DependencyObject element, bool value) => element.SetValue(EditProperty, value);

    public static bool GetLive(DependencyObject element) => (bool)element.GetValue(LiveProperty);

    public static void SetLive(DependencyObject element, bool value) => element.SetValue(LiveProperty, value);

    /// <summary>このモードで隠す印か。</summary>
    public static bool IsHidden(DependencyObject element, AppMode mode) =>
        (GetEdit(element) && mode != AppMode.Edit) || (GetLive(element) && mode != AppMode.Live);

    /// <summary><paramref name="root"/> の中の、<paramref name="mode"/> で隠す部品を隠して、その数を返す。</summary>
    public static int Apply(DependencyObject root, AppMode mode)
    {
        int hidden = 0;
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            DependencyObject element = stack.Pop();
            if (IsHidden(element, mode) && element is UIElement ui)
            {
                ui.Visibility = Visibility.Collapsed;
                hidden++;
                continue;
            }
            foreach (object child in LogicalTreeHelper.GetChildren(element))
            {
                if (child is DependencyObject d)
                {
                    stack.Push(d);
                }
            }
        }
        return hidden;
    }

    private static void OnEditChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && CurrentMode != AppMode.Edit)
        {
            Hide(d);
        }
    }

    private static void OnLiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && CurrentMode != AppMode.Live)
        {
            Hide(d);
        }
    }

    private static void Hide(DependencyObject d)
    {
        if (d is not UIElement ui)
        {
            return;
        }
        ui.Visibility = Visibility.Collapsed;
        if (ui is FrameworkElement element)
        {
            element.Loaded += (sender, _) => ((UIElement)sender).Visibility = Visibility.Collapsed;
        }
    }
}
