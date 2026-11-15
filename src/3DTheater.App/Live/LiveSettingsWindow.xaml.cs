using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Theater3D.App.Live;

/// <summary>
/// 設定画面の 1 項目。<paramref name="Build"/> は、選ばれるたび・並べ直すたびに中身を作る（引数は、並べ直してほしいときに呼ぶ処理）。
/// <paramref name="OwnScroll"/> なら、中身が自分で縦に動く（キー操作のパネルなど）。
/// </summary>
public sealed record LiveSettingsPage(string Title, Func<Action, FrameworkElement> Build, bool OwnScroll = false);

/// <summary>
/// ライブモードの設定画面（設計判断記録 78 章）。左の一覧で選んだ項目の中身を、右に出す。
/// メインの窓を持ち主にした、別の窓（開いたまま、プレビューを見ながら変えられる）。
/// </summary>
public partial class LiveSettingsWindow : Window
{
    // 押した処理が後から状態を変えるもの（仮想カメラを入れるなど、時間のかかる処理）のため、少し後にもう一度並べ直す
    private static readonly TimeSpan LateRefresh = TimeSpan.FromSeconds(1.5);

    private readonly IReadOnlyList<LiveSettingsPage> _pages;
    private readonly DispatcherTimer _lateRefresh;

    public LiveSettingsWindow(IReadOnlyList<LiveSettingsPage> pages, string? firstPage = null)
    {
        InitializeComponent();
        _pages = pages;
        _lateRefresh = new DispatcherTimer(LateRefresh, DispatcherPriority.Normal, (_, _) =>
        {
            if (Mouse.LeftButton == MouseButtonState.Pressed)
            {
                return; // 押している最中に並べ直すと、押した物が消えて押せなくなるので、離すまで待つ
            }
            _lateRefresh!.Stop();
            ShowPage(keepScroll: true);
        }, Dispatcher);
        _lateRefresh.Stop();
        foreach (LiveSettingsPage page in pages)
        {
            PageList.Items.Add(new ListBoxItem { Content = page.Title, Padding = new Thickness(10, 7, 10, 7) });
        }
        int first = firstPage is null ? 0 : Math.Max(0, pages.ToList().FindIndex(p => p.Title == firstPage));
        PageList.SelectedIndex = pages.Count > 0 ? first : -1;
        Closed += (_, _) =>
        {
            _lateRefresh.Stop();
            PageHost.Content = null; // パネル（キー操作）を外しておく（次に開いたときに、また置けるように）
        };
    }

    /// <summary>今選んでいる項目の名前。</summary>
    public string? CurrentPage => PageList.SelectedIndex >= 0 ? _pages[PageList.SelectedIndex].Title : null;

    /// <summary>項目を選ぶ（名前で）。</summary>
    public void SelectPage(string title)
    {
        int index = _pages.ToList().FindIndex(p => p.Title == title);
        if (index >= 0)
        {
            PageList.SelectedIndex = index;
        }
    }

    private void OnPageChanged(object sender, SelectionChangedEventArgs e) => ShowPage(keepScroll: false);

    private void ShowPage(bool keepScroll)
    {
        if (PageList.SelectedIndex < 0)
        {
            return;
        }
        LiveSettingsPage page = _pages[PageList.SelectedIndex];
        double offset = keepScroll ? PageScroll.VerticalOffset : 0;
        PageHost.Content = null; // 前の中身を外してから作る（同じパネルを置き直すため）
        PageTitle.Text = page.Title;
        PageScroll.VerticalScrollBarVisibility = page.OwnScroll ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        PageHost.Content = page.Build(Refresh);
        if (keepScroll)
        {
            PageScroll.UpdateLayout();
            PageScroll.ScrollToVerticalOffset(offset);
        }
        else
        {
            PageScroll.ScrollToTop();
        }
    }

    // 押した後に並べ直す（すぐと、少し後）。並べ直しは、押したボタンの処理が終わってから
    private void Refresh()
    {
        Dispatcher.BeginInvoke(() => ShowPage(keepScroll: true), DispatcherPriority.Background);
        _lateRefresh.Stop();
        _lateRefresh.Start();
    }
}
