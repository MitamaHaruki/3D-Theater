using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Theater3D.App.Panels;

/// <summary>
/// モーフのパネル。名前をクリックするとそのモーフを選び、つまみか数値で値を変え、◆ でキーを登録する。
/// 下の欄で表情のプリセットを保存し、割合で混ぜて当てる。
/// </summary>
public partial class MorphPanel : UserControl
{
    private readonly MorphPanelViewModel _viewModel;

    public MorphPanel(EditorContext context)
        : this(context, null)
    {
    }

    /// <param name="pinned">選んでいるモデルの代わりに出すモデル（ライブモードの「キャラクター」。変わったら <see cref="RefreshTarget"/>）。</param>
    public MorphPanel(EditorContext context, Func<Domain.ModelNode?>? pinned)
    {
        InitializeComponent();
        _viewModel = new MorphPanelViewModel(context, pinned);
        DataContext = _viewModel;
    }

    /// <summary>出すモデルを見直す。</summary>
    public void RefreshTarget() => _viewModel.RefreshTarget();

    private void OnResetAllClick(object sender, RoutedEventArgs e) => _viewModel.ResetAll();

    private void OnNameMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MorphRow row })
        {
            _viewModel.Select(row);
            e.Handled = true;
        }
    }

    private void OnKeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MorphRow row })
        {
            _viewModel.RegisterKey(row);
        }
    }

    private void OnRegisterChangedClick(object sender, RoutedEventArgs e) => _viewModel.RegisterUnregistered();

    private void OnPresetNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SavePreset();
            e.Handled = true;
        }
    }

    private void OnSavePresetClick(object sender, RoutedEventArgs e) => SavePreset();

    private void SavePreset()
    {
        Window? owner = Window.GetWindow(this);
        if (_viewModel.PendingPresetName is not { } name)
        {
            MessageBox.Show(owner!, $"プリセットの名前は {Editor.MorphPreset.MaxNameLength} 文字までにしてください。", "表情のプリセット",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_viewModel.Rows.Count == 0)
        {
            MessageBox.Show(owner!, "保存するモーフがありません（上の一覧に出ているモーフの値を保存します）。", "表情のプリセット",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_viewModel.Presets.Any(p => p.Name == name)
            && MessageBox.Show(owner!, $"「{name}」は、もうあります。今の値で置き換えますか？", "表情のプリセット",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        _viewModel.SavePreset(name);
    }

    private void OnApplyPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MorphPresetRow row })
        {
            _viewModel.ApplyPreset(row);
        }
    }

    private void OnDeletePresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MorphPresetRow row }
            && MessageBox.Show(Window.GetWindow(this)!, $"表情のプリセット「{row.Name}」を消しますか？（モーフの値は変わりません）", "表情のプリセット",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _viewModel.DeletePreset(row);
        }
    }

    private void OnResetMixClick(object sender, RoutedEventArgs e) => _viewModel.ResetMix();
}
