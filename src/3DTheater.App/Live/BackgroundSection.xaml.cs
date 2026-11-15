using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Theater3D.App.Panels;
using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Live;

/// <summary>
/// サイドバーの「背景」（設計判断記録 78 章の 2 段目）。キャラクター以外のモデル・アクセサリを読み込む・外す・操作する。
/// 一覧で選ぶと、プレビューでも選んだことになる（ギズモで動かせる）。プレビューで背景のモデルを選ぶと、一覧でも選ぶ。
/// 下の欄は、一覧で選んだモデルを相手にする。
/// </summary>
public partial class BackgroundSection : UserControl
{
    private readonly EditorContext _context;
    private readonly ILiveSceneActions _actions;
    private readonly PropertiesPanel _properties;
    private bool _syncing;

    internal BackgroundSection(EditorContext context, ILiveSceneActions actions)
    {
        InitializeComponent();
        _context = context;
        _actions = actions;
        _properties = new PropertiesPanel(context, () => Selected) { Background = Brushes.Transparent };
        PropertiesHost.Content = _properties;
        context.LiveTracking.Changed += (_, _) => Rebuild();
        context.Scene.NodeChanged += (_, e) =>
        {
            if ((e.Node == context.Scene.ModelsGroup && e.PropertyName == nameof(SceneNode.Children))
                || (e.Node is ModelNode && e.PropertyName == nameof(SceneNode.Name)))
            {
                Rebuild();
            }
        };
        context.Selection.Changed += (_, _) => FollowSelection();
        Rebuild();
    }

    /// <summary>一覧で選んでいるモデル。</summary>
    private ModelNode? Selected => (ItemList.SelectedItem as ListBoxItem)?.Tag as ModelNode;

    // 一覧を作り直す（選んでいたモデルは選んだまま。無ければ最初のモデル）
    private void Rebuild()
    {
        ModelNode? keep = Selected;
        IReadOnlyList<ModelNode> models = LiveCast.Background(_context.Scene, _context.LiveTracking);
        _syncing = true;
        try
        {
            ItemList.Items.Clear();
            foreach (ModelNode model in models)
            {
                ItemList.Items.Add(new ListBoxItem { Content = Label(model), Tag = model, Padding = new Thickness(8, 5, 8, 5) });
            }
            ItemList.SelectedIndex = models.Count == 0 ? -1 : Math.Max(0, models.ToList().IndexOf(keep!));
        }
        finally
        {
            _syncing = false;
        }
        UpdateBody();
    }

    private static string Label(ModelNode model)
    {
        string extension = Path.GetExtension(model.FilePath).TrimStart('.').ToLowerInvariant();
        string kind = extension switch
        {
            "pmx" => "PMX",
            "vrm" => "VRM",
            "glb" or "gltf" => "glTF",
            "fbx" => "FBX",
            _ => $"アクセサリ {extension}",
        };
        return $"{model.Name}（{kind}）";
    }

    // プレビュー・シーンの一覧で背景のモデル（そのボーンなども）を選んだら、この一覧でも選ぶ
    private void FollowSelection()
    {
        if (_context.ActiveModel is not { } active || ReferenceEquals(active, Selected))
        {
            return;
        }
        int index = ItemList.Items.Cast<ListBoxItem>().ToList().FindIndex(i => ReferenceEquals(i.Tag, active));
        if (index < 0)
        {
            return;
        }
        _syncing = true;
        try
        {
            ItemList.SelectedIndex = index;
        }
        finally
        {
            _syncing = false;
        }
        UpdateBody();
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }
        if (Selected is { } model)
        {
            _context.Selection.Select(model, SelectMode.Replace, this); // プレビューでも選ぶ（ギズモで動かせる）
        }
        UpdateBody();
    }

    private void UpdateBody()
    {
        ModelNode? model = Selected;
        _properties.RefreshTarget();
        bool any = ItemList.Items.Count > 0;
        ItemList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        EmptyNote.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        Body.Visibility = model is null ? Visibility.Collapsed : Visibility.Visible;
        MakeCharacterButton.Visibility = model is not null && LiveCast.IsCharacterFile(model.FilePath) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnLoadClick(object sender, RoutedEventArgs e) => await _actions.LoadBackgroundAsync();

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } model)
        {
            _actions.RemoveModel(model);
        }
    }

    private void OnMakeCharacterClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } model)
        {
            _actions.MakeCharacter(model);
        }
    }

    private async void OnIdleMotionClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } model)
        {
            await _actions.LoadIdleMotionAsync(model);
        }
    }

    private void OnRemoveIdleMotionClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } model)
        {
            _actions.RemoveIdleMotion(model);
        }
    }

    private void OnMaterialsClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } model)
        {
            _actions.OpenMaterials(model);
        }
    }
}
