using System.Collections;
using System.Windows;
using System.Windows.Controls;
using Theater3D.App.Shell;

namespace Theater3D.App.Live;

/// <summary>
/// エディットモードの設定の窓（影・霧・映像効果など）の中身を借りて、ライブモードの設定画面の右側に出す（設計判断記録 78 章の 6 段目）。
/// 窓は見せずに作り、中身だけを外して使う。値を変えたときの処理は、窓（の裏のコード）がそのまま行う。
/// エディットモードは今まで通り、その窓を開く（手を入れずに済む）。
/// 中身は一度だけ作って使い回すので、ほかの所で値が変わったら <see cref="ShowCurrentValues"/> で出し直す。
/// </summary>
/// <param name="header">中身の上に足す物（影の項目の、セルフシャドウと地面の影の切り替えなど）。</param>
internal sealed class BorrowedContent(Func<Window> create, Func<FrameworkElement>? header = null)
{
    private Window? _window;
    private FrameworkElement? _content;

    /// <summary>借りた中身（初めて要るときに窓を作る。前に置いた所からは外す）。</summary>
    public FrameworkElement Content
    {
        get
        {
            if (_content is null)
            {
                _window = create();
                FrameworkElement body = Take(_window);
                if (header is null)
                {
                    _content = body;
                }
                else
                {
                    var stack = new StackPanel();
                    stack.Children.Add(header());
                    stack.Children.Add(body);
                    _content = stack;
                }
            }
            Detach(_content);
            return _content;
        }
    }

    /// <summary>設定の今の値を出し直す（まだ借りていなければ何もしない。次に借りるときに今の値で作る）。</summary>
    public void ShowCurrentValues() => (_window as IShowsCurrentValues)?.ShowCurrentValues();

    // 窓の中身を外す。窓の Resources を中身に写し（後から探す見た目のため）、「閉じる」（Esc で閉じるボタン）は隠す
    // （設定画面では、ほかの項目を選ぶか設定画面を閉じれば終わる）。中身は、設定画面の幅に広がりすぎないようにする
    private static FrameworkElement Take(Window window)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        foreach (DictionaryEntry entry in window.Resources)
        {
            if (!content.Resources.Contains(entry.Key))
            {
                content.Resources[entry.Key] = entry.Value;
            }
        }
        foreach (Button button in Descendants(content).OfType<Button>().Where(b => b.IsCancel))
        {
            button.Visibility = Visibility.Collapsed;
        }
        content.MaxWidth = 620;
        content.HorizontalAlignment = HorizontalAlignment.Left;
        return content;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject d)
            {
                yield return d;
                foreach (DependencyObject below in Descendants(d))
                {
                    yield return below;
                }
            }
        }
    }

    private static void Detach(FrameworkElement element)
    {
        switch (element.Parent)
        {
            case Panel panel:
                panel.Children.Remove(element);
                break;
            case ContentControl control:
                control.Content = null;
                break;
            case Decorator decorator:
                decorator.Child = null;
                break;
        }
    }
}
