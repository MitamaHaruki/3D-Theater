using System.Windows;
using System.Windows.Controls;
using Plugin.UI.Wpf;
using Theater3D.Plugin.Sdk;

namespace Theater3D.SamplePlugin;

/// <summary>
/// サンプルのパネル: 今のフレーム・再生中か・モデルの一覧・選択と、選んだモデルのキーの数を出し、シーンの知らせで出し直す。
/// キーは編集の知らせ（<see cref="ITheaterScene.Edited"/>）と選択の変化で数え直す（再生中に毎フレーム数えないように）。
/// 色はアプリの配色の資源（<see cref="ThemeKeys"/>）に合わせる。
/// </summary>
internal sealed class SceneInfoView : ScrollViewer
{
    private readonly ITheaterScene _scene;
    private readonly Func<int> _runCount;
    private readonly TextBlock _frame = Line();
    private readonly TextBlock _selection = Line();
    private readonly TextBlock _models = Line();
    private readonly TextBlock _keys = Line();
    private readonly TextBlock _runs = Line(sub: true);

    public SceneInfoView(ITheaterScene scene, Func<int> runCount)
    {
        _scene = scene;
        _runCount = runCount;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var button = new Button { Content = "先頭のフレームへ", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 4, 12, 4) };
        button.Click += (_, _) => _scene.SeekFrame(0);
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(Heading("再生"));
        panel.Children.Add(_frame);
        panel.Children.Add(Heading("選択"));
        panel.Children.Add(_selection);
        panel.Children.Add(Heading("モデル"));
        panel.Children.Add(_models);
        panel.Children.Add(Heading("選んだモデルのキー"));
        panel.Children.Add(_keys);
        panel.Children.Add(button);
        panel.Children.Add(_runs);
        Content = panel;

        _scene.FrameChanged += OnChanged;
        _scene.SelectionChanged += OnChanged;
        _scene.SceneChanged += OnChanged;
        _scene.SelectionChanged += OnKeysChanged;
        _scene.SceneChanged += OnKeysChanged;
        _scene.Edited += OnKeysChanged;
        Refresh();
        RefreshKeys();
    }

    /// <summary>出し直す。</summary>
    public void Refresh()
    {
        _frame.Text = $"フレーム {_scene.CurrentFrame} / {_scene.EndFrame}（{_scene.FrameRate} fps）{(_scene.IsPlaying ? "・再生中" : string.Empty)}";
        _selection.Text = _scene.SelectedName ?? "（選んでいません）";
        IReadOnlyList<ModelInfo> models = _scene.Models;
        _models.Text = models.Count == 0
            ? "（ありません）"
            : string.Join("\n", models.Select(m => $"{m.Name}（ボーン {m.BoneCount}・モーフ {m.MorphCount}{(m.IsVisible ? string.Empty : "・非表示")}）"));
        _runs.Text = $"「今のフレームを知らせる」を使った回数: {_runCount()} 回（プラグインの設定に保存）";
    }

    /// <summary>選んだモデルのキーを数え直す。</summary>
    public void RefreshKeys()
    {
        if (_scene.SelectedModelId is not { } modelId)
        {
            _keys.Text = "（モデルを選んでいません）";
            return;
        }
        int boneKeys = _scene.GetBoneNames(modelId).Sum(b => _scene.GetBoneKeys(modelId, b).Count);
        int morphKeys = _scene.GetMorphNames(modelId).Sum(m => _scene.GetMorphKeys(modelId, m).Count);
        IReadOnlyList<MorphKeyInfo> blink = _scene.GetMorphKeys(modelId, SceneInfoPlugin.BlinkMorph);
        _keys.Text = $"ボーン {boneKeys} 個・モーフ {morphKeys} 個\n"
            + $"「{SceneInfoPlugin.BlinkMorph}」: {(blink.Count == 0 ? "キーなし" : string.Join("・", blink.Select(k => $"{k.Frame}（{k.Weight:0.##}）")))}"
            + (_scene.GetBonePose(modelId, CenterDragTool.CenterBone) is { } center
                ? $"\n「{CenterDragTool.CenterBone}」の移動: {center.Translation.X:0.00}, {center.Translation.Y:0.00}, {center.Translation.Z:0.00}"
                : string.Empty);
    }

    /// <summary>知らせの受け取りをやめる（プラグインを止めたとき）。</summary>
    public void Detach()
    {
        _scene.FrameChanged -= OnChanged;
        _scene.SelectionChanged -= OnChanged;
        _scene.SceneChanged -= OnChanged;
        _scene.SelectionChanged -= OnKeysChanged;
        _scene.SceneChanged -= OnKeysChanged;
        _scene.Edited -= OnKeysChanged;
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private void OnKeysChanged(object? sender, EventArgs e) => RefreshKeys();

    private static TextBlock Heading(string text)
    {
        var block = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) };
        block.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.Text);
        return block;
    }

    private static TextBlock Line(bool sub = false)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
        block.SetResourceReference(TextBlock.ForegroundProperty, sub ? ThemeKeys.SubText : ThemeKeys.Text);
        if (sub)
        {
            block.Margin = new Thickness(0, 10, 0, 0);
            block.FontSize = 11;
        }
        return block;
    }
}
