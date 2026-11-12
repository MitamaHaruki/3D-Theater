using System.Windows;
using Theater3D.App.Logging;
using Theater3D.Editor;

namespace Theater3D.App.Shell;

/// <summary>
/// 起動したときに、エディットモードとライブモードのどちらで始めるかを選ぶ画面。
/// プロジェクトファイルを渡されたとき（ダブルクリックで開いたときなど）と「--edit」「--live」の指定があるときは出さない。
/// </summary>
public partial class ModeSelectWindow : Window
{
    public ModeSelectWindow(AppMode? lastMode)
    {
        InitializeComponent();
        Icon = AppIcon.CreateBitmap(64);
        // 前回のモードを選んだ状態にする（Enter でそのまま始められる）
        Loaded += (_, _) => (lastMode == AppMode.Live ? LiveButton : EditButton).Focus();
        UpdateAssociation();
    }

    /// <summary>選んだモード（やめたら null）。</summary>
    public AppMode? SelectedMode { get; private set; }

    /// <summary>「ファイルを開く」で選んだファイル。</summary>
    public string? SelectedFile { get; private set; }

    private void OnEditClick(object sender, RoutedEventArgs e) => Choose(AppMode.Edit, null);

    private void OnLiveClick(object sender, RoutedEventArgs e) => Choose(AppMode.Live, null);

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "ファイルを開く", Filter = AppModes.AnyFileFilter };
        if (dialog.ShowDialog(this) == true)
        {
            Choose(AppModes.ModeOfFile(dialog.FileName) ?? AppMode.Edit, dialog.FileName);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void Choose(AppMode mode, string? file)
    {
        SelectedMode = mode;
        SelectedFile = file;
        DialogResult = true;
    }

    private void OnAssociationClick(object sender, RoutedEventArgs e)
    {
        FileAssociationCommands.Toggle(this, AssociationCheck.IsChecked == true);
        UpdateAssociation();
    }

    private void UpdateAssociation()
    {
        bool registered = FileAssociationService.IsRegistered(FileAssociationService.ExePath);
        AssociationCheck.IsChecked = registered;
        AssociationNote.Text = registered ? $"登録済み: {FileAssociationService.ExePath}" : string.Empty;
        AssociationNote.Visibility = registered ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>関連付けの登録・削除を、画面から行う（モードの選択とツールのメニューで共通）。</summary>
internal static class FileAssociationCommands
{
    /// <summary><paramref name="register"/> なら登録、そうでなければ外す。できなければ知らせる。</summary>
    public static void Toggle(Window owner, bool register)
    {
        try
        {
            if (register)
            {
                if (FileAssociationService.ExePath is not { } exe)
                {
                    MessageBox.Show(owner, "アプリの場所が分からないため、登録できませんでした。", "3D Theater", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                FileAssociationService.Register(exe);
            }
            else
            {
                FileAssociationService.Unregister();
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            AppLog.Error(register ? "ファイルの関連付けを登録できませんでした" : "ファイルの関連付けを外せませんでした", ex);
            MessageBox.Show(owner, $"{(register ? "登録" : "登録を外すこと")}ができませんでした。\n\n{ex.Message}", "3D Theater", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
