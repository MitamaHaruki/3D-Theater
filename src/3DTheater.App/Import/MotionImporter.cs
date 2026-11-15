using System.Diagnostics;
using System.IO;
using System.Windows;
using Theater3D.App.Logging;
using Theater3D.Domain;
using Theater3D.Editor;
using Theater3D.Formats;
using Theater3D.Formats.Fbx;
using Theater3D.Formats.Vmd;

namespace Theater3D.App.Import;

/// <summary>
/// モーション（VMD・FBX）とポーズ（VPD）を読み込み、今操作しているモデルに当てはめる。FBX のモーションは、当てはめるモデルの骨組みに合わせて
/// VMD の中身にしてから当てはめる（設計判断記録 85 章）。
/// モーションはキーとして足し（同じフレームのキーは置き換える）、ポーズは今の値として置く（未登録の変更になる）。
/// 注意があれば読み込みレポートを出す。ファイルの読み込みは画面を止めないよう別のスレッドで行う。
/// </summary>
internal static class MotionImporter
{
    public const string FileFilter =
        "モーション・ポーズ (*.vmd;*.vpd;*.fbx)|*.vmd;*.vpd;*.fbx|モーション (*.vmd)|*.vmd|ポーズ (*.vpd)|*.vpd|FBX のモーション (*.fbx)|*.fbx|すべてのファイル (*.*)|*.*";

    public static bool IsMotionFile(string path) =>
        Path.GetExtension(path).Equals(".vmd", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".vpd", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// モーションだけの FBX か（形が無く、動くテイクがある。Mixamo の「スキンなし」など）。ドラッグしたときに、モデルかモーションかを分ける
    /// （中身を読むので、ドラッグの途中では呼ばない）。
    /// </summary>
    public static bool IsFbxMotion(string path) => FbxReader.IsFbxFile(path) && FbxMotionReader.LooksLikeMotion(path);

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
        bool isPoseFile = Path.GetExtension(path).Equals(".vpd", StringComparison.OrdinalIgnoreCase);
        bool isFbx = FbxReader.IsFbxFile(path);
        VmdLoadResult? preloaded = null;
        if (!isPoseFile && !isFbx)
        {
            // カメラ・照明用のモーション（またはカメラ・照明を選んでいるとき）は、モデルではなくシーンのカメラ・照明に当てはめる。
            // 照明のキーは、照明を選んでいればその照明、そうでなければ MMD の照明へ
            preloaded = await Task.Run(() => VmdReader.Load(path));
            if (preloaded.Succeeded && context.MainCamera is { } camera)
            {
                VmdDocument vmd = preloaded.Document!;
                bool cameraSelected = context.Selection.Primary is CameraNode && vmd.CameraKeys.Count > 0;
                bool lightSelected = context.Selection.Primary is LightNode && vmd.LightKeys.Count > 0;
                bool shadowSelected = context.Selection.Primary is SelfShadowNode && vmd.ShadowKeys.Count > 0;
                if (vmd.IsCameraMotion || cameraSelected || lightSelected || shadowSelected)
                {
                    LightNode light = context.Selection.Primary as LightNode ?? context.Scene.MainLight;
                    bool useCamera = vmd.CameraKeys.Count > 0 && !lightSelected && !shadowSelected;
                    bool useLight = vmd.LightKeys.Count > 0 && !shadowSelected && (vmd.IsCameraMotion || lightSelected);
                    bool useShadow = vmd.ShadowKeys.Count > 0 && ((vmd.IsCameraMotion && !lightSelected) || shadowSelected);
                    ImportCameraAndLight(context, useCamera ? camera : null, useLight ? light : null, useShadow ? context.Scene.SelfShadow : null,
                        path, preloaded, owner);
                    return;
                }
            }
        }

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
            PoseImportResult applied = null!;
            context.Edits.Change($"ポーズの読み込み（{Path.GetFileName(path)}）", [.. model.Bones, .. model.Morphs],
                () => applied = MotionImport.ApplyVpd(result.Document!, model, result.Report));
            Finish(owner, path, result.Report, "ポーズ",
                $"ポーズを「{model.Name}」に当てはめました（ボーン {applied.Bones:N0}・モーフ {applied.Morphs:N0}、{stopwatch.Elapsed.TotalMilliseconds:F0} ms）。" +
                "キーにするにはタイムラインの「登録」を押してください");
            return;
        }

        VmdLoadResult motion;
        if (isFbx)
        {
            // FBX のモーション: 当てはめるモデルの骨組みに合わせて、VMD の中身にする
            if (context.GetLoaded(model)?.Document is not { } document)
            {
                AppLog.Warning($"モーションを読み込めませんでした（モデル「{model.Name}」の中身がありません）: {path}");
                return;
            }
            motion = await Task.Run(() => FbxMotionReader.Load(path, document));
        }
        else
        {
            motion = preloaded ?? await Task.Run(() => VmdReader.Load(path));
        }
        if (!motion.Succeeded)
        {
            Finish(owner, path, motion.Report, "モーション", $"モーションを読み込めませんでした: {path}");
            return;
        }
        MotionImportResult imported = null!;
        var targets = new List<SceneNode>([.. model.Bones, .. model.Morphs]);
        if (model.Display is { } display)
        {
            targets.Add(display); // 表示・IK のキーも 1 回の元に戻すで戻す
        }
        // 読み込みと、それに合わせて伸ばした終わりのフレームは、1 回の元に戻すで戻す
        using (context.Undo.BeginGroup())
        {
            context.Edits.Change($"モーションの読み込み（{Path.GetFileName(path)}）", targets,
                () => imported = MotionImport.ApplyVmd(motion.Document!, model, context.Playback.FrameRate, motion.Report), includeKeys: true);
            if (context.Edits.ExtendEndFrame(imported.LastFrame))
            {
                motion.Report.Info(LoadIssueCategory.Note, $"モーションの長さに合わせて、終わりのフレームを {imported.LastFrame:N0} にしました");
            }
        }
        context.Animation.ApplyFrame();
        Finish(owner, path, motion.Report, "モーション",
            $"モーションを「{model.Name}」に当てはめました（ボーン {imported.Bones:N0} 本のキー {imported.BoneKeys:N0}・モーフ {imported.Morphs:N0} 個のキー {imported.MorphKeys:N0}" +
            (imported.DisplayKeys > 0 ? $"・表示・IK のキー {imported.DisplayKeys:N0}、" : "、") +
            $"最後のキー {imported.LastFrame:N0} フレーム、{stopwatch.Elapsed.TotalMilliseconds:F0} ms）");
    }

    /// <summary>
    /// カメラ・照明・セルフ影のモーションを当てはめる。<paramref name="camera"/>・<paramref name="light"/>・<paramref name="selfShadow"/>
    /// が null なら、そのキーは使わない（どれかは null でない）。
    /// </summary>
    private static void ImportCameraAndLight(EditorContext context, CameraNode? camera, LightNode? light, SelfShadowNode? selfShadow, string path,
        VmdLoadResult motion, Window owner)
    {
        VmdDocument vmd = motion.Document!;
        foreach (SceneNode node in new SceneNode?[] { camera, light, selfShadow }.OfType<SceneNode>())
        {
            if (!node.CanEdit)
            {
                MessageBox.Show(owner, $"「{node.Name}」は編集ロック中のため、モーションを当てはめられません（シーン一覧の鍵で外せます）。",
                    "モーションの読み込み", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        context.Playback.Pause();
        SceneNode[] targets = [.. new SceneNode?[] { camera, light, selfShadow }.OfType<SceneNode>()];
        AppLog.Info($"カメラ・照明のモーションを読み込みます: {path}（当てはめる先: {string.Join("・", targets.Select(n => n.Name))}）");
        CameraImportResult? cameraResult = null;
        LightImportResult? lightResult = null;
        SelfShadowImportResult? shadowResult = null;
        int lastFrame;
        using (context.Undo.BeginGroup())
        {
            context.Edits.Change($"カメラ・照明のモーションの読み込み（{Path.GetFileName(path)}）", targets, () =>
            {
                cameraResult = camera is null ? null : CameraMotion.Apply(vmd, camera, context.Playback.FrameRate, motion.Report);
                lightResult = light is null ? null : LightMotion.Apply(vmd, light, context.Playback.FrameRate, motion.Report);
                shadowResult = selfShadow is null ? null : SelfShadowMotion.Apply(vmd, selfShadow, context.Playback.FrameRate, motion.Report);
            }, includeKeys: true);
            lastFrame = Math.Max(Math.Max(cameraResult?.LastFrame ?? 0, lightResult?.LastFrame ?? 0), shadowResult?.LastFrame ?? 0);
            if (context.Edits.ExtendEndFrame(lastFrame))
            {
                motion.Report.Info(LoadIssueCategory.Note, $"モーションの長さに合わせて、終わりのフレームを {lastFrame:N0} にしました");
            }
        }
        if (light is null && vmd.LightKeys.Count > 0)
        {
            motion.Report.Info(LoadIssueCategory.Note, $"照明のキー {vmd.LightKeys.Count:N0} 個は使いませんでした（照明を選んで読み込むと当てはまります）");
        }
        context.Animation.ApplyFrame();
        var parts = new List<string>();
        if (camera is not null && cameraResult is not null)
        {
            parts.Add($"カメラ「{camera.Name}」にキー {cameraResult.Keys:N0}");
        }
        if (light is not null && lightResult is not null)
        {
            parts.Add($"照明「{light.Name}」にキー {lightResult.Keys:N0}");
        }
        if (selfShadow is not null && shadowResult is not null)
        {
            parts.Add($"セルフ影にキー {shadowResult.Keys:N0}");
        }
        if (selfShadow is null && vmd.ShadowKeys.Count > 0)
        {
            motion.Report.Info(LoadIssueCategory.Note, $"セルフ影のキー {vmd.ShadowKeys.Count:N0} 個は使いませんでした（セルフ影を選んで読み込むと当てはまります）");
        }
        Finish(owner, path, motion.Report, "モーション",
            $"カメラ・照明のモーションを当てはめました（{string.Join("・", parts)}、最後のキー {lastFrame:N0} フレーム）。"
            + (camera is not null ? "カメラ視点の区画で見られます" : string.Empty));
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
