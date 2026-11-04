using Theater3D.Domain;

namespace Theater3D.Editor;

public enum SelectMode
{
    /// <summary>今の選択を捨てて置き換える。</summary>
    Replace,
    /// <summary>今の選択に足す。</summary>
    Add,
    /// <summary>選ばれていれば外し、選ばれていなければ足す（Ctrl + クリック）。</summary>
    Toggle,
}

public sealed class SceneSelectionChangedEventArgs(object? source) : EventArgs
{
    /// <summary>選択を変えた側（シーン一覧・プレビューなど）。自分が起こした変更を見分けるのに使う。</summary>
    public object? Source { get; } = source;
}

/// <summary>
/// プレビュー・シーン一覧・タイムライン・プロパティで共有する選択状態。
/// 中身が同じになる変更では通知しないので、各画面が互いに選択を反映し合っても通知が往復し続けない。
/// </summary>
public sealed class SelectionService
{
    private readonly List<SceneNode> _items = [];

    public event EventHandler<SceneSelectionChangedEventArgs>? Changed;

    public IReadOnlyList<SceneNode> Items => _items;

    /// <summary>最後に選んだ項目（プロパティに出す対象）。</summary>
    public SceneNode? Primary => _items.Count > 0 ? _items[^1] : null;

    public bool Contains(SceneNode node) => _items.Contains(node);

    public void Select(SceneNode node, SelectMode mode = SelectMode.Replace, object? source = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        var next = new List<SceneNode>(_items);
        switch (mode)
        {
            case SelectMode.Replace:
                next.Clear();
                next.Add(node);
                break;
            case SelectMode.Add:
                next.Remove(node);
                next.Add(node);
                break;
            case SelectMode.Toggle:
                if (!next.Remove(node))
                {
                    next.Add(node);
                }
                break;
        }
        Commit(next, source);
    }

    public void SelectMany(IEnumerable<SceneNode> nodes, object? source = null) => Commit(nodes.Distinct().ToList(), source);

    public void Clear(object? source = null) => Commit([], source);

    private void Commit(List<SceneNode> next, object? source)
    {
        if (next.SequenceEqual(_items))
        {
            return;
        }
        _items.Clear();
        _items.AddRange(next);
        Changed?.Invoke(this, new SceneSelectionChangedEventArgs(source));
    }
}
