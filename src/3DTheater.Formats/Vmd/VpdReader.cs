using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Theater3D.Formats.Vmd;

public sealed record VpdBone(string Name, Vector3 Translation, Quaternion Rotation);

public sealed record VpdMorph(string Name, float Weight);

/// <summary>VPD（MMD のポーズ）の中身。1 フレーム分の、ボーンの移動・回転とモーフの値。</summary>
public sealed class VpdDocument
{
    /// <summary>ポーズを作ったモデルのファイル名（「miku.osm」など。参考の情報）。</summary>
    public string ModelFileName { get; init; } = string.Empty;

    public IReadOnlyList<VpdBone> Bones { get; init; } = [];

    public IReadOnlyList<VpdMorph> Morphs { get; init; } = [];
}

public sealed record VpdLoadResult(VpdDocument? Document, LoadReport Report, string? SourcePath)
{
    public bool Succeeded => Document is not null;
}

/// <summary>
/// VPD（MMD のポーズ、Shift-JIS の文字のファイル）を読む。書き方の小さな違い（空白・改行・コメントの位置）は許す。
/// 数が読めないボーンは飛ばしてレポートに載せ、ほかのボーンは使う。
/// </summary>
public static partial class VpdReader
{
    public static VpdLoadResult Load(string path)
    {
        var report = new LoadReport();
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            report.Error(LoadIssueCategory.Format, $"ファイルを開けませんでした: {ex.Message}");
            return new VpdLoadResult(null, report, path);
        }
        return Read(data, path, report);
    }

    public static VpdLoadResult Read(byte[] data) => Read(data, null, new LoadReport());

    private static VpdLoadResult Read(byte[] data, string? path, LoadReport report)
    {
        string text = VmdText.ShiftJis.GetString(data);
        // コメント（// から行の終わりまで）を消す
        string body = CommentPattern().Replace(text, string.Empty);
        if (!body.TrimStart().StartsWith("Vocaloid Pose Data file", StringComparison.Ordinal))
        {
            report.Error(LoadIssueCategory.Format, "VPD ファイルではありません（最初の行が「Vocaloid Pose Data file」ではありません）");
            return new VpdLoadResult(null, report, path);
        }

        // 見出しの後ろ、最初の「;」までがモデルのファイル名
        string afterHeader = body.TrimStart()["Vocaloid Pose Data file".Length..];
        int semicolon = afterHeader.IndexOf(';');
        string modelFile = semicolon >= 0 ? afterHeader[..semicolon].Trim() : string.Empty;

        var bones = new List<VpdBone>();
        var badBones = new List<string>();
        foreach (Match m in BonePattern().Matches(body))
        {
            string name = m.Groups["name"].Value.Trim();
            float[] numbers = ParseNumbers(m.Groups["body"].Value);
            if (name.Length == 0 || numbers.Length != 7 || numbers.Any(n => !float.IsFinite(n)))
            {
                badBones.Add(name.Length == 0 ? "（名前なし）" : name);
                continue;
            }
            var rotation = new Quaternion(numbers[3], numbers[4], numbers[5], numbers[6]);
            rotation = rotation.LengthSquared() > 1e-12f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
            bones.Add(new VpdBone(name, new Vector3(numbers[0], numbers[1], numbers[2]), rotation));
        }

        var morphs = new List<VpdMorph>();
        var badMorphs = new List<string>();
        foreach (Match m in MorphPattern().Matches(body))
        {
            string name = m.Groups["name"].Value.Trim();
            float[] numbers = ParseNumbers(m.Groups["body"].Value);
            if (name.Length == 0 || numbers.Length != 1 || !float.IsFinite(numbers[0]))
            {
                badMorphs.Add(name.Length == 0 ? "（名前なし）" : name);
                continue;
            }
            morphs.Add(new VpdMorph(name, numbers[0]));
        }

        report.AddAggregated(LoadSeverity.Warning, LoadIssueCategory.Format, "数が正しく読めなかったため使わないボーン", badBones, badBones.Count);
        report.AddAggregated(LoadSeverity.Warning, LoadIssueCategory.Format, "数が正しく読めなかったため使わないモーフ", badMorphs, badMorphs.Count);
        if (bones.Count == 0 && morphs.Count == 0 && badBones.Count == 0 && badMorphs.Count == 0)
        {
            report.Warning(LoadIssueCategory.Format, "ポーズのボーンが 1 つも書かれていません");
        }
        return new VpdLoadResult(new VpdDocument { ModelFileName = modelFile, Bones = bones, Morphs = morphs }, report, path);
    }

    private static float[] ParseNumbers(string body) =>
        [.. body.Split([',', ';', '\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : float.NaN)];

    [GeneratedRegex(@"//[^\r\n]*")]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"Bone\d*\s*\{(?<name>[^\r\n{}]*)(?<body>[^{}]*)\}")]
    private static partial Regex BonePattern();

    [GeneratedRegex(@"Morph\d*\s*\{(?<name>[^\r\n{}]*)(?<body>[^{}]*)\}")]
    private static partial Regex MorphPattern();
}
