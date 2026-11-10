using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
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
    private DateTime _recoveredAt = DateTime.MinValue;
    private bool _videoMemoryUnavailable;
    private bool _busy;
    private readonly Project.ProjectService _project;

    public MainWindow(EditorContext context, string? engineError)
    {
        InitializeComponent();
        _context = context;
        _project = new Project.ProjectService(context, this);
        _project.StateChanged += (_, _) => UpdateTitle();
        _project.Opened += (_, _) => FocusViewsOnFirstModel();
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
        context.RenderLoop.DeviceStateChanged += OnDeviceStateChanged;
        context.Playback.StateChanged += (_, _) => UpdatePlayButton();
        context.Playback.FrameChanged += (_, _) => UpdateFrameText();
        context.Undo.Changed += (_, _) => UpdateUndoButtons();
        UpdateUndoButtons();
        context.PreviewQuality.Quality = _settings.Rendering.PreviewQuality;
        context.Gizmo.Style = _settings.Gizmo.Style;
        context.Gizmo.Tool = _settings.Gizmo.Tool;
        context.Gizmo.LocalSpace = _settings.Gizmo.LocalSpace;
        context.Gizmo.Changed += (_, _) => UpdateGizmoControls();
        UpdateGizmoControls();
        context.Physics.Changed += (_, _) => UpdatePhysicsControls();
        context.Rendering.Changed += (_, _) => UpdateRenderingControls();
        UpdatePhysicsControls();
        UpdateRenderingControls();

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
        // 保存していない変更があれば、保存するか尋ねる（黙って捨てない）
        if (!_project.ConfirmClose())
        {
            e.Cancel = true;
            return;
        }

        _statusTimer.Stop();
        _context.Playback.Pause();
        _context.RenderLoop.Stop();

        _settings.Window = new WindowPlacement(RestoreBounds.Left, RestoreBounds.Top, RestoreBounds.Width, RestoreBounds.Height,
            WindowState == WindowState.Maximized);
        _layout.Save(_settings);
        _settings.Gizmo = new GizmoPreferences { Style = _context.Gizmo.Style, Tool = _context.Gizmo.Tool, LocalSpace = _context.Gizmo.LocalSpace };
        _settings.Rendering = new RenderingPreferences { PreviewQuality = _context.PreviewQuality.Quality };
        UserSettingsStore.Save(_settings);
        AppLog.Info("レイアウトと画面の設定を保存しました");

        // 親ウィンドウが消える前に、描画先とモデル（GPU の資源）を片付ける。エンジンは App の終了時に破棄する
        foreach (PreviewPanel preview in _layout.Previews.ToList())
        {
            preview.Dispose();
        }
        _context.Undo.Clear(); // 外したままのモデル（履歴の中にある）の GPU の資源も、エンジンより先に手放す
        _context.DisposeModels();
        _context.Audio.Dispose();
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
            case Key.Z when modifiers == ModifierKeys.Control:
            case Key.Y when modifiers == ModifierKeys.Control:
            case Key.Z when modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                if (key == Key.Z && modifiers == ModifierKeys.Control)
                {
                    UndoOnce();
                }
                else
                {
                    RedoOnce();
                }
                e.Handled = true;
                break;
            case Key.Delete when KeyCommands.HasSelection(_context):
                KeyCommands.Delete(_context);
                e.Handled = true;
                break;
            case Key.Delete when _context.Selection.Items.OfType<Domain.ModelNode>().Any():
                RemoveSelectedModels();
                e.Handled = true;
                break;
            case Key.C when modifiers == ModifierKeys.Control && KeyCommands.HasSelection(_context):
                KeyCommands.Copy(_context);
                e.Handled = true;
                break;
            case Key.V when modifiers == ModifierKeys.Control && KeyCommands.CanPaste(_context):
                KeyCommands.Paste(_context, _context.Playback.CurrentFrame);
                e.Handled = true;
                break;
            case Key.A when modifiers == ModifierKeys.Control:
                SelectAll();
                e.Handled = true;
                break;
            case Key.N when modifiers == ModifierKeys.Control:
                _project.New();
                e.Handled = true;
                break;
            case Key.O when modifiers == ModifierKeys.Control:
                _ = _project.OpenWithDialogAsync();
                e.Handled = true;
                break;
            case Key.S when modifiers == ModifierKeys.Control:
                _project.Save();
                e.Handled = true;
                break;
            case Key.S when modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                _project.SaveAs();
                e.Handled = true;
                break;
            case Key.I when modifiers == ModifierKeys.Control:
                OnImportModelClick(this, e);
                e.Handled = true;
                break;
            case Key.Q when modifiers == ModifierKeys.None:
            case Key.W when modifiers == ModifierKeys.None:
            case Key.E when modifiers == ModifierKeys.None:
                // 道具の切り替え（Blender 方式で使う。MMD 方式なら Blender 方式に切り替える）
                _context.Gizmo.Style = GizmoStyle.Blender;
                _context.Gizmo.Tool = key switch { Key.Q => GizmoTool.Select, Key.W => GizmoTool.Move, _ => GizmoTool.Rotate };
                e.Handled = true;
                break;
            case Key.I when modifiers == ModifierKeys.None:
                RegisterKeys();
                e.Handled = true;
                break;
            case Key.Escape:
                _context.Selection.Clear(this);
                _context.KeySelection.Clear();
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
        PhysicsText.Text = _context.Physics.Enabled && _context.Physics.BakeProgress is { } bake && bake.Done < bake.Total
            ? $"物理を計算中 {bake.Done} / {bake.Total}"
            : string.Empty;
        NativeEngine? engine = _context.Engine;
        if (engine is null || engine.IsDisposed)
        {
            SetStatus(_renderFailure ?? "描画エンジンなしで起動しています", isError: true);
            RendererText.Text = string.Empty;
            return;
        }

        string? device = _context.RenderLoop.DeviceState switch
        {
            DeviceState.Recovering => $"GPU が使えなくなったため、作り直しています（{Math.Max(1, _context.RenderLoop.RecoveryAttempts)} 回目）…",
            DeviceState.Failed => "GPU を作り直せませんでした。作業を保存して、アプリを再起動してください（ヘルプの「GPU を作り直す」で、もう一度試せます）",
            _ => null,
        };
        if (device is not null)
        {
            SetStatus(device, isError: true);
        }
        else if (!_busy)
        {
            bool recovered = _renderFailure is null && DateTime.Now - _recoveredAt < TimeSpan.FromSeconds(8);
            SetStatus(_renderFailure ?? (recovered ? "GPU を作り直して、描画を再開しました" : "準備完了"), isError: _renderFailure is not null);
        }
        EngineInfo info = engine.Info;
        string presenter = _context.SurfaceSharingUnavailable ? "読み出し方式" : "共有方式";
        RendererText.Text = $"描画: Direct3D 11（{presenter}）・{(info.IsSoftware ? "ソフトウェア" : info.AdapterName)}";
        RenderLoop loop = _context.RenderLoop;
        FrameRateText.Text = loop.IsRunning
            ? $"描画 {loop.FramesPerSecond:F0} 回/秒・{loop.AverageFrameMilliseconds:F1} ms"
            : "描画 待機中";
        RenderBreakdown b = loop.Breakdown;
        FrameRateText.ToolTip =
            $"1 回の描画の内訳（UI スレッドの時間）: 変形 {b.Prepare:F1} ms・描画の依頼 {b.Submit:F1} ms・GPU 待ち {b.Wait:F1} ms・表示 {b.Present:F1} ms";
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
        catch (NativeEngineException ex) when (ex.Result == NativeResult.DeviceLost)
        {
            return string.Empty; // 作り直した後にまた出す
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
        // GPU が使えなくなったときは描画ループが作り直すので、ここへは来ない（区画ごとの失敗だけ）
        _renderFailure = $"描画に失敗したため一部の区画を止めました: {e.Exception.Message}";
        UpdateStatus();
    }

    private void OnDeviceStateChanged(object? sender, EventArgs e)
    {
        if (_context.RenderLoop.DeviceState == DeviceState.Ready)
        {
            _recoveredAt = DateTime.Now;
        }
        UpdateStatus();
    }

    /// <summary>
    /// ヘルプ → GPU を作り直す。GPU が使えなくなったときと同じ手順（気づく → 面を外す → 作り直す → 描き直す）を通す。
    /// 作り直せなかったまま止まっているときは、もう一度だけ作り直しを試す。
    /// </summary>
    private void OnRecoverGpuClick(object sender, RoutedEventArgs e)
    {
        NativeEngine? engine = _context.Engine;
        if (engine is null || engine.IsDisposed)
        {
            SetStatus("描画エンジンなしで起動しているため、GPU を作り直せません", isError: true);
            return;
        }
        if (_context.RenderLoop.DeviceState == DeviceState.Failed)
        {
            _context.RenderLoop.RecoverNow();
            UpdateStatus();
            return;
        }
        AppLog.Info("ヘルプのメニューから GPU を作り直します（使えなくなったときと同じ手順を通す）");
        engine.SimulateDeviceLost();
        _context.RenderLoop.InvalidateAll();
    }

    /// <summary>起動時の引数で渡されたモデルを、画面が出てから読み込む（「プログラムから開く」など）。</summary>
    public void ImportOnStartup(IReadOnlyList<string> files)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () =>
        {
            // 前に保存しないまま終わった作業が残っていれば、先に尋ねる
            await _project.CheckRecoveryAsync();
            if (files.Count > 0)
            {
                await ImportFilesAsync(files);
            }
        });
    }

    private void OnNewProjectClick(object sender, RoutedEventArgs e) => _project.New();

    private async void OnOpenProjectClick(object sender, RoutedEventArgs e) => await _project.OpenWithDialogAsync();

    private void OnSaveProjectClick(object sender, RoutedEventArgs e) => _project.Save();

    private void OnSaveProjectAsClick(object sender, RoutedEventArgs e) => _project.SaveAs();

    private void OnExportMotionClick(object sender, RoutedEventArgs e) => MotionExporter.ExportVmd(_context, this);

    private void OnExportPoseClick(object sender, RoutedEventArgs e) => MotionExporter.ExportVpd(_context, this);

    private void OnExportCameraClick(object sender, RoutedEventArgs e) => MotionExporter.ExportCameraVmd(_context, this);

    private void OnRemoveModelClick(object sender, RoutedEventArgs e) => RemoveSelectedModels();

    private async void OnImportAudioClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "音声を読み込む",
            Filter = Audio.AudioImporter.FileFilter,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            await ImportFilesAsync(dialog.FileNames);
        }
    }

    private void OnRemoveAudioClick(object sender, RoutedEventArgs e)
    {
        if (_context.Scene.Audio is null)
        {
            SetStatus("外す音声がありません", isError: false);
            return;
        }
        if (Audio.AudioPresenceAction.Replace(_context, null))
        {
            SetStatus("音声を外しました（Ctrl+Z で戻せます）", isError: false);
        }
    }

    // メニューは押すと印が切り替わってから Click が来る
    private void OnPhysicsClick(object sender, RoutedEventArgs e)
    {
        _context.Physics.Enabled = PhysicsMenuItem.IsChecked;
        UpdatePhysicsControls();
    }

    // ツールバーのボタンは、押した状態そのものを見る（キーボードや支援技術から切り替えたときも効くように）
    private void OnPhysicsButtonToggled(object sender, RoutedEventArgs e)
    {
        _context.Physics.Enabled = PhysicsButton.IsChecked == true;
        UpdatePhysicsControls();
    }

    private void OnResetPhysicsClick(object sender, RoutedEventArgs e)
    {
        _context.Physics.Reset();
        Logging.AppLog.Info("物理をリセットしました（剛体を今の姿勢に置き直しました）");
    }

    private void OnExportStillClick(object sender, RoutedEventArgs e) => OpenExport(Editor.ExportKind.Still);

    private void OnExportSequenceClick(object sender, RoutedEventArgs e) => OpenExport(Editor.ExportKind.Sequence);

    private void OnExportVideoClick(object sender, RoutedEventArgs e) => OpenExport(Editor.ExportKind.Video);

    private void OpenExport(Editor.ExportKind kind)
    {
        if (_context.Engine is null)
        {
            MessageBox.Show(this, "描画エンジンが使えないため、書き出せません。", "書き出し", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _context.Playback.Pause();
        var window = new Export.ExportWindow(_context, _settings.Export, kind, _project.CurrentPath) { Owner = this };
        window.ShowDialog();
    }

    private void OnReflectionClick(object sender, RoutedEventArgs e)
    {
        var window = new ReflectionWindow(_context.Rendering) { Owner = this };
        window.ShowDialog();
    }

    private void OnEffectsClick(object sender, RoutedEventArgs e)
    {
        var window = new EffectsWindow(_context.Rendering) { Owner = this };
        window.ShowDialog();
    }

    private void OnMaterialsClick(object sender, RoutedEventArgs e) => OpenMaterials(this, _context);

    /// <summary>操作中のモデルの材質の設定を開く（モデルを選んでいなければ知らせる）。</summary>
    internal static void OpenMaterials(Window owner, EditorContext context)
    {
        if (context.ActiveModel is not { } model)
        {
            MessageBox.Show(owner, "材質を設定するモデルを、シーン一覧かプレビューで選んでください。", "材質の設定",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var window = new MaterialsWindow(context.Edits, model) { Owner = owner };
        window.ShowDialog();
    }

    private void OnIllustrationClick(object sender, RoutedEventArgs e)
    {
        var window = new IllustrationWindow(_context.Rendering) { Owner = this };
        window.ShowDialog();
    }

    private void OnRenderQualityClick(object sender, RoutedEventArgs e)
    {
        var window = new RenderQualityWindow(_context) { Owner = this };
        window.ShowDialog();
    }

    private void OnSelfShadowClick(object sender, RoutedEventArgs e) => _context.Rendering.SelfShadow = SelfShadowMenuItem.IsChecked;

    private void OnGroundShadowClick(object sender, RoutedEventArgs e) => _context.Rendering.GroundShadow = GroundShadowMenuItem.IsChecked;

    private void UpdateRenderingControls()
    {
        SelfShadowMenuItem.IsChecked = _context.Rendering.SelfShadow;
        GroundShadowMenuItem.IsChecked = _context.Rendering.GroundShadow;
    }

    private void UpdatePhysicsControls()
    {
        bool enabled = _context.Physics.Enabled;
        PhysicsMenuItem.IsChecked = enabled;
        PhysicsButton.IsChecked = enabled;
    }

    /// <summary>選んだモデル（ボーン・モーフを選んでいればその持ち主）か音声を、シーンから外す（Ctrl+Z で戻せる）。</summary>
    private void OnAddLightClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse(tag, out Domain.LightType type))
        {
            return;
        }
        // 最初のモデルの真ん中あたり（無ければ人の大きさのモデルの胸の高さ）を照らすように置く
        Vector3 target = new(0f, 10f, 0f);
        if (_context.Scene.ModelsGroup.Children.OfType<Domain.ModelNode>().FirstOrDefault() is { } model)
        {
            target = Vector3.Transform((model.BoundsMin + model.BoundsMax) * 0.5f, model.GetLocalMatrix());
        }
        Domain.LightNode light = LightPresenceAction.Create(_context.Scene, type, target);
        LightPresenceAction.Add(_context.Scene, _context.Undo, light);
        _context.Selection.Select(light, SelectMode.Replace, null);
        SetStatus($"照明「{light.Name}」を足しました（色・明るさ・届く距離はプロパティ欄で変えられます。Ctrl+Z で戻せます）", isError: false);
    }

    private void OnRemoveLightClick(object sender, RoutedEventArgs e) => RemoveSelectedLights(reportNone: true);

    /// <summary>選んでいる足した照明を外す。外したら true。</summary>
    private bool RemoveSelectedLights(bool reportNone)
    {
        Domain.LightNode[] lights = [.. _context.Selection.Items.OfType<Domain.LightNode>()];
        if (lights.Length == 0)
        {
            if (reportNone)
            {
                SetStatus("外す照明を、シーン一覧で選んでください", isError: false);
            }
            return false;
        }
        int removed = lights.Count(l => LightPresenceAction.Remove(_context.Scene, _context.Undo, l));
        if (removed == 0)
        {
            SetStatus("MMD の照明とロックした照明は外せません", isError: false);
            return true;
        }
        _context.Selection.Clear();
        SetStatus($"照明を {removed} つ外しました（Ctrl+Z で戻せます）", isError: false);
        return true;
    }

    private void RemoveSelectedModels()
    {
        if (_context.Selection.Items.OfType<Domain.AudioNode>().Any())
        {
            OnRemoveAudioClick(this, new RoutedEventArgs());
            return;
        }
        if (RemoveSelectedLights(reportNone: false))
        {
            return;
        }
        Domain.ModelNode[] models = [.. _context.Selection.Items
            .Select(n => n as Domain.ModelNode ?? n.FindAncestor<Domain.ModelNode>())
            .OfType<Domain.ModelNode>().Distinct()];
        if (models.Length == 0)
        {
            SetStatus("外すモデルを、シーン一覧かプレビューで選んでください", isError: false);
            return;
        }
        int removed = models.Count(m => ModelPresenceAction.Remove(_context, m));
        if (removed > 0)
        {
            SetStatus($"モデルを {removed} 体外しました（Ctrl+Z で戻せます）", isError: false);
        }
    }

    private void UpdateTitle()
    {
        string title = $"{_project.DisplayName}{(_project.IsDirty ? " ＊" : string.Empty)} - 3D Theater";
        TitleText.Text = title;
        Title = title;
    }

    // MMD のモデルは確認用の立方体よりずっと大きいので、全プレビューの区画をモデルに合わせる（カメラ視点は動かさない）
    private void FocusViewsOnFirstModel()
    {
        if (_context.Scene.ModelsGroup.Children.OfType<Domain.ModelNode>().FirstOrDefault() is not { } model)
        {
            return;
        }
        _context.Selection.Select(model, SelectMode.Replace, null);
        foreach (ViewportControl view in _layout.Previews.SelectMany(p => p.VisibleViews))
        {
            if (view.Camera.Kind != ViewKind.Camera)
            {
                view.FocusSelection();
            }
        }
    }

    private async void OnImportMotionClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "モーション・ポーズを読み込む",
            Filter = MotionImporter.FileFilter,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            await ImportFilesAsync(dialog.FileNames);
        }
    }

    private void OnRegisterKeysClick(object sender, RoutedEventArgs e) => RegisterKeys();

    /// <summary>選んだボーン・モーフ（モデルを選んでいれば、その中の未登録の変更）を、今のフレームのキーにする。</summary>
    private void RegisterKeys()
    {
        _context.Playback.Pause();
        IReadOnlyList<Domain.SceneNode> targets = _context.Animation.RegistrationTargets(_context.Selection.Items);
        int count = 0;
        _context.Edits.Change("キーの登録", targets, () => count = _context.Animation.Register(targets), includeKeys: true);
        if (count > 0)
        {
            AppLog.Info($"フレーム {_context.Playback.CurrentFrame} にキーを {count} 個登録しました");
        }
        else
        {
            SetStatus("登録するボーン・モーフがありません（ボーンかモーフを選ぶか、モデルを選んで動かしてから登録してください）", isError: false);
        }
    }

    private bool _updatingGizmoControls;

    private void UpdateGizmoControls()
    {
        _updatingGizmoControls = true;
        GizmoSettings g = _context.Gizmo;
        GizmoStyleCombo.SelectedIndex = g.Style == GizmoStyle.Mmd ? 0 : 1;
        GizmoSpaceCombo.SelectedIndex = g.LocalSpace ? 1 : 0;
        bool blender = g.Style == GizmoStyle.Blender;
        foreach (var (button, tool) in new[] { (SelectToolButton, GizmoTool.Select), (MoveToolButton, GizmoTool.Move), (RotateToolButton, GizmoTool.Rotate) })
        {
            button.IsChecked = blender && g.Tool == tool;
            button.IsEnabled = blender;
        }
        _updatingGizmoControls = false;
    }

    private void OnGizmoStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingGizmoControls && GizmoStyleCombo.SelectedIndex >= 0)
        {
            _context.Gizmo.Style = GizmoStyleCombo.SelectedIndex == 0 ? GizmoStyle.Mmd : GizmoStyle.Blender;
        }
    }

    private void OnGizmoSpaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingGizmoControls && GizmoSpaceCombo.SelectedIndex >= 0)
        {
            _context.Gizmo.LocalSpace = GizmoSpaceCombo.SelectedIndex == 1;
        }
    }

    private void OnGizmoToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out GizmoTool tool))
        {
            _context.Gizmo.Tool = tool;
        }
        UpdateGizmoControls();
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => UndoOnce();

    private void OnRedoClick(object sender, RoutedEventArgs e) => RedoOnce();

    private void UndoOnce()
    {
        string? what = _context.Undo.UndoDescription;
        if (_context.Undo.Undo())
        {
            SetStatus($"元に戻しました: {what}", isError: false);
        }
    }

    private void RedoOnce()
    {
        string? what = _context.Undo.RedoDescription;
        if (_context.Undo.Redo())
        {
            SetStatus($"やり直しました: {what}", isError: false);
        }
    }

    private void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = _context.Undo.CanUndo;
        RedoButton.IsEnabled = _context.Undo.CanRedo;
        UndoButton.ToolTip = _context.Undo.CanUndo ? $"元に戻す: {_context.Undo.UndoDescription}（Ctrl+Z）" : "元に戻す（Ctrl+Z）";
        RedoButton.ToolTip = _context.Undo.CanRedo ? $"やり直し: {_context.Undo.RedoDescription}（Ctrl+Y）" : "やり直し（Ctrl+Y）";
    }

    private void OnEditMenuOpened(object sender, RoutedEventArgs e)
    {
        UndoMenuItem.IsEnabled = _context.Undo.CanUndo;
        UndoMenuItem.Header = _context.Undo.CanUndo ? $"元に戻す: {_context.Undo.UndoDescription}" : "元に戻す(_U)";
        RedoMenuItem.IsEnabled = _context.Undo.CanRedo;
        RedoMenuItem.Header = _context.Undo.CanRedo ? $"やり直し: {_context.Undo.RedoDescription}" : "やり直し(_R)";
        CopyKeysMenuItem.IsEnabled = KeyCommands.HasSelection(_context);
        DeleteKeysMenuItem.IsEnabled = KeyCommands.HasSelection(_context);
        PasteKeysMenuItem.IsEnabled = KeyCommands.CanPaste(_context);
    }

    private void OnCopyKeysClick(object sender, RoutedEventArgs e) => KeyCommands.Copy(_context);

    private void OnPasteKeysClick(object sender, RoutedEventArgs e) => KeyCommands.Paste(_context, _context.Playback.CurrentFrame);

    private void OnDeleteKeysClick(object sender, RoutedEventArgs e) => KeyCommands.Delete(_context);

    private void OnMotionMenuOpened(object sender, RoutedEventArgs e) => AutoKeyMenuItem.IsChecked = _context.Animation.AutoKey;

    private void OnAutoKeyClick(object sender, RoutedEventArgs e) =>
        _context.Animation.AutoKey = sender is MenuItem { IsChecked: true };

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
            await ImportFilesAsync(dialog.FileNames);
        }
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        bool accepted = e.Data.GetData(DataFormats.FileDrop) is string[] files
            && files.Any(f => ModelImporter.IsModelFile(f) || MotionImporter.IsMotionFile(f) || Audio.AudioImporter.IsAudioFile(f)
                || Project.ProjectService.IsProjectFile(f));
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            e.Handled = true;
            await ImportFilesAsync(files);
        }
    }

    /// <summary>モデルを先に、続いてモーション・ポーズ、最後に音声を読み込む（モーションは読み込んだモデルに当てはめる）。</summary>
    private async Task ImportFilesAsync(IEnumerable<string> paths)
    {
        List<string> files = [.. paths];
        if (files.FirstOrDefault(Project.ProjectService.IsProjectFile) is { } projectFile)
        {
            await _project.OpenAsync(projectFile); // プロジェクトは 1 つだけ開く
            return;
        }
        await ImportModelsAsync(files.Where(ModelImporter.IsModelFile));
        foreach (string path in files.Where(MotionImporter.IsMotionFile))
        {
            SetStatus($"読み込み中: {System.IO.Path.GetFileName(path)}", isError: false);
            _busy = true;
            try
            {
                await MotionImporter.ImportAsync(_context, path, this);
            }
            finally
            {
                _busy = false;
                UpdateStatus();
            }
        }
        if (files.LastOrDefault(Audio.AudioImporter.IsAudioFile) is { } audioFile) // 音声は 1 つまで
        {
            await Audio.AudioImporter.ImportAsync(_context, audioFile, this);
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
                bool first = _context.Models.Count == 0;
                Domain.ModelNode? node = await ModelImporter.ImportAsync(_context, path, this);
                if (first && node is not null)
                {
                    FocusViewsOnFirstModel();
                }
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
