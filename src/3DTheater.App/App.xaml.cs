using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Theater3D.App.Logging;
using Theater3D.Engine.Interop;

namespace Theater3D.App;

public partial class App : Application
{
    private NativeEngine? _engine;

    public static string VersionText { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppLog.Initialize();
        AppLog.Info($"3D Theater {VersionText} を起動します（.NET {Environment.Version} / {RuntimeInformation.OSDescription}）");
        RegisterExceptionHandlers();
        NativeLog.MessageReceived += OnNativeLog;

        var options = StartupOptions.Parse(e.Args);
        _engine = CreateEngine(options, out string? engineError);

        if (options.SafeMode)
        {
            AppLog.Info("安全モードで起動します（プラグインを始めません）");
        }
        var window = new Shell.MainWindow(new EditorContext(_engine), engineError, options.SafeMode);
        MainWindow = window;
        window.Show();
        window.ImportOnStartup(options.Files);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 描画先（ビュー）は MainWindow を閉じるときに破棄済み。最後にエンジンを破棄する
        try
        {
            _engine?.Dispose();
        }
        catch (NativeEngineException ex)
        {
            AppLog.Error("描画エンジンの破棄に失敗しました", ex);
        }
        AppLog.Info($"終了します（終了コード {e.ApplicationExitCode}）");
        AppLog.Shutdown();
        base.OnExit(e);
    }

    private static NativeEngine? CreateEngine(StartupOptions options, out string? error)
    {
        error = null;
        var flags = options.DebugLayer ? NativeEngineOptions.DebugLayer : NativeEngineOptions.None;
        try
        {
            var (major, minor) = NativeEngine.ApiVersion;
            AppLog.Info($"描画エンジン API {major}.{minor} を読み込みました");
            if (!options.UseWarp)
            {
                try
                {
                    return LogCreated(NativeEngine.Create(flags));
                }
                catch (NativeEngineException ex)
                {
                    AppLog.Warning("GPU での初期化に失敗したため、ソフトウェア描画（WARP）で起動します", ex);
                }
            }
            return LogCreated(NativeEngine.Create(flags | NativeEngineOptions.UseWarp));
        }
        catch (DllNotFoundException ex)
        {
            AppLog.Error("描画エンジン (3DTheater.Engine.Native.dll) が見つかりません", ex);
            error = "描画エンジン (3DTheater.Engine.Native.dll) が見つかりません。ビルド手順（README）を確認してください。";
        }
        catch (NativeEngineException ex)
        {
            AppLog.Error("描画エンジンを初期化できませんでした", ex);
            error = $"描画エンジンを初期化できませんでした。\n{ex.Message}";
        }
        return null;
    }

    private static NativeEngine LogCreated(NativeEngine engine)
    {
        EngineInfo info = engine.Info;
        AppLog.Info(
            $"描画エンジンを初期化しました: {info.AdapterName} / 機能レベル {info.FeatureLevel} / " +
            $"ソフトウェア描画 {(info.IsSoftware ? "はい" : "いいえ")} / 検査機能 {(info.DebugLayerEnabled ? "あり" : "なし")}");
        return engine;
    }

    private static void OnNativeLog(NativeLogLevel level, string message)
    {
        string text = $"[エンジン] {message}";
        switch (level)
        {
            case NativeLogLevel.Error:
                AppLog.Error(text);
                break;
            case NativeLogLevel.Warning:
                AppLog.Warning(text);
                break;
            case NativeLogLevel.Info:
                AppLog.Info(text);
                break;
            default:
                AppLog.Debug(text);
                break;
        }
    }

    private void RegisterExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("処理されなかった例外のため終了します", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("待たれていない非同期処理で例外が起きました", args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("画面の処理中に予期しない例外が起きました", e.Exception);
        MessageBox.Show(
            MainWindow,
            $"予期しないエラーが起きました。作業内容に問題がないか確認してください。\n\n{e.Exception.Message}\n\nログ: {AppLog.CurrentLogFile}",
            "3D Theater",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <param name="Files">「--」で始まらない引数。起動後に読み込むモデルのファイル。</param>
    /// <param name="SafeMode">安全モード（プラグインを始めない）。「--safe-mode」か、Shift を押しながらの起動。</param>
    private sealed record StartupOptions(bool UseWarp, bool DebugLayer, IReadOnlyList<string> Files, bool SafeMode)
    {
        public static StartupOptions Parse(string[] args)
        {
            bool useWarp = args.Contains("--warp", StringComparer.OrdinalIgnoreCase);
#if DEBUG
            bool debugLayer = true;
#else
            bool debugLayer = args.Contains("--debug-layer", StringComparer.OrdinalIgnoreCase);
#endif
            string[] files = [.. args.Where(a => !a.StartsWith("--", StringComparison.Ordinal))];
            bool safeMode = args.Contains("--safe-mode", StringComparer.OrdinalIgnoreCase)
                || System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LeftShift)
                || System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RightShift);
            return new StartupOptions(useWarp, debugLayer, files, safeMode);
        }
    }
}
