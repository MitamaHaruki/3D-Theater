using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Theater3D.App.Logging;
using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Live;

/// <summary>
/// サイドバーの「カメラ」（設計判断記録 78 章の 3 段目）。「ライブカメラ」（初めからある・外せない・名前を変えない）と、
/// 別の視点のカメラを足す・外す・名前を変える。プレビュー・配信に映すカメラの切り替えは「カメラ情報」で行う。
/// </summary>
public partial class CameraSection : UserControl
{
    private readonly EditorContext _context;
    private bool _syncing;

    internal CameraSection(EditorContext context)
    {
        InitializeComponent();
        _context = context;
        context.LiveCameras.Changed += (_, _) => Rebuild();
        context.Scene.NodeChanged += (_, e) =>
        {
            if ((e.Node == context.Scene.CamerasGroup && e.PropertyName == nameof(SceneNode.Children))
                || (e.Node is CameraNode && e.PropertyName is nameof(SceneNode.Name) or nameof(SceneNode.IsLocked)))
            {
                Rebuild();
            }
        };
        Rebuild();
    }

    private SceneDocument Scene => _context.Scene;

    /// <summary>一覧で選んでいるカメラ。</summary>
    private CameraNode? Selected => (ItemList.SelectedItem as ListBoxItem)?.Tag as CameraNode;

    private void Rebuild()
    {
        CameraNode? keep = Selected;
        CameraNode[] cameras = [.. Scene.CamerasGroup.Children.OfType<CameraNode>()];
        CameraNode preview = _context.LiveCameras.PreviewCamera(Scene);
        CameraNode onAir = _context.LiveCameras.OnAirCamera(Scene);
        _syncing = true;
        try
        {
            ItemList.Items.Clear();
            foreach (CameraNode camera in cameras)
            {
                var marks = new List<string>();
                if (ReferenceEquals(camera, preview))
                {
                    marks.Add("見ている");
                }
                if (ReferenceEquals(camera, onAir))
                {
                    marks.Add("配信中");
                }
                string label = marks.Count == 0 ? camera.Name : $"{camera.Name}（{string.Join("・", marks)}）";
                ItemList.Items.Add(new ListBoxItem { Content = label, Tag = camera, Padding = new Thickness(8, 5, 8, 5) });
            }
            ItemList.SelectedIndex = cameras.Length == 0 ? -1 : Math.Max(0, Array.IndexOf(cameras, keep));
        }
        finally
        {
            _syncing = false;
        }
        UpdateBody();
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing)
        {
            UpdateBody();
        }
    }

    private void UpdateBody()
    {
        CameraNode? camera = Selected;
        Body.Visibility = camera is null ? Visibility.Collapsed : Visibility.Visible;
        if (camera is null)
        {
            return;
        }
        bool live = ReferenceEquals(camera, Scene.MainCamera);
        NameBox.Text = camera.Name;
        NameBox.IsEnabled = !live && camera.CanEdit;
        RenameButton.IsEnabled = !live && camera.CanEdit;
        RemoveButton.IsEnabled = !live && camera.CanEdit;
        ViewButton.IsEnabled = !ReferenceEquals(camera, _context.LiveCameras.PreviewCamera(Scene));
        LiveNote.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        CameraNode from = _context.LiveCameras.PreviewCamera(Scene);
        CameraNode camera = CameraPresenceAction.Create(Scene, from);
        CameraPresenceAction.Add(Scene, _context.Undo, camera);
        _context.LiveCameras.Preview = camera.Id; // 足したカメラで見る（動かして、別の視点にする）
        int index = ItemList.Items.Cast<ListBoxItem>().ToList().FindIndex(i => ReferenceEquals(i.Tag, camera));
        ItemList.SelectedIndex = index;
        AppLog.Info($"カメラ「{camera.Name}」を足しました（「{from.Name}」と同じ所から）");
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } camera && CameraPresenceAction.Remove(Scene, _context.Undo, camera))
        {
            AppLog.Info($"カメラ「{camera.Name}」を外しました");
        }
    }

    private void OnViewClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } camera)
        {
            _context.LiveCameras.Preview = ReferenceEquals(camera, Scene.MainCamera) ? null : camera.Id;
        }
    }

    private void OnRenameClick(object sender, RoutedEventArgs e) => Rename();

    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Rename();
            e.Handled = true;
        }
    }

    private void Rename()
    {
        if (Selected is not { } camera || ReferenceEquals(camera, Scene.MainCamera) || !camera.CanEdit)
        {
            return;
        }
        string name = NameBox.Text.Trim();
        if (name.Length == 0 || name == camera.Name)
        {
            NameBox.Text = camera.Name;
            return;
        }
        if (name == CameraPresenceAction.LiveCameraName)
        {
            NameBox.Text = camera.Name; // ライブカメラと見分けがつかなくなるので使わない
            return;
        }
        _context.Edits.Change($"「{camera.Name}」の名前", [camera], () => camera.Name = name);
    }
}
