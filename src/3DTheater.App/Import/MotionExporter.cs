using System.IO;
using System.Windows;
using Theater3D.App.Logging;
using Theater3D.App.Shell;
using Theater3D.Domain;
using Theater3D.Editor;
using Theater3D.Formats;
using Theater3D.Formats.Vmd;

namespace Theater3D.App.Import;

/// <summary>今操作しているモデルのキーを VMD に、今の姿勢を VPD に書き出す。注意があれば書き出しのレポートを出す。</summary>
internal static class MotionExporter
{
    public static void ExportVmd(EditorContext context, Window owner)
    {
        if (Target(context, owner, "モーションの書き出し") is not { } model)
        {
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "モーションを書き出す（VMD）",
            Filter = "モーション (*.vmd)|*.vmd",
            DefaultExt = ".vmd",
            AddExtension = true,
            FileName = SafeName(model.Name) + ".vmd",
        };
        if (dialog.ShowDialog(owner) != true)
        {
            return;
        }
        var report = new LoadReport();
        VmdDocument vmd = MotionExport.CreateVmd(model, context.Playback.FrameRate, report);
        Write(owner, dialog.FileName, VmdWriter.Write(vmd), report, "モーション",
            $"モーションを書き出しました: {dialog.FileName}（ボーンのキー {vmd.BoneKeys.Count:N0}・モーフのキー {vmd.MorphKeys.Count:N0}）");
    }

    public static void ExportCameraVmd(EditorContext context, Window owner)
    {
        if (context.MainCamera is not { } camera)
        {
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "カメラ・照明のモーションを書き出す（VMD）",
            Filter = "モーション (*.vmd)|*.vmd",
            DefaultExt = ".vmd",
            AddExtension = true,
            FileName = "カメラ.vmd",
        };
        if (dialog.ShowDialog(owner) != true)
        {
            return;
        }
        var report = new LoadReport();
        VmdDocument vmd = CameraMotion.CreateVmd(camera, context.Playback.FrameRate, report, context.Scene.MainLight);
        Write(owner, dialog.FileName, VmdWriter.Write(vmd), report, "カメラ・照明のモーション",
            $"カメラ・照明のモーションを書き出しました: {dialog.FileName}（カメラのキー {vmd.CameraKeys.Count:N0}・照明のキー {vmd.LightKeys.Count:N0}）");
    }

    public static void ExportVpd(EditorContext context, Window owner)
    {
        if (Target(context, owner, "ポーズの書き出し") is not { } model)
        {
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "ポーズを書き出す（VPD）",
            Filter = "ポーズ (*.vpd)|*.vpd",
            DefaultExt = ".vpd",
            AddExtension = true,
            FileName = SafeName(model.Name) + ".vpd",
        };
        if (dialog.ShowDialog(owner) != true)
        {
            return;
        }
        VpdDocument pose = MotionExport.CreateVpd(model, Path.GetFileNameWithoutExtension(model.FilePath) + ".osm");
        var report = new LoadReport();
        if (pose.Bones.Count == 0 && pose.Morphs.Count == 0)
        {
            report.Warning(LoadIssueCategory.Note, "動かしているボーン・値の入ったモーフが無いため、空のポーズになります");
        }
        Write(owner, dialog.FileName, VpdWriter.Write(pose), report, "ポーズ",
            $"ポーズを書き出しました: {dialog.FileName}（ボーン {pose.Bones.Count:N0}・モーフ {pose.Morphs.Count:N0}、今のフレーム {context.Playback.CurrentFrame} の姿勢）");
    }

    private static ModelNode? Target(EditorContext context, Window owner, string what)
    {
        ModelNode? model = MotionImporter.TargetModel(context);
        if (model is null)
        {
            MessageBox.Show(owner, "書き出すモデルを、シーン一覧かプレビューで選んでください。", what, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        return model;
    }

    private static void Write(Window owner, string path, byte[] bytes, LoadReport report, string what, string summary)
    {
        try
        {
            SafeFile.Write(path, temp => File.WriteAllBytes(temp, bytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            AppLog.Error($"{what}を書き出せませんでした: {path}", ex);
            MessageBox.Show(owner, $"{what}を書き出せませんでした。\n\n{ex.Message}", "書き出し", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        foreach (LoadIssue issue in report.Issues)
        {
            AppLog.Info($"[書き出し] {issue.CategoryText}: {issue.Message}");
        }
        AppLog.Info(summary);
        if (report.Issues.Any(i => i.Severity >= LoadSeverity.Warning))
        {
            LoadReportWindow.Show(owner, path, report, null, what, isExport: true);
        }
    }

    private static string SafeName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
