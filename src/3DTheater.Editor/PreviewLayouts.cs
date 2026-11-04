namespace Theater3D.Editor;

/// <summary>プレビュー 1 枚の中の分け方。</summary>
public enum PreviewLayout
{
    Single,
    SideBySide,
    Quad,
}

/// <summary>4 画面のときの視点の組み合わせ。</summary>
public sealed record QuadPreset(string Name, IReadOnlyList<ViewKind> Views);

public static class PreviewLayouts
{
    public static IReadOnlyList<QuadPreset> QuadPresets { get; } =
    [
        new("正面・右側面・背面・自由視点", [ViewKind.Front, ViewKind.Right, ViewKind.Back, ViewKind.Free]),
        new("正面・右側面・上面・自由視点", [ViewKind.Front, ViewKind.Right, ViewKind.Top, ViewKind.Free]),
        new("上面・正面・左側面・右側面", [ViewKind.Top, ViewKind.Front, ViewKind.Left, ViewKind.Right]),
    ];

    public static QuadPreset DefaultQuadPreset => QuadPresets[1];

    public static int ViewCount(this PreviewLayout layout) => layout switch
    {
        PreviewLayout.Single => 1,
        PreviewLayout.SideBySide => 2,
        _ => 4,
    };

    public static string DisplayName(this PreviewLayout layout) => layout switch
    {
        PreviewLayout.Single => "1 画面",
        PreviewLayout.SideBySide => "2 画面",
        _ => "4 画面",
    };

    /// <summary>分け方を切り替えたときに各区画へ割り当てる視点。</summary>
    public static IReadOnlyList<ViewKind> DefaultViews(PreviewLayout layout, QuadPreset? preset = null) => layout switch
    {
        PreviewLayout.Single => [ViewKind.Free],
        PreviewLayout.SideBySide => [ViewKind.Front, ViewKind.Free],
        _ => (preset ?? DefaultQuadPreset).Views,
    };
}
