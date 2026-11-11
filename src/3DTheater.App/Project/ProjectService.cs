using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Theater3D.App.Import;
using Theater3D.App.Logging;
using Theater3D.App.Shell;
using Theater3D.Domain;
using Theater3D.Editor;
using Theater3D.Formats;

namespace Theater3D.App.Project;

/// <summary>
/// プロジェクト（.3dtproj）の新規・開く・保存と、変更の有無・自動保存・復旧を受け持つ（指示書 8 章）。
/// 保存は一時ファイルに書いてから置き換える。保存していない変更があるときは、閉じる・新規・開く の前に確かめる。
/// 自動保存は、変更があれば数分ごとに作業用のフォルダーへ書き、きちんと閉じたら消す。
/// 起動したときに、ほかの（終わっている）アプリの自動保存が残っていれば、開くかどうかを尋ねる。
/// </summary>
internal sealed class ProjectService
{
    public const string FileFilter = "3D Theater のプロジェクト (*.3dtproj)|*.3dtproj|すべてのファイル (*.*)|*.*";
    private static readonly TimeSpan AutosaveInterval = TimeSpan.FromMinutes(2);

    private readonly EditorContext _context;
    private readonly Window _owner;
    private readonly DispatcherTimer _autosaveTimer;
    private int _suppressDirty;
    private (int FrameRate, int EndFrame, bool Loop, bool Physics, bool PhysicsFloor, bool SelfShadow, bool GroundShadow, VisualEffects Effects,
        IllustrationStyle Illustration, FloorReflection Reflection, SceneFog Fog, ShadowStyle Shadows, SceneEnvironment Environment) _playbackSettings;

    public ProjectService(EditorContext context, Window owner)
    {
        _context = context;
        _owner = owner;
        _playbackSettings = PlaybackSettings();
        context.Undo.Changed += (_, _) => MarkDirty();
        context.Scene.NodeChanged += (_, e) =>
        {
            if (e.Node == context.Scene.ModelsGroup && e.PropertyName == nameof(SceneNode.Children))
            {
                MarkDirty(); // モデルの読み込み（元に戻せない操作）
            }
        };
        context.Physics.Changed += (_, _) => OnPlaybackSettingsChanged();
        context.Rendering.Changed += (_, _) => OnPlaybackSettingsChanged();
        context.Playback.StateChanged += (_, _) => OnPlaybackSettingsChanged();
        _autosaveTimer = new DispatcherTimer(AutosaveInterval, DispatcherPriority.Background, (_, _) => Autosave(), owner.Dispatcher);
        _autosaveTimer.Start();
    }

    /// <summary>題名や変更の有無が変わった。</summary>
    public event EventHandler? StateChanged;

    /// <summary>プロジェクトを開き終えた（区画をモデルに合わせるため）。</summary>
    public event EventHandler? Opened;

    public string? CurrentPath { get; private set; }

    public bool IsDirty { get; private set; }

    /// <summary>プロジェクトを開いている途中。</summary>
    public bool IsBusy { get; private set; }

    public string DisplayName => CurrentPath is null ? "無題" : Path.GetFileNameWithoutExtension(CurrentPath);

    public static string AutosaveDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "3DTheater", "AutoSave");

    private static string OwnAutosaveFile => Path.Combine(AutosaveDirectory, $"autosave_{Environment.ProcessId}{ProjectSerializer.Extension}");

    public static bool IsProjectFile(string path) => Path.GetExtension(path).Equals(ProjectSerializer.Extension, StringComparison.OrdinalIgnoreCase);

    public void MarkDirty()
    {
        if (_suppressDirty > 0 || IsDirty)
        {
            return;
        }
        IsDirty = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool New()
    {
        if (IsBusy || !ConfirmDiscard("新しいプロジェクトにする"))
        {
            return false;
        }
        _suppressDirty++;
        try
        {
            _context.ResetProject();
        }
        finally
        {
            _suppressDirty--;
        }
        SetClean(null);
        DeleteOwnAutosave();
        AppLog.Info("新しいプロジェクトにしました");
        return true;
    }

    public async Task<bool> OpenWithDialogAsync()
    {
        if (IsBusy)
        {
            return false;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "プロジェクトを開く", Filter = FileFilter };
        return dialog.ShowDialog(_owner) == true && await OpenAsync(dialog.FileName);
    }

    /// <summary>
    /// プロジェクトを開く。モデルのファイルが見つからないときは、そのモデルの設定を残したまま（保存し直しても消えない）知らせる。
    /// <paramref name="recoveredFrom"/> は自動保存から復旧するときの、その自動保存のファイル。
    /// </summary>
    public async Task<bool> OpenAsync(string path, string? recoveredFrom = null)
    {
        if (IsBusy || !ConfirmDiscard("プロジェクトを開く"))
        {
            return false;
        }
        var report = new LoadReport();
        string fullPath = Path.GetFullPath(path);
        AppLog.Info($"プロジェクトを開きます: {fullPath}");
        string json;
        try
        {
            json = await File.ReadAllTextAsync(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            report.Error(LoadIssueCategory.Format, $"ファイルを開けませんでした: {ex.Message}");
            ShowReport(fullPath, report);
            return false;
        }
        ProjectFile? project = ProjectSerializer.Deserialize(json, report);
        if (project is null)
        {
            ShowReport(fullPath, report);
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        IsBusy = true;
        _suppressDirty++;
        try
        {
            _context.ResetProject();
            ProjectApply.ApplyScene(_context.Scene, _context.Playback, _context.Animation, project, report);
            _context.Physics.Enabled = project.Physics;
            _context.Physics.FloorEnabled = project.PhysicsFloor;
            // 自動保存のファイルは別の場所にあるので、モデル・環境の画像の場所は元のプロジェクトのフォルダーから探す
            string? baseDirectory = Path.GetDirectoryName(recoveredFrom is not null && project.AutosaveOf is { } original ? original : fullPath);
            using (_context.Animation.SuspendTracking())
            {
                // 開いた値を、映像効果の手の変更とみなさない
                (project.Rendering ?? new RenderingEntry()).ApplyTo(_context.Rendering, baseDirectory);
            }
            foreach (ModelEntry entry in project.Models)
            {
                await OpenModelAsync(entry, baseDirectory, report);
            }
            if (project.Audio is { } audio)
            {
                await OpenAudioAsync(audio, baseDirectory, report);
            }
            _context.Animation.ApplyFrame();
            _context.Playback.Seek(project.Playback.CurrentFrame);
            _context.Animation.NotifyStateChanged();
            _context.Undo.Clear();
            _context.Physics.Reset(); // 開いたフレームの姿勢から物理を始める
            _playbackSettings = PlaybackSettings();
        }
        finally
        {
            _suppressDirty--;
            IsBusy = false;
        }

        if (recoveredFrom is null)
        {
            SetClean(fullPath);
            DeleteOwnAutosave();
        }
        else
        {
            // 復旧した作業はまだ保存していないので、変更ありのままにする（保存先は元のプロジェクト）
            CurrentPath = project.AutosaveOf;
            IsDirty = true;
            StateChanged?.Invoke(this, EventArgs.Empty);
            TryDelete(recoveredFrom);
        }
        AppLog.Info($"プロジェクトを開きました（モデル {project.Models.Count - _context.MissingModels.Count}/{project.Models.Count} 体、" +
            $"{stopwatch.Elapsed.TotalMilliseconds:F0} ms、注意 {report.WarningCount} 件）");
        Opened?.Invoke(this, EventArgs.Empty);
        if (report.Issues.Any(i => i.Severity >= LoadSeverity.Warning))
        {
            ShowReport(fullPath, report);
        }
        else
        {
            LogIssues(report);
        }
        return true;
    }

    /// <summary>保存する（まだ名前が無ければ、名前を付けて保存する）。保存できたら true。</summary>
    public bool Save() => CurrentPath is null ? SaveAs() : SaveTo(CurrentPath);

    public bool SaveAs()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "名前を付けて保存",
            Filter = FileFilter,
            DefaultExt = ProjectSerializer.Extension,
            AddExtension = true,
            FileName = CurrentPath is null ? "無題" : Path.GetFileName(CurrentPath),
            InitialDirectory = CurrentPath is null ? null : Path.GetDirectoryName(CurrentPath),
        };
        return dialog.ShowDialog(_owner) == true && SaveTo(dialog.FileName);
    }

    /// <summary>閉じてよいか（保存していない変更があれば尋ねる）。閉じるときは自動保存も消す。</summary>
    public bool ConfirmClose()
    {
        if (!ConfirmDiscard("終了する"))
        {
            return false;
        }
        _autosaveTimer.Stop();
        DeleteOwnAutosave();
        return true;
    }

    /// <summary>起動したときに、前に保存せずに終わったときの自動保存が残っていれば、開くか尋ねる。</summary>
    public async Task CheckRecoveryAsync()
    {
        string? file = FindOrphanAutosave();
        if (file is null)
        {
            return;
        }
        DateTime time = File.GetLastWriteTime(file);
        string name = "無題";
        try
        {
            ProjectFile? peek = ProjectSerializer.Deserialize(await File.ReadAllTextAsync(file), new LoadReport());
            name = peek?.AutosaveOf is { } original ? Path.GetFileNameWithoutExtension(original) : "無題";
        }
        catch (IOException)
        {
        }
        MessageBoxResult answer = MessageBox.Show(_owner,
            $"前回、保存しないまま終わったときの作業が残っています（{name}、{time:yyyy/MM/dd HH:mm} の自動保存）。\n\n" +
            "開きますか？\n\n［はい］開く　［いいえ］捨てる　［キャンセル］今は開かない（次に起動したときにまた尋ねます）",
            "作業の復旧", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            AppLog.Info($"自動保存から作業を復旧します: {file}");
            await OpenAsync(file, recoveredFrom: file);
        }
        else if (answer == MessageBoxResult.No)
        {
            AppLog.Info($"自動保存を捨てました: {file}");
            TryDelete(file);
        }
    }

    private async Task OpenModelAsync(ModelEntry entry, string? baseDirectory, LoadReport report)
    {
        string label = string.IsNullOrWhiteSpace(entry.Name) ? Path.GetFileName(entry.File) : entry.Name;
        string? modelPath = ProjectApply.ResolveModelPath(entry, baseDirectory);
        if (modelPath is null)
        {
            _context.MissingModels.Add(entry);
            report.Warning(LoadIssueCategory.MissingFile,
                $"モデル「{label}」のファイルが見つかりません（{entry.File}）。このモデルの設定とキーは、保存し直しても消えずに残ります。ファイルを元の場所に戻してから開き直してください");
            return;
        }
        ModelNode? node = await ModelImporter.ImportAsync(_context, modelPath, _owner, interactive: false);
        if (node is null)
        {
            _context.MissingModels.Add(entry);
            report.Warning(LoadIssueCategory.Format, $"モデル「{label}」を読み込めませんでした（{modelPath}）。このモデルの設定とキーは、保存し直しても消えずに残ります");
            return;
        }
        ProjectApply.ApplyModel(node, entry, _context.Animation, report);
        if (_context.GetLoaded(node) is { } loaded && loaded.Report.WarningCount > 0)
        {
            report.Info(LoadIssueCategory.Note, $"モデル「{node.Name}」の読み込みレポートに注意が {loaded.Report.WarningCount} 件あります（プロパティ欄の「読み込みレポートを見る」で確かめられます）");
        }
    }

    private bool SaveTo(string path)
    {
        string fullPath = Path.GetFullPath(path);
        try
        {
            ProjectFile project = ProjectCapture.Capture(_context.Scene, _context.Playback, _context.Animation,
                Path.GetDirectoryName(fullPath), _context.MissingModels, _context.MissingAudio);
            project.Physics = _context.Physics.Enabled;
            project.PhysicsFloor = _context.Physics.FloorEnabled;
            project.Rendering = RenderingEntry.From(_context.Rendering, Path.GetDirectoryName(fullPath));
            SafeFile.WriteAllText(fullPath, ProjectSerializer.Serialize(project));
            SetClean(fullPath);
            DeleteOwnAutosave();
            AppLog.Info($"プロジェクトを保存しました: {fullPath}（モデル {project.Models.Count} 体）");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            AppLog.Error($"プロジェクトを保存できませんでした: {fullPath}", ex);
            MessageBox.Show(_owner, $"プロジェクトを保存できませんでした。\n\n{ex.Message}", "保存", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // 保存していない変更があれば、保存するか尋ねる。続けてよければ true
    private bool ConfirmDiscard(string action)
    {
        if (!IsDirty)
        {
            return true;
        }
        MessageBoxResult answer = MessageBox.Show(_owner,
            $"「{DisplayName}」への変更を保存しますか？\n\n［はい］保存してから{action}　［いいえ］保存せずに{action}　［キャンセル］やめる",
            "3D Theater", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private void Autosave()
    {
        if (!IsDirty || IsBusy)
        {
            return;
        }
        try
        {
            string? directory = CurrentPath is null ? null : Path.GetDirectoryName(CurrentPath);
            ProjectFile project = ProjectCapture.Capture(_context.Scene, _context.Playback, _context.Animation, directory, _context.MissingModels, _context.MissingAudio);
            project.Physics = _context.Physics.Enabled;
            project.PhysicsFloor = _context.Physics.FloorEnabled;
            project.Rendering = RenderingEntry.From(_context.Rendering, directory);
            project.AutosaveOf = CurrentPath;
            SafeFile.WriteAllText(OwnAutosaveFile, ProjectSerializer.Serialize(project));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("自動保存できませんでした", ex);
        }
    }

    private static string? FindOrphanAutosave()
    {
        if (!Directory.Exists(AutosaveDirectory))
        {
            return null;
        }
        return Directory.EnumerateFiles(AutosaveDirectory, $"autosave_*{ProjectSerializer.Extension}")
            .Where(f => !IsRunning(f))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        static bool IsRunning(string file)
        {
            string id = Path.GetFileNameWithoutExtension(file)["autosave_".Length..];
            if (!int.TryParse(id, out int pid))
            {
                return false;
            }
            if (pid == Environment.ProcessId)
            {
                return true;
            }
            try
            {
                using var process = Process.GetProcessById(pid);
                return process.ProcessName.Equals(Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false; // そのアプリはもう動いていない
            }
        }
    }

    private void DeleteOwnAutosave() => TryDelete(OwnAutosaveFile);

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"自動保存のファイルを消せませんでした: {file}", ex);
        }
    }

    private void SetClean(string? path)
    {
        CurrentPath = path;
        IsDirty = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 保存した音声を開く。見つからない・読めないときは知らせ、保存し直しても消さないよう覚えておく。
    /// </summary>
    private async Task OpenAudioAsync(AudioEntry entry, string? baseDirectory, LoadReport report)
    {
        string? path = ProjectApply.ResolveAudioPath(entry, baseDirectory);
        if (path is null)
        {
            report.Warning(LoadIssueCategory.MissingFile, $"音声のファイルが見つかりません: {entry.AbsoluteFile ?? entry.File}（保存し直しても、この音声の設定は残します）");
            _context.MissingAudio = entry;
            return;
        }
        Media.AudioClip clip;
        try
        {
            clip = await Task.Run(() => Media.AudioClip.Open(path));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            report.Warning(LoadIssueCategory.Format, $"音声を読み込めませんでした: {path}（{ex.Message}）");
            _context.MissingAudio = entry;
            return;
        }
        var node = new AudioNode(string.IsNullOrWhiteSpace(entry.Name) ? Path.GetFileNameWithoutExtension(path) : entry.Name, path, clip.Duration)
        {
            StartFrame = entry.StartFrame,
            Volume = entry.Volume,
            IsVisible = entry.Visible,
            IsLocked = entry.Locked,
        };
        _context.Scene.Add(_context.Scene.AudioGroup, node);
    }

    // 再生の設定（フレームの速さ・終わりのフレーム・ループ）、物理の ON/OFF、描き方（影・映像効果・イラスト風）が変わったら、変更ありにする
    private void OnPlaybackSettingsChanged()
    {
        var now = PlaybackSettings();
        if (now != _playbackSettings)
        {
            _playbackSettings = now;
            if (!_context.Animation.IsApplying)
            {
                MarkDirty(); // 映像効果のキーを当てはめただけ（再生・フレームの移動）なら変更にしない
            }
        }
    }

    private (int, int, bool, bool, bool, bool, bool, VisualEffects, IllustrationStyle, FloorReflection, SceneFog, ShadowStyle, SceneEnvironment) PlaybackSettings() =>
        (_context.Playback.FrameRate, _context.Playback.EndFrame, _context.Playback.Loop, _context.Physics.Enabled, _context.Physics.FloorEnabled,
            _context.Rendering.SelfShadow, _context.Rendering.GroundShadow, _context.Rendering.Effects, _context.Rendering.Illustration,
            _context.Rendering.Reflection, _context.Rendering.Fog, _context.Rendering.ShadowStyle, _context.Rendering.Environment);

    private void ShowReport(string path, LoadReport report)
    {
        LogIssues(report);
        LoadReportWindow.Show(_owner, path, report, null, "プロジェクト");
    }

    private static void LogIssues(LoadReport report)
    {
        foreach (LoadIssue issue in report.Issues)
        {
            string line = $"[プロジェクト] {issue.CategoryText}: {issue.Message}";
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
    }
}
