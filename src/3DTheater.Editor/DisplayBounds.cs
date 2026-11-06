using System.Numerics;

namespace Theater3D.Editor;

/// <summary>
/// 画面に収めるときと、選択中の枠に使う「見た目の箱」を求める。
/// MMD のモデルには、眼鏡などの使わない部品を遠く（足元の何百も下など）に隠しておく作りがよくあり、
/// すべての頂点を囲むと箱が大きくなりすぎて、モデルが見えないほど引いてしまう。
/// そこで、ほかから大きく離れた頂点を除いて囲む。当たり判定（クリックでの選択）には使わない（そちらはすべての頂点を使う）。
/// </summary>
public static class DisplayBounds
{
    /// <summary>この割合の頂点が入る距離を、モデルの大きさの目安にする。隠した部品がこれより少なければ除ける。</summary>
    private const float CoreFraction = 0.8f;

    /// <summary>目安の距離の何倍より遠い頂点を「離れた頂点」とみなすか。</summary>
    private const float OutlierFactor = 4f;

    /// <summary>
    /// 箱に入れる頂点の印。面に使われていない頂点と、ほかから大きく離れた頂点は false。
    /// 頂点の真ん中（座標ごとの中央の値）からの距離で決める。全部が除かれることはない。
    /// </summary>
    public static bool[] CoreVertices(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices)
    {
        var used = new bool[positions.Length];
        int usedCount = 0;
        foreach (uint index in indices)
        {
            if (index < (uint)positions.Length && !used[index])
            {
                used[index] = true;
                usedCount++;
            }
        }
        if (usedCount == 0)
        {
            // 面が無いモデルは、すべての頂点で囲む
            Array.Fill(used, true);
            usedCount = positions.Length;
        }
        if (usedCount < 8)
        {
            return used;
        }

        var xs = new float[usedCount];
        var ys = new float[usedCount];
        var zs = new float[usedCount];
        for (int i = 0, k = 0; i < positions.Length; i++)
        {
            if (used[i])
            {
                xs[k] = positions[i].X;
                ys[k] = positions[i].Y;
                zs[k] = positions[i].Z;
                k++;
            }
        }
        var center = new Vector3(Median(xs), Median(ys), Median(zs));

        var distances = new float[usedCount];
        for (int i = 0, k = 0; i < positions.Length; i++)
        {
            if (used[i])
            {
                distances[k++] = Vector3.Distance(positions[i], center);
            }
        }
        Array.Sort(distances);
        float typical = distances[Math.Min(usedCount - 1, (int)(usedCount * CoreFraction))];
        float limit = Math.Max(typical * OutlierFactor, 1e-4f);

        var core = new bool[positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            core[i] = used[i] && Vector3.Distance(positions[i], center) <= limit;
        }
        return core;
    }

    /// <summary>印の付いた頂点を囲む箱。印の付いた頂点が無ければ、すべての頂点を囲む。</summary>
    public static (Vector3 Min, Vector3 Max) Compute(ReadOnlySpan<Vector3> positions, ReadOnlySpan<bool> core)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        bool any = false;
        for (int i = 0; i < positions.Length; i++)
        {
            if (i < core.Length && core[i])
            {
                min = Vector3.Min(min, positions[i]);
                max = Vector3.Max(max, positions[i]);
                any = true;
            }
        }
        if (any)
        {
            return (min, max);
        }
        if (positions.IsEmpty)
        {
            return (Vector3.Zero, Vector3.Zero);
        }
        foreach (Vector3 p in positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }

    private static float Median(float[] values)
    {
        Array.Sort(values);
        return values[values.Length / 2];
    }
}
