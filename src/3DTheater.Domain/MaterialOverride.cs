using System.Numerics;

namespace Theater3D.Domain;

/// <summary>材質の色の計算。</summary>
public enum MaterialShading
{
    /// <summary>MMD と同じトゥーンの色の計算（PMX の材質のまま）。</summary>
    Toon,

    /// <summary>拡張材質: 金属らしさ・粗さで光を返す（PBR）。ベースカラーは PMX の拡散色 × テクスチャ × 色合い。</summary>
    Pbr,
}

/// <summary>
/// 材質の上書き（拡張材質。指示書 6.2「MMD 互換材質を保持しつつ、任意の拡張材質オーバーライドを別データとして設定可能にする」）。
/// PMX の材質とテクスチャは書き換えず、描くときにだけ使う。<see cref="None"/> は「上書きなし」（PMX の材質のまま）。
/// 発光はどちらの色の計算にも足す（ブルームで光らせられる）。
/// </summary>
public sealed record MaterialOverride
{
    public const float MaxEmissiveStrength = 10f;

    /// <summary>上書きなし。</summary>
    public static MaterialOverride None { get; } = new();

    public MaterialShading Shading { get; init; } = MaterialShading.Toon;

    /// <summary>ベースカラーに掛ける色合い（PBR のとき。0〜1）。</summary>
    public Vector3 Tint { get; init; } = Vector3.One;

    /// <summary>金属らしさ（0〜1。PBR のとき）。</summary>
    public float Metallic { get; init; }

    /// <summary>粗さ（0.04〜1。PBR のとき。小さいほど照り返しが鋭い）。</summary>
    public float Roughness { get; init; } = 0.5f;

    /// <summary>発光の色（0〜1）。</summary>
    public Vector3 EmissiveColor { get; init; } = Vector3.One;

    /// <summary>発光の強さ（0〜10。0 なら光らない）。</summary>
    public float EmissiveStrength { get; init; }

    /// <summary>上書きなしと同じか（値を範囲に収めたうえで）。</summary>
    public bool IsDefault => Normalized() == None;

    /// <summary>値を範囲に収めたもの（数でない値は初期値にする）。</summary>
    public MaterialOverride Normalized()
    {
        MaterialOverride d = None;
        return this with
        {
            Shading = Enum.IsDefined(Shading) ? Shading : MaterialShading.Toon,
            Tint = FitColor(Tint, d.Tint),
            Metallic = Fit(Metallic, 0f, 1f, d.Metallic),
            Roughness = Fit(Roughness, 0.04f, 1f, d.Roughness),
            EmissiveColor = FitColor(EmissiveColor, d.EmissiveColor),
            EmissiveStrength = Fit(EmissiveStrength, 0f, MaxEmissiveStrength, d.EmissiveStrength),
        };
    }

    private static float Fit(float value, float minimum, float maximum, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static Vector3 FitColor(Vector3 value, Vector3 fallback) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
            ? Vector3.Clamp(value, Vector3.Zero, Vector3.One)
            : fallback;
}

/// <summary>
/// モデルの材質の上書きの組（材質の番号 → 上書き）。上書きのある材質だけを持つ。値で比べる（元に戻す・やり直しで、変わったかを見るため）。
/// 変えるときは <see cref="With"/> で新しい組を作る（中身は変わらない）。
/// </summary>
public sealed class MaterialOverrideSet : IEquatable<MaterialOverrideSet>
{
    private readonly SortedDictionary<int, MaterialOverride> _items;

    private MaterialOverrideSet(SortedDictionary<int, MaterialOverride> items) => _items = items;

    /// <summary>上書きなし。</summary>
    public static MaterialOverrideSet Empty { get; } = new([]);

    /// <summary>上書きのある材質の番号と上書き（番号の順）。</summary>
    public IReadOnlyDictionary<int, MaterialOverride> Items => _items;

    public int Count => _items.Count;

    /// <summary>材質の上書き（無ければ <see cref="MaterialOverride.None"/>）。</summary>
    public MaterialOverride this[int index] => _items.TryGetValue(index, out MaterialOverride? value) ? value : MaterialOverride.None;

    /// <summary>1 つの材質の上書きを変えた組。範囲に収め、上書きなしと同じなら外す。</summary>
    public MaterialOverrideSet With(int index, MaterialOverride value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        MaterialOverride normalized = (value ?? MaterialOverride.None).Normalized();
        var items = new SortedDictionary<int, MaterialOverride>(_items);
        if (normalized == MaterialOverride.None)
        {
            items.Remove(index);
        }
        else
        {
            items[index] = normalized;
        }
        return items.Count == 0 ? Empty : new MaterialOverrideSet(items);
    }

    /// <summary>番号と上書きの組から作る（範囲に収め、上書きなしと同じものは外す）。</summary>
    public static MaterialOverrideSet From(IEnumerable<KeyValuePair<int, MaterialOverride>> items)
    {
        MaterialOverrideSet set = Empty;
        foreach (var (index, value) in items)
        {
            set = set.With(index, value);
        }
        return set;
    }

    public bool Equals(MaterialOverrideSet? other) =>
        other is not null && other._items.Count == _items.Count
        && _items.All(pair => other._items.TryGetValue(pair.Key, out MaterialOverride? value) && value == pair.Value);

    public override bool Equals(object? obj) => Equals(obj as MaterialOverrideSet);

    public override int GetHashCode() => _items.Count;
}
