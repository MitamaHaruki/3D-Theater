using System.Windows;
using System.Windows.Controls;
using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Panels;

/// <summary>
/// グラフエディタ（指示書 3.1）。左に選んだ項目の値のグラフ、右に選んだキーの補間曲線の枠（MMD と同じ 0〜127）を出す。
/// 補間曲線は、点のドラッグ・数の入力・決まった形のボタンで変えられ、元に戻せる。
/// </summary>
public partial class GraphEditorPanel : UserControl
{
    private readonly EditorContext _context;
    private readonly GraphEditorViewModel _viewModel;
    private readonly GraphCanvas _graph;

    public GraphEditorPanel(EditorContext context)
    {
        InitializeComponent();
        _context = context;
        _viewModel = new GraphEditorViewModel(context);
        DataContext = _viewModel;
        _graph = new GraphCanvas(context, _viewModel);
        GraphHost.Content = _graph;
        BoxHost.Content = new InterpolationBox(_viewModel);
    }

    private void OnFitClick(object sender, RoutedEventArgs e) => _graph.FitToKeys();

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.Tag as string)
        {
            case "Linear":
                _viewModel.ApplyPreset(BezierCurve.Linear, "線形");
                break;
            case "Smooth":
                _viewModel.ApplyPreset(KeyEditing.SmoothCurve, "なめらか");
                break;
            case "EaseIn":
                _viewModel.ApplyPreset(BezierCurve.FromMmd(64, 0, 127, 127), "加速");
                break;
            case "EaseOut":
                _viewModel.ApplyPreset(BezierCurve.FromMmd(0, 0, 64, 127), "減速");
                break;
            case "Step":
                _viewModel.ApplyStep();
                break;
        }
    }
}
