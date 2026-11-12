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
using Theater3D.Formats.XFile;
using Theater3D.Physics;

namespace Theater3D.App.Import;

/// <summary>
/// モデルのファイルを読み込み、シーンの「モデル」に加える。読み込みは画面を止めないよう別のスレッドで行う。
/// 注意やエラーがあれば読み込みレポートを出す（情報だけのときはログに残すだけ）。
/// 読めたモデルは GPU に載せて全プレビューに表示し、ボーンとモーフをシーン一覧に加える。
/// </summary>
internal static class ModelImporter
{
    public const string FileFilter =
        "モデル・アクセサリ (*.pmx;*.x;*.vac)|*.pmx;*.x;*.vac|PMX モデル (*.pmx)|*.pmx|アクセサリ (*.x;*.vac)|*.x;*.vac|すべてのファイル (*.*)|*.*";

    public const string AccessoryFilter = "アクセサリ (*.x;*.vac)|*.x;*.vac|すべてのファイル (*.*)|*.*";

    /// <summary>ドラッグで受け付けるファイルか（PMD は読めない理由をレポートで伝えるために受け付ける）。.x・.vac は MMD のアクセサリ。</summary>
    public static bool IsModelFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".pmx" or ".pmd" or ".x" or ".vac";

    public static bool IsXFile(string path) => Path.GetExtension(path).Equals(".x", StringComparison.OrdinalIgnoreCase);

    public static bool IsVacFile(string path) => Path.GetExtension(path).Equals(".vac", StringComparison.OrdinalIgnoreCase);

    /// <param name="interactive">
    /// true なら読み込んだモデルを選び、注意があれば読み込みレポートの窓を出す。
    /// false（プロジェクトを開くとき）は選ばず、窓も出さない（レポートはプロパティの「読み込みレポートを見る」で見られる）。
    /// </param>
    /// <param name="configure">シーンに加える前に、名前・置き方・付ける先などを決める（.vac のとき）。</param>
    public static async Task<ModelNode?> ImportAsync(EditorContext context, string path, Window owner, bool interactive = true, Action<ModelNode>? configure = null)
    {
        AppLog.Info($"モデルを読み込みます: {path}");
        var stopwatch = Stopwatch.StartNew();
        string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;

        // ファイルの読み込み・形の組み立て・テクスチャの読み込みは別のスレッドで行い、画面を止めない
        (PmxLoadResult result, PreparedModel? prepared, ModelRig? rig, ModelPhysics? physics) =
            await Task.Run<(PmxLoadResult, PreparedModel?, ModelRig?, ModelPhysics?)>(() =>
        {
            PmxLoadResult loaded = IsXFile(path) ? XFileReader.Load(path) : PmxReader.Load(path);
            if (!loaded.Succeeded)
            {
                return (loaded, null, null, null);
            }
            ModelRig rig = ModelRig.FromPmx(loaded.Document!);
            return (loaded, ModelMeshBuilder.Prepare(loaded.Document!, baseDirectory, loaded.Report), rig, CreatePhysics(loaded.Document!, rig, loaded.Report));
        });

        if (!result.Succeeded)
        {
            physics?.Dispose();
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
                mesh = UploadMesh(context, prepared!, result.Report);
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
            IsXFile(path) ? $"DirectX .x（{XFormatName(path)}・MMD のアクセサリ）" : $"PMX {d.VersionText}（{(d.Encoding == PmxTextEncoding.Utf16 ? "UTF-16" : "UTF-8")}）",
            d.Vertices.Length,
            d.FaceCount,
            d.Materials.Length,
            d.Textures.Length,
            d.Bones.Length,
            d.Morphs.Length,
            d.RigidBodies.Length,
            d.Joints.Length);
        var node = new ModelNode(name, Path.GetFullPath(path), summary, d.Comment, prepared!.DisplayBoundsMin, prepared.DisplayBoundsMax)
        {
            MaterialNames = [.. d.Materials.Select(m => m.Name)],
        };
        ModelPartsBuilder.Attach(node, d, rig!);
        configure?.Invoke(node);

        context.Models[node.Id] = new LoadedModel(d, result.Report, mesh, rig!, prepared, physics);
        context.Scene.Add(context.Scene.ModelsGroup, node);
        if (interactive)
        {
            context.Selection.Select(node, SelectMode.Replace, null);
            ModelPresenceAction.RecordImport(context, node); // 読み込みも元に戻せる
        }
        AppLog.Info(
            $"モデル「{name}」を読み込みました（{stopwatch.Elapsed.TotalMilliseconds:F0} ms、頂点 {summary.Vertices:N0}・材質 {summary.Materials:N0}・" +
            $"ボーン {summary.Bones:N0}・モーフ {summary.Morphs:N0}、注意 {result.Report.WarningCount} 件）");
        if (physics is not null)
        {
            AppLog.Info($"物理演算: 剛体 {physics.BodyCount:N0} 個（物理で動くもの {physics.DynamicBodyCount:N0} 個）・ジョイント {physics.JointCount:N0} 個");
        }

        if (interactive && result.Report.Issues.Any(i => i.Severity >= LoadSeverity.Warning))
        {
            LoadReportWindow.Show(owner, path, result.Report, summary);
        }
        return node;
    }

    /// <summary>
    /// .vac（MMD のアクセサリの置き方）を読み、書かれた .x を読み込んで、名前・大きさ・位置・回転を決め、書かれたボーンに付ける。
    /// 付ける先は、選んでいるモデルにそのボーンがあればそのモデル、無ければそのボーンを持つ最初のモデル（無ければ付けずに置く）。
    /// 位置・回転は、付けるボーンから見た値。
    /// </summary>
    public static async Task<ModelNode?> ImportVacAsync(EditorContext context, string path, Window owner)
    {
        VacFile vac;
        try
        {
            vac = VacFile.Load(path);
        }
        catch (Exception ex) when (ex is ModelFormatException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($".vac を読めませんでした: {path}", ex);
            MessageBox.Show(owner, $".vac を読めませんでした。\n\n{ex.Message}", "アクセサリ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        string xPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, vac.XFile);
        ModelNode? target = null;
        if (vac.BoneName is { } boneName)
        {
            bool HasBone(ModelNode m) => m.Bones.Any(b => b.Name == boneName);
            target = context.ActiveModel is { } active && HasBone(active)
                ? active
                : context.Scene.ModelsGroup.Children.OfType<ModelNode>().FirstOrDefault(HasBone);
        }
        AppLog.Info($".vac を読み込みます: {path}（{vac.XFile}・大きさ {vac.Scale}・ボーン {vac.BoneName ?? "なし"}）");
        ModelNode? node = await ImportAsync(context, xPath, owner, interactive: true, configure: n =>
        {
            if (vac.Name.Length > 0)
            {
                n.Name = vac.Name;
            }
            n.Scale = new System.Numerics.Vector3(vac.Scale);
            n.Position = vac.Position;
            n.Rotation = vac.RotationDegrees;
            if (target is not null)
            {
                n.Attachment = new ModelAttachment(target.Id, vac.BoneName!);
            }
        });
        if (node is not null)
        {
            if (target is not null)
            {
                AppLog.Info($"「{node.Name}」を「{target.Name}」の「{vac.BoneName}」に付けました");
            }
            else if (vac.BoneName is not null)
            {
                AppLog.Warning($"ボーン「{vac.BoneName}」を持つモデルが無いため、「{node.Name}」を付けずに置きました（後からプロパティ欄の「付ける先」で付けられます）");
            }
        }
        return node;
    }

    private static string XFormatName(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            byte[] head = new byte[16];
            int read = stream.Read(head, 0, head.Length);
            return XFileReader.FormatName(head.AsSpan(0, read)) ?? "不明";
        }
        catch (IOException)
        {
            return "不明";
        }
    }

    /// <summary>
    /// 形を GPU に載せる。GPU が使えなくなっていたら、作り直してから 1 度だけやり直す
    /// （作り直せなければ例外。モデルは形なしで加わる）。
    /// </summary>
    private static NativeMesh UploadMesh(EditorContext context, PreparedModel prepared, LoadReport report)
    {
        NativeEngine engine = context.Engine!;
        if (engine.IsDeviceLost)
        {
            context.RenderLoop.RecoverNow();
        }
        try
        {
            return ModelMeshBuilder.Upload(engine, prepared, report);
        }
        catch (NativeEngineException ex) when (ex.Result == NativeResult.DeviceLost)
        {
            AppLog.Warning("モデルを GPU に載せる途中で GPU が使えなくなったため、作り直してからやり直します", ex);
            if (!context.RenderLoop.RecoverNow())
            {
                throw;
            }
            return ModelMeshBuilder.Upload(engine, prepared, report);
        }
    }

    /// <summary>剛体とジョイントから物理を作る（剛体が無ければ null）。物理の DLL が使えなくても、モデルは読み込む。</summary>
    private static ModelPhysics? CreatePhysics(PmxDocument document, ModelRig rig, LoadReport report)
    {
        try
        {
            return ModelPhysics.Create(document, rig, report);
        }
        catch (Exception ex) when (ex is PhysicsException or DllNotFoundException or EntryPointNotFoundException)
        {
            report.Warning(LoadIssueCategory.Unsupported, $"物理演算を用意できなかったため、このモデルは物理なしで動かします（{ex.Message}）");
            return null;
        }
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
