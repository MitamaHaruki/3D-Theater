using System.Collections.ObjectModel;
using System.Windows.Media;
using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Panels;

/// <summary>グラフの線 1 本の表示の切り替え（凡例）。</summary>
public sealed class GraphSeriesItem(string name, Brush brush, bool isVisible) : ObservableObject
{
    private bool _isVisible = isVisible;

    public string Name { get; } = name;

    public Brush Brush { get; } = brush;

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }
}

/// <summary>
/// グラフエディタの中身。対象は、タイムラインで選んだキーの項目（無ければ、シーン一覧で選んだボーン・モーフ・カメラ）。
/// 編集するのは、選んだキーの補間曲線（前のキーからこのキーまでの動き方。MMD と同じ）。キーを複数選んでいれば、全部に同じ曲線を当てる。
/// キーを選んでいないときは、今のフレームにあるキーを編集する（MMD の補間曲線の欄と同じ）。
/// 曲線の変更は元に戻せる（ドラッグ 1 回・数の入力の続けた変更は、それぞれ 1 回分）。
/// </summary>
public sealed class GraphEditorViewModel : ObservableObject
{
    // 線の色（移動・注視点 X = 赤、Y = 緑、Z = 青。回転は明るめ、モーフの値は黄、距離は橙、視野角は紫）
    private static readonly Dictionary<string, Brush> Palette = new()
    {
        ["移動 X"] = Frozen(0xE0, 0x5A, 0x5A),
        ["移動 Y"] = Frozen(0x6A, 0xC8, 0x5A),
        ["移動 Z"] = Frozen(0x4A, 0x8E, 0xF0),
        ["注視点 X"] = Frozen(0xE0, 0x5A, 0x5A),
        ["注視点 Y"] = Frozen(0x6A, 0xC8, 0x5A),
        ["注視点 Z"] = Frozen(0x4A, 0x8E, 0xF0),
        ["回転 X"] = Frozen(0xFF, 0x9A, 0x9A),
        ["回転 Y"] = Frozen(0xA8, 0xF0, 0x98),
        ["回転 Z"] = Frozen(0x98, 0xC0, 0xFF),
        ["値"] = Frozen(0xF0, 0xC8, 0x40),
        ["距離"] = Frozen(0xF0, 0x9A, 0x40),
        ["視野角"] = Frozen(0xC0, 0x8A, 0xF0),
    };

    private readonly EditorContext _context;
    private SceneNode? _target;
    private KeyRef? _key;
    private int _channel;
    private bool _allChannels;
    private EditTransaction? _drag;

    public GraphEditorViewModel(EditorContext context)
    {
        _context = context;
        context.KeySelection.Changed += (_, _) => Refresh();
        context.Selection.Changed += (_, _) => Refresh();
        context.Playback.FrameChanged += (_, _) =>
        {
            if (!context.Playback.IsPlaying && context.KeySelection.Count == 0)
            {
                Refresh(); // 今のフレームのキーが変わる
            }
        };
        context.Scene.NodeChanged += (_, e) =>
        {
            if (_target is not null && e.Node == _target)
            {
                DataVersion++;
                if (e.PropertyName is nameof(BoneNode.Keys))
                {
                    Refresh(); // 選んだキーが消えた・曲線が変わった
                }
                else
                {
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        };
        Refresh();
    }

    /// <summary>表示が変わった（グラフと曲線の枠を描き直す）。</summary>
    public event EventHandler? Changed;

    /// <summary>対象が別の項目に変わった（グラフの範囲を合わせ直す）。</summary>
    public event EventHandler? TargetChanged;

    public SceneNode? Target => _target;

    /// <summary>対象の値かキーが変わるたびに増える（グラフの値を求め直すかの目安）。</summary>
    public int DataVersion { get; private set; }

    public ObservableCollection<GraphSeriesItem> SeriesItems { get; } = [];

    public IReadOnlyList<string> ChannelNames => _target is null ? [] : CurveEditing.ChannelNames(_target);

    public string Title => _target is null
        ? "タイムラインでキーを選ぶか、シーン一覧でボーン・モーフ・カメラを選んでください"
        : $"{_target.Name} のグラフ";

    public bool HasTarget => _target is not null;

    /// <summary>補間曲線を変えられる（キーを選んでいて、前のキーがあり、ロックしていない）。</summary>
    public bool CanEditCurve => _key is { } key && CurveEditing.PreviousKeyFrame(key) is not null && key.Node.CanEdit;

    public string CurveNote
    {
        get
        {
            if (_key is not { } key)
            {
                return "キーを選ぶか、キーのあるフレームへ動かすと、そのキーの補間曲線（前のキーからこのキーまでの動き方）を変えられます。";
            }
            if (CurveEditing.PreviousKeyFrame(key) is not { } previous)
            {
                return $"フレーム {key.Frame} は最初のキーなので、補間曲線はありません（次のキーを選んでください）。";
            }
            if (!key.Node.CanEdit)
            {
                return "ロックしているため変えられません（シーン一覧の鍵で外せます）。";
            }
            int count = KeysToEdit().Length;
            string others = count > 1 ? $"（選んでいるキー {count} 個に同じ曲線を当てます）"
                : _context.KeySelection.Count == 0 ? "（今のフレームのキー）" : string.Empty;
            return IsStep
                ? $"フレーム {previous} → {key.Frame} は「段階」です。点を動かすと曲線に戻ります。{others}"
                : $"フレーム {previous} → {key.Frame} の動き方{others}";
        }
    }

    /// <summary>選んだキーの補間が「段階」か。</summary>
    public bool IsStep => _key is { } key && CurveEditing.TryGetCurve(key, _channel, out _, out KeyInterpolationKind kind) && kind == KeyInterpolationKind.Step;

    /// <summary>編集する補間曲線の番号（<see cref="ChannelNames"/>）。</summary>
    public int Channel
    {
        get => _channel;
        set
        {
            int clamped = Math.Clamp(value, 0, Math.Max(0, ChannelNames.Count - 1));
            if (SetProperty(ref _channel, clamped))
            {
                RaiseCurveChanged();
            }
        }
    }

    /// <summary>全部の曲線を同じ形にする（ボーンの移動 X・Y・Z と回転をまとめて変えるなど）。</summary>
    public bool AllChannels
    {
        get => _allChannels;
        set
        {
            if (SetProperty(ref _allChannels, value))
            {
                RaiseCurveChanged();
            }
        }
    }

    /// <summary>編集している曲線（キーが無ければ直線）。</summary>
    public BezierCurve Curve => _key is { } key && CurveEditing.TryGetCurve(key, _channel, out BezierCurve curve, out _) ? curve : BezierCurve.Linear;

    /// <summary>グラフで強調する曲線の番号（編集している曲線で動く線を太く描く）。</summary>
    public int HighlightChannel => _allChannels ? -1 : _channel;

    public KeyRef? SelectedKey => _key;

    public double X1 { get => CurveEditing.ToMmd(Curve).X1; set => SetPoint(x1: value); }

    public double Y1 { get => CurveEditing.ToMmd(Curve).Y1; set => SetPoint(y1: value); }

    public double X2 { get => CurveEditing.ToMmd(Curve).X2; set => SetPoint(x2: value); }

    public double Y2 { get => CurveEditing.ToMmd(Curve).Y2; set => SetPoint(y2: value); }

    /// <summary>決まった形の曲線を当てる（全部の曲線にするかは <see cref="AllChannels"/> に従う）。</summary>
    public void ApplyPreset(BezierCurve curve, string name)
    {
        if (!CanEditCurve)
        {
            return;
        }
        KeyRef[] keys = KeysToEdit();
        _context.Playback.Pause();
        _context.Edits.Change($"補間曲線を「{name}」に（{keys.Length} 個）", Nodes(keys), () => CurveEditing.SetCurve(keys, EditChannel, curve), includeKeys: true);
    }

    /// <summary>「段階」（次のキーまで値を変えない）にする（全部の曲線）。</summary>
    public void ApplyStep()
    {
        if (!CanEditCurve)
        {
            return;
        }
        KeyRef[] keys = KeysToEdit();
        _context.Playback.Pause();
        _context.Edits.Change($"キーの補間を「段階」に（{keys.Length} 個）", Nodes(keys), () => KeyEditing.SetInterpolation(keys, InterpolationPreset.Step), includeKeys: true);
    }

    /// <summary>曲線の枠で点をつかんだ（ドラッグの間の変更を 1 つの操作にまとめる）。</summary>
    public bool BeginDrag()
    {
        if (!CanEditCurve || _drag is { IsActive: true })
        {
            return false;
        }
        _context.Playback.Pause();
        KeyRef[] keys = KeysToEdit();
        _drag = _context.Edits.Begin($"補間曲線の変更（{keys.Length} 個）", Nodes(keys), includeKeys: true);
        return true;
    }

    public void UpdateDrag(BezierCurve curve)
    {
        if (_drag is { IsActive: true })
        {
            CurveEditing.SetCurve(KeysToEdit(), EditChannel, CurveEditing.RoundToMmd(curve));
        }
    }

    /// <summary>ドラッグを終える（<paramref name="commit"/> が false なら、つかむ前の曲線に戻す）。</summary>
    public void EndDrag(bool commit)
    {
        if (_drag is not { IsActive: true } drag)
        {
            return;
        }
        if (commit)
        {
            drag.Commit();
        }
        else
        {
            drag.Cancel();
        }
        _drag = null;
    }

    /// <summary>グラフで、項目のキーを選ぶ（<paramref name="add"/> なら選んでいるキーに足す）。</summary>
    public void SelectKey(int frame, bool add)
    {
        if (_target is null)
        {
            return;
        }
        var key = new KeyRef(_target, frame);
        if (add)
        {
            _context.KeySelection.Add([key]);
        }
        else
        {
            _context.KeySelection.Set([key]);
        }
    }

    private int? EditChannel => _allChannels ? null : _channel;

    // 編集するキー: 選んでいるキー（ロックしていないもの）。選んでいなければ、今のフレームのキー
    private KeyRef[] KeysToEdit()
    {
        KeyRef[] selected = KeyCommands.EditableSelection(_context);
        if (selected.Length > 0 || _context.KeySelection.Count > 0)
        {
            return selected;
        }
        return _key is { } key && key.Node.CanEdit && KeyEditing.Exists(key) ? [key] : [];
    }

    private void SetPoint(double? x1 = null, double? y1 = null, double? x2 = null, double? y2 = null)
    {
        if (!CanEditCurve)
        {
            RaiseCurveChanged();
            return;
        }
        var (a, b, c, d) = CurveEditing.ToMmd(Curve);
        static byte B(double? v, int old) => (byte)Math.Clamp((int)Math.Round(v ?? old), 0, 127);
        BezierCurve curve = BezierCurve.FromMmd(B(x1, a), B(y1, b), B(x2, c), B(y2, d));
        KeyRef[] keys = KeysToEdit();
        _context.Playback.Pause();
        _context.Edits.Change($"補間曲線の変更（{keys.Length} 個）", Nodes(keys), () => CurveEditing.SetCurve(keys, EditChannel, curve),
            mergeKey: $"curve:{_key}:{EditChannel}", includeKeys: true);
    }

    private void Refresh()
    {
        // 対象: 選んだキーの項目（キーが 1 つの項目だけならそれ）→ シーン一覧で選んだ項目
        KeyRef[] keys = [.. _context.KeySelection.Items.Where(KeyEditing.Exists)];
        SceneNode? primary = _context.Selection.Primary;
        SceneNode? target = keys.Select(k => k.Node).Distinct().ToArray() switch
        {
            [var only] => only,
            { Length: > 1 } nodes when primary is not null && nodes.Contains(primary) => primary,
            { Length: > 1 } nodes => nodes[0],
            _ => primary is not null && KeyEditing.HasKeys(primary) ? primary : null,
        };
        KeyRef? key = target is null ? null : keys.Where(k => k.Node == target).OrderBy(k => k.Frame).Cast<KeyRef?>().FirstOrDefault();
        if (key is null && target is not null && keys.Length == 0)
        {
            var now = new KeyRef(target, _context.Playback.CurrentFrame);
            key = KeyEditing.Exists(now) ? now : null; // キーを選んでいなければ、今のフレームのキー
        }

        bool targetChanged = !ReferenceEquals(target, _target);
        _target = target;
        _key = key;
        if (targetChanged)
        {
            _channel = target is BoneNode ? 3 : 0; // ボーンはふつう回転の曲線を変えることが多い
            RebuildSeriesItems();
            DataVersion++;
            TargetChanged?.Invoke(this, EventArgs.Empty);
        }
        OnPropertyChanged(string.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildSeriesItems()
    {
        SeriesItems.Clear();
        if (_target is null)
        {
            return;
        }
        foreach (GraphSeries s in CurveEditing.Sample(_target, 0, 0))
        {
            var item = new GraphSeriesItem(s.Name, Palette.GetValueOrDefault(s.Name, Brushes.LightGray), isVisible: true);
            item.PropertyChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            SeriesItems.Add(item);
        }
    }

    private void RaiseCurveChanged()
    {
        OnPropertyChanged(nameof(Curve));
        OnPropertyChanged(nameof(X1));
        OnPropertyChanged(nameof(Y1));
        OnPropertyChanged(nameof(X2));
        OnPropertyChanged(nameof(Y2));
        OnPropertyChanged(nameof(IsStep));
        OnPropertyChanged(nameof(CurveNote));
        OnPropertyChanged(nameof(HighlightChannel));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static SceneNode[] Nodes(IEnumerable<KeyRef> keys) => [.. keys.Select(k => k.Node).Distinct()];

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
