using System.Diagnostics;
using System.IO;
using System.Windows;
using Theater3D.App.Logging;
using Theater3D.Domain;
using Theater3D.Editor;
using Theater3D.Formats;
using Theater3D.Formats.Vmd;

namespace Theater3D.App.Import;

/// <summary>
/// モーション（VMD）とポーズ（VPD）を読み込み、今操作しているモデルに当てはめる。
/// モーションはキーとして足し（同じフレームのキーは置き換える）、ポーズは今の値として置く（未登録の変更になる）。
/// 注意があれば読み込みレポートを出す。ファイルの読み込みは画面を止めないよう別のスレッドで行う。
/// </summary>
internal static class MotionImporter
{
    public const string FileFilter = "モーション・ポーズ (*.vmd;*.vpd)|*.vmd;*.vpd|モーション (*.vmd)|*.vmd|ポーズ (*.vpd)|*.vpd|すべてのファイル (*.*)|*.*";

    public static bool IsMotionFile(string path) =>
        Path.GetExtension(path).Equals(".vmd", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".vpd", StringComparison.OrdinalIgnoreCase);

    /// <summary>当てはめる先: 今操作しているモデル。無ければ、シーンにモデルが 1 体だけならそのモデル。</summary>
    public static ModelNode? TargetModel(EditorContext context)
    {
        if (context.ActiveModel is { } active)
        {
            return active;
        }
        ModelNode[] models = [.. context.Scene.ModelsGroup.Children.OfType<ModelNode>()];
        return models.Length == 1 ? models[0] : null;
    }

    public static async Task ImportAsync(EditorContext context, string path, Window owner)
    {
        ModelNode? model = TargetModel(context);
        if (model is null)
        {
            string message = context.Scene.ModelsGroup.Children.OfType<ModelNode>().Any()
                ? "モーションを当てはめるモデルを、シーン一覧かプレビューで選んでから読み込んでください。"
                : "先にモデル（PMX）を読み込んでください。モーションはモデルに当てはめて使います。";
            AppLog.Warning($"モーションを読み込めませんでした（当てはめるモデルがありません）: {path}");
            MessageBox.Show(owner, message, "モーションの読み込み", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!model.CanEdit)
        {
            MessageBox.Show(owner, $"「{model.Name}」は編集ロック中のため、モーションを当てはめられません（シーン一覧の鍵で外せます）。",
                "モーションの読み込み", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        context.Playback.Pause();
        bool isPose = Path.GetExtension(path).Equals(".vpd", StringComparison.OrdinalIgnoreCase);
        AppLog.Info($"{(isPose ? "ポーズ" : "モーション")}を読み込みます: {path}（当てはめる先: {model.Name}）");
        var stopwatch = Stopwatch.StartNew();
        if (isPose)
        {
            VpdLoadResult result = await Task.Run(() => VpdReader.Load(path));
            if (!result.Succeeded)
            {
                Finish(owner, path, result.Report, "ポーズ", $"ポーズを読み込めませんでした: {path}");
                return;
            }
            PoseImportResult applied = MotionImport.ApplyVpd(result.Document!, model, result.Report);
            Finish(owner, path, result.Report, "ポーズ",
                $"ポーズを「{model.Name}」に当てはめました（ボーン {applied.Bones:N0}・モーフ {applied.Morphs:N0}、{stopwatch.Elapsed.TotalMilliseconds:F0} ms）。" +
                "キーにするにはタイムラインの「登録」を押してください");
            return;
        }

        VmdLoadResult motion = await Task.Run(() => VmdReader.Load(path));
        if (!motion.Succeeded)
        {
            Finish(owner, path, motion.Report, "モーション", $"モーションを読み込めませんでした: {path}");
            return;
        }
        MotionImportResult imported = MotionImport.ApplyVmd(motion.Document!, model, context.Playback.FrameRate, motion.Report);
        if (imported.LastFrame > context.Playback.EndFrame)
        {
            context.Playback.EndFrame = imported.LastFrame;
            motion.Report.Info(LoadIssueCategory.Note, $"モーションの長さに合わせて、終わりのフレームを {imported.LastFrame:N0} にしました");
        }
        context.Animation.ApplyFrame();
        Finish(owner, path, motion.Report, "モーション",
            $"モーションを「{model.Name}」に当てはめました（ボーン {imported.Bones:N0} 本のキー {imported.BoneKeys:N0}・モーフ {imported.Morphs:N0} 個のキー {imported.MorphKeys:N0}、" +
            $"最後のキー {imported.LastFrame:N0} フレーム、{stopwatch.Elapsed.TotalMilliseconds:F0} ms）");
    }

    private static void Finish(Window owner, string path, LoadReport report, string what, string summary)
    {
        foreach (LoadIssue issue in report.Issues)
        {
            string line = $"[読み込み] {issue.CategoryText}: {issue.Message}";
            switch (issue.Severity)
            {
                case LoadSeverity.Error:
                    AppLog.Error(line);
                    break;
                case LoadSeverity.Warning:
                    AppLog.Warning(line);
                    break;
                default:
                    AppLog.Info(line);
                    break;
            }
        }
        if (report.HasErrors)
        {
            AppLog.Warning(summary);
        }
        else
        {
            AppLog.Info(summary);
        }
        if (report.Issues.Any(i => i.Severity >= LoadSeverity.Warning))
        {
            LoadReportWindow.Show(owner, path, report, null, what);
        }
    }
}
