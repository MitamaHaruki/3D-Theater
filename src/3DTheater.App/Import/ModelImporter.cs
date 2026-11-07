using System.Diagnostics;
using System.IO;
using System.Windows;
using Theater3D.Animation;
using Theater3D.App.Logging;
using Theater3D.Domain;
using Theater3D.Editor;
using Theater3D.Engine.Interop;
using Theater3D.Formats;
using Theater3D.Formats.Pmx;

namespace Theater3D.App.Import;

/// <summary>
/// モデルのファイルを読み込み、シーンの「モデル」に加える。読み込みは画面を止めないよう別のスレッドで行う。
/// 注意やエラーがあれば読み込みレポートを出す（情報だけのときはログに残すだけ）。
/// 読めたモデルは GPU に載せて全プレビューに表示し、ボーンとモーフをシーン一覧に加える。
/// </summary>
internal static class ModelImporter
{
    public const string FileFilter = "PMX モデル (*.pmx)|*.pmx|すべてのファイル (*.*)|*.*";

    /// <summary>ドラッグで受け付けるファイルか（PMD は読めない理由をレポートで伝えるために受け付ける）。</summary>
    public static bool IsModelFile(string path) =>
        Path.GetExtension(path).Equals(".pmx", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".pmd", StringComparison.OrdinalIgnoreCase);

    /// <param name="interactive">
    /// true なら読み込んだモデルを選び、注意があれば読み込みレポートの窓を出す。
    /// false（プロジェクトを開くとき）は選ばず、窓も出さない（レポートはプロパティの「読み込みレポートを見る」で見られる）。
    /// </param>
    public static async Task<ModelNode?> ImportAsync(EditorContext context, string path, Window owner, bool interactive = true)
    {
        AppLog.Info($"モデルを読み込みます: {path}");
        var stopwatch = Stopwatch.StartNew();
        string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;

        // ファイルの読み込み・形の組み立て・テクスチャの読み込みは別のスレッドで行い、画面を止めない
        (PmxLoadResult result, PreparedModel? prepared, ModelRig? rig) = await Task.Run<(PmxLoadResult, PreparedModel?, ModelRig?)>(() =>
        {
            PmxLoadResult loaded = PmxReader.Load(path);
            if (!loaded.Succeeded)
            {
                return (loaded, null, null);
            }
            return (loaded, ModelMeshBuilder.Prepare(loaded.Document!, baseDirectory, loaded.Report), ModelRig.FromPmx(loaded.Document!));
        });

        if (!result.Succeeded)
        {
            LogIssues(result.Report);
            AppLog.Warning($"モデルを読み込めませんでした: {path}");
            if (interactive)
            {
                LoadReportWindow.Show(owner, path, result.Report, null);
            }
            return null;
        }

        // GPU に載せるのはエンジンを作ったスレッド（画面のスレッド）で行う
        NativeMesh? mesh = null;
        if (context.Engine is not null)
        {
            try
            {
                mesh = ModelMeshBuilder.Upload(context.Engine, prepared!, result.Report);
            }
            catch (NativeEngineException ex)
            {
                result.Report.Warning(LoadIssueCategory.Format, $"モデルの形を GPU に載せられなかったため、表示できません（{ex.Message}）");
            }
        }
        stopwatch.Stop();
        LogIssues(result.Report);

        PmxDocument d = result.Document!;
        string name = d.Name.Trim().Length > 0 ? d.Name.Trim() : Path.GetFileNameWithoutExtension(path);
        var summary = new ModelSummary(
            $"PMX {d.VersionText}（{(d.Encoding == PmxTextEncoding.Utf16 ? "UTF-16" : "UTF-8")}）",
            d.Vertices.Length,
            d.FaceCount,
            d.Materials.Length,
            d.Textures.Length,
            d.Bones.Length,
            d.Morphs.Length,
            d.RigidBodies.Length,
            d.Joints.Length);
        var node = new ModelNode(name, Path.GetFullPath(path), summary, d.Comment, prepared!.DisplayBoundsMin, prepared.DisplayBoundsMax);
        ModelPartsBuilder.Attach(node, d, rig!);

        context.Models[node.Id] = new LoadedModel(d, result.Report, mesh, rig!, prepared);
        context.Scene.Add(context.Scene.ModelsGroup, node);
        if (interactive)
        {
            context.Selection.Select(node, SelectMode.Replace, null);
            ModelPresenceAction.RecordImport(context, node); // 読み込みも元に戻せる
        }
        AppLog.Info(
            $"モデル「{name}」を読み込みました（{stopwatch.Elapsed.TotalMilliseconds:F0} ms、頂点 {summary.Vertices:N0}・材質 {summary.Materials:N0}・" +
            $"ボーン {summary.Bones:N0}・モーフ {summary.Morphs:N0}、注意 {result.Report.WarningCount} 件）");

        if (interactive && result.Report.Issues.Any(i => i.Severity >= LoadSeverity.Warning))
        {
            LoadReportWindow.Show(owner, path, result.Report, summary);
        }
        return node;
    }

    private static void LogIssues(LoadReport report)
    {
        foreach (LoadIssue issue in report.Issues)
        {
            string line = $"[読み込み] {issue.CategoryText}: {issue.Message}";
            if (issue.Severity == LoadSeverity.Error)
            {
                AppLog.Error(line);
            }
            else if (issue.Severity == LoadSeverity.Warning)
            {
                AppLog.Warning(line);
            }
            else
            {
                AppLog.Info(line);
            }
        }
    }
}
