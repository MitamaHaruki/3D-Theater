using System.Windows;
using Theater3D.Editor;

namespace Theater3D.App.Shell;

/// <summary>
/// エディットモードだけで使うメニュー・ボタンの印（XAML で <c>shell:ModeOnly.Edit="True"</c>）。
/// ライブモードでは、印の付いた部品を出さない。
/// </summary>
public static class ModeOnly
{
    public static readonly DependencyProperty EditProperty =
        DependencyProperty.RegisterAttached("Edit", typeof(bool), typeof(ModeOnly), new PropertyMetadata(false));

    public static bool GetEdit(DependencyObject element) => (bool)element.GetValue(EditProperty);

    public static void SetEdit(DependencyObject element, bool value) => element.SetValue(EditProperty, value);

    /// <summary><paramref name="root"/> の中の印の付いた部品を、<paramref name="mode"/> に合わせて隠す。</summary>
    public static int Apply(DependencyObject root, AppMode mode)
    {
        if (mode == AppMode.Edit)
        {
            return 0;
        }
        int hidden = 0;
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            DependencyObject element = stack.Pop();
            if (GetEdit(element) && element is UIElement ui)
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
}
