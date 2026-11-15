using System.Text.Json;
using Theater3D.Formats.Gltf;
using Theater3D.Formats.Pmx;

namespace Theater3D.Formats.Fbx;

/// <summary>
/// FBX（.fbx。バイナリと文字の両方、7.0 以降）を読み、PMX と同じ中身（<see cref="PmxDocument"/>）にする（設計判断記録 83 章）。
/// FBX の場面を glTF の中身にしてから、VRM・glTF と同じ変換（向き・大きさ・材質・表情の元）を使う。元のファイルは書き換えない。
/// </summary>
public static class FbxReader
{
    /// <summary>このリーダーで読むファイルか（拡張子が .fbx）。</summary>
    public static bool IsFbxFile(string? path) => string.Equals(Path.GetExtension(path), ".fbx", StringComparison.OrdinalIgnoreCase);

    public static PmxLoadResult Load(string path)
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
            return new PmxLoadResult(null, report, path);
        }
        return Read(data, Path.GetDirectoryName(Path.GetFullPath(path)), path, Path.GetFileNameWithoutExtension(path), report);
    }

    /// <summary>メモリ上のデータから読む。<paramref name="baseDirectory"/> は、別のファイルの画像を探す場所。</summary>
    public static PmxLoadResult Read(byte[] data, string? baseDirectory = null, string? name = null) =>
        Read(data, baseDirectory, null, name, new LoadReport());

    private static PmxLoadResult Read(byte[] data, string? baseDirectory, string? path, string? name, LoadReport report)
    {
        try
        {
            FbxDocument document = FbxFile.Parse(data);
            if (document.Version < 7000)
            {
                string version = document.Version > 0 ? $"{document.Version / 1000}.{document.Version / 100 % 10}" : "不明";
                report.Error(LoadIssueCategory.Unsupported,
                    $"FBX の版 {version} には対応していません（7.0 以降だけ）。Blender などで、新しい FBX（7.4 など）に書き出し直してください");
                return new PmxLoadResult(null, report, path);
            }
            FbxScene scene = FbxScene.Build(document);
            string modelName = name ?? "モデル";
            byte[] glb = FbxConverter.ToGlb(scene, baseDirectory, modelName, report);
            using GltfFile file = GltfFile.Parse(glb, baseDirectory);
            string comment = scene.Creator.Length > 0 ? $"\n作ったソフト: {scene.Creator}" : string.Empty;
            PmxDocument pmx = GltfConverter.ToPmx(file, modelName, baseDirectory, report, new GltfSource("FBX", comment));
            PmxValidator.Validate(pmx, report, baseDirectory);
            return new PmxLoadResult(pmx, report, path);
        }
        catch (Exception ex) when (ex is ModelFormatException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException
            or OverflowException or IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException or InvalidCastException)
        {
            report.Error(LoadIssueCategory.Format, $"FBX を読めませんでした（{ex.Message}）");
            return new PmxLoadResult(null, report, path);
        }
    }
}
