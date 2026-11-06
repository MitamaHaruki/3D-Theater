using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Theater3D.Editor;

namespace Theater3D.App.Preview;

/// <summary>プレビュー 1 枚の設定。レイアウトと一緒に保存する。</summary>
public sealed record PreviewSettings(PreviewLayout Layout, string QuadPresetName, ViewKind[] Views, bool ShowGrid, bool ShowBones = true)
{
    public static PreviewSettings Default(PreviewLayout layout = PreviewLayout.Quad) =>
        new(layout, PreviewLayouts.DefaultQuadPreset.Name, [.. PreviewLayouts.DefaultViews(layout)], true);
}

/// <summary>
/// ドッキング画面に置くプレビュー 1 枚。中を 1・2・4 画面に分け、区画ごとに視点を持つ。
/// プレビューは何枚でも追加でき、たとえば片方を四面図、もう片方をカメラ視点にできる。
/// </summary>
public partial class PreviewPanel : UserControl, IDisposable
{
    private const int MaxViews = 4;

    private readonly ViewportControl[] _views = new ViewportControl[MaxViews];
    private PreviewLayout _layout;
    private QuadPreset _quadPreset = PreviewLayouts.DefaultQuadPreset;
    private int _maximizedIndex = -1;
    private bool _updatingControls;

    public PreviewPanel(EditorContext context, string contentId, PreviewSettings? settings = null)
    {
        InitializeComponent();
        ContentId = contentId;
        PresetCombo.ItemsSource = PreviewLayouts.QuadPresets;
        PresetCombo.DisplayMemberPath = nameof(QuadPreset.Name);

        for (int i = 0; i < MaxViews; i++)
        {
            var view = new ViewportControl(context, ViewKind.Free);
            view.Activated += (_, _) => ViewportActivated?.Invoke(this, view);
            view.MaximizeToggled += (_, _) => ToggleMaximize(view);
            _views[i] = view;
            ViewGrid.Children.Add(view);
        }
        Apply(settings ?? PreviewSettings.Default());
    }

    /// <summary>中の区画のどれかが操作された。</summary>
    public event EventHandler<ViewportControl>? ViewportActivated;

    /// <summary>ドッキング画面とレイアウト保存で使う識別名（例: "Preview.1"）。</summary>
    public string ContentId { get; }

    public PreviewLayout Layout => _layout;

    public IEnumerable<ViewportControl> VisibleViews => _views.Where(v => v.Visibility == Visibility.Visible);

    public ViewportControl FirstView => _views[0];

    public void SetLayout(PreviewLayout layout)
    {
        _layout = layout;
        _maximizedIndex = -1;
        IReadOnlyList<ViewKind> kinds = PreviewLayouts.DefaultViews(layout, _quadPreset);
        for (int i = 0; i < kinds.Count; i++)
        {
            _views[i].Camera.SetKind(kinds[i]);
        }
        Arrange();
    }

    public void SetQuadPreset(QuadPreset preset)
    {
        _quadPreset = preset;
        SetLayout(PreviewLayout.Quad);
    }

    public bool ShowGrid
    {
        get => _views[0].ShowGrid;
        set
        {
            foreach (ViewportControl view in _views)
            {
                view.ShowGrid = value;
            }
            UpdateControls();
        }
    }

    /// <summary>選んだモデルのボーンを線で重ねるか。</summary>
    public bool ShowBones
    {
        get => _views[0].ShowBones;
        set
        {
            foreach (ViewportControl view in _views)
            {
                view.ShowBones = value;
            }
            UpdateControls();
        }
    }

    public PreviewSettings GetSettings() =>
        new(_layout, _quadPreset.Name, [.. _views.Select(v => v.Camera.Kind)], ShowGrid, ShowBones);

    public void Dispose()
    {
        foreach (ViewportControl view in _views)
        {
            view.Dispose();
        }
    }

    private void Apply(PreviewSettings settings)
    {
        _layout = settings.Layout;
        _quadPreset = PreviewLayouts.QuadPresets.FirstOrDefault(p => p.Name == settings.QuadPresetName) ?? PreviewLayouts.DefaultQuadPreset;
        for (int i = 0; i < MaxViews; i++)
        {
            ViewKind kind = i < settings.Views.Length ? settings.Views[i] : ViewKind.Free;
            _views[i].Camera.SetKind(kind);
            _views[i].ShowGrid = settings.ShowGrid;
            _views[i].ShowBones = settings.ShowBones;
        }
        Arrange();
    }

    private void ToggleMaximize(ViewportControl view)
    {
        int index = Array.IndexOf(_views, view);
        _maximizedIndex = _maximizedIndex == index ? -1 : index;
        Arrange();
    }

    // 区画の並べ方。見えない区画は Collapsed にして描かない（GPU の資源も作らない）
    private void Arrange()
    {
        int count = _layout.ViewCount();
        for (int i = 0; i < MaxViews; i++)
        {
            ViewportControl view = _views[i];
            bool visible = i < count && (_maximizedIndex < 0 || _maximizedIndex == i);
            view.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            view.IsMaximized = _maximizedIndex == i;
            view.Margin = new Thickness(1);

            (int row, int column, int rowSpan, int columnSpan) = (_maximizedIndex >= 0, _layout) switch
            {
                (true, _) or (false, PreviewLayout.Single) => (0, 0, 2, 2),
                (false, PreviewLayout.SideBySide) => (0, i, 2, 1),
                _ => (i / 2, i % 2, 1, 1),
            };
            Grid.SetRow(view, row);
            Grid.SetColumn(view, column);
            Grid.SetRowSpan(view, rowSpan);
            Grid.SetColumnSpan(view, columnSpan);
        }
        UpdateControls();
    }

    private void UpdateControls()
    {
        _updatingControls = true;
        SingleButton.IsChecked = _layout == PreviewLayout.Single;
        SideBySideButton.IsChecked = _layout == PreviewLayout.SideBySide;
        QuadButton.IsChecked = _layout == PreviewLayout.Quad;
        PresetCombo.SelectedItem = _quadPreset;
        PresetCombo.IsEnabled = _layout == PreviewLayout.Quad;
        GridButton.IsChecked = ShowGrid;
        BonesButton.IsChecked = ShowBones;
        _updatingControls = false;
    }

    private void OnLayoutClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && Enum.TryParse(tag, out PreviewLayout layout))
        {
            SetLayout(layout);
        }
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingControls && PresetCombo.SelectedItem is QuadPreset preset)
        {
            SetQuadPreset(preset);
        }
    }

    private void OnGridClick(object sender, RoutedEventArgs e) => ShowGrid = GridButton.IsChecked == true;

    private void OnBonesClick(object sender, RoutedEventArgs e) => ShowBones = BonesButton.IsChecked == true;
}
