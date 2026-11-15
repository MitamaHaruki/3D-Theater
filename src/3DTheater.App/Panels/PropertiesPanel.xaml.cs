using System.Windows;
using System.Windows.Controls;
using Theater3D.App.Import;

namespace Theater3D.App.Panels;

public partial class PropertiesPanel : UserControl
{
    private readonly EditorContext _context;
    private readonly PropertiesViewModel _viewModel;

    public PropertiesPanel(EditorContext context)
        : this(context, null)
    {
    }

    /// <param name="pinned">選んでいる項目の代わりに出す項目（ライブモードのサイドバー。変わったら <see cref="RefreshTarget"/>）。</param>
    public PropertiesPanel(EditorContext context, Func<Domain.SceneNode?>? pinned)
    {
        InitializeComponent();
        _context = context;
        _viewModel = new PropertiesViewModel(context, pinned);
        DataContext = _viewModel;
    }

    /// <summary>出す項目を見直す。</summary>
    public void RefreshTarget() => _viewModel.Refresh();

    private void OnResetBoneClick(object sender, RoutedEventArgs e) => _viewModel.ResetBone();

    private void OnResetPoseClick(object sender, RoutedEventArgs e) => _viewModel.ResetModelPose();

    private void OnMaterialsClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } owner)
        {
            Shell.MainWindow.OpenMaterials(owner, _context);
        }
    }

    private void OnShowReportClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Model is { } model && _context.Models.TryGetValue(model.Id, out LoadedModel? loaded))
        {
            LoadReportWindow.Show(Window.GetWindow(this), model.FilePath, loaded.Report, model.Summary);
        }
    }
}
