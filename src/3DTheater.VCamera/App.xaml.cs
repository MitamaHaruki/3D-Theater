using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Theater3D.VCamera;

/// <summary>
/// 3D Theater VCamera（設計判断記録 98 章）。通知領域に常駐し、ほかの PC のカメラで読んだ顔を 3D Theater（とほかのアプリ）へ送る。
/// 1 つだけ動かす（2 つ目を起動したら、知らせて終わる）。窓を閉じても動き続け、終わるのは通知領域のアイコンの「終了」。
/// </summary>
public partial class App : Application
{
    private Mutex? _single;
    private TrayIcon? _tray;
    private SendService? _service;
    private MainWindow? _window;

    public static string VersionText { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, @"Local\3DTheater.VCamera", out bool first);
        if (!first)
        {
            MessageBox.Show("3D Theater VCamera は、もう動いています（画面の右下の通知領域のアイコンから開けます）。", "3D Theater VCamera",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }
        VLog.Info($"3D Theater VCamera {VersionText} を起動します（.NET {Environment.Version} / {RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}）");
        DispatcherUnhandledException += (_, args) =>
        {
            VLog.Error("思わぬエラーが起きました", args.Exception);
            args.Handled = true;
        };

        VOptions options = VOptions.Parse(e.Args);
        foreach (string problem in options.Problems)
        {
            VLog.Warning(problem);
        }
        VSettings settings = VSettings.Load();
        if (options.Host is { } host)
        {
            settings.Host = host;
        }
        if (options.Port is { } port)
        {
            settings.Port = port;
        }
        if (options.Camera is { } camera)
        {
            settings.Camera = camera;
        }

        _service = new SendService();
        _window = new MainWindow(_service, settings, options.Video);
        _tray = new TrayIcon(ShowWindow, () => _window.ToggleSending(), () => _service.Recalibrate(), Quit);
        _service.StateChanged += (_, _) => Dispatcher.BeginInvoke(() => _tray?.Update(_service.IsRunning));
        if (!options.Hidden)
        {
            ShowWindow();
        }
        if (options.Start || settings.StartOnLaunch)
        {
            _window.StartSending();
        }
        if (options.ExitAfter is { } after)
        {
            VLog.Info($"確かめのため、起動してから {after.TotalSeconds:F0} 秒で終わります");
            var timer = new DispatcherTimer { Interval = after };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                VLog.Info($"確かめのため、起動してから {after.TotalSeconds:F0} 秒たったので終わります");
                Quit();
            };
            timer.Start();
        }
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }
        _window.Activate();
    }

    private void Quit()
    {
        _window?.CloseForExit();
        _service?.Dispose();
        _tray?.Dispose();
        VLog.Info("終了します");
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }
}
