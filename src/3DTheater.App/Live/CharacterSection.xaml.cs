using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Theater3D.App.Panels;
using Theater3D.Domain;
using Theater3D.Editor;

namespace Theater3D.App.Live;

/// <summary>
/// サイドバーの「キャラクター」（設計判断記録 78 章の 2 段目）。キャラクターを 1 体だけ読み込む・外す・操作する。
/// 中に、トラッキングの当てはめ方・表情（モーフ）・位置などの欄を置く（どれもキャラクターを相手にする。選んでいる項目には従わない）。
/// </summary>
public partial class CharacterSection : UserControl
{
    private readonly EditorContext _context;
    private readonly ILiveSceneActions _actions;
    private readonly PropertiesPanel _properties;
    private readonly MorphPanel _morphs;
    private ModelNode? _shown;

    internal CharacterSection(EditorContext context, ILiveSceneActions actions)
    {
        InitializeComponent();
        _context = context;
        _actions = actions;
        _properties = new PropertiesPanel(context, () => Character) { Background = Brushes.Transparent };
        _morphs = new MorphPanel(context, () => Character) { Background = Brushes.Transparent };
        PropertiesHost.Content = _properties;
        MorphHost.Content = _morphs;
        FitHost.Content = new TrackingFitPanel(context);
        context.LiveTracking.Changed += (_, _) => Update();
        context.Scene.NodeChanged += (_, e) =>
        {
            if ((e.Node == context.Scene.ModelsGroup && e.PropertyName == nameof(SceneNode.Children))
                || (ReferenceEquals(e.Node, _shown) && e.PropertyName == nameof(SceneNode.Name)))
            {
                Update();
            }
        };
        Update();
    }

    /// <summary>今のキャラクター（いなければ null）。</summary>
    private ModelNode? Character => LiveCast.Character(_context.Scene, _context.LiveTracking);

    private void Update()
    {
        ModelNode? character = Character;
        if (!ReferenceEquals(character, _shown))
        {
            _shown = character;
            _properties.RefreshTarget();
            _morphs.RefreshTarget();
        }
        NameText.Text = character is null ? "キャラクターがいません" : character.Name;
        LoadButton.Content = character is null ? "読み込む..." : "入れ替える...";
        RemoveButton.IsEnabled = character is not null;
        EmptyNote.Visibility = character is null ? Visibility.Visible : Visibility.Collapsed;
        Body.Visibility = character is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnLoadClick(object sender, RoutedEventArgs e) => await _actions.LoadCharacterAsync();

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (Character is { } character)
        {
            _actions.RemoveModel(character);
        }
    }

    private async void OnIdleMotionClick(object sender, RoutedEventArgs e)
    {
        if (Character is { } character)
        {
            await _actions.LoadIdleMotionAsync(character);
        }
    }

    private void OnRemoveIdleMotionClick(object sender, RoutedEventArgs e)
    {
        if (Character is { } character)
        {
            _actions.RemoveIdleMotion(character);
        }
    }

    private void OnMaterialsClick(object sender, RoutedEventArgs e)
    {
        if (Character is { } character)
        {
            _actions.OpenMaterials(character);
        }
    }

    private void OnResetPhysicsClick(object sender, RoutedEventArgs e) => _actions.ResetPhysics();
}
