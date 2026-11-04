using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Theater3D.App.Logging;
using Theater3D.Editor;

namespace Theater3D.App.Panels;

/// <summary>
/// シーン一覧。クリックで選択、Ctrl + クリックで追加・解除、Shift + クリックで範囲選択、
/// ダブルクリックか F2 で名前の変更。階層の組み替えは Phase 1 ではできない（読み込んだ構造を勝手に変えないため）。
/// </summary>
public partial class ScenePanel : UserControl
{
    private readonly EditorContext _context;
    private readonly SceneTreeItem _root;
    private SceneTreeItem? _rangeAnchor;

    public ScenePanel(EditorContext context)
    {
        InitializeComponent();
        _context = context;
        _root = new SceneTreeItem(context.Scene.Root, null);
        Tree.ItemsSource = new[] { _root };
        context.Selection.Changed += (_, _) => SyncSelection();
        SyncSelection();
    }

    private IEnumerable<SceneTreeItem> AllItems => _root.DescendantsAndSelf();

    /// <summary>今画面に見えている行を上から順に。</summary>
    private IEnumerable<SceneTreeItem> VisibleItems =>
        AllItems.Where(i => i.IsShownByFilter && Ancestors(i).All(a => a.IsExpanded && a.IsShownByFilter));

    private static IEnumerable<SceneTreeItem> Ancestors(SceneTreeItem item)
    {
        for (SceneTreeItem? p = item.Parent; p is not null; p = p.Parent)
        {
            yield return p;
        }
    }

    private void SyncSelection()
    {
        foreach (SceneTreeItem item in AllItems)
        {
            item.IsSelected = _context.Selection.Contains(item.Node);
            if (item.IsSelected)
            {
                foreach (SceneTreeItem ancestor in Ancestors(item))
                {
                    ancestor.IsExpanded = true;
                }
            }
        }
    }

    private void OnTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<ToggleButton>(source) is not null || FindAncestor<TextBox>(source) is not null)
        {
            return; // 目・鍵・開閉のボタンと、名前の入力欄はそのまま動かす
        }
        if (FindAncestor<TreeViewItem>(source)?.DataContext is not SceneTreeItem item)
        {
            return;
        }

        Tree.Focus();
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            BeginRename(item);
            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Shift) && _rangeAnchor is not null)
        {
            SelectRange(_rangeAnchor, item);
            return;
        }
        _rangeAnchor = item;
        _context.Selection.Select(item.Node, modifiers.HasFlag(ModifierKeys.Control) ? SelectMode.Toggle : SelectMode.Replace, this);
    }

    private void SelectRange(SceneTreeItem from, SceneTreeItem to)
    {
        List<SceneTreeItem> visible = [.. VisibleItems];
        int a = visible.IndexOf(from);
        int b = visible.IndexOf(to);
        if (a < 0 || b < 0)
        {
            _context.Selection.Select(to.Node, SelectMode.Replace, this);
            return;
        }
        IEnumerable<SceneTreeItem> range = a <= b ? visible.Skip(a).Take(b - a + 1) : visible.Skip(b).Take(a - b + 1).Reverse();
        _context.Selection.SelectMany(range.Select(i => i.Node), this);
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        SceneTreeItem? current = AllItems.FirstOrDefault(i => i.Node == _context.Selection.Primary);
        switch (e.Key)
        {
            case Key.F2 when current is not null:
                BeginRename(current);
                e.Handled = true;
                break;
            case Key.Up or Key.Down:
                List<SceneTreeItem> visible = [.. VisibleItems];
                if (visible.Count == 0)
                {
                    break;
                }
                int index = current is null ? -1 : visible.IndexOf(current);
                int next = Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, visible.Count - 1);
                _rangeAnchor = visible[next];
                _context.Selection.Select(visible[next].Node, SelectMode.Replace, this);
                e.Handled = true;
                break;
            case Key.Left when current is { IsExpanded: true, Children.Count: > 0 }:
                current.IsExpanded = false;
                e.Handled = true;
                break;
            case Key.Right when current is { IsExpanded: false, Children.Count: > 0 }:
                current.IsExpanded = true;
                e.Handled = true;
                break;
        }
    }

    private void BeginRename(SceneTreeItem item)
    {
        if (!item.CanRename)
        {
            return;
        }
        item.EditName = item.Name;
        item.IsRenaming = true;
    }

    private void OnRenameVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                box.Focus();
                box.SelectAll();
            });
        }
    }

    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: SceneTreeItem item })
        {
            return;
        }
        if (e.Key == Key.Enter)
        {
            CommitRename(item);
            Tree.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            item.IsRenaming = false;
            Tree.Focus();
            e.Handled = true;
        }
    }

    private void OnRenameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: SceneTreeItem { IsRenaming: true } item })
        {
            CommitRename(item);
        }
    }

    private static void CommitRename(SceneTreeItem item)
    {
        if (!item.IsRenaming)
        {
            return;
        }
        item.IsRenaming = false;
        if (item.EditName.Trim() == item.Name)
        {
            return;
        }
        try
        {
            item.Node.Name = item.EditName;
        }
        catch (ArgumentException ex)
        {
            AppLog.Warning($"名前を変えられませんでした: {ex.Message}");
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        string text = SearchBox.Text.Trim();
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _root.ApplyFilter(text);
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
            {
                return match;
            }
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }
}
