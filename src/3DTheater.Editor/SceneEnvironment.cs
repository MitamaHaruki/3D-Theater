using System.Text.Json.Serialization;

namespace Theater3D.Editor;

/// <summary>
/// 環境の画像（HDRI。全方向の景色を、横 360 度・縦 180 度に広げた HDR 画像）。作品の描き方としてプロジェクトに保存する。
/// PBR の材質（拡張材質）の環境光と映り込みに使い、<see cref="ShowBackground"/> なら背景にも描く（カメラ視点と自由視点。
/// 固定の視点と、透明な背景の書き出しには描かない）。トゥーンの材質には使わない（MMD と同じ見え方を保つ）。
/// </summary>
public sealed record SceneEnvironment
{
    public const float MaxIntensity = 4f;

    /// <summary>環境の画像なし（新しいプロジェクト・前の版のファイル）。</summary>
    public static SceneEnvironment Off { get; } = new();

    public bool Enabled { get; init; }

    /// <summary>画像のファイル（絶対パス。プロジェクトには、プロジェクトのフォルダーから見た場所でも書く）。</summary>
    public string? File { get; init; }

    /// <summary>明るさ（0〜4）。</summary>
    public float Intensity { get; init; } = 1f;

    /// <summary>景色を Y 軸のまわりに回す角度（度。-180〜180。上から見て時計回り）。</summary>
    public float Rotation { get; init; }

    /// <summary>背景に描く。</summary>
    public bool ShowBackground { get; init; } = true;

    /// <summary>使うか（ON で、ファイルが決まっている）。</summary>
    [JsonIgnore]
    public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(File);

    /// <summary>描画エンジンへ渡す回転（ラジアン）。</summary>
    [JsonIgnore]
    public float RotationRadians => Rotation * MathF.PI / 180f;

    /// <summary>値を範囲に収めたもの（数でない値は初期値にする。回転は -180〜180 に回し込む）。</summary>
    public SceneEnvironment Normalized() => this with
    {
        File = string.IsNullOrWhiteSpace(File) ? null : File.Trim(),
        Intensity = float.IsFinite(Intensity) ? Math.Clamp(Intensity, 0f, MaxIntensity) : Off.Intensity,
        Rotation = float.IsFinite(Rotation) ? WrapDegrees(Rotation) : Off.Rotation,
    };

    private static float WrapDegrees(float degrees)
    {
        float wrapped = degrees % 360f;
        if (wrapped > 180f)
        {
            wrapped -= 360f;
        }
        else if (wrapped < -180f)
        {
            wrapped += 360f;
        }
        return wrapped;
    }
}
