using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Panels;

/// <summary>シーン一覧の 1 行。シーンの項目（<see cref="SceneNode"/>）を画面用に包む。</summary>
public sealed class SceneTreeItem : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isShownByFilter = true;
    private bool _isRenaming;
    private string _editName = string.Empty;

    private readonly EditService _edits;

    public SceneTreeItem(SceneNode node, SceneTreeItem? parent, EditService edits)
    {
        Node = node;
        Parent = parent;
        _edits = edits;
        _isExpanded = !node.StartsCollapsed;
        Children = new ObservableCollection<SceneTreeItem>(node.Children.Select(c => new SceneTreeItem(c, this, edits)));
        node.PropertyChanged += OnNodePropertyChanged;
        ((INotifyCollectionChanged)node.Children).CollectionChanged += OnNodeChildrenChanged;
    }

    public SceneNode Node { get; }

    public SceneTreeItem? Parent { get; }

    public ObservableCollection<SceneTreeItem> Children { get; }

    public string Name => Node.Name;

    public string Icon => Node.Kind switch
    {
        SceneNodeKind.Camera => "",
        SceneNodeKind.Light => "",
        SceneNodeKind.Model => "",
        SceneNodeKind.TestObject => "",
        SceneNodeKind.Stage => "",
        SceneNodeKind.Bone => Node is BoneNode { Traits.IsIk: true } ? "" : "",
        SceneNodeKind.Morph => "",
        SceneNodeKind.Audio => "\uE8D6",
        SceneNodeKind.Display => "\uE7B3",
        SceneNodeKind.Effects => "\uE790",
        _ => Parent is null ? "" : "",
    };

    public bool IsVisible
    {
        get => Node.IsVisible;
        set => _edits.Change($"{Node.Name}の表示", [Node], () => Node.IsVisible = value);
    }

    public bool IsLocked
    {
        get => Node.IsLocked;
        set => _edits.Change($"{Node.Name}のロック", [Node], () => Node.IsLocked = value);
    }

    /// <summary>名前を変える（元に戻せる）。</summary>
    public void Rename(string name) => _edits.Change($"「{Node.Name}」の名前", [Node], () => Node.Name = name);

    public bool CanRename => Node.CanRename && Node.CanEdit;

    public bool CanToggleVisibility => Node.CanToggleVisibility;

    /// <summary>名前の後ろに小さく出す補足（モーフの値、動かしている印、未登録の変更の印「＊」）。</summary>
    public string Detail
    {
        get
        {
            string value = Node switch
            {
                MorphNode { Weight: > 0 } morph => morph.Weight.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture),
                BoneNode { IsPosed: true } => "●",
                _ => string.Empty,
            };
            return IsUnregistered ? "＊" + value : value;
        }
    }

    /// <summary>キーに登録していない変更がある。</summary>
    public bool IsUnregistered => Node is BoneNode { HasUnregisteredChange: true } or MorphNode { HasUnregisteredChange: true } or CameraNode { HasUnregisteredChange: true }
        or LightNode { HasUnregisteredChange: true } or ModelDisplayNode { HasUnregisteredChange: true } or EffectsNode { HasUnregisteredChange: true };

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsShownByFilter
    {
        get => _isShownByFilter;
        set => SetProperty(ref _isShownByFilter, value);
    }

    public bool IsRenaming
    {
        get => _isRenaming;
        set => SetProperty(ref _isRenaming, value);
    }

    public string EditName
    {
        get => _editName;
        set => SetProperty(ref _editName, value);
    }

    public IEnumerable<SceneTreeItem> DescendantsAndSelf()
    {
        yield return this;
        foreach (SceneTreeItem child in Children)
        {
            foreach (SceneTreeItem item in child.DescendantsAndSelf())
            {
                yield return item;
            }
        }
    }

    /// <summary>今の検索語で表示するかを決める。子が当たれば親も表示し、開いておく。</summary>
    public bool ApplyFilter(string text)
    {
        bool childMatched = false;
        foreach (SceneTreeItem child in Children)
        {
            childMatched |= child.ApplyFilter(text);
        }
        bool selfMatched = text.Length == 0 || Name.Contains(text, StringComparison.OrdinalIgnoreCase);
        IsShownByFilter = selfMatched || childMatched;
        if (childMatched && text.Length > 0)
        {
            IsExpanded = true;
        }
        return IsShownByFilter;
    }

    // モデルの読み込みなどで子が増減したら、行を合わせる（今ある行は開閉などの状態ごと使い回す）
    private void OnNodeChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var existing = Children.ToDictionary(c => c.Node);
        Children.Clear();
        foreach (SceneNode child in Node.Children)
        {
            Children.Add(existing.TryGetValue(child, out SceneTreeItem? item) ? item : new SceneTreeItem(child, this, _edits));
        }
        IsExpanded = true;
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SceneNode.Name):
                OnPropertyChanged(nameof(Name));
                break;
            case nameof(SceneNode.IsVisible):
                OnPropertyChanged(nameof(IsVisible));
                break;
            case nameof(MorphNode.Weight) or nameof(SceneNode.Position) or nameof(SceneNode.Rotation):
                OnPropertyChanged(nameof(Detail));
                break;
            case nameof(BoneNode.HasUnregisteredChange):
                OnPropertyChanged(nameof(Detail));
                OnPropertyChanged(nameof(IsUnregistered));
                break;
            case nameof(SceneNode.IsLocked):
                OnPropertyChanged(nameof(IsLocked));
                OnPropertyChanged(nameof(CanRename));
                break;
        }
    }
}
