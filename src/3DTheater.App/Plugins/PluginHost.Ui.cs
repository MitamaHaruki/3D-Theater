using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Plugin.Core;
using Plugin.UI.Wpf;
using Theater3D.App.Shell;
using Theater3D.Plugin.Sdk;

namespace Theater3D.App.Plugins;

/// <summary>
/// プラグインのツールバーのボタン・右クリックのメニューの項目・状態表示の欄（指示書 7.2 の UI。SDK 1.2）。
/// ボタンとメニューの項目は、プラグインが登録したコマンドを実行する（コマンドを外すと、そのボタンと項目も外す）。
/// </summary>
public sealed partial class PluginHost
{
    private readonly List<ToolbarButton> _toolbar = [];
    private readonly List<ContextItem> _contextItems = [];
    private readonly List<StatusItem> _statusItems = [];

    /// <summary>ツールバーのボタンが増えた・減った（ツールバーを作り直す）。</summary>
    public event EventHandler? ToolbarChanged;

    /// <summary>状態表示の欄が増えた・減った（状態表示を作り直す）。</summary>
    public event EventHandler? StatusBarChanged;

    /// <summary>ツールバーのボタン（登録順）。</summary>
    public IReadOnlyList<ToolbarButton> ToolbarButtons => _toolbar;

    /// <summary>状態表示の欄（登録順）。</summary>
    public IEnumerable<FrameworkElement> StatusViews => _statusItems.Select(s => s.View);

    /// <summary>ツールバーのボタン 1 つ。</summary>
    public sealed class ToolbarButton
    {
        internal ToolbarButton(PluginHost host, RegisteredCommand source, string? glyph, string? text)
        {
            Source = source;
            Glyph = glyph;
            Text = text ?? source.Command.Title;
            Command = new CommandAdapter(host, source);
            string gesture = source.GestureText.Length > 0 ? $"（{source.GestureText}）" : string.Empty;
            string description = source.Command.Description is { Length: > 0 } d ? "\n" + d : string.Empty;
            ToolTip = $"{source.Command.Title}{gesture}{description}\n（プラグイン「{source.Plugin.Name}」）";
        }

        /// <summary>実行するコマンド。</summary>
        public RegisteredCommand Source { get; }

        /// <summary>絵の文字（無ければ <see cref="Text"/> を出す）。</summary>
        public string? Glyph { get; }

        /// <summary>ボタンの文字。</summary>
        public string Text { get; }

        public string ToolTip { get; }

        /// <summary>ボタンに付ける WPF のコマンド（押せるかどうかは、画面の操作のたびに確かめる）。</summary>
        public ICommand Command { get; }
    }

    private sealed record ContextItem(RegisteredCommand Command, MenuPlace Place);

    /// <summary>プラグインのコマンドを WPF のコマンドとして使う（例外は <see cref="Run"/>・<see cref="CanRun"/> が受け止める）。</summary>
    private sealed class CommandAdapter(PluginHost host, RegisteredCommand command) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => host.CanRun(command);

        public void Execute(object? parameter) => host.Run(command);
    }

    /// <summary>状態表示の欄 1 つ。</summary>
    private sealed class StatusItem : IStatusBarItem
    {
        private readonly PluginHost _host;

        public StatusItem(PluginHost host, PluginEntry plugin, string id)
        {
            _host = host;
            Plugin = plugin;
            Id = id;
            View = new TextBlock
            {
                Margin = new Thickness(18, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 320,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Visibility = Visibility.Collapsed,
            };
            View.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.SubText);
        }

        public PluginEntry Plugin { get; }

        public string Id { get; }

        public TextBlock View { get; }

        public string Text
        {
            get => View.Text;
            set
            {
                View.Text = value ?? string.Empty;
                View.Visibility = View.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public string? ToolTip
        {
            get => View.ToolTip as string;
            set => View.ToolTip = string.IsNullOrEmpty(value) ? null : value;
        }

        public void Dispose() => _host.RemoveStatus(this);
    }

    // 右クリックのメニューに足す項目（開くたびに作り、そのときに使えるかどうかを決める）
    private List<Control> CreateContextMenuItems(MenuPlace place)
    {
        var items = new List<Control>();
        foreach (ContextItem entry in _contextItems.Where(i => i.Place == place))
        {
            RegisteredCommand command = entry.Command;
            var item = new MenuItem
            {
                Header = command.Command.Title.Replace("_", "__", StringComparison.Ordinal),
                InputGestureText = command.GestureText,
                ToolTip = command.Command.Description,
                IsEnabled = CanRun(command),
            };
            item.Click += (_, _) => Run(command);
            items.Add(item);
        }
        return items;
    }

    // ボタンの押せるかどうかを、選択・編集・再生位置が変わったときにも確かめ直す（画面の操作が無くても変わるため）
    private void RequeryToolbar()
    {
        if (_toolbar.Count > 0)
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void AddToolbar(ToolbarButton button)
    {
        _toolbar.Add(button);
        ToolbarChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveToolbar(ToolbarButton button)
    {
        if (_toolbar.Remove(button))
        {
            ToolbarChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void AddStatus(StatusItem item)
    {
        _statusItems.Add(item);
        StatusBarChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveStatus(StatusItem item)
    {
        if (_statusItems.Remove(item))
        {
            StatusBarChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // コマンドを外したら、そのコマンドのボタンと右クリックの項目も外す
    private void RemoveUiFor(RegisteredCommand command)
    {
        _contextItems.RemoveAll(i => i.Command == command);
        if (_toolbar.RemoveAll(b => b.Source == command) > 0)
        {
            ToolbarChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static MenuPlace ParsePlace(string? place) => place switch
    {
        TheaterMenuPlaces.Preview => MenuPlace.Preview,
        TheaterMenuPlaces.SceneTree => MenuPlace.SceneTree,
        _ => throw new ArgumentException(
            $"知らないメニューの名前です（{place}）。使えるのは {TheaterMenuPlaces.Preview}・{TheaterMenuPlaces.SceneTree} です", nameof(place)),
    };

    private sealed partial class Scope : IToolbarRegistry, IContextMenuRegistry, IStatusBarRegistry
    {
        public IDisposable Register(PluginToolbarButton button)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(button);
            if (button.Glyph is { } glyph && (glyph.Length == 0 || glyph.Length > 2))
            {
                throw new ArgumentException("絵の文字（Glyph）は 1 文字にしてください", nameof(button));
            }
            var entry = new ToolbarButton(_host, OwnCommand(button.CommandId), button.Glyph, string.IsNullOrWhiteSpace(button.Text) ? null : button.Text);
            _host.AddToolbar(entry);
            return Track(new Registration(() => _host.RemoveToolbar(entry)));
        }

        public IDisposable Register(PluginContextMenuItem item)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(item);
            var entry = new ContextItem(OwnCommand(item.CommandId), ParsePlace(item.Place));
            _host._contextItems.Add(entry);
            return Track(new Registration(() => _host._contextItems.Remove(entry)));
        }

        public IStatusBarItem Add(string id)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            if (_host._statusItems.Any(s => s.Plugin == _entry && s.Id == id))
            {
                throw new ArgumentException($"同じ Id（{id}）の状態表示の欄がすでにあります", nameof(id));
            }
            var item = new StatusItem(_host, _entry, id);
            _host.AddStatus(item);
            _ = Track(new Registration(item.Dispose));
            return item;
        }

        private RegisteredCommand OwnCommand(string? id) =>
            _host._commands.FirstOrDefault(c => c.Plugin == _entry && c.Command.Id == id)
            ?? throw new ArgumentException($"コマンド「{id}」が登録されていません（ボタンやメニューの項目より先に、コマンドを登録してください）", nameof(id));
    }
}
