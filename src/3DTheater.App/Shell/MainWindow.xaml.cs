using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock.Themes;
using Theater3D.App.Import;
using Theater3D.App.Logging;
using Theater3D.App.Panels;
using Theater3D.App.Preview;
using Theater3D.App.Rendering;
using Theater3D.Editor;
using Theater3D.Engine.Interop;

namespace Theater3D.App.Shell;

public partial class MainWindow : Window
{
    private readonly EditorContext _context;
    private readonly DockingLayout _layout;
    private readonly UserSettings _settings;
    private readonly DispatcherTimer _statusTimer;
    private PreviewPanel? _activePreview;
    private ViewportControl? _activeView;
    private string? _renderFailure;
    private bool _videoMemoryUnavailable;
    private bool _busy;

    public MainWindow(EditorContext context, string? engineError)
    {
        InitializeComponent();
        _context = context;
        _settings = UserSettingsStore.Load();
        _renderFailure = engineError;

        Icon = AppIcon.CreateBitmap(64);
        TitleIcon.Source = AppIcon.CreateDrawing();
        TitleBarHelper.Attach(this, MaximizeButton, RootBorder);
        ApplyWindowPlacement(_settings.Window);

        Dock.Theme = new Vs2013DarkTheme();
        _layout = new DockingLayout(Dock, context);
        _layout.PreviewCreated += (_, preview) => preview.ViewportActivated += (_, view) => Activate(preview, view);
        _layout.PreviewRemoved += (_, preview) => OnPreviewRemoved(preview);
        Dock.ActiveContentChanged += (_, _) =>
        {
            if (Dock.ActiveContent is PreviewPanel preview && preview != _activePreview)
            {
                Activate(preview, preview.VisibleViews.FirstOrDefault() ?? preview.FirstView);
            }
        };
        _layout.LoadOrDefault(_settings);

        BuildPanelsMenu();
        BuildViewKindsMenu();
        context.RenderLoop.RenderFailed += OnRenderFailed;
        context.Playback.StateChanged += (_, _) => UpdatePlayButton();
        context.Playback.FrameChanged += (_, _) => UpdateFrameText();

        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => UpdateStatus(), Dispatcher);
        _statusTimer.Start();
        Loaded += (_, _) =>
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            AppLog.Info($"メインウィンドウを表示しました（拡大率 {dpi.DpiScaleX * 100:F0}%、プレビュー {_layout.Previews.Count} 枚）");
            UpdateStatus();
            UpdateFrameText();
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
        {
            return;
        }

        _statusTimer.Stop();
        _context.Playback.Pause();
        _context.RenderLoop.Stop();

        _settings.Window = new WindowPlacement(RestoreBounds.Left, RestoreBounds.Top, RestoreBounds.Width, RestoreBounds.Height,
            WindowState == WindowState.Maximized);
        _layout.Save(_settings);
        UserSettingsStore.Save(_settings);
        AppLog.Info("レイアウトと画面の設定を保存しました");

        // 親ウィンドウが消える前に、描画先（GPU の資源）を片付ける
        foreach (PreviewPanel preview in _layout.Previews.ToList())
        {
            preview.Dispose();
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || IsTyping())
        {
            return;
        }
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys modifiers = Keyboard.Modifiers;
        bool inSceneTree = IsFocusWithin<ScenePanel>();

        // 視点のショートカットは、最後に操作したプレビューの区画が受け取る
        if (_activeView is not null && (IsFocusWithin<ViewportControl>() || key is >= Key.NumPad0 and <= Key.NumPad9 or Key.Decimal))
        {
            if (_activeView.HandleKey(key, modifiers))
            {
                e.Handled = true;
                return;
            }
        }

        switch (key)
        {
            case Key.Space:
                _context.Playback.TogglePlay();
                e.Handled = true;
                break;
            case Key.Left when !inSceneTree:
                _context.Playback.Pause();
                _context.Playback.Step(-1);
                e.Handled = true;
                break;
            case Key.Right when !inSceneTree:
                _context.Playback.Pause();
                _context.Playback.Step(1);
                e.Handled = true;
                break;
            case Key.Home:
                _context.Playback.GoToStart();
                e.Handled = true;
                break;
            case Key.End:
                _context.Playback.GoToEnd();
                e.Handled = true;
                break;
            case Key.A when modifiers == ModifierKeys.Control:
                SelectAll();
                e.Handled = true;
                break;
            case Key.I when modifiers == ModifierKeys.Control:
                OnImportModelClick(this, e);
                e.Handled = true;
                break;
            case Key.Escape:
                _context.Selection.Clear(this);
                e.Handled = true;
                break;
        }
    }

    private static bool IsTyping() =>
        Keyboard.FocusedElement is TextBoxBase or PasswordBox || (Keyboard.FocusedElement is ComboBox { IsEditable: true });

    private static bool IsFocusWithin<T>() where T : DependencyObject
    {
        for (DependencyObject? element = Keyboard.FocusedElement as DependencyObject; element is not null;
             element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is T)
            {
                return true;
            }
        }
        return false;
    }

    private void Activate(PreviewPanel preview, ViewportControl view)
    {
        _activePreview = preview;
        if (_activeView is not null && _activeView != view)
        {
            _activeView.IsActiveView = false;
        }
        _activeView = view;
        view.IsActiveView = true;
    }

    private PreviewPanel? ActivePreview => _activePreview ?? _layout.Previews.FirstOrDefault();

    private ViewportControl? ActiveView => _activeView ?? ActivePreview?.FirstView;

    private void OnPreviewRemoved(PreviewPanel preview)
    {
        if (_activePreview == preview)
        {
            _activePreview = null;
            _activeView = null;
        }
    }

    private void ApplyWindowPlacement(WindowPlacement? placement)
    {
        if (placement is null || placement.Width < MinWidth || placement.Height < MinHeight)
        {
            return;
        }
        // 前回のモニターが外れていても画面の外に出ないよう、今の画面に収まるときだけ戻す
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var bounds = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
        if (!screen.IntersectsWith(bounds))
        {
            return;
        }
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        if (placement.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void BuildPanelsMenu()
    {
        foreach (var (id, title) in DockingLayout.PanelIds)
        {
            var item = new MenuItem { Header = title, IsCheckable = true, Tag = id };
            item.Click += (_, _) => _layout.TogglePanel(id);
            PanelsMenu.Items.Add(item);
        }
    }

    private void BuildViewKindsMenu()
    {
        foreach (ViewKind kind in ViewKinds.All)
        {
            var item = new MenuItem { Header = kind.DisplayName(), InputGestureText = kind.ShortcutText() ?? string.Empty, Tag = kind.ToString() };
            item.Click += OnViewKindClick;
            ViewKindsMenu.Items.Add(item);
        }
    }

    private void OnViewMenuOpened(object sender, RoutedEventArgs e)
    {
        foreach (MenuItem item in PanelsMenu.Items.OfType<MenuItem>())
        {
            item.IsChecked = _layout.IsPanelShown((string)item.Tag);
        }
        foreach (MenuItem item in ViewKindsMenu.Items.OfType<MenuItem>())
        {
            item.IsChecked = ActiveView?.Camera.Kind.ToString() == (string)item.Tag;
        }
    }

    private void SelectAll() =>
        _context.Selection.SelectMany(_context.Scene.AllNodes.Where(n => n.HasTransform), this);

    private void UpdatePlayButton()
    {
        PlayButton.Content = _context.Playback.IsPlaying ? "" : "";
    }

    private void UpdateFrameText() =>
        FrameText.Text = $"フレーム {_context.Playback.CurrentFrame} / {_context.Playback.EndFrame}（{_context.Playback.FrameRate} fps）";

    private void UpdateStatus()
    {
        UpdateFrameText();
        NativeEngine? engine = _context.Engine;
        if (engine is null || engine.IsDisposed)
        {
            SetStatus(_renderFailure ?? "描画エンジンなしで起動しています", isError: true);
            RendererText.Text = string.Empty;
            return;
        }

        if (!_busy)
        {
            SetStatus(_renderFailure ?? "準備完了", isError: _renderFailure is not null);
        }
        EngineInfo info = engine.Info;
        string presenter = _context.SurfaceSharingUnavailable ? "読み出し方式" : "共有方式";
        RendererText.Text = $"描画: Direct3D 11（{presenter}）・{(info.IsSoftware ? "ソフトウェア" : info.AdapterName)}";
        RenderLoop loop = _context.RenderLoop;
        FrameRateText.Text = loop.IsRunning
            ? $"描画 {loop.FramesPerSecond:F0} 回/秒・{loop.AverageFrameMilliseconds:F1} ms"
            : "描画 待機中";
        VideoMemoryText.Text = FormatVideoMemory(engine);
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText.Text = text;
        StatusDot.Fill = isError ? Brushes.OrangeRed : new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    }

    private string FormatVideoMemory(NativeEngine engine)
    {
        if (_videoMemoryUnavailable)
        {
            return string.Empty;
        }
        try
        {
            VideoMemoryInfo memory = engine.QueryVideoMemory();
            const double gib = 1024.0 * 1024 * 1024;
            return $"VRAM（このアプリ）{memory.Usage / gib:F2} / {memory.Budget / gib:F1} GB";
        }
        catch (NativeEngineException ex)
        {
            AppLog.Warning("GPU メモリの使用量を取得できないため、表示をやめます", ex);
            _videoMemoryUnavailable = true;
            return string.Empty;
        }
    }

    private void OnRenderFailed(object? sender, RenderFailedEventArgs e)
    {
        bool deviceLost = e.Exception is NativeEngineException { Result: NativeResult.DeviceLost };
        // GPU の取り外しなどからの自動復旧はまだ無い。止めて知らせるだけ
        _renderFailure = deviceLost
            ? "GPU が使えなくなったため描画を止めました。アプリを再起動してください"
            : $"描画に失敗したため一部の区画を止めました: {e.Exception.Message}";
        UpdateStatus();
    }

    /// <summary>起動時の引数で渡されたモデルを、画面が出てから読み込む（「プログラムから開く」など）。</summary>
    public void ImportOnStartup(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () => await ImportModelsAsync(files));
    }

    private async void OnImportModelClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "モデルを読み込む",
            Filter = ModelImporter.FileFilter,
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            await ImportModelsAsync(dialog.FileNames);
        }
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        bool accepted = e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Any(ModelImporter.IsModelFile);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            e.Handled = true;
            await ImportModelsAsync(files.Where(ModelImporter.IsModelFile));
        }
    }

    private async Task ImportModelsAsync(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            SetStatus($"読み込み中: {System.IO.Path.GetFileName(path)}", isError: false);
            _busy = true;
            try
            {
                await ModelImporter.ImportAsync(_context, path, this);
            }
            finally
            {
                _busy = false;
                UpdateStatus();
            }
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => SelectAll();

    private void OnClearSelectionClick(object sender, RoutedEventArgs e) => _context.Selection.Clear(this);

    private void OnAddPreviewClick(object sender, RoutedEventArgs e)
    {
        PreviewPanel preview = _layout.AddPreview();
        Activate(preview, preview.FirstView);
    }

    private void OnPreviewLayoutClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out PreviewLayout layout))
        {
            ActivePreview?.SetLayout(layout);
        }
    }

    private void OnViewKindClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out ViewKind kind))
        {
            ActiveView?.Camera.SetKind(kind);
        }
    }

    private void OnToggleProjectionClick(object sender, RoutedEventArgs e) => ActiveView?.Camera.ToggleProjection();

    private void OnFocusSelectionClick(object sender, RoutedEventArgs e) => ActiveView?.FocusSelection();

    private void OnResetViewClick(object sender, RoutedEventArgs e) => ActiveView?.Camera.Reset();

    private void OnPlayClick(object sender, RoutedEventArgs e) => _context.Playback.TogglePlay();

    private void OnResetLayoutClick(object sender, RoutedEventArgs e)
    {
        _activePreview = null;
        _activeView = null;
        _layout.ResetToDefault();
        AppLog.Info("レイアウトを初期配置に戻しました");
    }

    private void OnShortcutsClick(object sender, RoutedEventArgs e) => MessageBox.Show(
        this,
        "プレビュー\n" +
        "  中ボタンでドラッグ … 視点を回す\n" +
        "  Shift + 中ボタンでドラッグ … 視点を平行に動かす\n" +
        "  ホイール … 拡大・縮小\n" +
        "  左クリック … 選ぶ（Ctrl で追加・解除）\n" +
        "  ダブルクリック / Num . … 選んだ物に合わせる\n" +
        "  右クリック … 視点のメニュー\n" +
        "  Num1 / Ctrl+Num1 … 正面 / 背面\n" +
        "  Num3 / Ctrl+Num3 … 右側面 / 左側面（Blender と同じ向き）\n" +
        "  Num7 / Ctrl+Num7 … 上面 / 下面\n" +
        "  Num0 … カメラ視点　Num5 … 透視・平行の切り替え\n\n" +
        "再生\n" +
        "  Space … 再生・一時停止　← / → … 1 フレーム移動　Home / End … 先頭 / 末尾\n\n" +
        "選択\n" +
        "  Ctrl+A … すべて選択　Esc … 選択を解除",
        "操作方法",
        MessageBoxButton.OK,
        MessageBoxImage.Information);

    private void OnOpenLogFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = AppLog.LogDirectory, UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            AppLog.Warning("ログフォルダーを開けませんでした", ex);
        }
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var (major, minor) = _context.Engine is null ? (0, 0) : NativeEngine.ApiVersion;
        string gpu = _context.Engine?.Info.AdapterName ?? "（なし）";
        MessageBox.Show(
            this,
            $"3D Theater {App.VersionText}（Phase 1 開発版）\n描画エンジン API {major}.{minor}\nGPU: {gpu}\n\n" +
            $"ログ: {AppLog.LogDirectory}\n画面の設定: {UserSettingsStore.Directory}\n\n" +
            "ドッキング画面: AvalonDock（Ms-PL）",
            "バージョン情報",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
