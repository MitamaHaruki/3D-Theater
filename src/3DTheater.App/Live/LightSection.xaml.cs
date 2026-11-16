using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Theater3D.App.Panels;
using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Live;

/// <summary>
/// サイドバーの「照明」（設計判断記録 78 章の 2 段目）。照明を足す・外す・名前を変える・操作する。
/// 一覧で選ぶと、プレビューでも選んだことになる（ギズモで動かせる）。プレビューで照明を選ぶと、一覧でも選ぶ。
/// 名前や色などは、一覧で選んだ照明のプロパティ欄で変える。サンライト（MMD の照明。太陽として扱う）は外せない（設計判断記録 87 章）。
/// </summary>
public partial class LightSection : UserControl
{
    private readonly EditorContext _context;
    private readonly ILiveSceneActions _actions;
    private readonly PropertiesPanel _properties;
    private bool _syncing;

    internal LightSection(EditorContext context, ILiveSceneActions actions)
    {
        InitializeComponent();
        _context = context;
        _actions = actions;
        _properties = new PropertiesPanel(context, () => Selected) { Background = Brushes.Transparent };
        PropertiesHost.Content = _properties;
        context.Scene.NodeChanged += (_, e) =>
        {
            if ((e.Node == context.Scene.LightsGroup && e.PropertyName == nameof(SceneNode.Children))
                || (e.Node is LightNode && e.PropertyName is nameof(SceneNode.Name) or nameof(SceneNode.IsLocked)))
            {
                Rebuild();
            }
        };
        context.Selection.Changed += (_, _) => FollowSelection();
        Rebuild();
    }

    /// <summary>一覧で選んでいる照明。</summary>
    private LightNode? Selected => (ItemList.SelectedItem as ListBoxItem)?.Tag as LightNode;

    private void Rebuild()
    {
        LightNode? keep = Selected;
        LightNode[] lights = [.. _context.Scene.LightsGroup.Children.OfType<LightNode>()];
        _syncing = true;
        try
        {
            ItemList.Items.Clear();
            foreach (LightNode light in lights)
            {
                ItemList.Items.Add(new ListBoxItem { Content = Label(light), Tag = light, Padding = new Thickness(8, 5, 8, 5) });
            }
            ItemList.SelectedIndex = lights.Length == 0 ? -1 : Math.Max(0, Array.IndexOf(lights, keep));
        }
        finally
        {
            _syncing = false;
        }
        UpdateBody();
    }

    private string Label(LightNode light)
    {
        string kind = light.Type switch
        {
            LightType.Point => "点光源",
            LightType.Spot => "スポットライト",
            _ => "平行光",
        };
        return ReferenceEquals(light, _context.Scene.MainLight) ? $"{light.Name}（太陽・MMD の照明）" : $"{light.Name}（{kind}）";
    }

    // プレビュー・シーンの一覧で照明を選んだら、この一覧でも選ぶ
    private void FollowSelection()
    {
        if (_context.Selection.Primary is not LightNode light || ReferenceEquals(light, Selected))
        {
            return;
        }
        int index = ItemList.Items.Cast<ListBoxItem>().ToList().FindIndex(i => ReferenceEquals(i.Tag, light));
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
        if (Selected is { } light)
        {
            _context.Selection.Select(light, SelectMode.Replace, this); // プレビューでも選ぶ（ギズモで動かせる）
        }
        UpdateBody();
    }

    private void UpdateBody()
    {
        LightNode? light = Selected;
        _properties.RefreshTarget();
        Body.Visibility = light is null ? Visibility.Collapsed : Visibility.Visible;
        bool main = light is not null && ReferenceEquals(light, _context.Scene.MainLight);
        RemoveButton.IsEnabled = light is { CanEdit: true } && !main;
        RemoveNote.Visibility = main ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out LightType type))
        {
            _actions.AddLight(type);
        }
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } light)
        {
            _actions.RemoveLight(light);
        }
    }
}
