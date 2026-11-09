using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Theater3D.Domain;

namespace Theater3D.Editor;

/// <summary>
/// モーフの値に名前を付けて覚えたもの（表情のプリセット）。モーフはモデルを問わず名前で合わせるので、
/// 同じ名前のモーフを持つモデルなら、別のモデルで作ったプリセットも使える。
/// </summary>
public sealed class MorphPreset
{
    public const int MaxNameLength = 100;

    public MorphPreset(string name, IEnumerable<KeyValuePair<string, float>> values)
    {
        Name = NormalizeName(name) ?? throw new ArgumentException("プリセットの名前が空です", nameof(name));
        var map = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var (morph, value) in values)
        {
            if (!string.IsNullOrEmpty(morph) && float.IsFinite(value))
            {
                map.TryAdd(morph, Math.Clamp(value, 0f, 1f));
            }
        }
        Values = map;
    }

    public string Name { get; }

    /// <summary>モーフの名前 → 値（0〜1）。</summary>
    public IReadOnlyDictionary<string, float> Values { get; }

    /// <summary>
    /// 今のモーフの値から作る。0 のモーフも覚える（当てると 0 に戻る）。同じ名前のモーフが 2 つ以上あれば、最初のものの値。
    /// </summary>
    public static MorphPreset Capture(string name, IEnumerable<MorphNode> morphs) =>
        new(name, morphs.Select(m => KeyValuePair.Create(m.Name, m.Weight)));

    /// <summary>このモデルで名前の合うモーフの数。</summary>
    public int CountMatches(ModelNode model) => model.Morphs.Select(m => m.Name).Distinct().Count(Values.ContainsKey);

    /// <summary>前後の空白を除いた名前。使えない（空・長すぎる）なら null。</summary>
    public static string? NormalizeName(string? name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        return trimmed.Length is > 0 and <= MaxNameLength ? trimmed : null;
    }
}

/// <summary>
/// プリセットの一覧。利用者ごとに 1 つ（プロジェクトとは別に保存する。画面の設定と同じ扱いで、元に戻すの対象にはしない）。
/// </summary>
public sealed class MorphPresetLibrary
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 日本語の名前をそのまま読めるように
    };

    private readonly List<MorphPreset> _presets = [];

    /// <summary>一覧が変わった（足した・消した・名前を変えた・置き換えた）。</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<MorphPreset> Presets => _presets;

    public MorphPreset? Find(string name) => _presets.Find(p => p.Name == name);

    /// <summary>足す。同じ名前があれば、同じ位置で置き換える。</summary>
    public void Put(MorphPreset preset)
    {
        int index = _presets.FindIndex(p => p.Name == preset.Name);
        if (index >= 0)
        {
            _presets[index] = preset;
        }
        else
        {
            _presets.Add(preset);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Remove(string name)
    {
        if (_presets.RemoveAll(p => p.Name == name) == 0)
        {
            return false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>名前を変える。新しい名前が使えないか、ほかのプリセットと同じなら変えずに false。</summary>
    public bool Rename(string name, string newName)
    {
        int index = _presets.FindIndex(p => p.Name == name);
        string? normalized = MorphPreset.NormalizeName(newName);
        if (index < 0 || normalized is null || (normalized != name && Find(normalized) is not null))
        {
            return false;
        }
        if (normalized == name)
        {
            return true;
        }
        _presets[index] = new MorphPreset(normalized, _presets[index].Values);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public string ToJson() => JsonSerializer.Serialize(new LibraryFile
    {
        Version = CurrentVersion,
        Presets = [.. _presets.Select(p => new PresetEntry { Name = p.Name, Values = new Dictionary<string, float>(p.Values) })],
    }, JsonOptions);

    /// <summary>
    /// 保存したものから作る。壊れた項目（名前が空・数でない値）は飛ばし、同じ名前は最初のものを使う。
    /// 形式が違う・新しすぎるときは <see cref="JsonException"/>。
    /// </summary>
    public static MorphPresetLibrary FromJson(string json)
    {
        LibraryFile file = JsonSerializer.Deserialize<LibraryFile>(json, JsonOptions) ?? throw new JsonException("プリセットのファイルが空です");
        if (file.Version is < 1 or > CurrentVersion)
        {
            throw new JsonException($"プリセットのファイルの版（{file.Version}）を読めません");
        }
        var library = new MorphPresetLibrary();
        foreach (PresetEntry entry in file.Presets ?? [])
        {
            if (MorphPreset.NormalizeName(entry.Name) is { } name && library.Find(name) is null && entry.Values is not null)
            {
                library._presets.Add(new MorphPreset(name, entry.Values));
            }
        }
        return library;
    }

    private sealed class LibraryFile
    {
        public int Version { get; set; }

        public List<PresetEntry>? Presets { get; set; }
    }

    private sealed class PresetEntry
    {
        public string? Name { get; set; }

        [JsonPropertyName("values")]
        public Dictionary<string, float>? Values { get; set; }
    }
}

/// <summary>
/// プリセットを混ぜて、モデルのモーフに当てる（指示書 4.3「複数値の混合」）。プリセットごとに割合（0〜1）を持ち、
/// 混ぜ始めたときの値（基準）から、それぞれのプリセットへ割合の分だけ寄せたものを足し合わせる:
/// 値 = 基準 + Σ 割合 × (プリセットの値 − 基準)（0〜1 に収める）。どのプリセットにも無いモーフ・ロックしたモーフは変えない。
/// 割合を全部 0 に戻すと基準の値に戻る。割合を変えるたびに値を当て、続けて変えた分は 1 つの「元に戻す」にまとめる
/// （まだ触っていないモーフを持つプリセットを混ぜ始めたところからは、新しい 1 つになる）。
/// ほかの操作（手で値を変えた・再生位置を動かした・元に戻した）でモーフの値が変わったら、その値を新しい基準として、
/// 割合を全部 0 にする（<see cref="Cleared"/>）。
/// </summary>
public sealed class MorphMixSession : IDisposable
{
    private readonly ModelNode _model;
    private readonly EditService _edits;
    private readonly Dictionary<MorphNode, float> _base = [];
    private readonly Dictionary<string, (MorphPreset Preset, float Ratio)> _parts = new(StringComparer.Ordinal);
    private readonly string _mergeKey;
    private int _applying;
    private bool _disposed;

    public MorphMixSession(ModelNode model, EditService edits)
    {
        _model = model;
        _edits = edits;
        _mergeKey = $"{model.Id}:表情の混合";
        foreach (MorphNode morph in model.Morphs)
        {
            morph.PropertyChanged += OnMorphChanged;
        }
    }

    /// <summary>ほかの操作で値が変わったため、割合を全部 0 にした。</summary>
    public event EventHandler? Cleared;

    public ModelNode Model => _model;

    /// <summary>混ぜている途中か（0 でない割合がある）。</summary>
    public bool IsMixing => _parts.Values.Any(p => p.Ratio > 0);

    public float RatioOf(string presetName) => _parts.TryGetValue(presetName, out var part) ? part.Ratio : 0f;

    /// <summary>プリセットの割合を変えて、混ぜた値を当てる。値が変わったら true。</summary>
    public bool SetRatio(MorphPreset preset, float ratio)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ratio = float.IsFinite(ratio) ? Math.Clamp(ratio, 0f, 1f) : 0f;
        if (ratio == RatioOf(preset.Name) && _parts.TryGetValue(preset.Name, out var same) && ReferenceEquals(same.Preset, preset))
        {
            return false;
        }
        _parts[preset.Name] = (preset, ratio);

        // 初めて触るモーフは、今の値を基準として覚える
        foreach (MorphNode morph in _model.Morphs)
        {
            if (preset.Values.ContainsKey(morph.Name) && morph.CanEdit)
            {
                _base.TryAdd(morph, morph.Weight);
            }
        }

        var targets = new Dictionary<MorphNode, float>();
        foreach (var (morph, baseValue) in _base)
        {
            if (!morph.CanEdit)
            {
                continue; // 混ぜている途中でロックした
            }
            float value = baseValue;
            foreach (var (p, r) in _parts.Values)
            {
                if (r > 0 && p.Values.TryGetValue(morph.Name, out float presetValue))
                {
                    value += r * (presetValue - baseValue);
                }
            }
            targets[morph] = Math.Clamp(value, 0f, 1f);
        }

        _applying++;
        try
        {
            return _edits.Change("表情の混合", targets.Keys, () =>
            {
                foreach (var (morph, value) in targets)
                {
                    morph.Weight = value;
                }
            }, mergeKey: _mergeKey);
        }
        finally
        {
            _applying--;
        }
    }

    /// <summary>今の値を新しい基準にして、割合を全部 0 にする（値は変えない）。</summary>
    public void Clear()
    {
        bool had = _parts.Count > 0;
        _parts.Clear();
        _base.Clear();
        if (had)
        {
            Cleared?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (MorphNode morph in _model.Morphs)
        {
            morph.PropertyChanged -= OnMorphChanged;
        }
    }

    private void OnMorphChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applying == 0 && e.PropertyName == nameof(MorphNode.Weight) && _parts.Count > 0)
        {
            Clear();
        }
    }
}
