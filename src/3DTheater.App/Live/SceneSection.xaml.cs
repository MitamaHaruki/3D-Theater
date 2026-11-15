using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Theater3D.App.Logging;

namespace Theater3D.App.Live;

/// <summary>シーンを切り替える・保存する操作（メインの窓が行う。設計判断記録 78 章の 4 段目）。</summary>
internal interface ILiveSceneFiles
{
    /// <summary>シーンのフォルダーの、その名前のシーンに切り替える（何か置いてあれば、先に確かめる）。</summary>
    Task ApplySceneAsync(string name);

    /// <summary>今のシーンに保存する（無題なら名前を聞く）。</summary>
    void SaveScene();

    /// <summary>名前を聞いて、新しいシーンとして保存する。</summary>
    void SaveSceneAs();

    /// <summary>何も置いていないシーンにする。</summary>
    void NewScene();

    /// <summary>シーンのフォルダーの外のファイルを開く。</summary>
    Task OpenOtherFileAsync();
}

/// <summary>
/// サイドバーの「シーン」（設計判断記録 78 章の 4 段目）。シーンは、「自室」「カラオケルーム」のような、使う場面ごとの舞台。
/// 1 つのシーンは、シーンのフォルダーのライブのファイル（.3dtlive）1 つ。保存してあるシーンをコンボボックスで選び、
/// 「適用」で、そのシーンのモデル・照明・カメラなどに切り替える。「保存」で今の状態をすべて保存する。
/// </summary>
public partial class SceneSection : UserControl
{
    private readonly ILiveSceneFiles _files;
    private bool _updating;
    private string? _currentName;
    private bool _dirty;

    internal SceneSection(ILiveSceneFiles files)
    {
        InitializeComponent();
        _files = files;
        RefreshList();
    }

    /// <summary>今のシーンを出す（題名には出さないので、ここに出す）。</summary>
    /// <param name="displayName">シーン（ファイル）の名前。無題なら「無題」。</param>
    /// <param name="libraryName">シーンのフォルダーのシーンなら、その名前（ほかの場所のファイル・無題なら null）。</param>
    public void ShowCurrent(string displayName, bool dirty, string? libraryName)
    {
        _currentName = libraryName;
        _dirty = dirty;
        string where = libraryName is null && displayName != "無題" ? "（シーンのフォルダーの外のファイル）" : string.Empty;
        CurrentText.Text = $"今のシーン: {displayName}{where}{(dirty ? " ＊（保存していない変更があります）" : string.Empty)}";
        RefreshList();
    }

    /// <summary>保存してあるシーンを読み直す（今のシーンを選んでおく）。</summary>
    public void RefreshList()
    {
        string? keep = (SceneCombo.SelectedItem as ComboBoxItem)?.Content as string;
        IReadOnlyList<string> names = SceneLibrary.Names();
        _updating = true;
        try
        {
            SceneCombo.Items.Clear();
            foreach (string name in names)
            {
                SceneCombo.Items.Add(new ComboBoxItem { Content = name });
            }
            string? select = _currentName is not null && names.Contains(_currentName) ? _currentName : keep;
            SceneCombo.SelectedIndex = select is null ? -1 : names.ToList().IndexOf(select);
        }
        finally
        {
            _updating = false;
        }
        EmptyNote.Visibility = names.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SceneCombo.IsEnabled = names.Count > 0;
        FolderText.Text = $"シーンの置き場所: {SceneLibrary.Folder}";
        UpdateApply();
    }

    private string? SelectedName => (SceneCombo.SelectedItem as ComboBoxItem)?.Content as string;

    // 選んだのが今のシーンで、保存していない変更も無ければ、適用しても何も変わらないので押せない形にする
    private void UpdateApply() =>
        ApplyButton.IsEnabled = SelectedName is { } name && !(name == _currentName && !_dirty);

    private void OnDropDownOpened(object? sender, EventArgs e) => RefreshList(); // ほかで足した・消したシーンも出す

    private void OnSceneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating)
        {
            UpdateApply();
        }
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (SelectedName is { } name)
        {
            await _files.ApplySceneAsync(name);
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => _files.SaveScene();

    private void OnSaveAsClick(object sender, RoutedEventArgs e) => _files.SaveSceneAs();

    private void OnNewClick(object sender, RoutedEventArgs e) => _files.NewScene();

    private async void OnOpenOtherClick(object sender, RoutedEventArgs e) => await _files.OpenOtherFileAsync();

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(SceneLibrary.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SceneLibrary.Folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppLog.Warning($"シーンのフォルダーを開けませんでした: {SceneLibrary.Folder}", ex);
        }
    }
}
