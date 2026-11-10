using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Theater3D.App.Logging;
using Theater3D.App.Shell;
using Theater3D.Editor;
using Theater3D.Engine.Interop;

namespace Theater3D.App.Export;

/// <summary>
/// PNG の書き出しの画面（レンダリングのメニュー）。静止画か連番か、フレーム、大きさ、なめらかさ、背景、書き出し先を決めて書き出す。
/// 書き出しの間は進み具合を出し、「やめる」で止められる。設定は次に開いたときのために覚える（利用者ごと）。
/// </summary>
public partial class ExportWindow : Window
{
    private static readonly (string Name, int Width, int Height)[] SizePresets =
    [
        ("フル HD（1920 × 1080）", 1920, 1080),
        ("HD（1280 × 720）", 1280, 720),
        ("4K（3840 × 2160）", 3840, 2160),
        ("縦長（1080 × 1920）", 1080, 1920),
        ("正方形（1080 × 1080）", 1080, 1080),
    ];

    private readonly EditorContext _context;
    private readonly ExportPreferences _preferences;
    private string _stillPath;
    private string _sequencePath;
    private ExportKind _kind;
    private CancellationTokenSource? _cancel;
    private bool _initialized;

    internal ExportWindow(EditorContext context, ExportPreferences preferences, ExportKind kind, string? projectPath)
    {
        InitializeComponent();
        _context = context;
        _preferences = preferences;
        string name = projectPath is null ? "書き出し" : Path.GetFileNameWithoutExtension(projectPath);
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "3DTheater");
        _stillPath = preferences.StillPath ?? Path.Combine(folder, $"{name}.png");
        _sequencePath = preferences.SequencePath ?? Path.Combine(folder, name, $"{name}.png");

        SizePresetBox.ItemsSource = SizePresets.Select(p => p.Name).ToArray();
        WidthBox.Text = preferences.Width.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = preferences.Height.ToString(CultureInfo.InvariantCulture);
        SmoothCheck.IsChecked = preferences.Smooth;
        TransparentCheck.IsChecked = preferences.Transparent;
        BackgroundBox.Text = preferences.Background;
        _kind = kind;
        // 種類のボタンは、準備ができる前に選ぶ（選んだときの処理で、空の書き出し先を覚えてしまわないように）
        (kind == ExportKind.Still ? StillRadio : SequenceRadio).IsChecked = true;
        _initialized = true;
        ApplyKind();
        UpdateBackground();
        NoteText.Text =
            "シーンのカメラ（カメラ視点）から描きます。格子・座標軸・ボーン・ギズモ・選択の枠・確認用の立方体は映りません。" +
            $"連番は「名前_フレームの番号.png」で、作品のフレームの速さ（{context.Playback.FrameRate} fps）の 1 フレームを 1 枚にします。" +
            "物理は焼き込みの結果を使います（まだのフレームは、書き出しながら計算します）。";
    }

    private void OnKindChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }
        // 種類ごとに書き出し先を覚えておき、切り替えたら入れ替える
        if (_kind == ExportKind.Still)
        {
            _stillPath = PathBox.Text;
        }
        else
        {
            _sequencePath = PathBox.Text;
        }
        _kind = SequenceRadio.IsChecked == true ? ExportKind.Sequence : ExportKind.Still;
        ApplyKind();
    }

    private void ApplyKind()
    {
        bool sequence = _kind == ExportKind.Sequence;
        FirstFrameBox.Text = (sequence ? 0 : _context.Playback.CurrentFrame).ToString(CultureInfo.InvariantCulture);
        LastFrameBox.Text = _context.Playback.EndFrame.ToString(CultureInfo.InvariantCulture);
        LastFrameBox.Visibility = RangeSeparator.Visibility = sequence ? Visibility.Visible : Visibility.Collapsed;
        FrameNote.Text = sequence ? $"（作品の終わりは {_context.Playback.EndFrame}）" : "（今のフレーム）";
        PathBox.Text = sequence ? _sequencePath : _stillPath;
    }

    private void OnSizePresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SizePresetBox.SelectedIndex is >= 0 and var index)
        {
            WidthBox.Text = SizePresets[index].Width.ToString(CultureInfo.InvariantCulture);
            HeightBox.Text = SizePresets[index].Height.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void OnTransparentChanged(object sender, RoutedEventArgs e) => UpdateBackground();

    private void OnBackgroundChanged(object sender, TextChangedEventArgs e) => UpdateBackground();

    private void UpdateBackground()
    {
        if (!_initialized)
        {
            return;
        }
        bool transparent = TransparentCheck.IsChecked == true;
        BackgroundBox.IsEnabled = BackgroundLabel.IsEnabled = !transparent;
        BackgroundSwatch.Opacity = transparent ? 0.3 : 1;
        BackgroundSwatch.Background = ImageExport.TryParseColor(BackgroundBox.Text, out Vector3 c)
            ? new SolidColorBrush(Color.FromRgb((byte)(c.X * 255), (byte)(c.Y * 255), (byte)(c.Z * 255)))
            : Brushes.Transparent;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        bool sequence = _kind == ExportKind.Sequence;
        string current = PathBox.Text.Trim();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = sequence ? "連番の名前（後ろに _0000 などのフレームの番号を付けます）" : "静止画の書き出し先",
            Filter = "PNG 画像 (*.png)|*.png",
            DefaultExt = ".png",
            OverwritePrompt = !sequence, // 連番は書き出す前に、まとめて置き換えるかを尋ねる
            FileName = Path.GetFileName(current),
        };
        if (Path.GetDirectoryName(current) is { Length: > 0 } directory && Directory.Exists(directory))
        {
            dialog.InitialDirectory = directory;
        }
        if (dialog.ShowDialog(this) == true)
        {
            PathBox.Text = dialog.FileName;
        }
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadOptions(out ExportOptions? options, out string? problem))
        {
            MessageBox.Show(this, problem, Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        IReadOnlyList<string> errors = options.Validate(_context.Playback.EndFrame);
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", errors), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        int existing = options.Paths.Count(File.Exists);
        if (existing > 0 && options.Kind == ExportKind.Sequence
            && MessageBox.Show(this, $"書き出し先に、同じ名前のファイルが {existing:N0} 個あります。置き換えますか？", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        Remember(options);
        await RunAsync(options);
    }

    private bool TryReadOptions([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ExportOptions? options, out string? problem)
    {
        options = null;
        problem = null;
        static bool Int(string text, out int value) => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        if (!Int(WidthBox.Text, out int width) || !Int(HeightBox.Text, out int height))
        {
            problem = "幅と高さは数で入れてください。";
            return false;
        }
        if (!Int(FirstFrameBox.Text, out int first) || (_kind == ExportKind.Sequence && !Int(LastFrameBox.Text, out _)))
        {
            problem = "フレームは数で入れてください。";
            return false;
        }
        Int(LastFrameBox.Text, out int last);
        bool transparent = TransparentCheck.IsChecked == true;
        Vector3 background = Vector3.One;
        if (!transparent && !ImageExport.TryParseColor(BackgroundBox.Text, out background))
        {
            problem = "背景の色は #RRGGBB の形で入れてください（例: 白 = #FFFFFF、黒 = #000000）。";
            return false;
        }
        options = new ExportOptions
        {
            Kind = _kind,
            Width = width,
            Height = height,
            Smooth = SmoothCheck.IsChecked == true,
            Transparent = transparent,
            Background = background,
            FirstFrame = first,
            LastFrame = _kind == ExportKind.Sequence ? last : first,
            OutputPath = PathBox.Text.Trim(),
        };
        return true;
    }

    private void Remember(ExportOptions options)
    {
        _preferences.Width = options.Width;
        _preferences.Height = options.Height;
        _preferences.Smooth = options.Smooth;
        _preferences.Transparent = options.Transparent;
        _preferences.Background = ImageExport.FormatColor(options.Background);
        if (options.Kind == ExportKind.Still)
        {
            _preferences.StillPath = options.OutputPath;
        }
        else
        {
            _preferences.SequencePath = options.OutputPath;
        }
    }

    private async Task RunAsync(ExportOptions options)
    {
        _cancel = new CancellationTokenSource();
        SetRunning(true);
        int done = 0;
        var stopwatch = Stopwatch.StartNew();
        var progress = new Progress<ExportProgress>(p =>
        {
            done = p.Done;
            ProgressBar.Value = p.Done / (double)p.Total;
            ProgressText.Text = $"{p.Done:N0} / {p.Total:N0} 枚（フレーム {p.Frame}）";
        });
        string where = options.Kind == ExportKind.Still ? options.Paths[0] : Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!;
        try
        {
            int saved = await ImageExporter.RunAsync(_context, options, progress, _cancel.Token);
            AppLog.Info($"PNG を {saved:N0} 枚書き出しました（{options.Width} × {options.Height}、{stopwatch.Elapsed.TotalSeconds:F1} 秒）: {where}");
            if (MessageBox.Show(this, $"PNG を {saved:N0} 枚書き出しました。\n{where}\n\n書き出し先のフォルダを開きますか？", Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                OpenFolder(options.Paths[0]);
            }
        }
        catch (OperationCanceledException)
        {
            AppLog.Info($"PNG の書き出しをやめました（{done:N0} 枚目まで）: {where}");
            MessageBox.Show(this, $"書き出しをやめました（{done:N0} 枚目まで書き出してあります）。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NativeEngineException or InvalidOperationException
            or ArgumentException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Error($"PNG の書き出しに失敗しました（{done:N0} 枚目まで）: {where}", ex);
            MessageBox.Show(this, $"書き出しに失敗しました（{done:N0} 枚目まで書き出してあります）。\n\n{ex.Message}", Title,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cancel.Dispose();
            _cancel = null;
            SetRunning(false);
        }
    }

    private void SetRunning(bool running)
    {
        SettingsPanel.IsEnabled = !running;
        ExportButton.IsEnabled = !running;
        CloseButton.Content = running ? "やめる" : "閉じる";
        ProgressPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (running)
        {
            ProgressBar.Value = 0;
            ProgressText.Text = "準備しています…";
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (_cancel is not null)
        {
            _cancel.Cancel(); // 書き出し中は「やめる」
            return;
        }
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_cancel is not null)
        {
            _cancel.Cancel(); // 書き出しが止まってから閉じる
            e.Cancel = true;
            return;
        }
        if (!_initialized)
        {
            base.OnClosing(e);
            return;
        }
        // 書き出さずに閉じても、入れた書き出し先は覚えておく
        if (_kind == ExportKind.Still)
        {
            _preferences.StillPath = PathBox.Text.Trim() is { Length: > 0 } still ? still : _preferences.StillPath;
        }
        else
        {
            _preferences.SequencePath = PathBox.Text.Trim() is { Length: > 0 } sequence ? sequence : _preferences.SequencePath;
        }
        base.OnClosing(e);
    }

    private static void OpenFolder(string file)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            AppLog.Warning("書き出し先のフォルダを開けませんでした", ex);
        }
    }
}
