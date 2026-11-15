using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Theater3D.Editor;

namespace Theater3D.App.Live;

/// <summary>
/// メニューの中身を、設定画面のボタンとチェックの並びにする（ライブモード。設計判断記録 78 章）。
/// ライブモードはメニューバーを出さないので、作り直すまでの間、メニューにあった物を設定画面から使えるようにする。
/// 押すと、メニューの項目を押したのと同じ処理が動く（処理はメニューの 1 か所だけに置いたまま）。
/// </summary>
internal static class MenuMirror
{
    /// <summary>
    /// <paramref name="menu"/> の中の項目を並べる。押した後は <paramref name="refresh"/> で並べ直す
    /// （チェックの状態などが変わるため）。隠れている項目（エディットだけの物など）は出さない。
    /// </summary>
    public static FrameworkElement Build(MenuItem menu, Action refresh)
    {
        Open(menu);
        var panel = new StackPanel();
        AddItems(panel, menu, refresh);
        return panel;
    }

    // 開いたときに中身を作り直す・チェックを合わせるメニューがあるので、開いたことにしてから読む
    private static void Open(MenuItem menu) => menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, menu));

    private static void AddItems(Panel panel, ItemsControl parent, Action refresh)
    {
        bool gap = false;
        foreach (object child in parent.Items)
        {
            if (child is Separator { Visibility: Visibility.Visible })
            {
                gap = panel.Children.Count > 0;
                continue;
            }
            if (child is not MenuItem { Visibility: Visibility.Visible } item)
            {
                continue;
            }
            if (gap)
            {
                panel.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 6, 0, 10),
                    Background = (Brush)Application.Current.FindResource("BorderBrush"),
                });
                gap = false;
            }
            panel.Children.Add(Row(item, refresh));
        }
    }

    private static FrameworkElement Row(MenuItem item, Action refresh)
    {
        string text = MenuText.Clean(item.Header as string ?? item.Header?.ToString());
        string? description = item.ToolTip as string;
        bool planned = item.InputGestureText == "予定";
        if (item.HasItems)
        {
            // 下の段のあるメニュー（照明を追加・配信用の画面の大きさなど）: 見出しと、その中の項目
            Open(item);
            var group = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
            group.Children.Add(new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.FindResource("SubTextBrush"),
                Margin = new Thickness(0, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap,
            });
            if (!string.IsNullOrEmpty(description))
            {
                group.Children.Add(Description(description));
            }
            var inner = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
            AddItems(inner, item, refresh);
            group.Children.Add(inner);
            return group;
        }

        FrameworkElement control;
        if (item.IsCheckable)
        {
            var check = new CheckBox { Content = text, IsChecked = item.IsChecked, IsEnabled = item.IsEnabled, VerticalAlignment = VerticalAlignment.Center };
            // 押したときだけでなく、画面の読み上げなど（UI オートメーション）で切り替えたときも動くよう、印の変化で呼ぶ
            void Changed(object sender, RoutedEventArgs e)
            {
                item.IsChecked = check.IsChecked == true; // メニューを押したときと同じく、先に印を変えてから処理を呼ぶ
                Invoke(item, refresh);
            }
            check.Checked += Changed;
            check.Unchecked += Changed;
            control = check;
        }
        else
        {
            var button = new Button
            {
                Content = text,
                IsEnabled = item.IsEnabled,
                MinWidth = 180,
                HorizontalAlignment = HorizontalAlignment.Left,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 5, 12, 5),
            };
            button.Click += (_, _) => Invoke(item, refresh);
            control = button;
        }
        AutomationProperties.SetName(control, text);
        if (!string.IsNullOrEmpty(description))
        {
            AutomationProperties.SetHelpText(control, description);
        }

        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var line = new DockPanel { LastChildFill = false };
        line.Children.Add(control);
        string gesture = planned ? "（予定）" : item.InputGestureText;
        if (!string.IsNullOrEmpty(gesture))
        {
            line.Children.Add(new TextBlock
            {
                Text = gesture,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.FindResource("SubTextBrush"),
            });
        }
        row.Children.Add(line);
        if (!string.IsNullOrEmpty(description))
        {
            row.Children.Add(Description(description));
        }
        return row;
    }

    // 説明（メニューではマウスを乗せると出ていたもの）を、項目の下に小さく出す
    private static TextBlock Description(string text) => new()
    {
        Text = text,
        Margin = new Thickness(2, 4, 0, 0),
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.FindResource("SubTextBrush"),
    };

    private static void Invoke(MenuItem item, Action refresh)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
        refresh();
    }
}
