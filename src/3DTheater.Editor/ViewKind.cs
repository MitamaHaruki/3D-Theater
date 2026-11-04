namespace Theater3D.Editor;

/// <summary>プレビューの 1 区画が何を映すか。</summary>
public enum ViewKind
{
    Front,
    Back,
    Right,
    Left,
    Top,
    Bottom,
    Free,
    Camera,
}

public enum GridPlane
{
    XZ,
    XY,
    YZ,
}

public static class ViewKinds
{
    public static IReadOnlyList<ViewKind> All { get; } = Enum.GetValues<ViewKind>();

    public static bool IsFixed(this ViewKind kind) => kind is >= ViewKind.Front and <= ViewKind.Bottom;

    public static string DisplayName(this ViewKind kind) => kind switch
    {
        ViewKind.Front => "正面",
        ViewKind.Back => "背面",
        ViewKind.Right => "右側面",
        ViewKind.Left => "左側面",
        ViewKind.Top => "上面",
        ViewKind.Bottom => "下面",
        ViewKind.Free => "自由視点",
        ViewKind.Camera => "カメラ視点",
        _ => kind.ToString(),
    };

    /// <summary>Blender に合わせた既定のショートカットの表示用の名前。</summary>
    public static string? ShortcutText(this ViewKind kind) => kind switch
    {
        ViewKind.Front => "Num1",
        ViewKind.Back => "Ctrl+Num1",
        ViewKind.Right => "Num3",
        ViewKind.Left => "Ctrl+Num3",
        ViewKind.Top => "Num7",
        ViewKind.Bottom => "Ctrl+Num7",
        ViewKind.Camera => "Num0",
        _ => null,
    };

    /// <summary>
    /// 固定視点の向き（度）。ヨーは Y 軸まわり、ピッチは正で見下ろす向き。
    /// 正面はモデルの前（−Z 側）から +Z 方向を見る。
    /// 右側面は Blender と同じく画面の右側（+X 側）から見る（2026-11-04 決定。docs/設計判断記録.md 5 章）。
    /// </summary>
    public static (float YawDegrees, float PitchDegrees) FixedAngles(this ViewKind kind) => kind switch
    {
        ViewKind.Front => (0f, 0f),
        ViewKind.Back => (180f, 0f),
        ViewKind.Right => (-90f, 0f),
        ViewKind.Left => (90f, 0f),
        ViewKind.Top => (0f, 90f),
        ViewKind.Bottom => (0f, -90f),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "固定視点ではありません"),
    };

    /// <summary>その視点で床の格子をどの平面に描くか。固定視点では画面に平行な面にする。</summary>
    public static GridPlane GridPlaneFor(this ViewKind kind) => kind switch
    {
        ViewKind.Front or ViewKind.Back => GridPlane.XY,
        ViewKind.Right or ViewKind.Left => GridPlane.YZ,
        _ => GridPlane.XZ,
    };
}
