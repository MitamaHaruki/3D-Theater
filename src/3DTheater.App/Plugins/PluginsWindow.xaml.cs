using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Plugin.Core;

namespace Theater3D.App.Plugins;

/// <summary>
/// プラグインの管理（プラグインのメニュー）。見つけたプラグインの一覧と状態・説明・問題の記録を出し、有効・無効を切り替える。
/// </summary>
public partial class PluginsWindow : Window
{
    private readonly PluginHost _host;

    internal PluginsWindow(PluginHost host)
    {
        InitializeComponent();
        _host = host;
        SafeModeBanner.Visibility = host.Manager.IsSafeMode ? Visibility.Visible : Visibility.Collapsed;
        host.Manager.Changed += OnManagerChanged;
        Closed += (_, _) => host.Manager.Changed -= OnManagerChanged;
        Refresh();
        if (PluginList.Items.Count > 0)
        {
            PluginList.SelectedIndex = 0;
        }
    }

    /// <summary>一覧の 1 行。</summary>
    public sealed record Row(PluginEntry Entry)
    {
        public string Name => Entry.Name;

        public string StatusText => StatusOf(Entry);
    }

    internal static string StatusOf(PluginEntry entry) => entry.Status switch
    {
        PluginStatus.Running => "有効（動いています）",
        PluginStatus.Disabled => "無効",
        PluginStatus.NotStarted => "有効（始めていません）",
        PluginStatus.Failed => "有効だが始められません",
        PluginStatus.Incompatible => "このアプリでは使えません",
        PluginStatus.Invalid => "plugin.json が読めません",
        PluginStatus.Duplicate => "同じ id のプラグインがあります",
        _ => entry.Status.ToString(),
    };

    private void OnManagerChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        PluginEntry? selected = (PluginList.SelectedItem as Row)?.Entry;
        PluginList.ItemsSource = _host.Manager.Plugins.Select(p => new Row(p)).ToList();
        EmptyText.Visibility = _host.Manager.Plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selected is not null)
        {
            PluginList.SelectedItem = PluginList.Items.OfType<Row>().FirstOrDefault(r => r.Entry == selected);
        }
        ShowDetails();
    }

    private void ShowDetails()
    {
        if (PluginList.SelectedItem is not Row { Entry: var entry })
        {
            DetailPanel.Visibility = Visibility.Hidden;
            ToggleButton.IsEnabled = OpenFolderButton.IsEnabled = false;
            return;
        }
        DetailPanel.Visibility = Visibility.Visible;
        PluginManifest? manifest = entry.Manifest;
        NameText.Text = entry.Name;
        StatusText.Text = entry.StatusMessage is { } message ? $"{StatusOf(entry)}。{message}" : StatusOf(entry);
        VersionText.Text = manifest is null ? "－" : $"{manifest.Version}（SDK {manifest.SdkVersion.Major}.{Math.Max(manifest.SdkVersion.Minor, 0)}、対象 {manifest.Host}）";
        IdText.Text = entry.Id;
        AuthorText.Text = string.IsNullOrEmpty(manifest?.Author) ? "－" : manifest.Author;
        DescriptionText.Text = string.IsNullOrEmpty(manifest?.Description) ? "－" : manifest.Description;
        RequiresText.Text = manifest is null || manifest.Requires.Count == 0 ? "－" : string.Join("・", manifest.Requires);
        FolderText.Text = entry.Folder + (entry.IsAssemblyLoaded && !entry.IsEnabled ? "（DLL はアプリを起動し直すまで読み込まれたままです）" : string.Empty);
        ProblemsBox.Text = entry.Problems.Count == 0
            ? "（ありません）"
            : string.Join(Environment.NewLine, entry.Problems.Select(p => $"{p.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  {p.Message}"));

        bool usable = entry.Status is not (PluginStatus.Incompatible or PluginStatus.Invalid or PluginStatus.Duplicate);
        ToggleButton.IsEnabled = usable;
        ToggleButton.Content = entry.IsEnabled ? "無効にする" : "有効にする";
        OpenFolderButton.IsEnabled = Directory.Exists(entry.Folder);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetails();

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (PluginList.SelectedItem is not Row { Entry: var entry })
        {
            return;
        }
        if (entry.IsEnabled)
        {
            _host.Manager.Disable(entry);
            return;
        }
        if (MessageBox.Show(this,
                $"プラグイン「{entry.Name}」を有効にしますか？\n\nプラグインはアプリと同じプロセスで動き、パソコンのファイルなどにも触れられます。信頼できる作者のものだけを有効にしてください。",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        if (!_host.Manager.Enable(entry))
        {
            MessageBox.Show(this, $"プラグイン「{entry.Name}」を始められませんでした。\n\n{entry.StatusMessage}", Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (PluginList.SelectedItem is Row { Entry: var entry })
        {
            OpenFolder(entry.Folder);
        }
    }

    private void OnOpenUserFolderClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(PluginHost.UserFolder);
        OpenFolder(PluginHost.UserFolder);
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Logging.AppLog.Warning($"フォルダーを開けませんでした: {folder}", ex);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
