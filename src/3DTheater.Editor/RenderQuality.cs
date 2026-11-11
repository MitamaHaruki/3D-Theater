namespace Theater3D.Editor;

/// <summary>プレビュー（編集中の表示）の品質。利用者ごとの設定で、プロジェクトには入れない（指示書 6.1 の「プレビュー品質プリセット」）。</summary>
public enum PreviewQuality
{
    /// <summary>なめらかにしない・影は粗め。GPU が弱いときや、再生がかくつくときに。</summary>
    Light,

    /// <summary>4 倍のなめらかさ（MSAA）・ふつうの影（初期値）。</summary>
    Standard,

    /// <summary>8 倍のなめらかさ・細かい影。GPU に余裕があるときに。</summary>
    High,
}

/// <summary>描き方の品質。<paramref name="SampleCount"/> はなめらかさ（MSAA の 1 画素あたりの点の数）、
/// <paramref name="ShadowMapSize"/> はセルフシャドウの奥行きの画像の 1 辺、<paramref name="RaySamples"/> はレイトレーシングの光線の数
/// （環境光の遮りは 1 点あたりこの数。やわらかな影は、影の境目だけこの数）。</summary>
public readonly record struct QualityProfile(int SampleCount, int ShadowMapSize, int RaySamples);

/// <summary>
/// 品質の決まり。編集中は軽く、書き出しは高い品質で描く（指示書 6.1「編集用の軽量描画と、書き出し用の高品質描画を分離する」）。
/// - プレビュー: 軽い = 1 点・影 1024・光線 4、ふつう = 4 点・影 2048・光線 8、きれい = 8 点・影 4096・光線 12
/// - 書き出し: プレビューの品質に関係なく 4 点・影 4096・光線 16（「なめらかにする」では、さらに 2 倍の大きさで描いて縮める）
/// - MSAA の描画先（1 点ごとに色 4 バイト・奥行き 4 バイト）が 512 MiB を超える大きさでは、収まるまで点の数を半分にする
/// - GPU が使えない点の数のときは、描画エンジンが使える数まで減らす
/// </summary>
public static class RenderQuality
{
    /// <summary>MSAA の描画先に使ってよいメモリの上限（1 つの描画先あたり）。</summary>
    public const long SampleMemoryBudget = 512L * 1024 * 1024;

    /// <summary>書き出しの品質。</summary>
    public static QualityProfile Export { get; } = new(4, 4096, 16);

    public static QualityProfile For(PreviewQuality quality) => quality switch
    {
        PreviewQuality.Light => new(1, 1024, 4),
        PreviewQuality.High => new(8, 4096, 12),
        _ => new(4, 2048, 8),
    };

    /// <summary>この大きさの描画先で使う点の数（メモリの上限に収まるまで、頼まれた数を半分にしていく）。</summary>
    public static int FitSampleCount(int requested, int width, int height)
    {
        int count = Math.Max(1, requested);
        long pixels = (long)Math.Max(0, width) * Math.Max(0, height);
        while (count > 1 && pixels * count * 8 > SampleMemoryBudget)
        {
            count /= 2;
        }
        return count;
    }

    public static string Name(PreviewQuality quality) => quality switch
    {
        PreviewQuality.Light => "軽い",
        PreviewQuality.High => "きれい",
        _ => "ふつう",
    };
}

/// <summary>今のプレビューの品質（全プレビューで共通。変わったら知らせる）。</summary>
public sealed class PreviewQualitySettings
{
    private PreviewQuality _quality = PreviewQuality.Standard;

    public event EventHandler? Changed;

    public PreviewQuality Quality
    {
        get => _quality;
        set
        {
            if (_quality == value || !Enum.IsDefined(value))
            {
                return;
            }
            _quality = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public QualityProfile Profile => RenderQuality.For(_quality);
}
