using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using AvalonDock.Controls;
using AvalonDock.Layout;

namespace Theater3D.App.Shell;

/// <summary>
/// 画面の読み上げ（UI オートメーション）で出る名前を付ける（設計判断記録 95 章）。アプリの起動のときに 1 回 <see cref="Register"/> する。
/// <list type="bullet">
/// <item>AvalonDock のパネルのタブ: そのままでは中身の型の名前（「AvalonDock.Layout.LayoutAnchorable」）が出るので、パネルの題名にする。</item>
/// <item>絵だけのボタン（パネルの × や、ツールバーの記号のボタン）: 名前が無いか、記号の字（読めない字）が名前になっているので、
/// マウスを載せたときに出る説明の文（ToolTip）を名前にする。</item>
/// </list>
/// どちらも、名前を自分で決めてある部品（<see cref="AutomationProperties.NameProperty"/>）は変えない。
/// </summary>
public static class AutomationNames
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }
        _registered = true;
        // 窓を開くたび・パネルを動かすたびに部品が作り直されるので、部品ごとに、初めて画面に並べたとき（大きさが決まったとき）に付ける。
        // 「読み込まれたとき」（Loaded）は使えない。WPF は、Loaded を個別に待っている部品にしか送らないため（型ごとの受け口では届かない）
        EventManager.RegisterClassHandler(typeof(TabItem), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(OnTabShown));
        EventManager.RegisterClassHandler(typeof(ButtonBase), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(OnButtonShown));
    }

    /// <summary>
    /// 読める字か（ボタンの中身の字が、そのまま名前になってよいか）。空白と、記号の字（Segoe の絵の字が使う、私用の領域 U+E000〜U+F8FF）だけなら読めない。
    /// </summary>
    public static bool IsReadableText(string? text) =>
        text is not null && text.Any(c => !char.IsWhiteSpace(c) && c is not (>= '' and <= ''));

    private static void OnTabShown(object sender, SizeChangedEventArgs e)
    {
        // AvalonDock のパネル・文書の並びのタブだけ（パネルの中の、ふつうのタブは変えない）
        if (sender is TabItem tab && !HasOwnName(tab)
            && ItemsControl.ItemsControlFromItemContainer(tab) is LayoutAnchorablePaneControl or LayoutDocumentPaneControl
            && tab.DataContext is LayoutContent)
        {
            tab.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(LayoutContent.Title)));
        }
    }

    private static void OnButtonShown(object sender, SizeChangedEventArgs e)
    {
        // チェックと丸の選び（CheckBox・RadioButton）は、横の字が名前になる作りなので変えない
        if (sender is ButtonBase button and not (CheckBox or RadioButton) && !HasOwnName(button)
            && !IsReadableText(button.Content as string) && button.ToolTip is not null)
        {
            // 説明の文が後から変わる（最大化 ↔ 元に戻す など）ので、決まった字ではなくバインドにする
            button.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(FrameworkElement.ToolTip))
            {
                RelativeSource = RelativeSource.Self,
                Converter = ToolTipText.Instance,
            });
        }
    }

    // 名前を決めてあるか（決めた字・バインド。こちらで付けたバインドも、ここで「ある」になるので、2 回付けない）
    private static bool HasOwnName(DependencyObject element) =>
        element.ReadLocalValue(AutomationProperties.NameProperty) != DependencyProperty.UnsetValue;

    // 説明の文（字のときだけ。字でなければ空にして、WPF の初めの決まりの名前にする）
    private sealed class ToolTipText : IValueConverter
    {
        public static readonly ToolTipText Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value as string ?? string.Empty;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
