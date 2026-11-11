using Theater3D.Domain;
using Theater3D.Plugin.Sdk;

namespace Theater3D.App.Plugins;

/// <summary>
/// SDK の <see cref="ITheaterScene"/> を、アプリの中身（<see cref="EditorContext"/>）で実装したもの（プラグイン 1 つに 1 つ）。
/// プラグインのイベントの受け取り手は 1 つずつ呼び、例外は受け止めて記録する（ほかの受け取り手とアプリは止めない）。
/// <see cref="Detach"/> の後は、イベントを知らせない（プラグインを無効にしたとき）。
/// </summary>
internal sealed class TheaterSceneService : ITheaterScene
{
    private readonly EditorContext _context;
    private readonly Action<string, Exception> _reportProblem;
    private bool _attached;

    public TheaterSceneService(EditorContext context, Action<string, Exception> reportProblem)
    {
        _context = context;
        _reportProblem = reportProblem;
        context.Playback.FrameChanged += OnFrameChanged;
        context.Selection.Changed += OnSelectionChanged;
        context.Scene.NodeChanged += OnNodeChanged;
        context.Undo.Changed += OnUndoChanged;
        _attached = true;
    }

    public event EventHandler? FrameChanged;

    public event EventHandler? SelectionChanged;

    public event EventHandler? SceneChanged;

    public event EventHandler? Edited;

    public int CurrentFrame => _context.Playback.CurrentFrame;

    public int EndFrame => _context.Playback.EndFrame;

    public int FrameRate => _context.Playback.FrameRate;

    public bool IsPlaying => _context.Playback.IsPlaying;

    public IReadOnlyList<ModelInfo> Models =>
        [.. _context.Scene.ModelsGroup.Children.OfType<ModelNode>()
            .Select(m => new ModelInfo(m.Name, m.FilePath, m.IsEffectivelyVisible, m.Bones.Count, m.Morphs.Count) { Id = m.Id.ToString() })];

    public string? SelectedName => _context.Selection.Primary?.Name;

    public string? SelectedModelId => SceneLookup.SelectedModel(_context)?.Id.ToString();

    public void SeekFrame(int frame) => _context.Playback.Seek(Math.Clamp(frame, 0, _context.Playback.EndFrame));

    public IReadOnlyList<string> GetBoneNames(string modelId) =>
        SceneLookup.Model(_context, modelId) is { } model ? [.. model.Bones.Select(b => b.Name)] : [];

    public IReadOnlyList<string> GetMorphNames(string modelId) =>
        SceneLookup.Model(_context, modelId) is { } model ? [.. model.Morphs.Select(m => m.Name)] : [];

    public BonePose? GetBonePose(string modelId, string bone) =>
        SceneLookup.Bone(_context, modelId, bone) is { } node ? new BonePose(node.Position, node.Orientation) : null;

    public float? GetMorphWeight(string modelId, string morph) => SceneLookup.Morph(_context, modelId, morph)?.Weight;

    public IReadOnlyList<BoneKeyInfo> GetBoneKeys(string modelId, string bone) =>
        SceneLookup.Bone(_context, modelId, bone) is { } node ? [.. node.Keys.Keys.Select(k => new BoneKeyInfo(k.Frame, k.Translation, k.Rotation))] : [];

    public IReadOnlyList<MorphKeyInfo> GetMorphKeys(string modelId, string morph) =>
        SceneLookup.Morph(_context, modelId, morph) is { } node ? [.. node.Keys.Keys.Select(k => new MorphKeyInfo(k.Frame, k.Weight))] : [];

    /// <summary>アプリのイベントから外す（もう知らせない）。</summary>
    public void Detach()
    {
        if (!_attached)
        {
            return;
        }
        _attached = false;
        _context.Playback.FrameChanged -= OnFrameChanged;
        _context.Selection.Changed -= OnSelectionChanged;
        _context.Scene.NodeChanged -= OnNodeChanged;
        _context.Undo.Changed -= OnUndoChanged;
        FrameChanged = null;
        SelectionChanged = null;
        SceneChanged = null;
        Edited = null;
    }

    private void OnFrameChanged(object? sender, EventArgs e) => Raise(FrameChanged, "再生位置の知らせ");

    private void OnSelectionChanged(object? sender, EventArgs e) => Raise(SelectionChanged, "選択の知らせ");

    private void OnUndoChanged(object? sender, EventArgs e) => Raise(Edited, "編集の知らせ");

    // モデルの追加・削除・名前・表示の変更だけを知らせる（ボーンの動きのような細かい変化は知らせない）
    private void OnNodeChanged(object? sender, SceneNodeChangedEventArgs e)
    {
        bool modelsChanged = (ReferenceEquals(e.Node, _context.Scene.ModelsGroup) && e.PropertyName == nameof(SceneNode.Children))
            || (e.Node is ModelNode && e.PropertyName is nameof(SceneNode.Name) or nameof(SceneNode.IsVisible));
        if (modelsChanged)
        {
            Raise(SceneChanged, "シーンの知らせ");
        }
    }

    private void Raise(EventHandler? handlers, string what)
    {
        if (!_attached || handlers is null)
        {
            return;
        }
        foreach (EventHandler handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _reportProblem($"{what}を受け取る処理でエラーが起きました", ex);
            }
        }
    }
}
