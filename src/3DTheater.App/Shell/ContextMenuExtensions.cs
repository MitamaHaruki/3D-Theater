using System.Windows.Controls;

namespace Theater3D.App.Shell;

/// <summary>外から項目を足せる、アプリの右クリックのメニュー。</summary>
public enum MenuPlace
{
    /// <summary>プレビューの右クリックと、区画の見出しの視点のボタン。</summary>
    Preview,

    /// <summary>シーンの一覧の行の右クリック。</summary>
    SceneTree,
}

/// <summary>
/// アプリの右クリックのメニューに、外から項目を足す（プラグインの項目。指示書 7.2 の「コンテキストメニュー」）。
/// メニューを開くたびに項目を作る（そのときに使えるかどうかを決めるため）。
/// </summary>
public sealed class ContextMenuExtensions
{
    private readonly List<Func<MenuPlace, IEnumerable<Control>>> _sources = [];

    /// <summary>項目の出どころを足す（メニューを開くたびに呼ぶ）。</summary>
    public void AddSource(Func<MenuPlace, IEnumerable<Control>> source) => _sources.Add(source);

    /// <summary>メニューの後ろに、区切り線をはさんで外の項目を足す。足した数を返す。</summary>
    public int AppendTo(ItemsControl menu, MenuPlace place)
    {
        List<Control> items = [.. _sources.SelectMany(source => source(place))];
        if (items.Count == 0)
        {
            return 0;
        }
        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new Separator());
        }
        foreach (Control item in items)
        {
            menu.Items.Add(item);
        }
        return items.Count;
    }
}
