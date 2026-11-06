using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Theater3D.App.Panels;

/// <summary>モーフのパネル。名前をクリックするとそのモーフを選び、つまみか数値で値を変える。</summary>
public partial class MorphPanel : UserControl
{
    private readonly MorphPanelViewModel _viewModel;

    public MorphPanel(EditorContext context)
    {
        InitializeComponent();
        _viewModel = new MorphPanelViewModel(context);
        DataContext = _viewModel;
    }

    private void OnResetAllClick(object sender, RoutedEventArgs e) => _viewModel.ResetAll();

    private void OnNameMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MorphRow row })
        {
            _viewModel.Select(row);
            e.Handled = true;
        }
    }
}
