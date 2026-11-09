using System.Collections.ObjectModel;
using System.ComponentModel;
using Theater3D.App.Import;
using Theater3D.App.Logging;
using Theater3D.Domain;
using Theater3D.Editor;

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

    /// <summary>未登録の変更があれば「＊」を付けた名前（シーン一覧と同じ）。</summary>
    public string DisplayName => Node.HasUnregisteredChange ? "＊" + Node.Name : Node.Name;

    public string CategoryName { get; }

    public string ToolTip => Node.HasUnregisteredChange
        ? $"{Node.Name}（{CategoryName}・{Node.TypeName}）\n＊ キーに登録していない変更があります"
        : $"{Node.Name}（{CategoryName}・{Node.TypeName}）";

    public bool IsEditable => Node.CanEdit;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>今のフレームにキーがあるか。</summary>
    public bool HasKey => Node.Keys.HasKeyAt(_context.Playback.CurrentFrame);

    public string KeyGlyph => HasKey ? "◆" : "◇";

    public string KeyToolTip => HasKey
        ? $"フレーム {_context.Playback.CurrentFrame} のキーを、今の値で登録し直します"
        : $"フレーム {_context.Playback.CurrentFrame} に、今の値でキーを登録します";

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
            _context.Edits.Change($"{Node.Name}の値", [Node], () => Node.Weight = (float)value, mergeKey: $"{Node.Id}:値");
        }
    }

    public void Detach() => Node.PropertyChanged -= OnNodePropertyChanged;

    /// <summary>自分か親のどれかの鍵が変わった。</summary>
    public void OnLockChanged() => OnPropertyChanged(nameof(IsEditable));

    /// <summary>今のフレームが変わった。</summary>
    public void OnFrameChanged()
    {
        OnPropertyChanged(nameof(HasKey));
        OnPropertyChanged(nameof(KeyGlyph));
        OnPropertyChanged(nameof(KeyToolTip));
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MorphNode.Weight):
                OnPropertyChanged(nameof(Weight));
                break;
            case nameof(MorphNode.Keys):
                OnFrameChanged();
                break;
            case nameof(MorphNode.HasUnregisteredChange):
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(ToolTip));
                break;
        }
    }
}

/// <summary>プリセットの一覧の 1 行。割合（%）を変えると、混ぜた値をすぐ当てる。</summary>
public sealed class MorphPresetRow(MorphPanelViewModel owner, MorphPreset preset) : ObservableObject
{
    public MorphPreset Preset { get; } = preset;

    public string Name => Preset.Name;

    public string MatchText => owner.MatchText(Preset);

    public string ToolTip => $"{Preset.Name}（モーフ {Preset.Values.Count} 個の値を覚えています）\n{owner.MatchNote(Preset)}";

    /// <summary>混ぜる割合（0〜100%）。</summary>
    public double Ratio
    {
        get => owner.RatioOf(Preset) * 100;
        set => owner.SetRatio(Preset, (float)(value / 100));
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Ratio));
        OnPropertyChanged(nameof(MatchText));
        OnPropertyChanged(nameof(ToolTip));
    }
}

/// <summary>
/// モーフのパネル（指示書 4.3「モーフ専用パネルから値変更、プリセット・キーフレーム登録、複数値の混合」）。
/// 今操作しているモデルのモーフを、分類（目・まゆ・口・その他）と名前で絞り込んで並べ、つまみで値を変える。
/// 行の ◆ で今のフレームにキーを登録し、「まとめて登録」で未登録の変更を全部キーにする。
/// プリセット: 一覧に出ているモーフの値を名前を付けて覚え（利用者ごと、モデルを問わず名前で合わせる）、割合で混ぜて当てる。
/// 何も選んでいないときは、最後に操作したモデルを出したままにする。
/// </summary>
public sealed class MorphPanelViewModel : ObservableObject
{
    public const string AllCategories = "すべて";

    private readonly EditorContext _context;
    private readonly List<MorphRow> _allRows = [];
    private ModelNode? _model;
    private MorphMixSession? _mix;
    private string _category = AllCategories;
    private string _filter = string.Empty;
    private string _presetName = string.Empty;

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
        context.Playback.FrameChanged += (_, _) =>
        {
            foreach (MorphRow row in Rows)
            {
                row.OnFrameChanged();
            }
        };
        context.Animation.StateChanged += (_, _) => OnUnregisteredChanged();
        context.MorphPresets.Changed += (_, _) => RebuildPresets();
        RebuildPresets();
        OnSelectionChanged();
    }

    public ObservableCollection<MorphRow> Rows { get; } = [];

    public ObservableCollection<MorphPresetRow> Presets { get; } = [];

    public IReadOnlyList<string> Categories { get; } = [AllCategories, "目", "まゆ", "口", "その他", "システム"];

    public bool HasModel => _model is not null;

    public string Title => _model is null ? "モデルを選ぶと、そのモデルのモーフをここで動かせます。" : $"{_model.Name} のモーフ（{_model.Morphs.Count} 個）";

    public bool IsEmptyNoteVisible => _model is not null && Rows.Count == 0;

    public string EmptyNote => _model is { Morphs.Count: 0 } ? "このモデルにはモーフがありません。" : "条件に合うモーフがありません。";

    /// <summary>このモデルの、キーに登録していないモーフの数の知らせ（無ければ空）。</summary>
    public string UnregisteredNote
    {
        get
        {
            int count = _model?.Morphs.Count(m => m.HasUnregisteredChange) ?? 0;
            return count > 0 ? $"＊ 未登録の変更 {count} 個" : string.Empty;
        }
    }

    public bool HasUnregistered => _model?.Morphs.Any(m => m.HasUnregisteredChange) ?? false;

    public bool HasPresets => Presets.Count > 0;

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

    /// <summary>保存するプリセットの名前（空なら「表情 1」のように付ける）。</summary>
    public string PresetName
    {
        get => _presetName;
        set => SetProperty(ref _presetName, value ?? string.Empty);
    }

    /// <summary>今出しているモデルのモーフをすべて 0 に戻す（ロックしたものは変えない）。</summary>
    public void ResetAll()
    {
        if (_model is null)
        {
            return;
        }
        _context.Playback.Pause();
        MorphNode[] morphs = [.. _model.Morphs.Where(m => m.CanEdit)];
        _context.Edits.Change($"{_model.Name}のモーフをすべて 0 に", morphs, () =>
        {
            foreach (MorphNode morph in morphs)
            {
                morph.Weight = 0;
            }
        });
    }

    public void Select(MorphRow row) => _context.Selection.Select(row.Node, Editor.SelectMode.Replace, this);

    /// <summary>このモーフのキーを、今のフレームに今の値で登録する（元に戻せる）。</summary>
    public void RegisterKey(MorphRow row)
    {
        if (!row.Node.CanEdit)
        {
            return;
        }
        _context.Playback.Pause();
        int frame = _context.Playback.CurrentFrame;
        _context.Edits.Change($"「{row.Name}」のキーの登録", [row.Node], () => _context.Animation.Register([row.Node]), includeKeys: true);
        AppLog.Info($"フレーム {frame} に、モーフ「{row.Name}」のキーを登録しました");
    }

    /// <summary>このモデルのモーフの、未登録の変更を全部キーにする。登録した数を返す。</summary>
    public int RegisterUnregistered()
    {
        if (_model is null)
        {
            return 0;
        }
        MorphNode[] nodes = [.. _model.Morphs.Where(m => m.HasUnregisteredChange && m.CanEdit)];
        if (nodes.Length == 0)
        {
            return 0;
        }
        _context.Playback.Pause();
        int count = 0;
        _context.Edits.Change($"{_model.Name}のモーフの未登録の変更を登録", nodes, () => count = _context.Animation.Register(nodes), includeKeys: true);
        AppLog.Info($"フレーム {_context.Playback.CurrentFrame} に、モーフのキーを {count} 個登録しました");
        return count;
    }

    /// <summary>名前の欄が空のとき使う名前（「表情 1」「表情 2」…の空いているもの）。</summary>
    public string NextPresetName()
    {
        for (int i = 1; ; i++)
        {
            string name = $"表情 {i}";
            if (_context.MorphPresets.Find(name) is null)
            {
                return name;
            }
        }
    }

    /// <summary>保存しようとしている名前（使えなければ null）。</summary>
    public string? PendingPresetName => _presetName.Trim().Length == 0 ? NextPresetName() : MorphPreset.NormalizeName(_presetName);

    /// <summary>今一覧に出ているモーフ（絞り込みに合うもの）の値を、プリセットとして保存する。保存したら true。</summary>
    public bool SavePreset(string name)
    {
        if (_model is null || Rows.Count == 0 || MorphPreset.NormalizeName(name) is null)
        {
            return false;
        }
        MorphPreset preset = MorphPreset.Capture(name, Rows.Select(r => r.Node));
        _context.MorphPresets.Put(preset);
        PresetName = string.Empty;
        AppLog.Info($"表情のプリセット「{preset.Name}」を保存しました（モーフ {preset.Values.Count} 個）");
        return true;
    }

    public void DeletePreset(MorphPresetRow row)
    {
        if (_context.MorphPresets.Remove(row.Name))
        {
            AppLog.Info($"表情のプリセット「{row.Name}」を消しました");
        }
    }

    /// <summary>このプリセットだけを全部当てる（割合 100%。ほかの割合はそのまま）。</summary>
    public void ApplyPreset(MorphPresetRow row) => SetRatio(row.Preset, 1f);

    /// <summary>混ぜる割合を全部 0 にして、混ぜる前の値に戻す。</summary>
    public void ResetMix()
    {
        if (_mix is null)
        {
            return;
        }
        _context.Playback.Pause();
        foreach (MorphPresetRow row in Presets)
        {
            if (_mix.RatioOf(row.Name) > 0)
            {
                _mix.SetRatio(row.Preset, 0);
            }
        }
        RefreshPresetRows();
    }

    internal float RatioOf(MorphPreset preset) => _mix?.RatioOf(preset.Name) ?? 0f;

    internal void SetRatio(MorphPreset preset, float ratio)
    {
        if (_mix is null)
        {
            return;
        }
        _context.Playback.Pause(); // 再生中はキーの値で上書きされるので、止めてから混ぜる
        _mix.SetRatio(preset, ratio);
        Presets.FirstOrDefault(p => ReferenceEquals(p.Preset, preset))?.Refresh();
    }

    internal string MatchText(MorphPreset preset) => _model is null ? string.Empty : $"{preset.CountMatches(_model)}/{preset.Values.Count}";

    internal string MatchNote(MorphPreset preset) => _model is null
        ? "モデルを選ぶと、名前の合うモーフに当てられます"
        : $"「{_model.Name}」で名前の合うモーフ: {preset.CountMatches(_model)} 個（合わないものは使いません）";

    private void RebuildPresets()
    {
        _mix?.Clear(); // 一覧が変わったら、今の値を基準にし直す（値は変えない）
        Presets.Clear();
        foreach (MorphPreset preset in _context.MorphPresets.Presets)
        {
            Presets.Add(new MorphPresetRow(this, preset));
        }
        OnPropertyChanged(nameof(HasPresets));
    }

    private void RefreshPresetRows()
    {
        foreach (MorphPresetRow row in Presets)
        {
            row.Refresh();
        }
    }

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
        _mix?.Dispose();
        _mix = null;
        _model = model;
        if (model is not null)
        {
            _allRows.AddRange(model.Morphs.Select(m => new MorphRow(_context, m)));
            _mix = new MorphMixSession(model, _context.Edits);
            _mix.Cleared += (_, _) => RefreshPresetRows();
        }
        ApplyFilter();
        RefreshPresetRows();
        OnPropertyChanged(nameof(HasModel));
        OnPropertyChanged(nameof(Title));
        OnUnregisteredChanged();
    }

    private void OnUnregisteredChanged()
    {
        OnPropertyChanged(nameof(UnregisteredNote));
        OnPropertyChanged(nameof(HasUnregistered));
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
                row.OnFrameChanged(); // 隠れている間にフレームが変わったかもしれない
                Rows.Add(row);
            }
        }
        OnPropertyChanged(nameof(IsEmptyNoteVisible));
        OnPropertyChanged(nameof(EmptyNote));
    }
}
