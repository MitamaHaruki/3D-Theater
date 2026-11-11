using System.Globalization;
using System.Numerics;

namespace Theater3D.Editor;

public enum ExportKind
{
    /// <summary>今のフレームを 1 枚。</summary>
    Still,

    /// <summary>フレームの範囲を 1 枚ずつ（連番）。</summary>
    Sequence,

    /// <summary>フレームの範囲を動画（MP4: H.264 と AAC）に。</summary>
    Video,
}

/// <summary>
/// 書き出しのレンダーパス（合成用の画像。指示書 6.3「Beauty／Alpha／Depth／Normal／Shadow／Emission／Object ID／Material ID 等」）。
/// 普通の絵（Beauty）と一緒に、パスごとに別の PNG を書く（静止画と連番のとき。動画では書かない）。
/// </summary>
[Flags]
public enum ExportPasses
{
    None = 0,

    /// <summary>アルファ（物の形の白黒。なめらかにする）。</summary>
    Alpha = 1 << 0,

    /// <summary>奥行き（16 ビットの白黒。手前ほど白く、奥ほど黒い。何も無い所は黒）。</summary>
    Depth = 1 << 1,

    /// <summary>法線（カメラから見た面の向きの色）。</summary>
    Normal = 1 << 2,

    /// <summary>物の ID（物ごとの色）。</summary>
    ObjectId = 1 << 3,

    /// <summary>材質の ID（材質ごとの色）。</summary>
    MaterialId = 1 << 4,

    /// <summary>影（白黒。白 = 影の中。セルフシャドウと地面の影）。</summary>
    Shadow = 1 << 5,

    /// <summary>発光（材質の発光の色）。</summary>
    Emission = 1 << 6,
}

/// <summary>レンダーパスの名前と、ファイルの名前に付ける語。</summary>
public static class ExportPassInfo
{
    /// <summary>書き出す順のパス。</summary>
    public static IReadOnlyList<ExportPasses> All { get; } =
        [ExportPasses.Alpha, ExportPasses.Depth, ExportPasses.Normal, ExportPasses.ObjectId, ExportPasses.MaterialId, ExportPasses.Shadow, ExportPasses.Emission];

    /// <summary>ファイルの名前に付ける語（合成ソフトで扱いやすいよう英語）。</summary>
    public static string Suffix(ExportPasses pass) => pass switch
    {
        ExportPasses.Alpha => "alpha",
        ExportPasses.Depth => "depth",
        ExportPasses.Normal => "normal",
        ExportPasses.ObjectId => "objectid",
        ExportPasses.MaterialId => "materialid",
        ExportPasses.Shadow => "shadow",
        ExportPasses.Emission => "emission",
        _ => throw new ArgumentOutOfRangeException(nameof(pass), pass, "パスを 1 つだけ選んでください"),
    };

    /// <summary>画面に出す名前。</summary>
    public static string DisplayName(ExportPasses pass) => pass switch
    {
        ExportPasses.Alpha => "アルファ",
        ExportPasses.Depth => "奥行き",
        ExportPasses.Normal => "法線",
        ExportPasses.ObjectId => "物の ID",
        ExportPasses.MaterialId => "材質の ID",
        ExportPasses.Shadow => "影",
        ExportPasses.Emission => "発光",
        _ => pass.ToString(),
    };
}

/// <summary>
/// 書き出しの設定（指示書 6.3）。大きさ・なめらかさ・背景（透明か色か）・フレームの範囲・書き出し先。
/// 連番は、書き出し先のファイル名の後ろに「_フレームの番号」を付ける（shot.png → shot_0000.png、shot_0001.png …）。
/// 動画は MP4（H.264・AAC）で、幅と高さは偶数・各 64〜4096・合わせて 4096 × 2304 まで（H.264 の決まり）。透明にはできない。
/// モーションブラーは、1 フレームの間（<see cref="Shutter"/> の長さ）を <see cref="MotionBlurSamples"/> 回に分けて描き、平均する。
/// </summary>
public sealed record ExportOptions
{
    /// <summary>描く大きさの上限（Direct3D 11 のテクスチャの 1 辺の上限）。</summary>
    public const int MaxRenderSize = 16384;

    /// <summary>動画の 1 辺の上限と、画素の数の上限（H.264 のレベル 5.1〜5.2 の 4096 × 2304）。</summary>
    public const int MaxVideoSide = 4096;

    /// <summary>動画の 1 辺の下限（Windows の符号化の部品には、32 × 32 などのごく小さい動画を受け付けないものがある）。</summary>
    public const int MinVideoSide = 64;

    public const int MaxVideoPixels = 4096 * 2304;

    /// <summary>動画の音声（AAC）の 1 秒あたりのビット数。</summary>
    public const int AudioBitrate = 192_000;

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

    /// <summary>静止画・動画はそのファイル、連番は名前の頭にするファイル（拡張子は .png・動画は .mp4）。</summary>
    public string OutputPath { get; init; } = string.Empty;

    /// <summary>動画の画質（高画質ならビット数を 2 倍にする）。</summary>
    public bool HighQuality { get; init; }

    /// <summary>動画にシーンの音声を入れる。</summary>
    public bool IncludeAudio { get; init; } = true;

    /// <summary>モーションブラーで分けて描く数（1 なら使わない。2〜64）。</summary>
    public int MotionBlurSamples { get; init; } = 1;

    /// <summary>ぶれの長さ（シャッターが開いている間。フレームの間隔に対する割合 0〜1。0.5 は映画の 180 度シャッター）。</summary>
    public float Shutter { get; init; } = 0.5f;

    /// <summary>普通の絵と一緒に書くレンダーパス（静止画と連番のとき。動画では書かない）。</summary>
    public ExportPasses Passes { get; init; }

    /// <summary>書くレンダーパス（書く順。動画なら空）。</summary>
    public IReadOnlyList<ExportPasses> PassList => IsVideo ? [] : [.. ExportPassInfo.All.Where(p => Passes.HasFlag(p))];

    /// <summary>
    /// レンダーパスのファイル（<see cref="Frames"/> と同じ順）。名前の後ろに「_パスの語」を付ける
    /// （shot.png → shot_depth.png。連番は shot_depth_0000.png …）。
    /// </summary>
    public IReadOnlyList<string> PassPaths(ExportPasses pass)
    {
        string directory = Path.GetDirectoryName(OutputPath) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(OutputPath) + "_" + ExportPassInfo.Suffix(pass);
        string basePath = Path.Combine(directory, name + ".png");
        return Kind == ExportKind.Sequence ? [.. Frames.Select(f => ImageExport.SequencePath(basePath, f, LastFrame))] : [basePath];
    }

    /// <summary>レンダーパスの説明（奥行きの範囲・物と材質の色の対応表）を書くファイル（名前_passes.json）。</summary>
    public string PassInfoPath =>
        Path.Combine(Path.GetDirectoryName(OutputPath) ?? string.Empty, Path.GetFileNameWithoutExtension(OutputPath) + "_passes.json");

    public const int MaxMotionBlurSamples = 64;

    /// <summary>モーションブラーを使うか。</summary>
    public bool UsesMotionBlur => MotionBlurSamples > 1 && Shutter > 0f;

    /// <summary>
    /// フレーム <paramref name="frame"/> を描くときに分けて描く時刻（フレームの単位）。ぶれの長さの真ん中が <paramref name="frame"/> に
    /// なるよう前後に等しく広げ、それぞれの区間の真ん中を使う。0 より前は 0 にする。モーションブラーを使わなければ <paramref name="frame"/> だけ。
    /// </summary>
    public IReadOnlyList<float> SampleTimes(int frame)
    {
        if (!UsesMotionBlur)
        {
            return [frame];
        }
        int count = Math.Clamp(MotionBlurSamples, 2, MaxMotionBlurSamples);
        float shutter = Math.Clamp(Shutter, 0f, 1f);
        var times = new float[count];
        for (int i = 0; i < count; i++)
        {
            times[i] = Math.Max(0f, frame + (shutter * (((i + 0.5f) / count) - 0.5f)));
        }
        return times;
    }

    public bool IsVideo => Kind == ExportKind.Video;

    /// <summary>透明にするか（動画は透明にできないので、いつも false）。</summary>
    public bool UsesTransparency => Transparent && !IsVideo;

    /// <summary>
    /// 動画の映像の 1 秒あたりのビット数: 画素の数 × フレームの速さ × 0.1（高画質は 0.2）。1〜100 Mbps に収める。
    /// フル HD・30 fps で約 6 Mbps（高画質は約 12 Mbps）。
    /// </summary>
    public int VideoBitrate(int frameRate)
    {
        double bits = (double)Width * Height * Math.Max(1, frameRate) * (HighQuality ? 0.2 : 0.1);
        return (int)Math.Clamp(bits, 1_000_000, 100_000_000);
    }

    /// <summary>描く倍率（なめらかにするなら 2）。</summary>
    public int Supersample => Smooth ? 2 : 1;

    public int RenderWidth => Width * Supersample;

    public int RenderHeight => Height * Supersample;

    /// <summary>書き出すフレーム（静止画は <see cref="FirstFrame"/> だけ）。</summary>
    public IReadOnlyList<int> Frames => Kind == ExportKind.Still || LastFrame < FirstFrame
        ? [FirstFrame]
        : [.. Enumerable.Range(FirstFrame, LastFrame - FirstFrame + 1)];

    /// <summary>書き出すファイル（<see cref="Frames"/> と同じ順）。</summary>
    public IReadOnlyList<string> Paths => Kind switch
    {
        ExportKind.Still => [ImageExport.WithPngExtension(OutputPath)],
        ExportKind.Video => [Path.ChangeExtension(OutputPath, ".mp4")],
        _ => [.. Frames.Select(f => ImageExport.SequencePath(OutputPath, f, LastFrame))],
    };

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
        if (MotionBlurSamples < 1 || MotionBlurSamples > MaxMotionBlurSamples || !float.IsFinite(Shutter) || Shutter < 0f || Shutter > 1f)
        {
            errors.Add($"モーションブラーは、分ける数を 1〜{MaxMotionBlurSamples}、ぶれの長さを 0〜1 にしてください。");
        }
        if (FirstFrame < 0 || FirstFrame > endFrame)
        {
            errors.Add($"フレームは 0〜{endFrame:N0}（作品の終わり）の間にしてください。");
        }
        if (IsVideo && Width >= 1 && Height >= 1)
        {
            if (Width % 2 != 0 || Height % 2 != 0)
            {
                errors.Add("動画の幅と高さは偶数にしてください（H.264 の決まり）。");
            }
            if (Width > MaxVideoSide || Height > MaxVideoSide || (long)Width * Height > MaxVideoPixels)
            {
                errors.Add($"動画の大きさは、幅・高さとも {MaxVideoSide:N0} まで、合わせて 4,096 × 2,304 の画素の数までにしてください（H.264 の決まり）。");
            }
            if (Width < MinVideoSide || Height < MinVideoSide)
            {
                errors.Add($"動画の幅と高さは {MinVideoSide} 以上にしてください。");
            }
        }
        if (Kind != ExportKind.Still && (LastFrame < FirstFrame || LastFrame > endFrame))
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
    /// <summary>
    /// 奥行きのパスの範囲（カメラから見る向きの距離）。<paramref name="worldCorners"/>（物を囲む箱の角、ワールド）を
    /// <paramref name="view"/> でカメラから見て、いちばん手前と奥を求め、動いても収まるよう前後にゆとりを持たせる
    /// （手前へ幅の 1/4、奥へ幅の 1/2。幅は 1 以上とみなす。手前は 0.01 より手前にしない）。角が無ければ (0.01, 100)。
    /// </summary>
    public static (float Near, float Far) DepthRange(IEnumerable<Vector3> worldCorners, Matrix4x4 view)
    {
        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        foreach (Vector3 corner in worldCorners)
        {
            float z = Vector3.Transform(corner, view).Z;
            if (float.IsFinite(z))
            {
                min = Math.Min(min, z);
                max = Math.Max(max, z);
            }
        }
        if (!float.IsFinite(min) || !float.IsFinite(max))
        {
            return (0.01f, 100f);
        }
        float span = Math.Max(max - min, 1f);
        float near = Math.Max(0.01f, min - (span * 0.25f));
        float far = Math.Max(near + 0.01f, max + (span * 0.5f));
        return (near, far);
    }

    /// <summary>
    /// 奥行きのパスの画素（BGRA。赤が上位 8 ビット・緑が下位 8 ビットの 0〜65535、0 が手前）を、16 ビットの白黒にする
    /// （手前ほど白く 65535、奥ほど黒く 0。何も描かれていない所（透明度 0）は 0）。
    /// </summary>
    public static void DepthToGray16(ReadOnlySpan<byte> bgra, Span<ushort> gray)
    {
        for (int i = 0, p = 0; p < gray.Length && i + 3 < bgra.Length; i += 4, p++)
        {
            gray[p] = bgra[i + 3] == 0 ? (ushort)0 : (ushort)(65535 - ((bgra[i + 2] << 8) | bgra[i + 1]));
        }
    }

    /// <summary>BGRA の 1 つの成分（0 = 青・1 = 緑・2 = 赤・3 = 透明度）を、8 ビットの白黒にする。</summary>
    public static void ChannelToGray8(ReadOnlySpan<byte> bgra, int channel, Span<byte> gray)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, 3);
        for (int i = 0, p = 0; p < gray.Length && i + 3 < bgra.Length; i += 4, p++)
        {
            gray[p] = bgra[i + channel];
        }
    }

    public static string FormatColor(Vector3 color)
    {
        int Channel(float v) => (int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
        return $"#{Channel(color.X):X2}{Channel(color.Y):X2}{Channel(color.Z):X2}";
    }
}
