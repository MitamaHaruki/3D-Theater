using System.Text.Json;
using Theater3D.Formats.Pmx;

namespace Theater3D.Formats.Gltf;

/// <summary>
/// VRM（.vrm）と glTF 2.0（.glb・.gltf）を読み、PMX と同じ中身（<see cref="PmxDocument"/>）にする（設計判断記録 82 章）。
/// 変換の決まりは <see cref="GltfConverter"/>。元のファイルは書き換えない。
/// </summary>
public static class GltfReader
{
    // 「これが無いと読めない」と書かれていてもよい拡張（中身を使わないものも、無視して描ければよいもの）
    private static readonly HashSet<string> KnownRequiredExtensions = new(StringComparer.Ordinal)
    {
        "KHR_materials_unlit",
        "KHR_texture_transform",
        "KHR_mesh_quantization",
        "KHR_materials_emissive_strength",
        "VRM",
        "VRMC_vrm",
        "VRMC_springBone",
        "VRMC_springBone_extended_collider",
        "VRMC_materials_mtoon",
        "VRMC_node_constraint",
    };

    /// <summary>このリーダーで読むファイルか（拡張子が .vrm・.glb・.gltf）。</summary>
    public static bool IsGltfFile(string? path) =>
        Path.GetExtension(path)?.ToLowerInvariant() is ".vrm" or ".glb" or ".gltf";

    /// <summary>VRM のファイルか（拡張子が .vrm）。</summary>
    public static bool IsVrmFile(string? path) => string.Equals(Path.GetExtension(path), ".vrm", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>メモリ上のデータから読む。<paramref name="baseDirectory"/> は、別のファイルのデータや画像（.gltf）を探す場所。</summary>
    public static PmxLoadResult Read(byte[] data, string? baseDirectory = null, string? name = null) =>
        Read(data, baseDirectory, null, name, new LoadReport());

    private static PmxLoadResult Read(byte[] data, string? baseDirectory, string? path, string? name, LoadReport report)
    {
        try
        {
            using GltfFile file = GltfFile.Parse(data, baseDirectory);
            string version = GltfFile.TryGet(file.Root, "asset", out JsonElement asset) ? GltfFile.String(asset, "version") ?? string.Empty : string.Empty;
            if (!version.StartsWith("2.", StringComparison.Ordinal))
            {
                report.Error(LoadIssueCategory.Format, $"glTF の版「{version}」には対応していません（glTF 2.0 だけ）");
                return new PmxLoadResult(null, report, path);
            }
            string[] missing = [.. GltfFile.Strings(file.Root, "extensionsRequired").Where(e => !KnownRequiredExtensions.Contains(e))];
            if (missing.Length > 0)
            {
                report.Error(LoadIssueCategory.Unsupported,
                    $"このファイルは「{string.Join("・", missing)}」に対応していないと読めない作りです（形の圧縮など）。まだ対応していません");
                return new PmxLoadResult(null, report, path);
            }
            PmxDocument document = GltfConverter.ToPmx(file, name ?? "モデル", baseDirectory, report);
            PmxValidator.Validate(document, report, baseDirectory);
            return new PmxLoadResult(document, report, path);
        }
        catch (Exception ex) when (ex is ModelFormatException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException
            or OverflowException or IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException)
        {
            report.Error(LoadIssueCategory.Format, $"VRM・glTF を読めませんでした（{ex.Message}）");
            return new PmxLoadResult(null, report, path);
        }
    }
}
