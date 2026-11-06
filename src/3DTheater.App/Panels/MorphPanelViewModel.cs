using System.Collections.ObjectModel;
using System.ComponentModel;
using Theater3D.App.Import;
using Theater3D.Domain;

namespace Theater3D.App.Panels;

/// <summary>モーフのパネルの 1 行（モーフ 1 つ）。</summary>
public sealed class MorphRow : ObservableObject
{
    private readonly EditorContext _context;
    private bool _isSelected;

    public MorphRow(EditorContext context, MorphNode node)
    {
        _context = context;
        Node = node;
        CategoryName = ModelPartsBuilder.CategoryName(node.Category);
        node.PropertyChanged += OnNodePropertyChanged;
    }

    public MorphNode Node { get; }

    public string Name => Node.Name;

    public string CategoryName { get; }

    public string ToolTip => $"{Node.Name}（{CategoryName}・{Node.TypeName}）";

    public bool IsEditable => Node.CanEdit;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public double Weight
    {
        get => Node.Weight;
        set
        {
            if (!Node.CanEdit)
            {
                OnPropertyChanged(nameof(Weight));
                return;
            }
            _context.Playback.Pause(); // 再生中の編集は止めてから行う（指示書 3.4）
            Node.Weight = (float)value;
        }
    }

    public void Detach() => Node.PropertyChanged -= OnNodePropertyChanged;

    /// <summary>自分か親のどれかの鍵が変わった。</summary>
    public void OnLockChanged() => OnPropertyChanged(nameof(IsEditable));

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MorphNode.Weight))
        {
            OnPropertyChanged(nameof(Weight));
        }
    }
}

/// <summary>
/// モーフのパネル。今操作しているモデルのモーフを、分類（目・まゆ・口・その他）と名前で絞り込んで並べ、つまみで値を変える。
/// 何も選んでいないときは、最後に操作したモデルを出したままにする。
/// </summary>
public sealed class MorphPanelViewModel : ObservableObject
{
    public const string AllCategories = "すべて";

    private readonly EditorContext _context;
    private readonly List<MorphRow> _allRows = [];
    private ModelNode? _model;
    private string _category = AllCategories;
    private string _filter = string.Empty;

    public MorphPanelViewModel(EditorContext context)
    {
        _context = context;
        context.Selection.Changed += (_, _) => OnSelectionChanged();
        context.Scene.NodeChanged += (_, e) =>
        {
            // 鍵はまとまり（「表情」など）に掛けることもあるので、どれかの鍵が変わったら行の状態を見直す
            if (e.PropertyName == nameof(SceneNode.IsLocked))
            {
                foreach (MorphRow row in _allRows)
                {
                    row.OnLockChanged();
                }
            }
        };
        OnSelectionChanged();
    }

    public ObservableCollection<MorphRow> Rows { get; } = [];

    public IReadOnlyList<string> Categories { get; } = [AllCategories, "目", "まゆ", "口", "その他", "システム"];

    public bool HasModel => _model is not null;

    public string Title => _model is null ? "モデルを選ぶと、そのモデルのモーフをここで動かせます。" : $"{_model.Name} のモーフ（{_model.Morphs.Count} 個）";

    public bool IsEmptyNoteVisible => _model is not null && Rows.Count == 0;

    public string EmptyNote => _model is { Morphs.Count: 0 } ? "このモデルにはモーフがありません。" : "条件に合うモーフがありません。";

    public string Category
    {
        get => _category;
        set
        {
            if (SetProperty(ref _category, value ?? AllCategories))
            {
                ApplyFilter();
            }
        }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>今出しているモデルのモーフをすべて 0 に戻す（ロックしたものは変えない）。</summary>
    public void ResetAll()
    {
        if (_model is null)
        {
            return;
        }
        _context.Playback.Pause();
        foreach (MorphNode morph in _model.Morphs.Where(m => m.CanEdit))
        {
            morph.Weight = 0;
        }
    }

    public void Select(MorphRow row) => _context.Selection.Select(row.Node, Editor.SelectMode.Replace, this);

    private void OnSelectionChanged()
    {
        ModelNode? active = _context.ActiveModel;
        if (active is not null && !ReferenceEquals(active, _model))
        {
            SetModel(active);
        }
        else if (_model is not null && _model.Parent is null)
        {
            SetModel(null); // シーンから外れたモデル
        }
        foreach (MorphRow row in _allRows)
        {
            row.IsSelected = _context.Selection.Contains(row.Node);
        }
    }

    private void SetModel(ModelNode? model)
    {
        foreach (MorphRow row in _allRows)
        {
            row.Detach();
        }
        _allRows.Clear();
        _model = model;
        if (model is not null)
        {
            _allRows.AddRange(model.Morphs.Select(m => new MorphRow(_context, m)));
        }
        ApplyFilter();
        OnPropertyChanged(nameof(HasModel));
        OnPropertyChanged(nameof(Title));
    }

    private void ApplyFilter()
    {
        string text = _filter.Trim();
        Rows.Clear();
        foreach (MorphRow row in _allRows)
        {
            bool categoryMatches = _category == AllCategories || row.CategoryName == _category;
            bool textMatches = text.Length == 0 || row.Name.Contains(text, StringComparison.OrdinalIgnoreCase);
            if (categoryMatches && textMatches)
            {
                Rows.Add(row);
            }
        }
        OnPropertyChanged(nameof(IsEmptyNoteVisible));
        OnPropertyChanged(nameof(EmptyNote));
    }
}
