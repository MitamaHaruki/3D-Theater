using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Theater3D.App.Preview;
using Theater3D.Editor;

namespace Theater3D.App.Live;

/// <summary>
/// ライブモードの画面（設計判断記録 78 章）。プレビュー 1 枚の上に、サイドバー・トラッキング情報・カメラ情報・
/// 右下の切り替え（グリッド・ボーン）・左下のエラーを重ねる。ドッキングは使わない。
/// 中身の操作（開く・保存・設定画面）は、メインの窓に知らせて、メインの窓が行う。
/// </summary>
public partial class LiveShell : UserControl
{
    private PreviewPanel? _preview;

    public LiveShell()
    {
        InitializeComponent();
    }

    /// <summary>「設定」が押された（設定画面を開く）。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>サイドバーの折り畳みの項目（上から順）。</summary>
    public IReadOnlyList<Expander> Sections => [SceneExpander, CameraExpander, LightExpander, CharacterExpander, BackgroundExpander, PanelsExpander];

    /// <summary>開いている項目の名前（<see cref="FrameworkElement.Tag"/>。次に起動したときに同じにするため、設定に覚える）。</summary>
    public IReadOnlyList<string> ExpandedSections
    {
        get => [.. Sections.Where(s => s.IsExpanded).Select(s => (string)s.Tag)];
        set
        {
            foreach (Expander section in Sections)
            {
                section.IsExpanded = value.Contains((string)section.Tag);
            }
        }
    }

    /// <summary>プレビューを置く（ツールバーと区画の見出しを隠し、カメラ視点だけを映す形にする）。</summary>
    public void SetPreview(PreviewPanel preview)
    {
        _preview = preview;
        preview.UseLiveLook();
        PreviewHost.Content = preview;
        UpdateToggles();
    }

    /// <summary>サイドバーの「カメラ」と、右上の「カメラ情報」の中身を置く（設計判断記録 78 章の 3 段目）。</summary>
    public void SetCameraParts(FrameworkElement section, FrameworkElement info)
    {
        CameraExpander.Content = section;
        CameraInfoHost.Content = info;
    }

    /// <summary>配信に映しているカメラで見ている印（赤い枠と札）を出すか。</summary>
    public void SetOnAir(bool onAir)
    {
        Visibility visibility = onAir ? Visibility.Visible : Visibility.Collapsed;
        if (OnAirFrame.Visibility != visibility)
        {
            OnAirFrame.Visibility = visibility;
            OnAirBadge.Visibility = visibility;
        }
    }

    /// <summary>サイドバーの「シーン」の中身を置く（設計判断記録 78 章の 4 段目）。</summary>
    public void SetSceneSection(FrameworkElement scene) => SceneExpander.Content = scene;

    /// <summary>サイドバーの「照明」「キャラクター」「背景」の中身を置く（設計判断記録 78 章の 2 段目）。</summary>
    public void SetCastSections(FrameworkElement light, FrameworkElement character, FrameworkElement background)
    {
        LightExpander.Content = light;
        CharacterExpander.Content = character;
        BackgroundExpander.Content = background;
    }

    /// <summary>トラッキングのパネルを、右上の「トラッキング情報」に置く（無ければ、その枠を出さない）。</summary>
    public void SetTrackingPanel(FrameworkElement? panel)
    {
        if (panel is null)
        {
            TrackingBox.Visibility = Visibility.Collapsed;
            return;
        }
        if (panel is Control control)
        {
            control.Background = Brushes.Transparent; // 半透明の黒が透けるように
        }
        TrackingHost.Content = panel;
    }

    /// <summary>作り直すまでの仮: 今のパネル（シーンの一覧・プロパティ・モーフ）を、サイドバーの「今のパネル」にタブで置く。</summary>
    public void AddPanel(string title, FrameworkElement content)
    {
        if (content is Control control)
        {
            control.Background = Brushes.Transparent;
        }
        PanelTabs.Items.Add(new TabItem { Header = title, Content = content });
        if (PanelTabs.Items.Count == 1)
        {
            PanelTabs.SelectedIndex = 0;
        }
    }

    /// <summary>左下に赤い字でエラーを出す（null か空なら消す）。</summary>
    public void ShowError(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            ErrorText.Visibility = Visibility.Collapsed;
            ErrorText.Text = string.Empty;
            ErrorText.ToolTip = null;
            return;
        }
        ErrorText.Text = text;
        ErrorText.ToolTip = text; // 長くて切れたときは、マウスを乗せると全部見える
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>今出しているエラー（無ければ空）。</summary>
    public string ErrorMessage => ErrorText.Text;

    private void UpdateToggles()
    {
        if (_preview is null)
        {
            return;
        }
        GridButton.IsChecked = _preview.ShowGrid;
        BonesButton.IsChecked = _preview.ShowBones;
    }

    // サイドバーの中に、自分で上下に動く欄（一覧・表情・プロパティ）がある。ホイールは、その欄がまだ動けるならその欄に、
    // 動けなければ（端まで来た・動く必要が無い）サイドバーに渡す。字を打っている欄・開いている選択肢は、その欄に任せる
    private void OnSidebarPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        for (DependencyObject? d = e.OriginalSource as DependencyObject; d is not null && !ReferenceEquals(d, SidebarScroll); d = ParentOf(d))
        {
            if (d is TextBoxBase { IsKeyboardFocusWithin: true } or ComboBox { IsDropDownOpen: true })
            {
                return;
            }
            if (d is ScrollViewer { ScrollableHeight: > 0 } inner
                && (e.Delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight))
            {
                return;
            }
        }
        SidebarScroll.ScrollToVerticalOffset(SidebarScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private void OnGridClick(object sender, RoutedEventArgs e)
    {
        if (_preview is not null)
        {
            _preview.ShowGrid = GridButton.IsChecked == true;
        }
    }

    private void OnBonesClick(object sender, RoutedEventArgs e)
    {
        if (_preview is not null)
        {
            _preview.ShowBones = BonesButton.IsChecked == true;
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
}
