using System.Windows;
using System.Windows.Controls;
using Theater3D.App.Import;

namespace Theater3D.App.Panels;

public partial class PropertiesPanel : UserControl
{
    private readonly EditorContext _context;
    private readonly PropertiesViewModel _viewModel;

    public PropertiesPanel(EditorContext context)
    {
        InitializeComponent();
        _context = context;
        _viewModel = new PropertiesViewModel(context);
        DataContext = _viewModel;
    }

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
