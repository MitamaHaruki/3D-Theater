namespace Theater3D.Editor;

/// <summary>
/// 数の欄を左右にドラッグして値を変える計算（設計判断記録 87 章）。右へ動かすと増え、左へ動かすと減る。
/// <see cref="PixelsPerStep"/> 画素ごとに、欄の 1 刻み（ホイールと同じ刻み）だけ変える。Shift で 1/10、Ctrl で 10 倍。
/// </summary>
public static class NumberDrag
{
    /// <summary>1 刻み変えるのに動かす画素（画面の拡大率で割った、アプリの単位）。</summary>
    public const double PixelsPerStep = 3;

    /// <summary>これより動かすまでは、ドラッグにしない（押して離すだけなら、字を打つ欄にする）。</summary>
    public const double Threshold = 3;

    /// <summary>
    /// ドラッグを始めた値 <paramref name="start"/> から、横に <paramref name="pixels"/> 動かしたときの値。
    /// 刻みの倍数だけ変え、小数の誤差は刻みの細かさで丸める。
    /// </summary>
    public static double ValueAt(double start, double pixels, double step, bool fine = false, bool coarse = false)
    {
        if (!double.IsFinite(start) || !double.IsFinite(pixels) || !double.IsFinite(step) || step <= 0)
        {
            return start;
        }
        double unit = step * (fine ? 0.1 : 1) * (coarse ? 10 : 1);
        double steps = Math.Truncate(pixels / PixelsPerStep);
        double value = start + (steps * unit);
        int digits = Math.Clamp((int)Math.Ceiling(-Math.Log10(unit)) + 2, 0, 12);
        return Math.Round(value, digits);
    }
}
