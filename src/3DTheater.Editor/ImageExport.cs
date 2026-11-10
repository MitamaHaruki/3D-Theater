using System.Globalization;
using System.Numerics;

namespace Theater3D.Editor;

public enum ExportKind
{
    /// <summary>今のフレームを 1 枚。</summary>
    Still,

    /// <summary>フレームの範囲を 1 枚ずつ（連番）。</summary>
    Sequence,
}

/// <summary>
/// PNG の書き出しの設定（指示書 6.3）。大きさ・なめらかさ・背景（透明か色か）・フレームの範囲・書き出し先。
/// 連番は、書き出し先のファイル名の後ろに「_フレームの番号」を付ける（shot.png → shot_0000.png、shot_0001.png …）。
/// </summary>
public sealed record ExportOptions
{
    /// <summary>描く大きさの上限（Direct3D 11 のテクスチャの 1 辺の上限）。</summary>
    public const int MaxRenderSize = 16384;

    public ExportKind Kind { get; init; } = ExportKind.Still;

    public int Width { get; init; } = 1920;

    public int Height { get; init; } = 1080;

    /// <summary>2 倍の大きさで描いて縮める（線や輪郭のギザギザを減らす）。</summary>
    public bool Smooth { get; init; } = true;

    /// <summary>背景を透明にする（透過 PNG）。</summary>
    public bool Transparent { get; init; }

    /// <summary>透明にしないときの背景の色（0〜1）。</summary>
    public Vector3 Background { get; init; } = Vector3.One;

    public int FirstFrame { get; init; }

    public int LastFrame { get; init; }

    /// <summary>静止画はそのファイル、連番は名前の頭にするファイル（拡張子は .png）。</summary>
    public string OutputPath { get; init; } = string.Empty;

    /// <summary>描く倍率（なめらかにするなら 2）。</summary>
    public int Supersample => Smooth ? 2 : 1;

    public int RenderWidth => Width * Supersample;

    public int RenderHeight => Height * Supersample;

    /// <summary>書き出すフレーム（静止画は <see cref="FirstFrame"/> だけ）。</summary>
    public IReadOnlyList<int> Frames => Kind == ExportKind.Still || LastFrame < FirstFrame
        ? [FirstFrame]
        : [.. Enumerable.Range(FirstFrame, LastFrame - FirstFrame + 1)];

    /// <summary>書き出すファイル（<see cref="Frames"/> と同じ順）。</summary>
    public IReadOnlyList<string> Paths => Kind == ExportKind.Still
        ? [ImageExport.WithPngExtension(OutputPath)]
        : [.. Frames.Select(f => ImageExport.SequencePath(OutputPath, f, LastFrame))];

    /// <summary>設定の誤り（無ければ空）。<paramref name="endFrame"/> は作品の終わりのフレーム。</summary>
    public IReadOnlyList<string> Validate(int endFrame)
    {
        var errors = new List<string>();
        if (Width < 1 || Height < 1)
        {
            errors.Add("幅と高さは 1 以上にしてください。");
        }
        else if (RenderWidth > MaxRenderSize || RenderHeight > MaxRenderSize)
        {
            errors.Add(Smooth
                ? $"なめらかにするときは、幅と高さを {MaxRenderSize / 2:N0} 以下にしてください（2 倍の大きさで描くため）。"
                : $"幅と高さは {MaxRenderSize:N0} 以下にしてください。");
        }
        if (FirstFrame < 0 || FirstFrame > endFrame)
        {
            errors.Add($"フレームは 0〜{endFrame:N0}（作品の終わり）の間にしてください。");
        }
        if (Kind == ExportKind.Sequence && (LastFrame < FirstFrame || LastFrame > endFrame))
        {
            errors.Add($"最後のフレームは、最初のフレーム以上・{endFrame:N0}（作品の終わり）以下にしてください。");
        }
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            errors.Add("書き出し先を選んでください。");
        }
        else if (Path.GetFileNameWithoutExtension(OutputPath).Length == 0 || Path.GetDirectoryName(Path.GetFullPath(OutputPath)) is null)
        {
            errors.Add("書き出し先のファイル名が正しくありません。");
        }
        return errors;
    }
}

/// <summary>書き出しの画像の計算（縮小・透明度の割り戻し・ファイル名・色の文字）。どのスレッドから呼んでもよい。</summary>
public static class ImageExport
{
    /// <summary>拡張子を .png にしたパス。</summary>
    public static string WithPngExtension(string path) => Path.ChangeExtension(path, ".png");

    /// <summary>
    /// 連番の 1 枚のパス。名前の後ろに「_フレームの番号」を、最後のフレームの桁数（4 桁以上）にそろえて付ける。
    /// </summary>
    public static string SequencePath(string path, int frame, int lastFrame)
    {
        int digits = Math.Max(4, Math.Max(0, lastFrame).ToString(CultureInfo.InvariantCulture).Length);
        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(path);
        return Path.Combine(directory, $"{name}_{frame.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0')}.png");
    }

    /// <summary>
    /// BGRA 8 ビットの画像を <paramref name="factor"/> 分の 1 に縮める（factor × factor 点の平均）。
    /// 透明度をかけ合わせた値のまま平均するので、透明な所の色が混ざって縁が黒ずむことはない。
    /// </summary>
    public static void Downsample(ReadOnlySpan<byte> source, int width, int height, int factor, Span<byte> destination)
    {
        if (factor < 1 || width % factor != 0 || height % factor != 0)
        {
            throw new ArgumentException($"縮める倍率（{factor}）で幅と高さ（{width} × {height}）が割り切れません");
        }
        int outWidth = width / factor;
        int outHeight = height / factor;
        if (source.Length < width * height * 4 || destination.Length < outWidth * outHeight * 4)
        {
            throw new ArgumentException("画像の大きさと配列の長さが合いません");
        }
        int count = factor * factor;
        for (int y = 0; y < outHeight; y++)
        {
            for (int x = 0; x < outWidth; x++)
            {
                int b = 0, g = 0, r = 0, a = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int row = (((y * factor) + dy) * width + (x * factor)) * 4;
                    for (int dx = 0; dx < factor; dx++)
                    {
                        int i = row + (dx * 4);
                        b += source[i];
                        g += source[i + 1];
                        r += source[i + 2];
                        a += source[i + 3];
                    }
                }
                int o = ((y * outWidth) + x) * 4;
                destination[o] = (byte)((b + (count / 2)) / count);
                destination[o + 1] = (byte)((g + (count / 2)) / count);
                destination[o + 2] = (byte)((r + (count / 2)) / count);
                destination[o + 3] = (byte)((a + (count / 2)) / count);
            }
        }
    }

    /// <summary>
    /// 透明度をかけ合わせた色（描画の結果）を、PNG の決まり（色は透明度と別）に直す: 色 ÷ 透明度。透明な点は黒にする。
    /// </summary>
    public static void Unpremultiply(Span<byte> bgra)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            int a = bgra[i + 3];
            if (a == 255)
            {
                continue;
            }
            if (a == 0)
            {
                bgra[i] = bgra[i + 1] = bgra[i + 2] = 0;
                continue;
            }
            bgra[i] = (byte)Math.Min(255, ((bgra[i] * 255) + (a / 2)) / a);
            bgra[i + 1] = (byte)Math.Min(255, ((bgra[i + 1] * 255) + (a / 2)) / a);
            bgra[i + 2] = (byte)Math.Min(255, ((bgra[i + 2] * 255) + (a / 2)) / a);
        }
    }

    /// <summary>「#RRGGBB」の色を読む。</summary>
    public static bool TryParseColor(string? text, out Vector3 color)
    {
        color = default;
        string t = (text ?? string.Empty).Trim().TrimStart('#');
        if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
        {
            return false;
        }
        color = new Vector3((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF) / 255f;
        return true;
    }

    /// <summary>色を「#RRGGBB」にする。</summary>
    public static string FormatColor(Vector3 color)
    {
        int Channel(float v) => (int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
        return $"#{Channel(color.X):X2}{Channel(color.Y):X2}{Channel(color.Z):X2}";
    }
}
