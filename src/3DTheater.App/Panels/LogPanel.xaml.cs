using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Theater3D.App.Logging;

namespace Theater3D.App.Panels;

/// <summary>アプリのログを画面で見る欄。ファイルにも同じ内容が残る。</summary>
public partial class LogPanel : UserControl
{
    private const int MaxEntries = 1000;
    private readonly ObservableCollection<LogEntry> _entries;

    public LogPanel()
    {
        InitializeComponent();
        _entries = new ObservableCollection<LogEntry>(AppLog.RecentEntries);
        Entries.ItemsSource = _entries;
        // ログはどのスレッドからも届くので、画面のスレッドへ渡してから足す
        AppLog.Written += entry => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Add(entry));
    }

    private void Add(LogEntry entry)
    {
        _entries.Add(entry);
        while (_entries.Count > MaxEntries)
        {
            _entries.RemoveAt(0);
        }
        if (IsVisible)
        {
            Entries.ScrollIntoView(entry);
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => _entries.Clear();

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
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
}
