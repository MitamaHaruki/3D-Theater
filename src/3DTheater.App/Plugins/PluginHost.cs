using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Plugin.Core;
using Plugin.UI.Wpf;
using Theater3D.App.Logging;
using Theater3D.App.Shell;
using Theater3D.Plugin.Sdk;

namespace Theater3D.App.Plugins;

/// <summary>
/// アプリの側のプラグインの受け持ち（指示書 7 章）。プラグインを見つけて始め、登録されたコマンドをメニューとショートカットに、
/// パネルをドッキングに出す。プラグインのコード（コマンド・パネルの中身・イベント）で起きた例外は受け止めて記録し、アプリは止めない。
/// プラグインを置く場所は 2 つ（同じ id なら前が優先）:
/// - アプリのフォルダーの Plugins（アプリと一緒に配るもの。サンプル）
/// - %APPDATA%\3DTheater\Plugins（利用者が入れるもの）
/// </summary>
public sealed class PluginHost
{
    /// <summary>アプリと一緒に配るプラグインの場所。</summary>
    public static string BundledFolder { get; } = Path.Combine(AppContext.BaseDirectory, "Plugins");

    /// <summary>利用者が入れるプラグインの場所。</summary>
    public static string UserFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "3DTheater", "Plugins");

    /// <summary>このアプリが出している SDK の版と機能。</summary>
    public static PluginHostInfo Info { get; } = new(TheaterSdk.HostId, TheaterSdk.Version,
    [
        CoreCapabilities.Commands, CoreCapabilities.Settings, UiCapabilities.Panels, UiCapabilities.Dialogs,
        TheaterCapabilities.SceneRead, TheaterCapabilities.SceneSeek,
    ]);

    // アプリとプラグインで共通にする契約の DLL（Plugin.Core は PluginManager が足す）
    private static readonly string[] SharedAssemblies = [typeof(IPanelRegistry).Assembly.GetName().Name!, typeof(ITheaterScene).Assembly.GetName().Name!];

    private readonly EditorContext _context;
    private readonly DockingLayout _layout;
    private readonly Window _owner;
    private readonly List<RegisteredCommand> _commands = [];

    public PluginHost(EditorContext context, DockingLayout layout, Window owner, UserSettings settings)
    {
        _context = context;
        _layout = layout;
        _owner = owner;
        Manager = new PluginManager(Info, new PluginStateStore(settings), entry => new Scope(this, entry), new LogSink(), SharedAssemblies);
    }

    public PluginManager Manager { get; }

    /// <summary>登録されているコマンド（登録順）。</summary>
    public IReadOnlyList<RegisteredCommand> Commands => _commands;

    /// <summary>コマンドが増えた・減った（メニューを作り直す）。</summary>
    public event EventHandler? CommandsChanged;

    /// <summary>プラグインを見つけて、有効なものを始める（安全モードなら始めない）。</summary>
    public void Start(bool safeMode)
    {
        try
        {
            Directory.CreateDirectory(UserFolder); // 「プラグインのフォルダーを開く」で開けるように
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("プラグインのフォルダーを作れませんでした", ex);
        }
        Manager.Discover([BundledFolder, UserFolder]);
        Manager.StartEnabled(safeMode);
    }

    /// <summary>コマンドを実行する（例外は受け止めて知らせる）。</summary>
    public void Run(RegisteredCommand command)
    {
        if (!CanRun(command))
        {
            return;
        }
        try
        {
            command.Command.Execute();
        }
        catch (Exception ex)
        {
            Manager.ReportProblem(command.Plugin, $"コマンド「{command.Command.Title}」でエラーが起きました", ex);
            MessageBox.Show(_owner, $"プラグイン「{command.Plugin.Name}」のコマンド「{command.Command.Title}」でエラーが起きました。\n\n{ex.Message}",
                "プラグイン", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>今実行できるか（例外は「できない」として記録する）。</summary>
    public bool CanRun(RegisteredCommand command)
    {
        try
        {
            return command.Command.CanExecute?.Invoke() ?? true;
        }
        catch (Exception ex)
        {
            Manager.ReportProblem(command.Plugin, $"コマンド「{command.Command.Title}」が実行できるかを確かめるときにエラーが起きました", ex);
            return false;
        }
    }

    /// <summary>ショートカットに合うコマンドがあれば実行する（アプリの操作に使われなかったキーだけが来る）。</summary>
    public bool TryRunShortcut(Key key, ModifierKeys modifiers)
    {
        RegisteredCommand? command = _commands.FirstOrDefault(c => c.Gesture is { } g && g.Key == key && g.Modifiers == modifiers);
        if (command is null)
        {
            return false;
        }
        Run(command);
        return true;
    }

    /// <summary>
    /// ショートカットの文字（例: "Ctrl+Alt+I"）を読む。Ctrl か Alt の無いもの（文字の入力とぶつかる）・読めないものは null（理由を返す）。
    /// </summary>
    internal static KeyGesture? ParseShortcut(string? text, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        KeyGesture? gesture;
        try
        {
            gesture = new KeyGestureConverter().ConvertFromInvariantString(text.Trim()) as KeyGesture;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or FormatException)
        {
            problem = $"ショートカット「{text}」を読めません";
            return null;
        }
        if (gesture is null || (gesture.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            problem = $"ショートカット「{text}」には Ctrl か Alt を含めてください";
            return null;
        }
        return gesture;
    }

    /// <summary>登録されたコマンド。</summary>
    public sealed record RegisteredCommand(PluginEntry Plugin, PluginCommand Command, KeyGesture? Gesture)
    {
        /// <summary>メニューに出すショートカットの文字。</summary>
        public string GestureText => Gesture?.GetDisplayStringForCulture(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private void AddCommand(RegisteredCommand command)
    {
        _commands.Add(command);
        CommandsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveCommand(RegisteredCommand command)
    {
        if (_commands.Remove(command))
        {
            CommandsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>プラグイン 1 つ分の機能。Dispose で、そのプラグインが登録したものを全部外す。</summary>
    private sealed class Scope : IPluginScope, ICommandRegistry, IPanelRegistry, IPluginUi
    {
        private readonly PluginHost _host;
        private readonly PluginEntry _entry;
        private readonly List<IDisposable> _registrations = [];
        private readonly TheaterSceneService _scene;
        private bool _disposed;

        public Scope(PluginHost host, PluginEntry entry)
        {
            _host = host;
            _entry = entry;
            _scene = new TheaterSceneService(host._context, (message, ex) => host.Manager.ReportProblem(entry, message, ex));
        }

        public Window? Owner => _host._owner;

        public object? GetService(Type serviceType) =>
            serviceType == typeof(ICommandRegistry) ? this
            : serviceType == typeof(IPanelRegistry) ? this
            : serviceType == typeof(IPluginUi) ? this
            : serviceType == typeof(ITheaterScene) ? _scene
            : null;

        public IDisposable Register(PluginCommand command)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(command);
            if (_host._commands.Any(c => c.Plugin == _entry && c.Command.Id == command.Id))
            {
                throw new ArgumentException($"同じ Id（{command.Id}）のコマンドがすでにあります", nameof(command));
            }
            KeyGesture? gesture = ParseShortcut(command.Shortcut, out string? problem);
            if (problem is not null)
            {
                _host.Manager.ReportProblem(_entry, problem + "（ショートカットなしで登録しました）");
            }
            if (gesture is not null && _host._commands.FirstOrDefault(c => c.Gesture is { } g && g.Key == gesture.Key && g.Modifiers == gesture.Modifiers) is { } other)
            {
                _host.Manager.ReportProblem(_entry,
                    $"ショートカット「{command.Shortcut}」は、プラグイン「{other.Plugin.Name}」のコマンドが使っているため付けませんでした");
                gesture = null;
            }
            var registered = new RegisteredCommand(_entry, command, gesture);
            _host.AddCommand(registered);
            return Track(new Registration(() => _host.RemoveCommand(registered)));
        }

        public IDisposable Register(PluginPanel panel)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(panel);
            string contentId = DockingLayout.PluginPanelId(_entry.Id, panel.Id);
            var side = panel.Placement switch
            {
                PanelPlacement.Left => AvalonDock.Layout.AnchorableShowStrategy.Left,
                PanelPlacement.Bottom => AvalonDock.Layout.AnchorableShowStrategy.Bottom,
                _ => AvalonDock.Layout.AnchorableShowStrategy.Right,
            };
            _host._layout.RegisterPluginPanel(contentId, panel.Title, () => CreatePanelContent(panel), side);
            return Track(new Registration(() => _host._layout.UnregisterPluginPanel(contentId)));
        }

        public void ShowMessage(string message, string? title = null) =>
            MessageBox.Show(_host._owner, message, title ?? _entry.Name, MessageBoxButton.OK, MessageBoxImage.Information);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _scene.Detach();
            for (int i = _registrations.Count - 1; i >= 0; i--)
            {
                _registrations[i].Dispose();
            }
            _registrations.Clear();
        }

        private IDisposable Track(Registration registration)
        {
            _registrations.Add(registration);
            return registration;
        }

        // パネルの中身を作る（失敗したら、代わりに理由を出す）
        private FrameworkElement CreatePanelContent(PluginPanel panel)
        {
            try
            {
                return panel.CreateContent() ?? throw new InvalidOperationException("中身が null です");
            }
            catch (Exception ex)
            {
                _host.Manager.ReportProblem(_entry, $"パネル「{panel.Title}」の中身を作るときにエラーが起きました", ex);
                var text = new TextBlock
                {
                    Margin = new Thickness(12),
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"プラグイン「{_entry.Name}」のパネルを作れませんでした。\n{ex.Message}\n（プラグインの管理で、問題の記録を見られます）",
                };
                text.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.SubText);
                return text;
            }
        }
    }

    /// <summary>1 度だけ外す登録（外したものは、もう一度外さない）。</summary>
    private sealed class Registration(Action remove) : IDisposable
    {
        private Action? _remove = remove;

        public void Dispose()
        {
            _remove?.Invoke();
            _remove = null;
        }
    }

    private sealed class LogSink : IPluginLogSink
    {
        public void Info(string message) => AppLog.Info("プラグイン: " + message);

        public void Warning(string message, Exception? exception = null) => AppLog.Warning("プラグイン: " + message, exception);

        public void Error(string message, Exception? exception = null) => AppLog.Error("プラグイン: " + message, exception);
    }
}
